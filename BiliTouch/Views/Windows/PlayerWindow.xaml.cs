using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

using BiliTouch.Views.Controls;

namespace BiliTouch.Views;

/// <summary>
/// 原生视频播放器窗口 (LibVLC 内核): 不再使用 WebView2, 视频流由 B 站 playurl 接口获取
/// 周边 UI 仍然是 WPF 原生(标题/UP 主卡片/点赞/收藏/评论/弹幕层), 视觉与应用主题一致
/// </summary>
public partial class PlayerWindow : FluentWindow
{
    /// <summary>
    /// 播放器**只让右侧信息栏参与**全局软件缩放(2026-10-06 用户决定): 视频画面与顶栏都保持原尺寸。
    ///
    /// ★ 为什么不能缩整窗: 本窗口的**视频区是原生 HWND**(HwndHost), 而 LibVLCSharp.WPF 还会把
    ///   `VideoView.Content`(控制栏 + 弹幕层)**搬进一棵独立的顶层浮层可视树**。整窗加 LayoutTransform
    ///   时那棵浮层树跟不上, 实测会与视频区错位 195×186px(还正好差一个系统 DPI 的 1/1.25):
    ///   `.probes/bd-probe-uiscale-player`。视频 HWND 本身其实是**跟得上**变换的(实测 0px 偏差),
    ///   出问题的只有浮层 —— 但浮层里有控制栏, 错位不可接受。
    /// ★ 所以缩放的落点是 <see cref="InfoPanel"/>: 右侧那 400px 一列(标题/UP 主卡/操作栏/
    ///   简介与评论 Tab/评论列表, 以及盖在它上面的合集面板与 Toast)全在它里面。
    ///   宽度仍是父容器给的 400(固定), LayoutTransform 只把内部按 1/倍率 排版再放大 ⇒
    ///   字与控件变大、可见内容变少, 视频区尺寸一点不受影响。
    /// </summary>
    protected override FrameworkElement? ScaleRoot => InfoPanel;

    private LibVLC _libVLC = null!;
    private MediaPlayer _mp = null!;
    /// <summary>实验性 vmem 模式：视频是普通 WPF 元素（VmemView），不是 HwndHost。</summary>
    private bool _vmemMode;
    private readonly DispatcherTimer _progressTimer;
    private string _currentBvid = "";
    /// <summary>当前播的是离线缓存里的本地文件(起播/重试/进度条完全走本地链路)</summary>
    private bool _isLocalPlayback;
    private OfflineEntry? _currentOfflineEntry;
    private string _currentLocalPath = "";
    /// <summary>
    /// 离线缓存条目对应的 B 站 BV 号（仅用于有网时在后台无感补充加载评论与弹幕，绝不写入 _currentBvid、
    /// 绝不干预本地起播或重播链路）。本地导入的非 B 站视频为空串。
    /// </summary>
    private string _offlineOnlineBvid = "";
    private CancellationTokenSource? _offlineOnlineCts;

    internal static bool HasBilibiliOnlineIdentity(OfflineEntry? entry) =>
        entry != null
        && string.IsNullOrWhiteSpace(entry.ImportSource)
        && !string.IsNullOrWhiteSpace(entry.Bvid)
        && entry.Bvid.StartsWith("BV", StringComparison.OrdinalIgnoreCase);

    private bool IsOfflineBiliMedia => _isLocalPlayback && !string.IsNullOrEmpty(_offlineOnlineBvid);
    private string EffectiveOnlineBvid => !string.IsNullOrEmpty(_currentBvid) ? _currentBvid : _offlineOnlineBvid;
    private bool CanUseOnlineDanmaku => !_isLive && (!_isLocalPlayback || (_currentCid > 0 && IsOfflineBiliMedia));
    private bool CanUseOnlineComments => !_isLive && (!_isLocalPlayback || (_aid > 0 && IsOfflineBiliMedia));

    /// <summary>
    /// 当前播的是**直播间**。和本地文件一样没有 aid/cid, 所以互动/评论/进度上报都不做;
    /// 单独留一个标志只是为了把提示文案和进度条形态区分开(直播没有总时长, 进度条要禁用)。
    /// </summary>
    private bool _isLive;
    private long _liveRoomId;
    /// <summary>直播弹幕(2026-11-04)。只在直播时启动, 换片/关闭时停掉。</summary>
    private LiveDanmakuService? _liveDanmaku;
    private long _currentCid;
    private long _ownerMid;
    private string _ownerName = "";
    /// <summary>UP 主头像地址(供右键"看大图/保存"使用, 显示时只用其缩放后的位图)</summary>
    private string _ownerFace = "";
    private long _aid;
    private long _totalMs;
    private bool _isSeeking;

    /// <summary>
    /// 跳转后的"宽限期"截止时刻(`Environment.TickCount64`)。
    ///
    /// 为什么需要: LibVLC 的 `Position`/`Time` **不会**在设置之后立刻反映新位置 ——
    /// 它得先重建解复用器、把目标位置附近的片段拉回来才能报出新时间。而进度定时器每 500ms
    /// 就会把 `_mp.Position` 回写到进度条上, 于是刚点完进度条就被"打回"到旧位置的刻度上,
    /// 用户看到的是"点了没反应", 过一会儿又突然跳过去。短视频(十几秒)尤其明显 ——
    /// 6 秒的视频里 0.5 秒的心跳间隔相当于整段时长的 8%。
    /// 宽限期内不参与回写, 并在这段时间里直接把滑块钉在跳转目标上。
    /// </summary>
    private long _seekGraceUntil;

    /// <summary>已经播到结尾(此时不 Stop, 而是盖一层"重播"遮罩, 见 OnEndReached)</summary>
    private bool _ended;

    /// <summary>
    /// 跳转后的宽限期时长: 这段时间内不把上报的位置回写到进度条。
    ///
    /// 原设计前提是"跳转刚发出时 Position 还是旧值, 回写会把用户刚点的位置顶掉"。
    /// 但 2026-09-25 实测表明前提不成立 —— LibVLC 的 Time/Position 在 seek 发出的 **100ms 内
    /// 就变成目标值**(那只是"已受理"回执, 画面还没动)。所以这个宽限期现在的实际作用只剩
    /// "兜底": 等待期本来就已经由看门狗(watchdog 分支提前 return)挡住回写了, 这里多一层保险,
    /// 防"看门狗因暂停态提前放行"那一瞬间滑块被回写摆动。
    /// 保留它是因为成本为零; 但**别再引用"Position 还是旧值"这个已被证伪的说法**。
    /// </summary>
    private const int SeekGraceMs = 500;

    // --- 跳转看门狗 ---
    // LibVLC 的 seek 是异步的: 网络流上偶尔会"丢"一次跳转指令(解复用器重建失败/片段拉取慢),
    // 表现为"拖了进度条要等好几秒甚至永远不动"。看门狗在跳转发出后盯着 _mp.Time,
    // 迟迟没到目标就重发一次(最多 SeekRetryMax 次), 等待太久就给出缓冲提示。
    private long _pendingSeekMs = -1;     // 待确认的跳转目标(毫秒), -1 = 没有
    private long _seekDeadline;           // 本次跳转的确认截止时刻(TickCount64)
    private int _seekRetries;             // 已重发的次数

    /// <summary>
    /// 本次跳转是否已经"重建 Media 救过一次"(2026-10-17 手术新增)。
    ///
    /// ★★★ 这个预算必须**跨重建存活**, 绝不能在重建后复位 —— 否则会无限循环(2026-10-17
    ///   实测用户报"快进后无限缓冲"): 重建 → Playing → MarkSeekIssued 把 _seekRetries 清零
    ///   → 补跳又失败 → 再重建 …… 每轮都要重新拉一次流, 而进度条照样在跑,
    ///   用户看到的就是"永远缓冲不完"。**重建救不回来的 DASH 长跳就该如实放弃**
    ///   (停在真实位置), 好过让播放器无限重试。
    ///
    ///   因此复位只发生在**用户真正发起一次新跳转**时(见 <see cref="SeekTo"/>),
    ///   重建路径自己不碰它。
    /// </summary>
    private bool _seekRebuildTried;

    /// <summary>
    /// "落点弹回起点"的连续确认值(-1 = 尚未观察到)。
    ///
    /// ★★★ 2026-10-31 修"长视频有时快进进度条失效、回到起点"的第二层兜底:
    ///   看门狗的既有判据是"距目标 900ms 以内才算到位", 而"跳 30 分钟 → 落回片子开头"
    ///   的偏差是分钟级, 永远进不了这个窗口 —— 于是每 2.5s 重发一次、重发 2 次、再重建
    ///   一次, 全部落回开头; 重建后 MarkSeekIssued 又把 _seekRetries 清零, 再来一轮 ……
    ///   用户看到的是**无限缓冲**。修法: 落点连续两次同时远离"跳转前位置"与"目标"
    ///   (即跳到了**别处**)时, 不再重试同一条 input, 直接重建 Media 一次 ——
    ///   重建=起新 input, 是"换片/切清晰度"那条一直好用的路。重建预算
    ///   (<see cref="_seekRebuildTried"/>)仍然只给一次, 防循环。
    /// </summary>
    private long _seekBounceSeenMs = -1;
    private long _seekBounceAt = -1;
    private long _seekIssuedAt = -1;
    private long _lastSeekTargetMs = -1;
    private bool _resumeByStartTime;
    private bool _eofSeekRebuildTried;
    private double _progressDragStartValue;

    /// <summary>
    /// seek 静音: 跳转后视频画面要等缓冲(几百 ms~几秒), 但音频解复用快, 会**先出声** ——
    /// 用户听到的是"画面还黑着/冻着, 声音已经播到那边去了"。所以 seek 一发出就静音,
    /// 画面确认到位(或放弃/换片/播完)再恢复。-1 = 当前没有处于 seek 静音。
    /// </summary>
    private int _seekMuteSavedVolume = -1;
    private const int SeekRetryMax = 2;
    private const long SeekConfirmWindowMs = 900;  // 偏早/跑偏起步窗口(实际到位容差见 SeekSettledToleranceMs)
    private const long SeekSettledToleranceMs = 3_000;
    private const long SeekBounceConfirmMs = 1_500;
    private const long SeekConfirmTimeoutMs = 3_500;
    private const long SeekEofRecoverWindowMs = 15_000;
    private const long SeekEofRecoverMinMissMs = 60_000;

    /// <summary>
    /// 判定"位置确实离开了跳转前那一点"的容差(毫秒)。
    ///
    /// 1500ms 的依据: 它只需要把"停在原处"(seek 被丢弃)与"落到别处"(分片边界)分开,
    /// 而 LibVLC 一旦真的受理了 seek, 位置会直接跳到几秒~几分钟以外 —— 实测 60s 的跳转
    /// 落在 55.2s、150s 的落在 145.7s, 都远超这个容差; 反过来"丢弃"时长视频里位置一动不动。
    /// 取太小会把 DASH 的"落点偏早"误判成丢失, 取太大则反过来漏掉真正的丢失。
    /// </summary>
    private const long SeekLandingToleranceMs = 1500;

    /// <summary>
    /// "落地但偏早"允许的**最大**偏早幅度(毫秒)。
    ///
    /// 真实偏早是秒级(实测 60s→55.2s = 4.8s、150s→145.7s = 4.3s), 取 30s 已经很宽松。
    /// 超过它就不是"落到前一个分片边界", 而是 seek 根本没落地(位置跑到片子别处去了)。
    /// ★ 2026-10-17 加这条的原因: 原来的判据没有上界, 把"落到开头 5s"(目标 329s)当成
    ///   "落地偏早"认账, 于是不重试 ⇒ 用户看到"拉过去又跳回去"。见 UpdateProgressUi。
    /// </summary>
    private const long SeekLandingMaxEarlyMs = 30_000;

    /// <summary>
    /// "落地但偏早"允许的**最小**偏早幅度(毫秒)。
    /// 与 <see cref="SeekConfirmWindowMs"/> 同量级: 比这更近就算"到位", 不必再区分。
    /// </summary>
    private const long SeekLandingMinEarlyMs = 3_000;

    /// <summary>
    /// 上一次心跳里"位置同时远离跳转起点与目标"的那个值(-1 = 上一次不满足)。
    /// 用来给"落地但偏早"做**连续两次采样**确认, 见 UpdateProgressUi 里的说明。
    /// </summary>
    private long _seekElsewhereSeenMs = -1;

    /// <summary>
    /// 上一次观察到的播放位置。**不是**用来判断"接近目标"的, 而是用来判断"位置有没有真的在走" ——
    /// 见 UpdateProgressUi 里那段"为什么不能只看 Time 接近目标"的说明。
    /// </summary>
    private long _seekObservedMs = -1;

    /// <summary>
    /// 本次跳转**发出之前**的位置(取自 TimeChanged 的最后一次真实上报)。
    ///
    /// 用途只有一个: 把"seek 被 LibVLC 丢了"和"seek 生效了、但落点明显偏早"分开 ——
    ///   · 位置还停在 <c>_seekFromMs</c> 附近  ⇒ 丢了, 该重发(见 UpdateProgressUi 的看门狗);
    ///   · 位置已经离开 <c>_seekFromMs</c> 且也不在目标附近 ⇒ 其实是落到了目标之前的分片边界
    ///     (远端 DASH 双流的固有行为), 该**收手**, 不能重发。
    ///
    /// ★ 为什么必须分开(2026-09-28 探针实测, 见下方 SeekLandingToleranceMs 的说明):
    ///   把这种情况当成"丢失"重发, 每次重发都会让 LibVLC 重建整个 input(重新建连 + 重新预缓冲),
    ///   把本来已经在正常前进的播放又打回去 —— 实测一次 5 秒的跳转因此被拖到 7.5 秒以上。
    /// </summary>
    private long _seekFromMs = -1;

    /// <summary>
    /// 跳转后多久还没到位就弹加载动画。
    ///
    /// 900ms 是实测出来的: 正常跳转"画面冻住"的时间实测为 470ms(本地/合流单文件)~
    /// 800ms(远端 DASH 双流), 阈值必须高于它, 否则每次正常跳转都会闪一下提示。
    /// 超过 900ms 还没动, 那就真的是在缓冲了, 这时候给反馈才有意义。
    /// </summary>
    private const long SeekBufferingDelayMs = 900;

    /// <summary>
    /// 跳转等待期的进度心跳间隔。
    /// 到位判定和缓冲提示都只在心跳里跑, 平时的 500ms 粒度太粗 —— 等满一跳才提示,
    /// 用户已经先看到了"冻住"。等待期改成 100ms: 提示能在阈值附近立刻出现, 到位判定也更细。
    /// </summary>
    private static readonly TimeSpan ProgressTickSeeking = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ProgressTickIdle = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 缓冲提示文案。"跳转卡住"和"播放中途卡住"共用同一条 —— 对用户来说都是"在缓冲",
    /// 而且收起时要用它判断"这张遮罩是不是我弹的"(别误收"正在切换清晰度"那种)。
    /// </summary>
    private const string BufferingText = "正在缓冲…";

    // --- 提前结束的自救(DASH 音轨先 EOF) ---
    // DASH 的音轨是 input-slave 挂载的独立流, 它先于视频到 EOF 时, LibVLC 3 会结束
    // 整个 input —— 表现为"最后几秒没声音、画面冻住、进度条提前满格"。这不是真的播完:
    // 记下最后位置, 重建 Media 从断点续播。次数 + 推进距离双保险, 防"音频确实比视频短"
    // 的片源陷入无限续播循环。
    //
    // ★ 尾部截断(2026-10-01, 用户报"结尾不断缓冲 + 进度条回退重播几秒前内容"的修复):
    //   离结尾太近时**绝不自救**。原因链: ① 续播要重建 input, 必然重新预缓冲(黑屏);
    //   ② 续播的 seek 在远端 DASH 双流上落点固定偏早 4.5~5s(见 notes-player-internals.md),
    //     所以每次自救都是"回退 5 秒重播已看过的内容"; ③ 播完回退的那几秒音轨又 EOF,
    //     再触发一次自救 —— 推进距离检查被"回退"本身满足, 于是缓冲+回退反复最多 3 次。
    //   ★★ 阈值取 10s(2026-10-01 第二次校准): 第一版 6.5s 实测不够 —— 用户实测一条
    //     08:39 的视频, 音轨 EOF 点在剩 7s 处(512s < 519-6.5=512.5s, 差 0.5s 自救照常
    //     触发), 表现 = 黑屏缓冲几秒 → 重建后续播点音轨已物理缺失 → 立刻再 EOF → 直接
    //     重播遮罩, 纯白等一趟。B 站 DASH 的音轨缺口(音频分片对齐导致)常见 0~10s,
    //     阈值必须盖过缺口的**上限**而不是"落点偏早量" —— 反正缺口之内自救也救不回
    //     任何东西(音轨在那里本来就没有数据), 直接按播完处理。
    private long _lastKnownMs;            // TimeChanged 报告的最后位置(EOF 后 _mp.Time 会归零, 不能用)
    private int _eofResumeCount;
    private long _lastEofResumeMs;
    /// <summary>
    /// 尾部音轨提前 EOF 时是否已切过一次「纯视频单流收尾」（每片最多一次，防循环）。
    /// 当远端 DASH 音轨比视频短 1.5s~10s 时，带音轨 slave 续播会立刻再 EOF，而剥离音轨后
    /// 单视频流能把最后几秒画面完整播完，避免结尾突然被截断。
    /// </summary>
    private bool _tailVideoOnlyResumed;
    private const int EofResumeMax = 3;
    private const long EofResumeMinAdvanceMs = 2000;
    private const long TailVideoOnlyMinRemainMs = 1500;
    /// <summary>
    /// 尾部静默区宽度(毫秒)。距结尾不足这个距离时, 三件事都不做:
    ///   ① EOF 不自救(重建 input 只会黑屏缓冲一趟, 音轨缺口内没有可救的数据);
    ///   ② 卡顿检测不报"正在缓冲"(两路流长度差导致的 Time 停滞是收尾常态);
    ///   ③ 进度显示改用"卡住起点 + 墙钟×倍速"外推(让进度条平滑走完最后一程)。
    /// 取值必须 ≥ B 站 DASH 音轨缺口的实际上限(实测 ~7s, 常见 0~10s)。
    /// </summary>
    private const long TailQuietZoneMs = 10000;

    /// <summary>
    /// 视频区手势层的常态底色(#01000000 —— 近透明但可命中鼠标)。
    ///
    /// ★★ 2026-10-13 的回归教训: 我曾为了"盖住 letterbox 白底"把它改成**不透明黑**,
    ///   结果用户报"视频播放时全黑"。层级搞反了 —— 这一层在 VideoView.Content
    ///   (浮层窗口)里, 位于 vout HWND **之上**, 不透明会盖住**视频画面**而不是白底。
    ///   想盖 vout 白底要用独立的遮罩层(EndOverlay / LoadingOverlay / 全屏黑幕那几层,
    ///   平时 Collapsed), 手势层必须保持近透明。
    ///
    /// EOF 时手势层会切黑盖住露出白底的 vout HWND(见 OnEndReached), 起播时用它还原。
    /// </summary>
    private static readonly Brush VideoAreaBackBrush = CreateVideoAreaBackBrush();

    // --- 全屏黑幕(见 ArmFullscreenCover 的说明) ---
    /// <summary>黑幕已压下、等待首个新帧撤掉</summary>
    private bool _fullscreenCoverArmed;
    private DispatcherTimer? _fullscreenCoverTimer;

    /// <summary>黑幕兜底超时(毫秒): 一直没等到新帧就自己撤掉, 免得挡住画面</summary>
    private const int FullscreenCoverTimeoutMs = 600;

    /// <summary>
    /// "拖动改大小期间的按住闸": 为真时 <see cref="ReleaseFullscreenCover"/> 拒绝撤罩。
    /// 用来保证遮罩从 WM_ENTERSIZEMOVE 一直压到 WM_EXITSIZEMOVE ——
    /// 拖动期间 LibVLC 会反复重协商 vout, 中途放行必然又露出白底(见 ArmResizeCover)。</summary>
    private bool _resizeHold;

    private static Brush CreateVideoAreaBackBrush()
    {
        // 近透明黑(#01000000): 见 VideoAreaBackBrush 的说明 —— 绝不能改成不透明,
        // 那会盖住视频画面(2026-10-13 实测回归: 用户看到整个画面全黑)。
        // 之所以不是完全透明: 背景全透明时鼠标事件会穿透, 手势就收不到了。
        var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x01, 0, 0, 0));
        b.Freeze();
        return b;
    }

    private bool _danmakuOn = true;

    /// <summary>是否显示彩色弹幕(全局设置)。关掉之后所有弹幕统一白色, 只看内容不受颜色干扰。</summary>
    private bool _danmakuColorful = true;

    /// <summary>是否开启弹幕文字描边(全局设置, 默认不开启)。</summary>
    private bool _danmakuOutline;

    // 弹幕条目类型见 Helpers/RawDanmaku(时间秒 / 文本 / 模式 / 颜色)
    private readonly List<RawDanmaku> _danmaku = new();

    /// <summary>
    /// 未经过滤的原始弹幕(按时间有序)。
    /// 保留它是为了让"设置页改了屏蔽词/智能屏蔽"能在不重新请求接口的前提下立刻重算 _danmaku ——
    /// 否则用户想试一个屏蔽词就得重新拉一遍弹幕, 体验很差(而且长视频要连拉几十个分段)。
    /// </summary>
    private readonly List<RawDanmaku> _danmakuRaw = new();
    private int _indexInLib;
    private string? _lastPlayUrl;

    /// <summary>
    /// 上一次用的音轨地址(DASH 才有, 单文件源为 null)。
    /// 留着它只为一件事: 「重播」。DASH 的音视频是两个文件, 重播必须原样重建这个组合,
    /// 否则重播会变成"只有画面没有声音"。
    /// </summary>
    private string? _lastAudioUrl;

    // --- 关闭流程状态 ---
    // _closing:     关闭流程一开始就置位, 用于拦住所有 LibVLC 原生回调与尚未跑完的异步流程。
    //               LibVLC 的事件都在它自己的内部线程上触发, 不受 WPF 关闭顺序保护,
    //               若不拦住, 窗口销毁后回调仍会访问已释放的原生对象(0xc0000005 访问违例)。
    // _teardownDone: 原生资源拆解完成标记, 让第二次 Close() 能正常放行。
    private volatile bool _closing;
    private bool _teardownDone;
    // 是否已在 VlcCore 登记本窗口的 MediaPlayer(构造函数中途抛异常时可能没登记, 避免计数漂移)
    private bool _playerRegistered;

    // --- 清晰度 ---
    private readonly List<(int qn, string label)> _qualities = new();

    /// <summary>
    /// 本次播放**请求**的清晰度(qn)。
    ///
    /// ★ 2026-10-04 起它的初值不再是写死的 80, 而是用户设置里的「默认画质」
    ///   (见 SettingsStore.PreferredQualityQn)。每个片源开始时由
    ///   <see cref="ResetForNewMedia"/> 重新快照一次 —— 所以用户在设置页改完再点开
    ///   下一个视频就生效, 不需要重启。
    /// ★ 起播完成后这里会被换成**实际拿到**的档位(见 FillQualityMenu): 该档不存在或
    ///   没有权限时 B 站会降级返回别的档, 之后"切清晰度"的基准应该是实际在播的那一档。
    /// </summary>
    private int _currentQn = QualityPreference.DefaultQn;
    private bool _switchingQuality;
    private bool _fillingQualityMenu;
    /// <summary>直播请求的档位(0=默认)。与点播的 _currentQn 分开: 直播的档位值域不一样。</summary>
    private int _liveQn;
    /// <summary>直播**实际**拿到的档位(服务端可能降级, 界面要按这个写)</summary>
    private int _liveActualQn;

    /// <summary>当前视频可选的音轨(2026-11-04)。空 = 这条视频没有多档音质。</summary>
    private List<Models.AudioTrack> _audioTracks = new();
    /// <summary>当前**实际在播**的音质(服务端可能降级, 界面按这个打勾)</summary>
    private Models.AudioQuality _actualAudio = Models.AudioQuality.Auto;
    // 切换清晰度后要恢复到的进度(>0 生效, 起播后清零)
    private long _resumeAfterSwitchMs;

    // --- 弹幕显示区域占比(默认 25%, 即弹幕只占画面上方 1/4) ---
    private double _danmakuAreaRatio = 0.25;

    // --- 点赞状态(用于点赞/取消点赞切换) ---
    private bool _isLiked;
    // --- 关注状态(用于关注/取消关注切换) ---
    private bool _isFollowed;
    // --- 进度条 UI 回写保护(避免定时器刷新触发 ValueChanged 造成 seek 循环) ---
    private bool _updatingSlider;

    /// <summary>收藏动作进行中, 防止连点导致 add/del 交叉</summary>
    private bool _favBusy;

    /// <summary>当前视频是否已在「稍后再看」里(用于按钮的点亮态)</summary>
    private bool _isInWatchLater;

    /// <summary>当前视频是否已投过币(用于投币按钮的点亮态)</summary>
    private bool _isCoined;

    /// <summary>稍后再看的加入/移出进行中, 防止连点把 add/del 交叉发出去</summary>
    private bool _watchLaterBusy;
    /// <summary>操作栏计数(2026-09-26 起操作栏是"图标 + 数量", 不再是"点赞/投币/收藏"三个字)</summary>
    private long _likeCount;
    private long _coinCount;
    private long _favCount;
    private bool _isFavorited;
    // 当前视频标题(收藏夹选择窗的副标题用; 窗口 Title 带 " - BiliTouch" 后缀不能直接用)
    private string _videoTitle = "";

    /// <summary>
    /// 当前视频封面地址。「听视频」聆听态拿它当画面锚点。
    /// 跟 _videoTitle 一样属于"这一片"的属性, 换片必须清。
    /// </summary>
    private string _coverUrl = "";

    // --- 视频区手势: 单击播放暂停 / 双击全屏 / 长按快进 ---
    private DispatcherTimer? _longPressTimer;     // 检测长按 >= 500ms
    private DispatcherTimer? _singleClickTimer;   // 单击延迟执行(给双击留判定窗口)
    private bool _longPressFired;                  // 当前是否处于长按快进态
    // 双击抑制标记: 第二次点击(Down, ClickCount>=2)到达时置位, 让随后的 Up 不再启动单击定时器。
    // 没有这个标记时: Down 阶段虽然停掉了单击定时器, 但 Up 阶段又会新建一个,
    // 于是双击会先全屏、再在 ~200ms 后补一次播放/暂停 —— 就是"双击既暂停又全屏"的根因。
    private bool _suppressSingleClick;

    // --- 长按快进: **改倍速(SetRate)** ---
    //
    // ★★★ 2026-10-16 定稿: 长按快进回到 SetRate。曾经改过一次 seek 式, 已撤回。
    //
    // 为什么撤掉 seek 式(用户实机反馈"画面和音频均卡顿"):
    //   周期性向前 seek 每 400ms 就要重定位解复用器、重建 DASH 音视频两路、重解码关键帧,
    //   **画面和音频各被反复打断** —— 这是结构性的, 调参数救不了(实测平均 1.9x 也救不了手感)。
    //   而"倍速按钮 2x"是顺滑的(用户对照验证过): SetRate 只改 scaletempo 的 scale,
    //   不重建滤镜链、不打散解码 —— 顺滑度是 SetRate 天生就有的。
    //
    // SetRate 的代价: libvlc 在速率生效时会丢一次音频输出缓冲, 原生日志打
    //   `playback way too late: flushing buffers`。**实测(探针 bd-probe-rateguard2)这个代价比想象中小**:
    //     松手后真正的停顿只有 **0.25~0.5s**(吞吐比 0.79~1.12), 不是曾经以为的 2 秒。
    //     之前听到的"2 秒"其实是: 松手瞬间 OS 缓冲里还排着已解码音频,
    //     它仍在**以 2 倍速**播完(实测按住期间吞吐达正常 1.3x 的 3.4 倍)⇒ 听感像"还在快进",
    //     等它播空才是那一小段静音。
    //
    // ★ 三个"缓解"手段实测**全都更糟**, 别再试:
    //     · 松手前先压音量再改 rate(prewarm)  → 停顿 2.25s、吞吐比 0.01
    //     · 按住期间全程静音(muteheld)        → 停顿 2.00s、吞吐比 0.27
    //     · 松手不改 rate 只补一次 seek(norestore) → 停顿 1.50s
    //   即: 什么都不做(base, 停顿 0.25~0.5s)就是最优解。
    //
    // ★ 长按期间不改 SpeedText: 它是控制栏那个**播放速率**指示。长按的反馈交给 ShowSpeedHint 浮层。
    /// <summary>长按快进的倍速</summary>
    private const float FastForwardRate = 2.0f;

    /// <summary>当前是否处于"长按快进"中。</summary>
    private bool _isFastForwarding;

    /// <summary>
    /// 本次长按快进的**目标速率**(= max(2x, 按下时的速率), 上限 4x)。
    /// ★ 提示文案要用它而不是常量 2x —— 用户在 3x 时长按, 屏幕该说"3x 快进中"而不是"2x"。
    /// </summary>
    private float _fastForwardTarget = FastForwardRate;

    /// <summary>
    /// 长按快进开始: 切到 <see cref="FastForwardRate"/>。
    ///
    /// ★ 松手时要恢复到的速率由 <c>_rateBeforeFastForward</c> 记住 —— 不能写死 1.0f:
    ///   用户可能先用倍速按钮设了 1.5x, 写死会让松手后悄悄变成 1x。
    ///   (旧版就有这个 bug, 2026-10-16 一并对齐。)
    /// </summary>
    private float _rateBeforeFastForward = 1.0f;

    /// <summary>开始长按快进(改倍速)。见上方那段说明为什么用 SetRate 而不是 seek。</summary>
    private void StartFastForward()
    {
        if (_closing || _mp == null) return;
        // 直播没有"加速往前放"这回事(本来就一直在最新处); 播完了也没有
        if (_isLive || _ended) return;

        try { _rateBeforeFastForward = _mp.Rate; }
        catch { _rateBeforeFastForward = 1.0f; }
        if (_rateBeforeFastForward <= 0) _rateBeforeFastForward = 1.0f;

        // ★★ 2026-11-04 合并上游 1.5.4: 长按目标改成 **max(2x, 当前倍速)**。
        //   原来固定切 2x, 于是用户本来在 3x 看视频时长按, 反而**变慢**到 2x ——
        //   手势语义是"暂时再快一点", 比当前还慢就毫无意义。
        //   ★ 上限跟倍速弹层一致(4.0x): 已经是 4x 时长按不再加速, 但也不降速。
        var target = Math.Max(FastForwardRate, _rateBeforeFastForward);
        if (target > MaxPlaybackRate) target = MaxPlaybackRate;
        _fastForwardTarget = target;

        _isFastForwarding = true;
        // ★ 顺序: 先让弹幕时钟按新倍速落定当前值, 再真正 SetRate(同 OnSpeedClick 那条说明)
        SyncDanmakuClockRate(target);
        try { _mp.SetRate(target); } catch { StopFastForward(); }
    }

    /// <summary>停止长按快进: 恢复按下前的速率。</summary>
    private void StopFastForward()
    {
        if (!_isFastForwarding) return;
        _isFastForwarding = false;
        // ★ 关窗路径不恢复速率: 那里 _closing 已置位, 而且这个窗口的 MediaPlayer
        //   马上就要被 Dispose —— 再去碰它是"访问已释放原生指针"的来源(0xc0000005),
        //   恢复了也没有意义(播放器跟窗口一起没了)。
        if (_closing || _mp == null) return;
        var back = _rateBeforeFastForward > 0 ? _rateBeforeFastForward : 1.0f;
        // 同 StartFastForward: 先落定弹幕时钟, 再改速率
        SyncDanmakuClockRate(back);
        try { _mp.SetRate(back); } catch { /* 播放器已失效 */ }
        // ★ 2026-10-31: 变速收尾也是音轨偶发掉线的暴露点(同 seek 后没声的机理)。
        //   挂一次带宽限期的自愈检查, 命中才动手(见 HealAudioIfDead)。
        HealAudioIfDead("fastforward-end");
        // ★★★ 2026-11-06 修「松手后偶发永久静音」:
        //   上面这一句**经常根本不会执行** —— HealAudioIfDead 的闸门是"距上次检查
        //   超过 AudioHealGraceMs(1500ms)", 而 _audioHealCheckAt 是**全窗口共享**的。
        //   长按 2 秒期间虽然没人主动检查, 但换片/seek 到位/别的自愈点都可能刚把它写过;
        //   一旦上一次检查落在 1.5s 内, 这一句就直接 return —— 而松手是**一次性**事件,
        //   之后再没有任何人补一次检查。于是那条竞态就永久留在那儿: 画面正常、时间在走、
        //   就是没声音, 直到下一次重建 input。
        //   ⇒ 松手后排一次**延迟复查**(宽限期之后), 保证"这一次变速"至少被检查一次。
        //   延迟复查走同一个入口, 闸门语义不变, 不会重复动手。
        ScheduleAudioHealRecheck();
    }

    /// <summary>
    /// 排一次"宽限期之后"的音频自愈复查(见 StopFastForward 里的说明)。
    ///
    /// ★ 为什么必须是延迟, 不能当场再调一次: 闸门本身就是"过渡期别碰",
    ///   当场再调只会被同一个闸门挡掉。等到 AudioHealGraceMs 之后再查, 那时
    ///   aout 已重建完、音轨枚举也稳定了, 判据才可信。
    /// ★ 用 DispatcherTimer 而不是 Thread.Sleep: 这是 UI 线程上的窗口级瞬态,
    ///   窗口关闭时由 StopAllTimers 统一收掉, 不会泄漏。
    /// </summary>
    private void ScheduleAudioHealRecheck()
    {
        if (_closing || _mp == null) return;
        _audioHealRecheckTimer?.Stop();
        var generation = _loadGen;
        var player = _mp;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AudioHealGraceMs + 200),
        };
        _audioHealRecheckTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_audioHealRecheckTimer, timer)) return;
            _audioHealRecheckTimer = null;
            if (_closing || generation != _loadGen || !ReferenceEquals(player, _mp) || _isFastForwarding) return;
            // 复查时把闸门基准清零, 让这一次检查一定能落地(它就是为"这一次变速"排的)
            _audioHealCheckAt = -1;
            HealAudioIfDead("fastforward-recheck");
        };
        timer.Start();
    }

    /// <summary>松手后音频自愈复查的定时器(见 ScheduleAudioHealRecheck)</summary>
    private DispatcherTimer? _audioHealRecheckTimer;


    public PlayerWindow() : this("") { }
    public PlayerWindow(string bvid)
    {
        InitializeComponent();
        InitializeTouch();
        InitializeAlternatePlayerLayout();
        PlayerStage.SizeChanged+=(_,_)=>UpdatePictureSplitGeometry();
        UiScale.Register(OriginalImageHost);
        VideoAreaRoot.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty,AppTypography.ResourceKey);
        AppTypography.Changed+=OnPlayerFontChanged;
        Closed += OnClosed;
        SourceInitialized += (_, _) => AttachPlayerTouchpadInput(PresentationSource.FromVisual(this) as HwndSource);
        Loaded += OnLoaded;
        SizeChanged += OnWindowSizeChangedForInfoPanel;
        PreviewKeyDown += OnPreviewKeyDown;
        // 鼠标侧键返回 + 非模态弹层点外部收起: 挂在窗口上, 也挂在视频区覆盖窗口上(见 HookOverlayKeyboard)
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewTouchDown += OnPreviewTouchDown;
        Svc.Toast.Shown += OnToast;
        Svc.Session.Changed += UpdateButtonState;

        // 这里**不再**关掉这个窗口的 DWM 过渡动画。
        // ★★★ 2026-10-02 按用户要求"全屏动画改用 Windows 系统自带的": 以前为了让"秒切"干净,
        //   在 SourceInitialized 里调 DwmInterop.DisableWindowTransitions 把系统那段窗口缩放动画
        //   关掉了 —— 那正是"全屏没有动画、硬切一下"的原因。现在反过来, 让 DWM 自己播这段过渡。
        //   ★ 别再把 DisableWindowTransitions 调回来: 它一开, 系统动画就被掐掉, 全屏又变成硬切。
        //   (主窗口的最大化/还原同理 —— 见 FluentWindow.OnStateChanged。)

        // 清晰度/弹幕区域弹层的定位目标**必须代码直连**: XAML 的 ElementName 绑定在
        // VideoView 的 ForegroundWindow 浮层里解析失败(内容被移进另一个可视树后名字
        // 查找断链), PlacementTarget 为空 → 弹层跑到了浮层左上角(真机截图实证)。
        // 同时右缘对齐: Placement=Top 默认左缘对齐, 这两个弹层在控制栏右段, 左对齐偏丑;
        // Opened 时量出宽度差做负偏移, 弹层右缘与按钮右缘平齐。
        VolumePopup.Child.MouseEnter += OnVolumeHoverEnter;
        VolumePopup.Child.MouseLeave += OnVolumeHoverLeave;
        VolumePopup.Child.LostMouseCapture += (_, _) => HideVolumePopupSoon();
        VolumePopup.Closed += (_, _) =>
        {
            _volumeCloseTimer?.Stop();
            _volumeHover = _volumePinned = false;
            if (!_closing) StartControlsHideCountdown();
        };
        SpeedPopup.PlacementTarget = SpeedBtn;
        SpeedPopup.Opened += (_, _) => _controlsHideTimer?.Stop();
        SpeedPopup.Closed += (_, _) => { if (!_closing) StartControlsHideCountdown(); };
        RightAlignPopup(QualityPopup, BtnQuality);
        RightAlignPopup(DanmakuAreaPopup, BtnDanmakuArea);
        RightAlignPopup(SpeedPopup, SpeedBtn);
        QualityPopup.PlacementTarget = BtnQuality;
        DanmakuAreaPopup.PlacementTarget = BtnDanmakuArea;
        HookPopupToggle(SpeedPopup, SpeedBtn);
        HookPopupToggle(QualityPopup, BtnQuality);
        // 右缘对齐(弹层右缘 = 按钮右缘)。注意必须等弹层布局完成后再量宽:
        // Opened 那一刻 ActualWidth 还是 0, 偏移会算成正数把弹层推出窗口右缘(真机实证)。
        // Loaded 优先级在 Popup 自身布局跑完之后, 那时 ActualWidth 才可信。
        QualityPopup.Opened += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                RightAlignPopup(QualityPopup, BtnQuality)));
        DanmakuAreaPopup.Opened += (_, _) =>
        {
            _controlsHideTimer?.Stop();
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    RightAlignPopup(DanmakuAreaPopup, BtnDanmakuArea)));
        };
        DanmakuAreaPopup.Closed += (_, _) => { if (!_closing) StartControlsHideCountdown(); };
        // ★ 两个弹层的"开着"状态本身就是判据(见 AnyPlayerPopupOpen), 不需要订阅 Closed。
        //   2026-10-13 第一版在这里订阅过 Closed 记时间窗, 实测证明 Closed 晚于视频区的
        //   Up(70ms), 时间窗一次都没生效过 —— 已删除。
        // 主题无需在此订阅: FluentWindow 基类已监听 ThemeChanged 并重建窗体 chrome,
        // 播放器自身的 DynamicResource 会随之自动刷新。之前这里多订阅了一次且从不退订,
        // 导致每个关闭过的播放器窗口都无法被 GC 回收。
        // 弹幕设置(全局): 初始化运行时状态, 并订阅设置页的变更
        _danmakuOn = Svc.Settings.DanmakuEnabled;
        _danmakuColorful = Svc.Settings.DanmakuColorful;
        _danmakuOutline = Svc.Settings.DanmakuOutline;
        _danmakuAreaRatio = Svc.Settings.DanmakuAreaPercent / 100.0;
        Svc.Settings.DanmakuSettingsChanged += OnDanmakuSettingsChanged;
        // 外观三项(不透明度/字号/速度)在此**首次**从设置里读出来。
        // 构造函数里字段还是编译期默认值(80% / 100% / 适中), 不走这一步的话,
        // 那些把旧值存进配置的用户(比如不透明度存的是 40%)开播放器会先按默认值起播,
        // 直到下一次改动才纠正 —— 表现为"设置里明明是 40%, 播放器却先亮一下再变暗"。
        ApplyDanmakuLook();
        UpdateDanmakuLookTexts();

        // 相关视频卡片点一下就换片(2026-10-12)。命令在 XAML 的 DataTemplate 里绑,
        // 所以必须是窗口的**属性**(DataContext 就是 this), RelativeSource 才找得到。
        PlayRelatedCommand = new Helpers.RelayCommand(PlayRelated);

        // 简介折叠要在**宽度确定之后**才判得出"要不要显示展开按钮", 而那一刻还没布局完
        // (Loaded 时 ActualWidth 可能仍是 0)。SizeChanged 才是可靠信号。
        DescText.SizeChanged += (_, _) => UpdateDescFold();

        // 跳过赞助片段: 开关/类别一变就重算(关掉要立刻停, 改类别要立刻生效)
        Svc.Settings.SponsorBlockChanged += OnSponsorSettingsChanged;        // 进度条色块是按像素摆的, 控件一改宽度就得重画(窗口缩放、切全屏、控制栏重排都会触发)。
        // 重画成本只有几个 Rectangle, 不必做节流。
        SponsorMarks.SizeChanged += (_, _) => RenderSponsorMarks();
        if (DanmakuAreaSlider != null) DanmakuAreaSlider.Value = Svc.Settings.DanmakuAreaPercent;
        // ★ 到这里四个滑杆都已经同步成"用户的真实设置", 才打开回灌闸门。
        //   在这之前的所有 ValueChanged 都只刷文字、不落库 —— 否则 XAML 里的初始
        //   Value(25/80/100/2)会在 InitializeComponent() 时把用户设置整个覆写成默认。
        _syncingDanmakuLook = false;
        UpdateDanmakuButton();

        // LibVLC 走进程级共享单例(见 VlcCore): 初始化全进程只做一次, 且永不在这里释放。
        // 反复 new/Dispose 原生库会引发 Windows 级访问违例弹窗:
        //   "Exception Processing Message 0xc0000005 - Unexpected parameters"
        // 窗口只负责挂载属于自己的 MediaPlayer, 开关窗口不再触碰原生库生命周期。
        //
        // ★ 实验性 vmem 模式：视频宿主换成普通 WPF Image（VmemVideoView），
        //   这样画面才能圆角/阴影/逐帧连续变形。--vout=vmem 是实例级选项，
        //   所以必须用专用的 VlcCore.GetVmem()，不能污染共享实例。
        _vmemMode = Svc.Settings.UseVmemRenderer;
        if (_vmemMode && !MountVmemSurface()) _vmemMode = false;
        try
        {
            _libVLC = _vmemMode ? VlcCore.GetVmem() : VlcCore.Get();
        }
        catch (Exception ex)
        {
            WriteLibVLCDebug(
                $"\n\n--- VlcCore.Get() failed ---\n{ex}\n");
            throw;
        }

        // 诊断: 把环境状态写到我们自己的 debug 文件, 万一 libvlc_new 失败也能看清
        WriteLibVLCDebug(
            $"AppContext.BaseDirectory = {AppContext.BaseDirectory}\n" +
            $"LIBVLC_PATH = {Environment.GetEnvironmentVariable("LIBVLC_PATH")}\n" +
            $"VLC_PLUGIN_PATH = {Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH")}\n" +
            $"libvlc dir = {VlcCore.LibVlcDirectory} exists = {Directory.Exists(VlcCore.LibVlcDirectory)}\n" +
            $"plugins dir = {VlcCore.PluginsDirectory} exists = {Directory.Exists(VlcCore.PluginsDirectory)}\n"
        );

        _playerRegistered = true;
        VlcCore.RegisterPlayer();
        CreateNativeVideoPlayer();
        InitializeEnhancementUi();

        _currentBvid = bvid;
        // ★ 这条构造函数路径(直接带 bvid new 出窗口)**不走 ResetForNewMedia**
        //   —— 而 _currentQn 的字段初值是"内置默认档"而不是用户设置。
        //   漏了这一句的结果是: 带 bvid 直接打开的第一个视频永远按内置默认档起播,
        //   用户在设置页选的「默认画质」要到第二条视频才生效(极难定位的那种"设置时灵时不灵")。
        ApplyPreferredQuality();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += (_, _) =>
        {
            UpdateProgressUi();
            // 跳过判定挂在同一个心跳上, 不另开定时器: 100ms/500ms 两种间隔它都会跟着走。
            // 必须在 UpdateProgressUi 之后 —— 后者在"跳转等待期"会提前 return, 而这里的
            // 判据(_pendingSeekMs)本来就要求"上一次跳转已经确认", 顺序正好对得上。
            MaybeSkipSponsorSegment();
        };
        _progressTimer.Start();

        // 音量: 从设置里读"上一次调到的音量"(2026-11-12 音量继承)。
        // ★ 这里必须是**读设置**, 不能写死 80 —— 写死的表现是"每个视频都要重调一遍音量",
        //   而用户调过的偏好应该跟着走到下一个视频。
        // ★ 也**不能**只依赖"复用同一个窗口时不重置": PlayerService 关窗重开时
        //   构造路径同样会跑到这里, 那时读的仍是设置, 所以两条路都对。
        _volumeCache = Svc.Settings.PlayerVolume;
        // ★ --mute 启动参数: 本次进程强制 0 音量(不落盘, 见 App.MuteForThisRun)
        if (App.MuteForThisRun) _volumeCache = 0;
        try { _mp.Volume = _volumeCache; } catch { }
        // ★★★ 滑块的 Value 也必须一起给(2026-11-12 用户报"进入下一个视频时音量条仍是 80%"):
        //   XAML 里写死 `Value="80"`, 而上面只改了 _volumeCache 与原生音量, **没碰滑块** ——
        //   于是声音确实是继承的那个值, 滑块却停在 80, 用户看到的就是"没继承"。
        //   ★ 必须放在 _mp 就绪之后: 滑块此时已在可视树里, 赋 Value 会触发 OnVolumeChanged,
        //     那条路会再写一次 _volumeCache/原生音量(同值回写, 无副作用), 正好对齐。
        if (VolumeSlider != null) VolumeSlider.Value = _volumeCache;
        UpdateVolumeChrome(_volumeCache);

        Svc.UserState.SubscribeFollow(this, change =>
        {
            if (_closing || change.Mid != _ownerMid || _isFollowed == change.Followed) return;
            _isFollowed = change.Followed;
            UpdateFollowButton();
        });

        // 换片重置覆盖度自检: 新增了状态字段却忘了归类, 会在 errors.txt 里被点出来(仅 DEBUG)
        VerifyResetCoverage();
    }

    /// <summary>
    /// 是否正在关闭(窗口已隐藏, 但原生资源拆解尚未完成)。
    /// PlayerService 必须据此判断"不能复用" —— 否则复用到的窗口会在拆解结束时被 Close(),
    /// 表现为"点了视频又立刻自动退出"。
    /// </summary>
    public bool IsClosing => _closing;

    /// <summary>切换视频(用于 PlayerService 复用窗口场景): 停止当前播放, 加载新 bvid</summary>
    public void PlayVideo(string bvid)
    {
        if (_closing || string.IsNullOrEmpty(bvid)) { StopSeamlessMorph(); return; }
        if (!_isLocalPlayback && !_isLive && bvid == _currentBvid && _mp.IsPlaying) { StopSeamlessMorph(); return; }
        _currentBvid = bvid;
        _isLocalPlayback = false;
        _isLive = false;
        ResetForNewMedia();
        TryStartSeamlessEntry(bvid);
        _ = ReplaceNativeInputAsync(() => _ = LoadVideoAsync());
    }



    /// <summary>
    /// 播放本地文件(离线缓存页用)。
    /// 与在线播放的区别: 不查视频信息/播放地址、不加载弹幕、不上报历史、不查点赞收藏状态 ——
    /// 本地文件没有这些上下文。所以这里直接构造一个 FromPath 的 Media 丢给播放器。
    /// </summary>
    private CancellationTokenSource? _offlinePrepCts;

    public void PlayLocalFile(string path)
    {
        if (_closing || string.IsNullOrEmpty(path) || !File.Exists(path)) { StopSeamlessMorph(); return; }
        StopSeamlessMorph();
        ++_loadGen;
        _currentBvid = "";
        _isLocalPlayback = true;
        _isLive = false;
        ResetForNewMedia();
        _currentLocalPath = path;
        // 本地文件没有 bvid, 自然也没有相关视频
        ClearRelatedPanel();
        _ = ReplaceNativeInputAsync(() => LoadLocalFile(path));
    }

    /// <summary>
    /// 播放离线缓存条目(含哔哩哔哩官方 Windows 客户端离线缓存)。
    /// 若为官方客户端缓存的 .m4s 分片对, 在后台安全剥离混淆头并合流为本地 .mp4 后起播(不改动原文件)。
    /// </summary>
    public void PlayOfflineEntry(OfflineEntry entry)
    {
        if (_closing || entry == null || !entry.HasPlayableMedia) { StopSeamlessMorph(); return; }
        StopSeamlessMorph();
        var gen = ++_loadGen;
        // 离线缓存走本地播放链路, 不能把在线 bvid 写进 _currentBvid, 否则 OnLoaded 会触发 LoadVideoAsync 拉在线流
        _currentBvid = "";
        _isLocalPlayback = true;
        _isLive = false;
        ResetForNewMedia();
        _currentOfflineEntry = entry;
        _offlineOnlineBvid = HasBilibiliOnlineIdentity(entry) ? entry.Bvid.Trim() : "";
        ClearRelatedPanel();
        _ = LoadOfflineEntryAsync(entry, gen);
    }

    /// <summary>
    /// 把"本片源要请求的清晰度"快照成设置里的「默认画质」。
    ///
    /// ★ 为什么单独一个方法: 有**三**条起播路径需要它, 各写一遍必然漏一条 ——
    ///   构造函数(带 bvid 直接开)、ResetForNewMedia(复用窗口换片)、以及将来新增的入口。
    ///   实际漏过一次的原形就摆在眼前: 只放 ResetForNewMedia 时, "带 bvid 开新窗口"这条
    ///   路径拿到的是字段初值而不是用户设置。
    ///
    /// ★ 为什么不沿用上一个视频手动切到的档位: 那个选项叫**默认**画质 —— 手动切清晰度只对
    ///   当前这一条有效, 下一条仍然从用户选定的默认档起播(B 站客户端同款语义)。
    ///
    /// ★ 为什么走 <see cref="QualityPreference.RequestQn"/> 而不是直接赋值:
    ///   设置页把「自动(最高可用)」存成哨兵值 0, 而发给接口的必须是"最高可用档"(127)。
    ///   同时 RequestQn 内部会 Normalize —— 配置被手改坏 / 存着已下线的档位时,
    ///   不会把一个谁也不认识的 qn 发进取流请求。
    /// </summary>
    private void ApplyPreferredQuality()
    {
        _currentQn = QualityPreference.RequestQn(Svc.Settings.PreferredQualityQn);
        // ★ 顺手把控件栏那个清晰度按钮的文案摆成"**本次要请求的**档位"(2026-10-04)。
        //
        // 为什么必须在这里写一次: XAML 里 `QualityText` 的初值是写死的 "1080P"。
        // 用户把默认画质设成 720P / 4K 之后, 从"点开视频"到"取流成功调用 FillQualityMenu"
        // 之间——以及**取流直接失败**那条路(ShowFail 后 return, 根本不会调 FillQualityMenu)
        // ——按钮上都会挂着一个 "1080P", 而那既不是用户选的档、也不是正在播的档。
        // 之前写死 qn=80 时它与请求一致所以看不出来, 有了这个设置项就变成实打实的错报。
        // 本地文件/直播那两条路随后会各自用 "本地"/"直播" 覆盖掉它, 不受这里影响。
        if (QualityText != null)
            QualityText.Text = QualityPreference.ShortLabelOf(Svc.Settings.PreferredQualityQn);
    }

    /// <summary>
    /// 换片源前把"上一条视频的痕迹"清干净。
    /// 尤其是 _commentsLoaded / CommentList.ItemsSource —— 原来没清, 于是同一个播放器窗口
    /// 换视频之后评论 tab 里一直挂着上一条视频的评论(而且不会再重新加载)。
    /// </summary>
    private void ResetForNewMedia()
    {
        _playerTouchpad?.Cancel(); _overlayTouchpad?.Cancel();
        _infoSettleTarget = -1; _infoWheelContactDx = _precisionVideoProgress = 0;
        _pageWheelIdle?.Stop();
        _pageWheelAccum = 0;
        _horizontalWheelRegion = HorizontalWheelRegion.None;
        _videoWheelStartMs = _videoWheelTargetMs = 0;
        _cancelInfoSettle?.Invoke();
        if (_infoDraggingPage) { FinishPageDrag(false, PageIndex); CompleteInfoPageSettle(); }
        _infoSwipeArmed = _infoHorizontalDecided = _infoDraggingPage = false;
        _infoPageAnimating = _infoGestureMoved = _infoPageSwitching = _infoWheelPreviewActive = false;
        _infoDragDx = _infoTouchOrigin = _infoWheelOrigin = 0;
        ThemeReactions?.Clear();
        StopWebVideoAnimation();
        OnWebMediaChanging();
        CancelVideoTouch();
        // 换片: 上一个直播间的 WebSocket 必须断开(新片不是直播时更不能留着)
        StopLiveDanmaku();
        // ★ 2026-11-04: 顶部 bar 的「在线人数 / 稍后再看」按"是不是直播"复位。
        //   直播路径随后会把这里再翻过来(见 LoadLiveAsync), 所以这里统一先把非直播态摆好。
        if (LiveOnlineBox != null) LiveOnlineBox.Visibility = Visibility.Collapsed;
        if (BtnWatchLaterTop != null) BtnWatchLaterTop.Visibility = Visibility.Visible;
        TouchHelp.Visibility = Visibility.Collapsed;
        // A new video starts at the normal picture brightness; the old dim must not leak over.
        _touchBrightness = 1;
        _touchStartBrightness = 1;
        TouchDimmer.Opacity = 0;
        _indexInLib = 0;
        // 换片: 音频链路诊断重新记一次(每个片源一条, 见 LogAudioStateOnce)
        _audioStateLogged = false;
        // 「听视频」的音轨地址属于上一条视频 —— 不清的话换成新片时会拿旧音轨去播。
        // ★ _isListenMode 本身**不清**(登记在 PersistentFields): 用户切到纯音频连听几集是常态;
        //   新片的音轨地址在 LoadVideoAsync 里重新快照(见 ApplyListenAudioUrl)。
        _listenAudioUrl = null;
        _listenResumeMs = 0;
        _listenWaveTick = 0;
        _coverUrl = "";
        // ★ 每个片源开始时都把"要请求的清晰度"快照成设置里的「默认画质」(2026-10-04)。
        //   放在这里而不是只放构造函数里, 是为了让"设置页改完 → 点开下一个视频"立刻生效,
        //   不必重启。
        ApplyPreferredQuality();
        _danmakuLoadCancellation?.Cancel();
        _offlinePrepCts?.Cancel();
        _offlinePrepCts?.Dispose();
        _offlinePrepCts = null;
        _currentOfflineEntry = null;
        _currentLocalPath = "";
        _danmaku.Clear();
        _danmakuRaw.Clear();
        // 相关视频同属"上一条视频"的界面内容: 换片瞬间就必须清掉, 不能等新片的
        // 异步请求回来 —— 那中间几百毫秒里, 界面上挂着的还是**上一条**的推荐,
        // 用户一点就跳到一个跟当前标题完全对不上的视频。
        ClearRelatedPanel();
        // SponsorBlock: 上一片的片段数据与"已处理"记录全部作废。
        // 掐掉那个查询同样重要 —— 否则上一个视频的响应回来了会写进新视频的状态。
        CancelSponsorFetch();
        _sponsorRaw = Array.Empty<SponsorSegment>();
        _sponsorActive = Array.Empty<SponsorSegment>();
        _sponsorHandled.Clear();
        _lastSkipSeg = null;
        HideSkipNotice();
        // 进度条上的旧色块也要立刻清掉: 本地文件/直播这两条路根本不会去取片段,
        // 不主动刷新的话上一条视频的色块会一直挂在进度条上。
        RenderSponsorMarks();
        _totalMs = 0;
        _currentCid = 0;
        _aid = 0;
        // 换片源: 上一片的合流任务立刻作废(否则会白下几十兆, 也可能把结果写进新片的键)
        CancelRemuxPrefetch();
        _ownerMid = 0;
        _commentsLoaded = false;
        _nextCommentPage = null;
        _loadingCommentPage = _commentPageFailed = false;
        if (BtnMoreComments != null) BtnMoreComments.Visibility = Visibility.Collapsed;
        if (BtnRetryComments != null) BtnRetryComments.Visibility = Visibility.Collapsed;
        if (CommentLoadingRing != null) CommentLoadingRing.Visibility = Visibility.Collapsed;
        // 下载状态属于"上一条视频"(见 UpdateActionCounts 里那个绿色终态)
        _cacheDownloaded = false;
        // 评论总数属于"上一条视频" —— 不清的话换了片子那句"共 N 条评论"还挂着旧数字,
        // 而排序入口的可见性正是跟着它走的。排序**方式**(_commentSort)不清: 那是用户习惯。
        _commentTotal = 0;
        // 本次实际排序回到用户偏好(_commentSort); 新视频重新试热门, 热门空再回退成时间
        _loadedCommentSort = _effectiveCommentSort = _commentSort;
        _commentReturnRpid = 0;
        _videoBackdropGuardTicks = 0;
        // 音频自愈的"上次检查时刻"(_audioHealCheckAt)不清: 它是宽限期计时基准,
        // 跨片保持最多把新片的第一次检查推迟 1.5s; 清零反而会在 aout 未就绪时立刻误检。
        // 遮罩安全网的"上次位置"同理要归零: 留着上一片的进度会让新片的第一次 TimeChanged
        // 被当成"在推进", 把刚弹出的加载遮罩误收掉。
        _lastLoadingRevealMs = -1;
        UpdateCommentSortUi(visible: false);
        // 换片时把评论详情也收掉(tab 文案与返回链接一并还原),
        // 否则新片子一进来右侧就挂着上一条视频的评论详情页
        if (CommentDetailPanel != null) CloseCommentDetail();
        if (CommentList != null) CommentList.ItemsSource = null;
        if (CommentHint != null) CommentHint.Text = "加载评论中…";
        // 上一条视频没写完的评论草稿不能带到下一条去(用户点开新视频发现输入框里
        // 还留着给上一个视频写了一半的话, 会以为是自己点错了)
        CommentDraft?.Clear();
        _postingComment = false;
        // 进度上报的节流状态也要重置, 让新视频的第一条云端历史能立刻落地
        Svc.HistorySync.Reset();
        // 清掉屏幕上残留的弹幕(防泄漏)并重置泳道占用
        DanmakuCanvas.Children.Clear();
        ResetLanes();
        TimeText.Text = "00:00 / 00:00";
        ProgressSlider.Value = 0;
        // 新片子当然还没播完: 清掉结尾状态与"重播"遮罩, 否则切下一条视频时会挂着上一段的遮罩
        _ended = false;
        _seekGraceUntil = 0;
        // 清掉跳转看门狗与"提前结束自救"的上一片痕迹
        _pendingSeekMs = -1;
        _seekObservedMs = -1;
        _seekFromMs = -1;
        _seekElsewhereSeenMs = -1;
        _seekBounceSeenMs = -1;
        _seekBounceAt = -1;
        _seekIssuedAt = -1;
        _lastSeekTargetMs = -1;
        _resumeByStartTime = false;
        _eofSeekRebuildTried = false;
        _progressDragStartValue = 0;
        ResetRecoveryState();
        // ★ 长按快进也要收: 若停在长按态换片, MediaPlayer 上的速率会**跟着留到新片子**
        //   (速率是 MediaPlayer 的属性, 不随 Media 重置 —— 实测)。所以这里必须先恢复。
        //   顺带把 _longPressFired 清掉 —— 否则新片的第一次 Up 会走进"结束快进"分支。
        StopFastForward();
        _longPressFired = false;
        _tripleComboAt = 0;
        _tripleComboBusy = false;
        _favBusy = false;
        _switchingQuality = false;
        _watchLaterBusy = false;
        _followBusy = false;
        _likePressCancelAt = 0;
        ResetLikePressProgress();
        _favoriteHeld = false;
        _favoriteHoldTimer?.Stop();
        // 复位"该恢复到几倍速"的记录: 它只描述**本片**那次长按, 不该带到下一片
        // (下一片起播速率由 _mp.Rate 自己决定, 这里只是清掉上一个手势的账)。
        _rateBeforeFastForward = 1.0f;
        RestoreVolumeAfterSeek();   // 上一片若停在 seek 静音中, 别把静音带进新片
        // 心跳间隔也要还原: 上一片可能停在了"跳转等待期"的 100ms 上没来得及还原
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
        _lastKnownMs = 0;
        _eofResumeCount = 0;
        _lastEofResumeMs = 0;
        _tailVideoOnlyResumed = false;
        _offlineOnlineBvid = "";
        try { _offlineOnlineCts?.Cancel(); _offlineOnlineCts?.Dispose(); _offlineOnlineCts = null; } catch { }
        // 卡顿检测状态同样要清: 换片后上一片"时间停在哪"的参照已经没意义, 留着会立刻误判成卡顿
        _stallLastMs = -1;
        _stallTicks = 0;
        // 进度显示的两个基线一并清: 不清的话"尾部只进不退"钳制会把新片钉在上一片的旧进度上
        _anchorMs = -1;
        _displayedMs = -1;
        if (EndOverlay != null) EndOverlay.Visibility = Visibility.Collapsed;
        // 进度条默认可用; 直播那条路会再把它置灰
        if (ProgressSlider != null) ProgressSlider.IsEnabled = true;
        // 弹幕按钮的可用性取决于 _isLive / _isLocalPlayback, 调用方都已在这之前设好这两个标志
        UpdateDanmakuButton();
        // 「听视频」的可用性同样取决于那两个标志(直播/本地文件没有独立音轨)。
        // ★ 必须在 _isListenMode 已经确定之后调: 它内部会在"不支持聆听"时顺手退出聆听态,
        //   否则聆听遮罩会盖在一个根本没有音轨的本地文件上。
        UpdateListenButtonState();
        // 「稍后再看」的点亮态属于"上一条视频", 一起清掉。放这里而不是 UpdateUiFromDetail:
        // 直播 / 本地离线缓存那两条路不走详情流程, 不清的话会挂着上一条视频的强调色。
        SetInWatchLater(false);
        // 合集: 换片就要把"上一个合集的列表"整个丢掉, 否则普通视频那一瞬间会挂着旧合集的条目
        // (点开面板那一帧还看得见)。★ 面板的**展开态**(_seasonOpen)不在这里清 —— 它登记在
        // PersistentFields 里: 用户连着看几集是常态, 每换一集都把面板收起来很烦。
        _currentSeasonEpisode = null;
        _season = null;
        _hasSeason = false;
        // ★ 这里**不**清 _switchingSeasonEpisode: 它的复位点是 SwitchToSeasonEpisodeAsync 的
        //   finally(与 _switchingQuality 同款)。本方法正是被那次换片调用的 —— 在这里清掉它,
        //   整个 await LoadVideoAsync() 期间闸门就是开的, 连点两集照样能叠起来跑。
        ClearSeasonUi(collapsePanel: false);
        // 换片时**不关**面板(collapsePanel: false): 用户开着面板点下一集是常态, 关掉会在
        // 整个加载过程里闪一下。列表内容已清空, 这里就地显示"加载中", 等 ApplySeason 灌数据。
        // 真到了没有合集的新片, ApplySeason 会把面板整个收起来。
        if (_seasonOpen)
        {
            if (SeasonHint != null) SeasonHint.Text = "正在获取合集…";
            if (SeasonLoadingRing != null) SeasonLoadingRing.Visibility = Visibility.Visible;
        }
        UpdateSeasonButtonState();

        // 分P: 与合集同款处理 —— 换片先把上一片的分P 列表整个丢掉, 入口按钮也收起来,
        // 等新详情到了由 ApplyPages 重新决定要不要露出来。
        // 弹层强制关掉: 分P 弹层是 Popup(独立 HWND), 不关的话它会**浮在下一条视频上面**,
        // 里面还是上一条的分P 列表 —— 而合集面板是窗口内的 Grid, 收起就跟着一起没了。
        _pages = new List<VideoPage>();
        _hasPages = false;
        PagesSection.Visibility = Visibility.Collapsed;
        ClosePagesPopup();
    }

    /// <summary>
    /// 把合集列表的内容清干净(入口按钮、标题、副标题、提示、加载圈、选中项、**条目本身**)。
    ///
    /// 单独抽出来是因为它有多个调用点 —— 换片时、详情加载完发现没有合集时。
    /// 两处各写一遍必然漏(尤其"底部加载圈没停"这种, 表现是加载完了圈还在转)。
    /// </summary>
    /// <param name="collapsePanel">
    /// 是否**顺便把面板收起来**。换片时必须传 false: 用户开着面板点下一集是常态,
    /// 换片期间把面板收掉会在整个加载过程里闪一下, 体验上像是"点一下面板没了"。
    /// 只有在"确定这个视频没有合集"时才收面板。
    /// </param>
    private void ClearSeasonUi(bool collapsePanel)
    {
        if (collapsePanel && SeasonPanel != null) SeasonPanel.Visibility = Visibility.Collapsed;
        if (SeasonTitleText != null) SeasonTitleText.Text = "";
        if (SeasonMetaText != null) SeasonMetaText.Text = "";
        if (SeasonHint != null) SeasonHint.Text = "";
        if (SeasonLoadingRing != null) SeasonLoadingRing.Visibility = Visibility.Collapsed;
        // ★★ 必须把 ItemsSource 也清掉, 不能只清 SelectedIndex(2026-10-02 探针实测出来的):
        //   换片时不关面板(见上面 collapsePanel 的说明), 于是从"点了下一集"到
        //   LoadVideoAsync 真正灌回新数据之间有几秒的窗口期 —— 那段时间面板是**开着**的。
        //   只清选中项的话, 列表里挂着的是**上一个合集**的条目, 而底部还写着"正在获取合集…",
        //   自相矛盾。更糟的是此时点一条旧条目会走到 OnSeasonEpisodeSelected:
        //   它的判据是 ep.Bvid != _currentBvid, 而 _currentBvid 已经是新视频了 ⇒ 恒真
        //   ⇒ 会拿旧合集的条目去换片, 播出一个跟用户点的东西无关的视频。
        //   (ApplySeason 保证重新灌数据, 所以这里清成 null 是安全的。)
        if (SeasonList != null)
        {
            SeasonList.ItemsSource = null;
            SeasonList.SelectedIndex = -1;
        }
    }

    // ------------------------------------------------------------ 换片重置的自检

    /// <summary>
    /// 「每片状态」字段清单 = <see cref="ResetForNewMedia"/> 必须清掉的那些。
    ///
    /// 为什么需要这份清单 + 自检: 换片重置那 60 多行是**手工维护**的, 而播放器是"换片就清"
    /// 的结构 —— 漏清一个字段不报错、不告警, 只会让新片悄悄继承上一片的残留(三连状态、
    /// 清晰度、seek 看门狗的锚点……), 表现为"换个视频还带着上一个的状态", 极难定位。
    ///
    /// 与 <see cref="StopAllTimers"/> 的区别: 定时器可以**自动**停(全停即可), 但重置**不能自动** ——
    /// 每个字段该清成什么值不一样, 有的还要连带清集合/退订/刷 UI, 反射猜不出来。
    /// 所以这里退一步: 清单仍然手写, 但用自检保证"新增字段时不会忘了登记"。
    /// </summary>
    private static readonly string[] PerMediaStateFields =
    {
        nameof(_pageWheelAccum), nameof(_horizontalWheelRegion), nameof(_videoWheelStartMs), nameof(_videoWheelTargetMs),
        nameof(_infoSettleTarget), nameof(_infoWheelContactDx), nameof(_precisionVideoProgress),
        nameof(_infoSwipeArmed), nameof(_infoHorizontalDecided), nameof(_infoDraggingPage),
        nameof(_infoPageAnimating), nameof(_infoGestureMoved), nameof(_infoPageSwitching), nameof(_infoWheelPreviewActive),
        nameof(_infoDragDx), nameof(_infoTouchOrigin), nameof(_infoWheelOrigin),
        // 三连 / 关注 / 稍后再看等"上一条视频"的态
        nameof(_isLiked), nameof(_isFollowed), nameof(_isFavorited), nameof(_favBusy),
        nameof(_isInWatchLater), nameof(_watchLaterBusy), nameof(_isCoined),
        nameof(_likeCount), nameof(_coinCount), nameof(_favCount), nameof(_cacheDownloaded),
        // 播放源标识
        nameof(_isLocalPlayback), nameof(_isLive), nameof(_liveRoomId), nameof(_currentCid),
        nameof(_aid), nameof(_ownerMid), nameof(_ownerName), nameof(_ownerFace),
        nameof(_videoTitle), nameof(_currentQn), nameof(_currentBvid), nameof(_currentLocalPath),
        nameof(_offlineOnlineBvid),
        nameof(_coverUrl),           // 封面属于"这一片"(聆听态的画面锚点)
        // 「听视频」的音轨地址与恢复进度: 换片后要重新取, 留着上一条的会播错东西
        nameof(_listenAudioUrl), nameof(_listenResumeMs), nameof(_listenWaveTick),
        nameof(_lastPlayUrl), nameof(_lastAudioUrl),   // 出错诊断日志要打"当时正在播的地址"
        // 进度 / 时长 / EOF 自救
        nameof(_totalMs), nameof(_lastKnownMs), nameof(_eofResumeCount), nameof(_lastEofResumeMs),
        nameof(_tailVideoOnlyResumed),
        nameof(_resumeAfterSwitchMs), nameof(_ended), nameof(_anchorMs), nameof(_anchorWall),
        nameof(_displayedMs),
        // 跳转看门狗
        nameof(_pendingSeekMs), nameof(_seekDeadline), nameof(_seekRetries),
        nameof(_seekObservedMs), nameof(_seekElsewhereSeenMs), nameof(_seekFromMs),
        nameof(_seekGraceUntil), nameof(_seekMuteSavedVolume), nameof(_isSeeking),
        nameof(_seekRebuildTried), nameof(_seekBounceSeenMs), nameof(_seekBounceAt),
        nameof(_seekIssuedAt), nameof(_lastSeekTargetMs), nameof(_resumeByStartTime),
        nameof(_eofSeekRebuildTried), nameof(_progressDragStartValue), nameof(_audioHealCheckAt),
        nameof(_volumeRestoreDeadline), nameof(_volumeRestoreTries),
        nameof(_lastLoadingRevealMs),
        // 卡顿自愈与地址保鲜
        nameof(_playUrlDeadline), nameof(_playUrlFetchedAt), nameof(_playUrlRefreshedAt),
        nameof(_urlRefreshAttemptAt), nameof(_urlRefreshInFlight), nameof(_stallRecoveryInFlight),
        nameof(_errorRecoveryTried),
        // 卡顿检测
        nameof(_stallLastMs), nameof(_stallTicks),
        // 弹幕
        nameof(_indexInLib), nameof(_audioStateLogged), nameof(_lastLaneCalcWidth),
        nameof(_lastLaneCalcHeight),
        // 异步代次(旧请求回来时必须已作废)
        nameof(_remuxGen), nameof(_loadingGen), nameof(_kaomojiIndex),
        // 界面交互态
        nameof(_switchingQuality), nameof(_fillingQualityMenu), nameof(_updatingSlider),
        nameof(_commentsLoaded), nameof(_postingComment), nameof(_longPressFired),
        // 长按快进的状态与 _longPressFired 同类(都是"这一次手势"的瞬时态): 换片必须复位。
        // ★ _rateBeforeFastForward 也登记在这里 —— 它决定"松手恢复到几倍速",
        //   不跟着换片复位的话, 新片子的第一次长按会恢复到上一片记下的速率。
        nameof(_isFastForwarding), nameof(_rateBeforeFastForward),
        nameof(_commentTotal), nameof(_nextCommentPage), nameof(_loadingCommentPage), nameof(_commentPageFailed),
        nameof(_effectiveCommentSort), nameof(_loadedCommentSort),
        nameof(_suppressSingleClick), nameof(_controlsHidden), nameof(_isFullscreen),
        // ---- 2026-10-31 补登记(此前 errors.txt 里一直被自检点名) ----
        nameof(_descExpanded),          // 简介折叠态: UpdateUiFromDetail 每片复位
        nameof(_relatedGen),            // 相关视频加载代次: LoadRelatedAsync 自增
        nameof(_relatedShown),          // 相关视频已展示条数: LoadRelatedAsync/ClearRelatedPanel 复位
        nameof(_relatedEmptyMessage),   // Empty/error copy is reset together with the per-video recommendation cache.
        nameof(_isReliableSeekSource),  // seek 可靠性标志: BuildMedia 按片源设置
        nameof(_dmClockRate),           // 弹幕时钟倍率: 换片后由 CalibrateDanmakuClock 重新校准
        nameof(_maximizeCoverArmed),    // 最大化黑幕闸: 百毫秒级窗口瞬态, 换片时不会存活
        nameof(_suppressStateCover),    // 状态切换遮罩抑制闸: 同上, 计时器自动收敛
        nameof(_commentReturnRpid),     // 评论详情返回锚: 属于"上一条评论详情", 换片必须归零
        nameof(_videoBackdropGuardTicks), // vout 底色守护计数: 3 秒自收尾的瞬态
        nameof(_audioHealCheckAt),      // 音频自愈上次检查时刻: 跨片保持宽限意义, 不必清(见该字段)
        // 合集: 「这个视频有没有合集」属于"这一片"的属性, 换片必须重算。
        // 不清的话换了普通视频入口还挂着, 点开是个空面板。
        // (_currentSeasonEpisode / _season 是引用类型, 自检只审值类型与 string, 不在清单里;
        //  但它们在 ResetForNewMedia 里同样被清了 —— 清单只负责"别漏", 不负责穷举。)
        nameof(_hasSeason), nameof(_switchingSeasonEpisode),
        // 分P 与合集是一对: 同样是"每片重算 + 换片闸门"。别只加一个漏一个。
        nameof(_hasPages), nameof(_switchingPage),
        // 长按点赞一键三连 & 收藏长按选夹态（换片必须清，防止抑制窗口与进度残留跨片触发）
        nameof(_favoriteHeld),
        nameof(_tripleComboAt), nameof(_tripleComboBusy),
        nameof(_likeDownTick), nameof(_likePressCancelAt), nameof(_likePressStartTick),
        nameof(_likePressCommitted), nameof(_likeConfirmed), nameof(_activeLikeTouchId),
        nameof(_followBusy),
    };

    /// <summary>
    /// 明确**跨片保持**的值类型字段 —— 不该进每片清单, 也不该被自检点名。
    /// 分类错了自检会报出来, 改这里即可, 别去动 <see cref="PerMediaStateFields"/> 消警告。
    ///
    /// ★ 这张表装两种东西, 别混淆:
    ///   ① **用户偏好/设置**(音量、弹幕开关、排序方式…)—— 换片必须保持, 清了是 bug;
    ///   ② **窗口级的瞬态与订阅标记**(关闭标记、黑幕闸、Rendering 订阅…)—
    ///      它们活不到换片那一刻, 或者由事件自动收敛, 所以"不需要清"。
    ///   两种都不该被自检点名, 但只有 ① 是"必须保持"的语义。
    /// </summary>
    private static readonly string[] PersistentFields =
    {
        nameof(_volumeCache),        // 音量是用户偏好, 换片不该重置
        nameof(_wheelAccum),         // 滚轮音量累计, 瞬时量
        // 音量弹层的两个交互态(2026-11-05): 悬停/点开都是**当前这一次操作**的状态,
        // 窗口级瞬态, 与片源无关 —— 换片时鼠标还压在喇叭上反而应该保持展开。
        nameof(_volumeHover), nameof(_volumePinned),
        nameof(_danmakuFilterRevision), // Monotonic async-work revision, invalidated on media change.
        nameof(_danmakuOn),          // 弹幕总开关是设置项
        nameof(_danmakuColorful),    // 彩色弹幕是设置项
        nameof(_danmakuOutline),     // 弹幕描边是设置项
        nameof(_danmakuAreaRatio),   // 弹幕显示区域是设置项
        nameof(_laneHeight),         // 泳道高随字号走, 跟着设置走
        // ★ 外观三项(2026-10-12)同理是**用户偏好**: 换片时必须保持, 清了就等于"换个视频
        //   弹幕又变回默认大小/速度"—— 那是用户马上会报上来的 bug。
        //   与 _danmakuAreaRatio 同一类, 三项+泳道高都是设置驱动的派生量。
        nameof(_danmakuFontSize), nameof(_danmakuOpacity), nameof(_danmakuSpeedLevel),
        nameof(_danmakuScrollSeconds), nameof(_danmakuFixedSeconds),
        nameof(_teardownDone),       // 拆解只做一次
        nameof(_playerRegistered),   // 注册计数, 生命周期是窗口而非片源
        nameof(_seasonOpen),         // 合集面板的展开态: 用户开着面板连看几集是常态, 换片收起它很烦
        nameof(_isFillWindow),       // 「铺满窗口」是窗口级的显示模式(同 _isFullscreen), 换片不该把它弹回去
        nameof(_commentRequestVersion),
        nameof(_commentSort),        // 评论排序是用户看评论的习惯(同 _seasonOpen): 选过一次就一直是那个顺序
        nameof(_isListenMode),       // 「听视频」是窗口级的播放模式(同 _isFullscreen): 换片不该把它弹回视频
        // hover 预览锁是"上一次操作的瞬时反馈"(750ms 自己过期, 与片源无关), 不属于任何一片
        nameof(_actionHoverLockUntil),
        // ---- 2026-10-12 补登记: 以下 8 个都是**窗口级**的瞬态/订阅标记, 换片不该清 ----
        //
        // ★★ 为什么它们不属于"每片状态": 自检只问"这个字段换片时该不该清", 而答案可以是
        //   "不该"—— 登记在这里就是把这个答案固化下来, 免得后人看到 errors.txt 又去
        //   ResetForNewMedia 里补一堆实际不需要的重置代码(那反而会引入新 bug)。
        //
        // 关闭流程标记(见字段声明处的说明): 窗口生命周期, 与片源无关
        nameof(_closing),
        // 全屏黑幕/拖动按住闸: 都是**百毫秒级**的窗口显示瞬态 ——
        //   黑幕压下来等首个新帧就撤(FullscreenCoverTimeoutMs = 600ms 兜底),
        //   不可能活到用户换下一个视频。留着 false 的初值即可, 清了没有任何可观察差异。
        nameof(_fullscreenCoverArmed),
        nameof(_resizeHold),
        // 手势"这一下是否由 Down 起步"的标记(左键/右键各一份): 它们描述的是**当前这一次点击**,
        //   窗口关闭/换片时鼠标多半早已抬起, 留着初值 false 即可;
        //   真按住不放时换片本来就是不可能的操作, 清了也没有可观察差异。
        nameof(_videoGestureActive), nameof(_rightGestureActive),
        // CompositionTarget.Rendering / 弹幕时钟的订阅只挂一次, 属窗口级(同 _playerRegistered)
        nameof(_modalSizeWatchAttached),
        nameof(_danmakuClockAttached),
        // 弹幕时钟的三个量: 靠 _mp.TimeChanged(约 4Hz)自动校准 —— 换片后新片一开播,
        //   下一次 TimeChanged 就把 _dmClockMedia 推到新片时间, 260ms 内自然收敛。
        //   ★ 特意**不**在 ResetForNewMedia 里清: 那会与 CalibrateDanmakuClock 抢写,
        //     而后者才是权威来源(2026-11-11 为修"暂停回跳"刚把权威收敛到它)。
        nameof(_dmClockMedia), nameof(_dmClockStamp), nameof(_dmClockRunning),
        // ---- 2026-10-30 补登记: 起播优化新增的三个窗口级瞬态 ----
        // 加载链代次与闸门: 换片时**故意不清** —— 新一次加载会自己 ++_loadGen 让旧的作废;
        //   清成 0 反而可能让旧任务的代次与新任务撞上。闸门由 LoadVideoAsync 的 finally 收敛。
        nameof(_loadGen), nameof(_loadRunning),
        nameof(_webSurfaceSettledAt), // Native-surface transition timestamp, reset by each dock target.
        nameof(_webRecommendedSelected), nameof(_webRelatedIndex), // Window-level web tab and reparenting state.
        nameof(_webDockSpacingPrepared), nameof(_webDockBaseGap), nameof(_webDockFullGap),
        nameof(_webDockFullViewport), // Cached right-rail geometry for one transition.
        // 首帧计时器(引用类型, 自检本来也不审, 这里登记只是表明它是窗口级瞬态)
        nameof(_playWatch),
        // 弹幕滑杆的回灌闸门(2026-10-30): 构造期间必须保持 true 挡住 XAML 初始 Value 的写回;
        //   换片不该碰它 —— 它是窗口级初始化状态, 不是"这一片"的弹幕设置。
        nameof(_syncingDanmakuLook),
        // 评论输入面板的展开态(2026-11-01): 窗口级 UI 瞬态 ——
        //   用户开着面板换片是常态(草稿都不清, 面板更不能收), 保持原样即可。
        nameof(_composerOpen),
        // 输入框的"展开高度"态(2026-11-01): 同样是窗口级 UI 偏好 ——
        //   用户展开了写长文, 换片/收面板再回来不该被打回 66px。发成功才归位(见 PostCommentAsync)。
        nameof(_composerExpanded),
        nameof(_nativeInputVersion),
        nameof(_listenSwitching),
        nameof(_modalResizeInProgress),
        nameof(_backdropSuspendQueued),
    };

    /// <summary>
    /// 换片重置覆盖度自检: 找出既没登记为"每片状态"、也没登记为"跨片保持"的字段。
    ///
    /// 新增 <c>private bool/int/long/double/string</c> 字段却忘了在上面两张表里登记时,
    /// 它会出现在 errors.txt 里 —— 这是"漏清字段"唯一能被自动发现的入口。
    ///
    /// 只在 DEBUG 下编译进调用点(<see cref="ConditionalAttribute"/>), Release 零开销。
    /// 定时器字段不在审计范围内(它们由 <see cref="StopAllTimers"/> 自动处理)。
    /// </summary>
    [Conditional("DEBUG")]
    private static void VerifyResetCoverage()
    {
        var known = new HashSet<string>(
            PerMediaStateFields.Concat(PersistentFields), StringComparer.Ordinal);

        var unlisted = StateFieldNames()
            .Where(n => !known.Contains(n))
            .ToList();

        if (unlisted.Count == 0) return;

        App.ReportError(new InvalidOperationException(
            "[PlayerWindow] 以下字段未登记到 PerMediaStateFields(每片需清)或 PersistentFields(跨片保持): "
            + string.Join(", ", unlisted)
            + " —— 换片时它会保留上一条视频的残留。请归类后补进对应清单。"));
    }

    /// <summary>
    /// XAML 编译器自动生成、但**不带任何特性标记**的字段名 —— 必须显式排除。
    ///
    /// ★★ 为什么不能靠特性过滤(2026-10-12 实测):
    ///   `PlayerWindow.g.cs` 生成的 `private bool _contentLoaded;` 是 `InitializeComponent()`
    ///   的防重入标记。它与手写字段同属一个部分类, 所以 `DeclaredOnly` 拦不住;
    ///   类型正好是 bool, 于是被自检当成"漏登记的状态字段"—— errors.txt 里因此刷了 **397 条**
    ///   (同一句话每打开一个视频报一次, 日志涨到 797KB)。
    ///   而它**既没有 `CompilerGeneratedAttribute` 也没有 `GeneratedCodeAttribute`**
    ///   (只有相邻的 `InitializeComponent` 方法带 GeneratedCode, 字段本身光秃秃),
    ///   类上同样没有标记 ⇒ 反射层面没有任何判据能区分它与手写字段。
    ///   (试过按 CompilerGenerated 过滤, 探针实测生成字段数为 0, 那条路走不通。)
    /// ⇒ 只能按名字排除。这个名字由 XAML 编译器固定生成, 全项目唯一, 不会与业务字段撞。
    /// </summary>
    private static readonly HashSet<string> XamlGeneratedFieldNames = new(StringComparer.Ordinal)
    {
        "_contentLoaded",
    };

    /// <summary>取本类里所有"简单状态"字段名(值类型 + string, 不含 UI 元素/集合/定时器/服务引用)</summary>
    private static IEnumerable<string> StateFieldNames()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return typeof(PlayerWindow).GetFields(flags)
            // ★★ XAML 编译器生成的字段必须排除(2026-10-12, errors.txt 里刷了 381 次的那条),
            //   原因与为什么只能按名字排除, 见 XamlGeneratedFieldNames 的注释。
            .Where(f => !XamlGeneratedFieldNames.Contains(f.Name))
            .Where(f => !typeof(DispatcherTimer).IsAssignableFrom(f.FieldType))
            .Where(f => f.FieldType.IsPrimitive || f.FieldType == typeof(string)
                        || f.FieldType.IsEnum
                        || f.FieldType == typeof(decimal) || f.FieldType == typeof(DateTime))
            .Select(f => f.Name);
    }


    // ------------------------------------------------------------ 关闭

    /// <summary>
    /// 把视频覆盖层从屏幕上摘下来。
    ///
    /// 背景 —— LibVLCSharp.WPF 的 airspace 方案: VideoView 会把它的 Content
    /// (也就是弹幕层 + 底部播放控制栏 + 各种遮罩) **搬运到一个独立的透明顶层窗口**
    /// (ForegroundWindow)里, 并把它设成 PlayerWindow 的 owned window 来跟随移动。
    ///
    /// 坑在于: Windows 只在 owner **最小化**时才连带隐藏被它拥有的窗口, 单纯 Hide() 不会。
    /// 我们的"秒关"走的正是 Hide(), 于是那层不透明内容(视觉上就是底部控制栏那一条,
    /// 因为弹幕层/视频区是透明的)会作为"残影"留在屏幕上, 直到约 1 秒后后台拆解完成
    /// 才被 VideoView.Dispose() 关掉。
    ///
    /// 这里做两件事, 任何一件单独都足以让残影消失, 一起做是保险:
    ///   1. 清空 VideoView.Content -> ForegroundWindow 里的内容被移除, 该窗口变成全透明
    ///      (空间上仍占着原位, 所以第 2 步还是有意义的 —— 否则它会吞掉那块区域的鼠标输入);
    ///   2. 反射把那个浮动窗口 Hide() 掉。属性是库的 private 成员, 拿不到就跳过,
    ///      此时第 1 步已经保证了"看不见"。
    /// </summary>
    private void DetachVideoOverlay()
    {
        // ★ vmem 模式下 VideoAreaRoot 已经搬到普通 WPF 树里了，这里**不能**清
        //   VideoView.Content（那本来就是 null，清了也无害，但别误伤已搬走的元素）。
        if (!_vmemMode)
        {
            try { VideoView.Content = null; } catch { }

            try
            {
                var prop = typeof(VideoView).GetProperty("ForegroundWindow",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (prop?.GetValue(VideoView) is Window fw) fw.Hide();
            }
            catch { /* 拿不到就算了, 内容已清空, 顶多剩下一个全透明的空窗口 */ }
        }
    }

    /// <summary>
    /// 把键盘与鼠标侧键处理也挂到 VideoView 的那个浮动覆盖层窗口上。
    ///
    /// 为什么必须这么做(用户报的"点一下视频后键盘就失灵"):
    /// 弹幕层 + 播放控制栏是渲染在 **ForegroundWindow** 里的(见上面 airspace 的说明),
    /// 那是一个独立的顶层窗口。鼠标点视频区/控制栏会把焦点交给它, 于是按键全部由它接收,
    /// PlayerWindow 的 PreviewKeyDown 一次都不会触发 —— 快捷键(空格/方向键/F/D)全哑。
    /// 点一下右侧信息栏焦点才回到 PlayerWindow, 键盘"就恢复正常了", 正是这个原因。
    ///
    /// 鼠标侧键是同一个道理: 指针在视频区上时, 事件属于那个浮动窗口, 根本不会路由到
    /// PlayerWindow —— 所以两个事件都要在这儿补一份。
    ///
    /// 与其在每次点击后抢回焦点(更容易和 Slider 拖动打架), 不如两个窗口共用一套处理:
    /// 谁拿到焦点都一样。属性是库的 private 成员, 拿不到就静默跳过。
    /// </summary>
    private void HookOverlayKeyboard()
    {
        if (_closing) return;
        try
        {
            var prop = typeof(VideoView).GetProperty("ForegroundWindow",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (prop?.GetValue(VideoView) is not Window fw) return;
            AttachVideoOverlayWheelHook(fw);
            if (ReferenceEquals(fw, _overlayKeyboardWindow)) return;
            _overlayKeyboardWindow = fw;
            fw.PreviewKeyDown += OnPreviewKeyDown;
            fw.PreviewMouseDown += OnPreviewMouseDown;
            fw.PreviewTouchDown += OnPreviewTouchDown;
            fw.Deactivated += OnTouchWindowDeactivated;
            // 滚轮也要挂上来: 鼠标压在视频画面上时事件属于这层浮动窗口(见 OnVideoAreaMouseWheel)
            fw.PreviewMouseWheel += OnVideoAreaMouseWheel;
            // 这个窗口不该出现在任务栏里(它是跟随主窗口的装饰层)
            fw.ShowInTaskbar = false;
        }
        catch { /* 拿不到就算了, 键盘至少在主窗口里是可用的 */ }
    }

    /// <summary>
    /// 把 HwndHost 那个原生宿主窗口的**类背景刷**改成黑色 —— 修"视频没填满时是一片白"。
    ///
    /// ★★★ 真根因(2026-10-17 探针 bd-probe-letterbox 实测钉死, 别再猜):
    ///   `VideoView` 是 <c>HwndHost</c>, 它下面由 libvlc 建了一串**同进程**的子窗口:
    ///   <code>
    ///     HwndWrapper[...]   ← HwndHost 宿主窗口(我们这侧建的)
    ///       Static           ← 铺满整个视频区(1000x945)   ★白边的作者
    ///       VLC video main   ← 铺满整个视频区
    ///       VLC video output ← 只盖住画面那一块(1000x562)
    ///   </code>
    ///   视频宽高比与视频区不一致时, `video output` 只占中间一条, 上下(或左右)剩下的区域
    ///   没有任何窗口画东西 ⇒ 露出 `Static` 的**窗口类默认背景(白色)**。
    ///   逐层隐藏实测: 藏 `Static` → 边带亮度 240→0; 藏另两个 → 不变。**作者就是 Static。**
    ///
    /// ★★ 为什么不能"把手势层涂黑"(2026-10-13 那条记录只说会全黑, 原因在此):
    ///   `VideoAreaRoot` 在 `VideoView.Content`(浮层窗口)里, **盖在整个视频区之上**。
    ///   实测: 涂品红 → 边带变品红(确实在最上层); 涂不透明黑 → **画面中心也黑了**。
    ///   ⇒ 它盖的是整块, 分不出"画面"和"边带", 这条路不可用。
    ///
    /// ★ 为什么改**父窗口**的类背景刷有效(而不是 Static 自己的):
    ///   实测 `Static` 的 `GCLP_HBRBACKGROUND` 本来就是 0(没设), 它的白来自 `Static` 类
    ///   自身的默认背景; 把**父窗口**设成黑之后, 那块区域由父窗口先擦黑, Static 再叠上去
    ///   就透不出白 —— 边带 152→0, 画面不受影响, 持续播放 2 秒仍正确(实测)。
    ///   ★ 关键: 它是**本进程的窗口**, 所以 `SetClassLongPtr` 可用;
    ///     libvlc 自己的 `VLC video *` 窗口怎么改都无效(探针 E/G/J 段都验证过)。
    ///
    /// ★ 起播/换片后必须再调一次(见调用点): libvlc 重建 vout 会重新建 `Static` 子窗口,
    ///   那时会再露出一次白 —— 这也是为什么"点一下就好"的观感像是"要碰一下才正常"。
    /// </summary>
    /// ★ 实测有效的只有一处: 把 `Static` 的**父窗口**(`HwndWrapper`, 也就是 HwndHost 的宿主)
    ///   的类背景刷设成黑 —— 边带 242→0 且画面正常, 持续播放不回弹。
    ///   ★ 反过来对 `Static` 自己涂黑**无效**(实测: 边带仍 242) —— 它自己那层不负责擦背景。
    ///     这条很容易搞反: "白边是 Static 画的" ≠ "改 Static 就能黑", 两个都试过才知道。
    /// </summary>
    private void ApplyHwndHostBlackBackground()
    {
        var hwnd = FindVideoStaticWindow();
        if (hwnd == IntPtr.Zero) return;
        // ★★ 只刷**宿主窗口**的类背景刷, 不隐藏 `Static`(2026-10-17 最终版):
        //   早两版试过 `ShowWindow(Static, SW_HIDE)`, 在探针里能把边带刷到 0,
        //   但真机一跑就发现 **整个视频画面也没了** —— 说明这台机器上画面就是 `Static`
        //   那条链画出来的(与探针顶部的拓扑假设不同, 不同 vout/显卡组合不一样)。
        //   ⇒ 隐藏它风险太大, 不能作为产品行为的默认值。
        //   ⇒ 回到"刷父窗口类背景刷"这一条: 探针实测 242→0, 且画面不受影响。
        //
        // ★★ 2026-11-04 合并上游 1.5.4: 白闪/白边的**根治**改成在原生层给宿主窗口
        //    加 WS_CLIPCHILDREN + 子类化 WM_ERASEBKGND 填黑(见 VideoBackdropInterop)。
        //    好处是**拖动窗口/最大化/全屏都不再需要 WPF 黑布** —— 那层黑布是"整块变黑"
        //    和白闪反复横跳的根源。EnsureBlackVideoHost 内部已经包含"类背景刷设黑"这一步,
        //    所以原来那行 PaintClassBackgroundBlack 不必再单独调(它仍是公开的兜底入口)。
        VideoBackdropInterop.EnsureBlackVideoHost(hwnd);
    }

    // --- 起播后把视频区原生窗口底色刷黑(见 ApplyHwndHostBlackBackground) ---

    /// <summary>
    /// **短时守护**: 每 <see cref="VideoBackdropGuardIntervalMs"/> 检查一次 `Static` 有没有
    /// 又被 libvlc 显示出来, 是就立刻再藏一次。
    ///
    /// ★★★ 为什么必须有它(2026-10-17 实测, 这是"白块依旧"的最后一层):
    ///   libvlc 在 **vout 建立 / 重建 / 首帧到达** 时会自己调 `ShowWindow(Static, SW_SHOW)`,
    ///   而我们的"藏一次"只在那一个瞬间有效 —— 它随后又被显示出来, 白色也跟着回来。
    ///   探针时间轴抓到的正是这个: 遮罩压着时边带 0, 撤罩后变成 242(白),
    ///   而那一刻 `Static` 的 `IsWindowVisible` 又是 True。
    ///   ⇒ 靠"事件触发时藏一次"必然有竞态(事件与 libvlc 的内部调用顺序由它决定);
    ///     只有**短时间反复确认**才能把这段时间盖死。
    ///
    /// ★ 为什么是"短时"而不是常驻: 起播/vout 重建之后约 2 秒内它才会折腾,
    ///   之后再频繁去摸原生窗口纯属浪费(而且 `CompositionTarget` 那条零原生调用的规矩
    ///   虽然只约束逐帧路径, 但也没必要给 DispatcherTimer 长期加活)。
    /// </summary>
    private DispatcherTimer? _videoBackdropGuard;
    private int _videoBackdropGuardTicks;

    /// <summary>守护检查间隔(ms)。比一帧长一点, 足够盖住"被重新显示"的窗口期。</summary>
    private const int VideoBackdropGuardIntervalMs = 120;

    /// <summary>守护跑多少拍收手。25 × 120ms = 3s, 覆盖 vout 重建 + 首帧那段。</summary>
    private const int VideoBackdropGuardTicks = 25;

    /// <summary>
    /// 启动短时守护(见 <see cref="RunVideoBackdropGuard"/>): 在接下来约 3 秒内反复确认
    /// `Static` 是隐藏的。尺寸剧变 / vout 重建 / 起播都要调一次。
    /// </summary>
    private void RunVideoBackdropGuard()
    {
        if (_closing) return;
        ApplyHwndHostBlackBackground();   // 立刻先来一次, 不等第一拍

        _videoBackdropGuard ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(VideoBackdropGuardIntervalMs)
        };
        _videoBackdropGuard.Tick -= OnVideoBackdropGuardTick;
        _videoBackdropGuard.Tick += OnVideoBackdropGuardTick;
        _videoBackdropGuardTicks = 0;
        _videoBackdropGuard.Stop();
        _videoBackdropGuard.Start();
    }

    private void OnVideoBackdropGuardTick(object? sender, EventArgs e)
    {
        if (_closing || ++_videoBackdropGuardTicks > VideoBackdropGuardTicks)
        {
            _videoBackdropGuard?.Stop();
            return;
        }
        ApplyHwndHostBlackBackground();
    }

    /// <summary>
    /// 起播 / vout 重建 / 尺寸剧变后, 把视频区原生窗口底色处理干净并启动短时守护。
    ///
    /// ★ 为什么是"守护一段时间"而不是"刷几次就停"(2026-10-17 实测):
    ///   `Playing` 触发时 `Static` 往往还没建出来(句柄 0); 建好之后 libvlc 还会在
    ///   首帧/重建时把它**重新显示**出来。固定 4 拍的预算在这两种情况下都不够用 ——
    ///   探针时间轴实测: 遮罩压着时边带 0, 撤罩后变 242, 而那一刻 `Static` 又是可见的。
    ///   ⇒ 改成 3 秒内每 120ms 确认一次(见 <see cref="RunVideoBackdropGuard"/>)。
    /// </summary>
    private void StartVideoBackdropFix() => RunVideoBackdropGuard();
    /// <summary>
    /// 找 libvlc 那个铺满视频区的 `Static` 窗口(白边的作者)。
    ///
    /// ★ 反射路径有两处必须踩准(2026-10-17 探针实测, 两处都错过一次, 错一处修复就**静默失效**):
    ///   ① `VideoView` 自己**不是** HwndHost —— 真正的宿主在私有字段 `_videoHwndHost`
    ///      (类型 `VideoHwndHost`)。
    ///   ② 该对象的句柄存在基类 `HwndHost._hwnd`, 类型是 **`HandleRef`**(不是 `IntPtr`),
    ///      必须读 `.Handle`。第一版只认 `IntPtr` ⇒ 永远拿到 0 ⇒ 边带一直白着。
    ///   ③ ★ 那个句柄拿到的**正是 `Static` 子窗口本身**(实测类名 `Static`, 父级才是
    ///      `HwndWrapper`), 所以直接对它涂黑就是对的; 不必再往上找父级
    ///      (改父级也有效, 但会波及宿主窗口的其它区域, 不如改 Static 精确)。
    ///   ★ 别用 `PresentationSource.FromVisual(VideoView)`: 那返回的是**外层播放器窗口**
    ///     的 HwndSource, 拿它改类背景刷会把主题底色一起改坏。
    /// </summary>
    private IntPtr FindVideoStaticWindow()
    {
        // vmem 模式下没有 libvlc 的 d3d11 子窗口，也就没有那个会露白底的 `Static`。
        if (_vmemMode) return IntPtr.Zero;
        try
        {
            var vv = VideoView;
            if (vv == null) return IntPtr.Zero;

            var hostField = vv.GetType().GetField("_videoHwndHost",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (hostField?.GetValue(vv) is not System.Windows.Interop.HwndHost host) return IntPtr.Zero;

            var hwndField = typeof(System.Windows.Interop.HwndHost).GetField("_hwnd",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return hwndField?.GetValue(host) switch
            {
                HandleRef hr => hr.Handle,
                IntPtr ip => ip,
                _ => IntPtr.Zero
            };
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// 把键盘焦点从原生子窗口抢回 WPF 树。
    /// 只有"WPF 侧完全没有焦点元素"时才动手 —— 那说明焦点落在 LibVLC 的原生视频窗口上了;
    /// 平时不能抢, 否则在评论框里打字时点一下视频区就丢焦点。
    /// </summary>
    private void RestoreKeyboardFocus()
    {
        if (_closing) return;
        try
        {
            if (Keyboard.FocusedElement == null) Keyboard.Focus(this);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>已经挂过键盘处理的覆盖层窗口(避免重复订阅)</summary>
    private Window? _overlayKeyboardWindow;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if(_enhancementSwitching){e.Cancel=true;_closeAfterEnhancementSwitch=true;return;}
        // 为什么要"先隐藏 -> 后台拆解 -> 再真正 Close":
        // LibVLC 的 Stop/Dispose 会阻塞等待解复用/解码线程退出(实测约 1 秒),
        // 原先在 UI 线程同步调用, 表现就是"点了关闭要卡一下窗口才消失"。
        // 现在第一次关闭请求只做两件极快的事(置标志 + 隐藏窗口), 用户看到的是瞬间关闭;
        // 慢的原生拆解丢到后台线程, 完成后回来真正 Close()。
        if (!_teardownDone)
        {
            e.Cancel = true;
            // 防重入：后台拆解正在进行时再次收到 Close() 不得重复启动 TeardownAndCloseAsync，
            // 否则并发调用原生 MediaPlayer.Dispose() 容易触发 0xC0000005。
            if (_closing) return;
            // 必须在 Stop() 之前抓取进度: 一旦拆解开始, _mp.Time 会归零/_mp 会被 Dispose,
            // 再取就只能拿到 0, 云端历史会丢位置。这里同步取好数值交给 fire-and-forget 任务。
            var finalAid = _aid;
            var finalCid = _currentCid;
            var finalProgress = 0L;
            try { if (_mp != null && _mp.Length > 0) finalProgress = _mp.Time / 1000; } catch { /* 播放器已失效 */ }
            if (finalAid > 0 && finalCid > 0)
                _ = Svc.HistorySync.ReportNowAsync(finalAid, finalCid, finalProgress);

            CancelVideoTouch();
            CloseDanmakuAreaPopup();
            _closing = true;

            // 拖动尺寸监听订阅的是 CompositionTarget.Rendering —— 它是**静态**事件、不随窗口销毁,
            // 所以必须显式解绑(OnModalResizeRendering 里的 _closing 分支只是兜底:
            // 窗口真关闭时渲染循环可能已经停摆, 根本走不到那里)。
            StopModalResizeSizeWatch();
            // 弹幕逐帧推进器订阅的是**同一个**静态事件, 同理必须解绑, 否则窗口关了
            // 渲染回调还在, 回调里又摸 DanmakuCanvas —— 轻则空转, 重则已释放对象。
            StopDanmakuClock();
            StopSeamlessMorph();
            SeamlessTransitionDirector.PlayReturnToSourceCard(_currentBvid);

            // ★ 立刻停掉声音 —— 这是"窗口关了但声音还响半秒"的修法。
            //
            // 为什么会响: 真正的 Stop() 会阻塞约 1 秒, 只能丢到后台线程去做(见 TeardownAndCloseAsync),
            // 而 Hide() 之后窗口已经看不见了、音频却还在播 —— 用户听到的就是"关掉后余音半秒"。
            //
            // Pause 是非阻塞的, 在 UI 线程当场生效, 于是声音在 Hide() 之前就断了。
            // 位置必须在 `_closing = true` 之后、DetachVideoOverlay 之前 —— 与上面读进度是同一个
            // "拆解尚未开始"的安全窗口, 再晚就有碰已释放原生指针的风险。
            // ★ 这里**不要**再叠一句 `_mp.Mute = true`: 实测(2026-09-30 探针)在 LibVLCSharp 3.8 +
            //   本机 libvlc 上 `MediaPlayer.Mute` 是不可靠读数 —— 全新进程里第一个播放器读回就是
            //   True, 显式置 false 也改不动; 往它上面写只会留下一个查不出真假的静音隐患。
            //   停声靠 Pause 就够了。
            try { _mp?.SetPause(true); } catch { /* 已经停了/正在拆解 */ }

            _progressTimer?.Stop();
            _kaomojiTimer?.Stop();
            // 手势定时器必须停掉: 它们可能在窗口已经关闭之后才 Tick, 而 Tick 里会去碰
            // 正在被 Dispose 的 _mp。访问已释放的原生指针不是托管异常, try/catch 拦不住,
            // 表现为进程级访问违例(0xc0000005)。反复"打开-关闭"时这几率会被放大。
            _longPressTimer?.Stop();
            _singleClickTimer?.Stop();
            // 长按快进也要收: 松手恢复速率的调用会碰 _mp, 而这时 _mp 可能正在 Dispose
            // (与上面两个手势定时器同理)
            StopFastForward();
            ResetLikePressProgress();
            _favoriteHoldTimer?.Stop();
            _localToastTimer?.Stop();
            _controlsHideTimer?.Stop();
            StopVolumeOsd();
            StopSpeedOsd();

            // 先把覆盖层摘掉再隐藏窗口, 否则底部控制栏会以残影形式留在屏幕上
            DetachVideoOverlay();

            // 从全屏态直接关闭时, 先摘掉"无边框 + 最大化"这个组合。
            // 该状态下窗口句柄的清理容易不干净(参见 Esc 分支里的同款处理),
            // 反复"全屏打开 -> 直接关闭"会累积成残留窗口/句柄, 也是原生访问违例的温床。
            // 先禁用系统窗口过渡动画并立刻隐藏，绝不在可见状态下切 WindowStyle/WindowState（消除关闭时闪标题栏与还原抖动）
            try { DwmInterop.SetTransitionsDisabled(this, true); } catch { }
            Hide();
            if (_isFullscreen && WindowStyle == WindowStyle.None)
            {
                try
                {
                    // 进全屏时 WS_CAPTION 被摘掉了(盖任务栏用, 见 ApplyFullscreenLayout):
                    // 这里要切成 SingleBorderWindow, 必须先把样式位补回来, 否则系统标题栏缺失。
                    DwmInterop.RestoreCaption(this);
                    WindowState = WindowState.Normal;
                }
                catch { /* 已经处于不可切换状态, 忽略 */ }
            }

            // 立刻隐藏, 感知上就是"秒关"; 拆解过程在后台继续
            // 已在上方提前执行 Hide()

            _ = TeardownAndCloseAsync();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// 异步拆解原生资源后真正关闭窗口。
    /// WPF 控件(VideoView)必须在 UI 线程释放, 只有会阻塞的 LibVLC Stop/Dispose 放到后台线程。
    /// </summary>
    private async Task TeardownAndCloseAsync()
    {
        var mp = _mp;

        // 顺序很关键: **先 Stop, 再解绑 Hwnd**。
        //
        // 反过来写(VideoView.MediaPlayer = null 会走到 libvlc_media_player_set_hwnd(NULL))
        // 有两个问题:
        //   1. 视频还在播的情况下把 Hwnd 清成 NULL, LibVLC 会另建一个属于它自己的顶层输出窗口,
        //      关闭瞬间就会闪出一个"多出来的视频窗口";
        //   2. "边播边换 HWND, 紧接着 Stop" 正是原生层最容易踩访问违例(0xc0000005)的组合 ——
        //      而这恰好对应用户反馈的"反复打开关闭后偶发 Unexpected parameters"。
        // Stop 会阻塞等待解码线程退出(约 1 秒), 所以先丢到后台线程。
        if (mp != null)
        {
            await ReplaceNativeInputAsync(null);
        }

        // 回到 UI 线程解绑(VideoView 是 DependencyObject, 只能在 UI 线程操作)
        if (_vmemMode)
        {
            // ★ vmem：先让宿主停止接收回调，再释放 MediaPlayer（顺序不能反，
            //   反了就是"回调读已释放缓冲"的 0xC0000005 —— 已在 --vmem-view 里踩过）。
            try { VmemView.Dispose(); } catch { }
        }
        else
        {
            try { VideoView.MediaPlayer = null; } catch { }
            try { VideoView.Content = null; } catch { }
            // 释放 VideoView 自己持有的原生子窗口 + ForegroundWindow
            try { VideoView.Dispose(); } catch { }
        }

        // MediaPlayer 的 Dispose 同样可能有内部等待, 也放后台
        if (mp != null)
        {
            await Task.Run(() =>
            {
                try { mp.Dispose(); } catch { }
            });
        }

        _teardownDone = true;
        try { Close(); } catch { /* 应用可能已在退出 */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _pinchWheelIdle?.Stop();
        _closing = true;
        _overlayWheelSource?.RemoveHook(OnVideoOverlayHorizontalWheel);
        _overlayWheelSource = null;
        if (_overlayKeyboardWindow != null)
        {
            _overlayKeyboardWindow.SourceInitialized -= OnVideoOverlaySourceInitialized;
            _overlayKeyboardWindow.PreviewKeyDown -= OnPreviewKeyDown;
            _overlayKeyboardWindow.PreviewMouseDown -= OnPreviewMouseDown;
            _overlayKeyboardWindow.PreviewTouchDown -= OnPreviewTouchDown;
            _overlayKeyboardWindow.Deactivated -= OnTouchWindowDeactivated;
            _overlayKeyboardWindow.PreviewMouseWheel -= OnVideoAreaMouseWheel;
            _overlayKeyboardWindow = null;
        }
        try { _danmakuLoadCancellation?.Cancel(); _danmakuLoadCancellation?.Dispose(); _danmakuLoadCancellation = null; } catch { }
        try { _offlinePrepCts?.Cancel(); _offlinePrepCts?.Dispose(); _offlinePrepCts = null; } catch { }
        try { _offlineOnlineCts?.Cancel(); _offlineOnlineCts?.Dispose(); _offlineOnlineCts = null; } catch { }
        _playerTouchpad?.Dispose(); _overlayTouchpad?.Dispose();
        CancelDanmakuFiltering();
        DisposeAlternatePlayerLayout();
        // 直播弹幕: 关窗必须断开 WebSocket, 否则后台线程会一直重连
        StopLiveDanmaku();
        // 后台合流任务要停: 窗口都没了, 下完也没地方切(而且它还在往磁盘写)
        CancelRemuxPrefetch();
        // ★ 全部定时器在这里统一停 —— 漏掉任何一个都会让整个窗口漏掉(见 StopAllTimers 的说明)
        StopAllTimers();
        StopVolumeOsd();
        StopSpeedOsd();
        if (!_teardownDone)
        {
            try { _mp?.Stop(); } catch { }
            try { _mp?.Dispose(); } catch { }
            if (_vmemMode) { try { VmemView?.Dispose(); } catch { } }
            else { try { VideoView?.Dispose(); } catch { } }
        }
        try { _nativeInputGate.Dispose(); } catch { }
        if (_playerRegistered)
        {
            _playerRegistered = false;
            VlcCore.UnregisterPlayer();
        }
        Svc.Toast.Shown -= OnToast;
        Svc.Session.Changed -= UpdateButtonState;
        Svc.Settings.DanmakuSettingsChanged -= OnDanmakuSettingsChanged;
        UnhookPopupToggles();
        Svc.Settings.SponsorBlockChanged -= OnSponsorSettingsChanged;
        CancelSponsorFetch();
        if (Svc.Player.IsCurrent(this)) SeamlessTransitionDirector.Clear();
        ReleaseHeavyContent();
        MemoryTrim.RequestTrimAndRelease();
        // 把焦点/激活权交还主窗口, 避免关闭后主界面不响应输入。
        // 但如果此刻已经有新的播放器窗口接管(_window 已指向别人), 就绝不能抢焦点 ——
        // 否则新窗口会被挤到后台, 看起来就像"点了视频又立刻退出了"。
        if (Svc.Player.IsCurrent(this))
        {
            try { (System.Windows.Application.Current?.MainWindow)?.Activate(); } catch { }
        }
    }

    /// <summary>
    /// 停掉本窗口拥有的**全部** DispatcherTimer。
    ///
    /// ★ 这不是"顺手收拾一下", 而是防内存泄漏的关键一步, 每一个都不能漏:
    ///   DispatcherTimer 一旦 Start 就被 Dispatcher 长期持有, 而它的 Tick 委托几乎都捕获了 this ——
    ///   只要**有一个还在跑**, 整个已关闭的 PlayerWindow(连同它手里的弹幕列表、评论树、位图、
    ///   整棵可视树)就永远回收不掉。而播放器是"看完就关、下次再看新建一个"的, 于是
    ///   **每看一个视频就永久泄漏一个窗口**。
    ///
    /// 2026-09-27 实测: `_controlsHideTimer` 就是漏掉的那一个 —— 它只在 HideControls/ShowControls
    /// 里停, 而"刚动过鼠标(倒计时重启)就把窗口关掉"正好落在它还在跑的时刻。用户报的
    /// "看视频内存涨很多、关掉也不回落" 就是它攒出来的。
    ///
    /// ★ 2026-10-01: 清单本身已改成**反射收集**(见 TimerFields), 所以"加定时器必须回来补一笔"
    /// 这条规矩**作废了** —— 新加的定时器会被自动停掉。原先靠"探针里一条断言守这份白名单"
    /// 来兜底, 现在兜底换成了机制本身: 漏写不再可能, 探针那条断言也就不再是必需项。
    /// </summary>
    private void StopAllTimers()
    {
        foreach (var f in TimerFields)
        {
            // 防御: 构造函数可能在某个定时器初始化之前就抛了(LibVLC 加载失败时), 所以取不到就跳过
            if (f.GetValue(this) is DispatcherTimer t) t.Stop();
        }
    }

    /// <summary>
    /// 本类(以及它的基类链上, 直到 <see cref="Window"/>)声明的**全部** DispatcherTimer 字段。
    ///
    /// ★ 2026-10-01: 这里原来是一份手打的 8 行白名单, 漏一个就是泄漏一个窗口 ——
    ///   2026-09-27 实测漏掉的就是 `_controlsHideTimer`, 用户报"看视频内存涨很多、关掉也不回落"。
    ///   改用反射收集之后, **加定时器不必再记得回来改这里**, 这类 bug 从根上没了。
    ///
    /// 为什么按**字段类型**收集而不是按名字/按白名单:
    ///   · 按名字 ⇒ `_localToastTimer` 声明时写的是全限定名
    ///     `System.Windows.Threading.DispatcherTimer?`(不是短名), 白名单迟早漏掉它;
    ///   · 显式列全类型 ⇒ 谁新增都跑不掉, 漏不掉就是漏不掉。
    ///
    /// 遍历基类链是防"以后有人把某个定时器挪到 FluentWindow 上"; 只取本类的话挪过去就会失效。
    /// 类型化只扫一次(静态初始化), 运行期开销可忽略。
    /// </summary>
    private static readonly FieldInfo[] TimerFields = CollectTimerFields();

    private static FieldInfo[] CollectTimerFields()
    {
        var found = new List<FieldInfo>();
        for (var t = typeof(PlayerWindow); t != null && t != typeof(Window); t = t.BaseType)
        {
            found.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => typeof(DispatcherTimer).IsAssignableFrom(f.FieldType)));
        }
        return found.ToArray();
    }

    /// <summary>
    /// 关窗时主动松掉几处"占地大"的引用。
    ///
    /// 窗口整体被回收当然也能释放它们, 但那要等下一次 GC 真的跑到那个代;
    /// 这里先断开, 紧接着的那次 RequestTrimAndRelease 才有东西可收。
    /// 顺序上它是"先松手再回收"的前半截, 少了它回收基本收不到什么。
    /// </summary>
    private void ReleaseHeavyContent()
    {
        try { DanmakuCanvas?.Children.Clear(); } catch { }
        try { _danmaku.Clear(); } catch { }
        try { _danmakuRaw.Clear(); } catch { }
        _sponsorRaw = Array.Empty<SponsorSegment>();
        _sponsorActive = Array.Empty<SponsorSegment>();
        _sponsorHandled.Clear();
        if (CommentList != null) CommentList.ItemsSource = null;
        // 封面/头像的 ImageBrush 也是"挂着位图"的引用, 一并放掉
        if (UpAvatarBox != null) UpAvatarBox.Background = null;
        if (SeamlessHeroCard != null) SeamlessHeroCard.Background = null;
        if (LoadingPoster != null) LoadingPoster.Background = null;
        if (DockTransitionPoster != null) DockTransitionPoster.Background = null;
    }

    /// <summary>音频链路诊断是否已经记过(每个片源只记一次)</summary>
    private bool _audioStateLogged;

    /// <summary>
    /// 每个片源只在**第一次** Playing 时记一条音频链路状态。
    ///
    /// 为什么需要它: "画面正常但没有声音"这类问题离线复现不了 —— 是没挂上音轨、还是音量被
    /// 压到 0、还是走了单文件源, 只有起播那一刻的原生状态能回答。Playing 之后 aout 才建立,
    /// 所以这个时机是唯一能读到有意义数值的地方(起播前原生音量读回 -1)。
    /// 记一次就够: Playing 在缓冲/seek 后会反复触发, 每次都写会把日志冲掉。
    /// </summary>
    private void LogAudioStateOnce()
    {
        if (_audioStateLogged) return;
        _audioStateLogged = true;
        try
        {
            WriteLibVLCDebug(
                $"[Audio] bvid={_currentBvid} cid={_currentCid} qn={_currentQn} " +
                $"音轨数={_mp.AudioTrackCount} 原生音量={_mp.Volume} 应用音量={_volumeCache} " +
                $"独立音轨={(string.IsNullOrEmpty(_lastAudioUrl) ? "无(单文件源)" : "有")}\n");
        }
        catch { /* 诊断信息拿不到就算了, 绝不影响播放 */ }
    }

    /// <summary>
    /// LibVLC 诊断日志(选流/初始化环境/播放失败时的上下文)。
    ///
    /// 两个发布前修掉的问题:
    ///   1. 旧版写到 <c>AppContext.BaseDirectory</c> —— 装进 Program Files 后该目录**不可写**,
    ///      日志会永远静默丢失; 现在写到数据目录(与 errors.txt 同一处)。
    ///   2. 旧版无限追加(开发机上已积到 600KB+); 现在超过 256KB 就整份重来 ——
    ///      这份日志只在"排查最近一次播放问题"时有价值, 老内容没必要留。
    /// </summary>
    /// <summary>诊断用: 描述手势层当前底色(纯黑 / 近透明 / 其它)</summary>
    private string DescribeVideoAreaBg()
    {
        if (VideoAreaRoot == null) return "(null)";
        return VideoAreaRoot.Background switch
        {
            SolidColorBrush sb => sb.Color.ToString(),
            null => "(none)",
            var b => b.GetType().Name,
        };
    }

    /// <summary>诊断用: 安全读一次播放位置(日志里不能因为异常就丢整条记录)</summary>
    private long SafeReadTime()
    {
        try
        {
            var t = _mp?.Time ?? -1;
            return (t < 0 || t > 24L * 3600 * 1000) ? -1 : t;
        }
        catch { return -1; }
    }

    /// <summary>原生日志(排查最近一次播放问题用, 见 WriteLibVLCDebug)</summary>
    private static void WriteLibVLCDebug(string content)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "vlc-debug.txt");
            var f = new FileInfo(path);
            if (f.Exists && f.Length > 256 * 1024) f.Delete();
            File.AppendAllText(path, content);
        }
        catch { /* 静默 */ }
    }
}
