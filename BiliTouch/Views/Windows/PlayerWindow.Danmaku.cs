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
using BiliTouch.Views.Controls;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliTouch.Views;

public partial class PlayerWindow
{
    // ------------------------------------------------------------ 弹幕

    // --- 弹幕轨道状态(消除重叠的核心) ---
    //
    // 旧实现每条弹幕都 Rand.Next 取一个纵坐标, 弹幕一多必然层层叠在一起。
    // 现在把显示区域切成固定高度的"泳道"(lane), 每条滚动弹幕占据一条泳道;
    // 分配时选择"最早可用"的泳道, 并校验与同泳道前一条弹幕的间距,
    // 保证同一泳道里后一条不会追上(追尾)前一条。
    private double _laneHeight = DefaultLaneHeight();   // 单条泳道高度(随字号联动)
    private double[]? _laneFreeAt;                 // 每条泳道的"可用时间点"(秒, 播放时间轴)
    private double[]? _laneLastWidth;              // 每条泳道最后一条弹幕的宽度
    private double _lastLaneCalcWidth;             // 上次计算泳道时的画布宽度(尺寸变化时重算)
    private double _lastLaneCalcHeight;

    // ---- 外观三项(不透明度 / 字号 / 速度), 2026-10-12 新增 ----
    //
    // ★ 三者的落点各不相同, 所以各有各的重算代价, 别一股脑"改了就清画布":
    //   · 字号    → 改字号必须**重建泳道**: 泳道高是字号推出来的, 不重建的话
    //               新弹幕按新字号画、泳道却还是旧高度, 弹幕就会上下叠在一起
    //               (这正是 DanmakuLaneRatio 那条注释要防的历史 bug)。
    //   · 速度    → 横穿时长变了, 泳道的"可用时间"与追尾校验全部作废, 也必须重建,
    //               否则新弹幕会被按旧速度判成"追上了前一条"而被整批丢弃。
    //   · 不透明度 → 只改基线透明度, 不影响布局, 因此可以**就地改**, 不必清屏;
    //               这一条对"用户正在拖滑杆"很关键 —— 拖一次就闪一次是不能接受的。
    private double _danmakuFontSize = DefaultFontSize();     // 当前实际字号
    private double _danmakuOpacity = 0.8;                    // 基线不透明度 0~1
    private int _danmakuSpeedLevel = 2;                      // 速度档位
    private double _danmakuScrollSeconds = Helpers.DanmakuMotion.ScrollSeconds(2);  // 滚动横穿时长
    private double _danmakuFixedSeconds = Helpers.DanmakuMotion.FixedSeconds(2);    // 固定停留时长

    /// <summary>
    /// 同步弹幕外观滑杆时的回灌保护。
    /// 见 <see cref="UpdateDanmakuLookTexts"/>: 赋 Value 会触发 ValueChanged。
    ///
    /// ★★★ 初值必须是 **true**(2026-10-30 修"弹幕字号/覆盖面永远回到默认"):
    ///   XAML 里四个滑杆都写了初始 `Value`(25 / 80 / 100 / 2) 且挂了 `ValueChanged` ——
    ///   `InitializeComponent()` 装载这些初始值时会**立刻触发**四个处理器, 把用户的设置
    ///   覆写成默认值。构造函数后面再从设置里读, 读到的已经是刚被覆写的 25/80/100/2。
    ///   闸门从一开始就关着, 等构造函数把滑杆同步成用户的真实设置后才打开。
    /// </summary>
    private bool _syncingDanmakuLook = true;

    /// <summary>基准弹幕字号(倍率 100% 时的值)</summary>
    private const double DanmakuBaseFontSize = 24;

    private static double DefaultFontSize() => DanmakuBaseFontSize;

    /// <summary>默认泳道高(字号 × 系数)。字段初始化器要用到, 所以给个静态入口。</summary>
    private static double DefaultLaneHeight()
        => DanmakuBaseFontSize * DanmakuLaneRatio;


    // ★★★ 滚动/固定弹幕的时长不再是常量(2026-10-12 加了「弹幕速度」设置),
    //   改为按当前档位算出来存在 _danmakuScrollSeconds / _danmakuFixedSeconds,
    //   见 Helpers.DanmakuMotion.ScrollSeconds / FixedSeconds(那边是纯函数, 可单测)。
    //   ★ 下面所有用旧常量名的地方都必须换成实例字段 —— 漏一处就是"速度滑块拖了没反应"。
    // 屏幕上的滚动弹幕上限(内存/渲染保护)
    private const int MaxOnScreenDanmaku = 80;

    /// <summary>基准弹幕字号(2026-10-11 用户要求"弹幕字体调大一些": 18 → 24)。</summary>
    /// <remarks>
    /// 原来这个值直接写在 AddDanmaku 里, 而泳道高 _laneHeight 又另写了一个 28 ——
    /// 两个数没有任何关联, 调字号必得漏掉泳道, 于是弹幕上下压在一起。现在两者都由
    /// <see cref="DanmakuLaneRatio"/> 推出来, 改字号只需要改这一处。
    /// ★ 2026-10-12 起它不再是"写死的值", 而是 100% 档位的基准, 实际字号
    ///   = 本值 × (用户的字号百分比 / 100), 见 <see cref="_danmakuFontSize"/>。
    /// ★ 2026-10-13 删掉了这里的别名常量 `DanmakuFontSize`(它只是 = DanmakuBaseFontSize,
    ///   全项目无引用): 两个名字指同一个值, 改字号时"该改哪一个"会让人停一下。
    ///   真正的定义在文件上方(见 DanmakuBaseFontSize)。
    /// </remarks>

    /// <summary>泳道高 = 字号 × 该系数(留出上下各约四分之一行距, 密集时才不会糊成一片)</summary>
    private const double DanmakuLaneRatio = 1.5;

    // ---- 在飞的弹幕(位移由**视频时钟**驱动, 不再用 WPF 动画) ----
    //
    // ★★★ 2026-10-11 用户报"视频暂停时弹幕不动"——原话是"视频暂停时弹幕不动"(即要停),
    //   根因是原来用 DoubleAnimation 起滚动: WPF 动画走合成线程 + 墙钟, 与播放器状态无关。
    //
    //   为什么没有用"暂停动画"来修(这的路子全都实测堵死, 见 .probes/bd-probe-animfreeze):
    //     · Animation / AnimationStorage / AnimationClock 全是 internal, 外部拿不到;
    //     · SpeedRatio 只在 AnimationTimeline 上, 而 CreateClock() 返回的基类时钟上没有它
    //       ⇒ "begin 之后再回来改 SpeedRatio"在公开 API 里根本走不通;
    //     · 唯一公开的暂停入口 Storyboard.Pause, 在"动画挂在 Border 的 RenderTransform 上"
    //       这种写法下实测**静默无效**(GetCurrentTime 直接报"Storyboard 未应用到该对象")。
    //
    //   所以改成: 每帧从 _mp.Time 取真实媒体时间, 自己算该到哪儿。
    //   这样"暂停"根本不是一个需要处理的状态 —— 媒体时间不前进, 弹幕自然就不动;
    //   顺带还白送了 seek(清表重建)与拖进度条时的正确重同步。
    private readonly List<FlyingDanmaku> _flying = new();
    private bool _danmakuClockAttached;

    // ---- 弹幕时钟: Stopwatch 走时 + 低频校准(2026-11 修"弹幕滑动很卡, 几帧几帧地跳") ----
    //
    // ★★★ 第一版是**每帧**直接读 `_mp.Time`(还多读了一次 `_mp.Length` 判断有没有媒体)。
    //   那是两次**跨语言边界的原生调用 × 每帧 60 次** = 每秒 120 次 P/Invoke,
    //   足以把渲染回调拖慢 ⇒ 用户看到的就是"弹幕几帧几帧地跳"。
    //   ★ 教训: `CompositionTarget.Rendering` 里**一次原生调用都不能有**。
    //     判"是不是还在放媒体"这类检查更不能放这里 —— 它每帧都跑, 哪怕它自己认为很便宜。
    //   ⇒ 改成: 用 `Stopwatch` 自己走时(纯本地), 只在 `_mp.TimeChanged`(约 4Hz)校准一次,
    //     暂停/恢复由 `_mp.Paused` 冻结/解冻。既零 P/Invoke, 又跟得上真实媒体时间
    //     (seek 会在下一个 TimeChanged 立刻纠偏)。
    private readonly Stopwatch _dmWatch = Stopwatch.StartNew();
    /// <summary>上次校准到的媒体时间(秒)</summary>
    private double _dmClockMedia;
    /// <summary>校准那一刻 <see cref="_dmWatch"/> 的毫秒读数 —— 走时的起点</summary>
    private long _dmClockStamp;
    /// <summary>是否在走时。暂停/未播放时为 false, 此时弹幕时钟就是 _dmClockMedia</summary>
    private bool _dmClockRunning;

    /// <summary>
    /// 走时期间的**播放倍速**。外推必须乘上它, 否则倍速播放时弹幕会一顿一顿。
    ///
    /// ★★★ 2026-10-12 修用户报的"倍速下弹幕滑动不连贯、卡顿":
    ///   弹幕时钟是"校准点 + 墙钟流逝"外推出来的, 而**媒体时间在 2 倍速下走得比墙钟快一倍**。
    ///   乘 1.0 的话外推值始终落后真实媒体时间, 每约 250ms 被校准强行拉回一次 ——
    ///   表现就是弹幕匀速前进一小段、然后**突然跳**一截, 即"不连贯/卡顿"。
    ///   倍速越大越明显(0.5x 则反过来, 外推跑太快、每次校准往回拽)。
    ///
    /// ★ 取值时机: 每次校准(<see cref="CalibrateDanmakuClock"/>)时读一次 `_mp.Rate` ——
    ///   那是约 4Hz 的低频事件, 一次原生调用不心疼; 而这里(逐帧路径)必须是**纯本地字段读取**。
    ///   切换倍速后最迟 250ms 内就会被下一次校准刷新, 肉眼看不出来。
    private double _dmClockRate = 1.0;

    /// <summary>
    /// 当前弹幕时钟(媒体秒)。每帧调用, **零原生调用** —— 只做一次 Stopwatch 读数 + 一次乘加。
    ///
    /// ★ 算式本体在 <see cref="Helpers.DanmakuMotion.Extrapolate"/> 里(纯函数, 探针可断言) ——
    ///   这里只负责"取当前墙钟读数"这一步, 保持热路径不碰任何非托管资源。
    /// </summary>
    private double DanmakuClock() => _dmClockRunning
        ? Helpers.DanmakuMotion.Extrapolate(
            _dmClockMedia, (_dmWatch.ElapsedMilliseconds - _dmClockStamp) / 1000.0, _dmClockRate)
        : _dmClockMedia;

    /// <summary>
    /// 用播放器推来的真实媒体时间校准一次(顺带把走时起点重置到"现在")。
    /// 只在 <c>_mp.TimeChanged</c> 里调 —— 那是约 4Hz 的低频事件, 一帧一次的原生调用在这里不心疼。
    /// </summary>
    private void CalibrateDanmakuClock(double mediaSeconds)
    {
        _dmClockMedia = mediaSeconds;
        _dmClockStamp = _dmWatch.ElapsedMilliseconds;
        // ★ 顺便刷新倍速(这里读原生 Rate 是低频路径, 不违反"逐帧零 P/Invoke"那条铁律)。
        //   Rate 偶尔会返 0(切换/初始化的一瞬), 那时按 1x 走, 别让外推停摆。
        var rate = _mp?.Rate ?? 1.0f;
        _dmClockRate = rate > 0 ? rate : 1.0;
        _dmClockRunning = true;
    }

    /// <summary>
    /// 暂停/恢复时冻结/解冻弹幕时钟。
    ///
    /// ★★★ **暂停时必须冻结在"当前外推值", 不能直接读 `_mp.Time`**(2026-11-11 用户报
    ///   "暂停时弹幕会突然向后移动一点"): 校准是约 4Hz 的 TimeChanged 驱动的, 两次校准之间
    ///   弹幕钟按墙钟外推; 若外推快于实际播放(缓冲/掉帧时 TimeChanged 间隔被拉长), 冻结那一刻
    ///   `_mp.Time` 就落后于外推值 ⇒ 弹幕整体回跳一小段。冻结语义应当是"停在此刻所见",
    ///   即当前的 DanmakuClock() 读数, 而不是重新取一次媒体时间。
    /// ★ 恢复时**也**以暂停时的冻结值为基准直接续跑, 不重新读 `_mp.Time`:
    ///   暂停没 seek 的话两者本就几乎相等, 而读 `_mp.Time` 若略小于冻结值,
    ///   恢复瞬间弹幕又会回跳一次(与暂停回跳同机理)。真正需要纠偏的场景(seek)
    ///   由 TimeChanged → CalibrateDanmakuClock 在 250ms 内自然收敛, 不必抢这一帧。
    /// </summary>
    private void SetDanmakuClockRunning(bool running)
    {
        if (running == _dmClockRunning || _closing || _mp == null) return;
        // 两侧都不额外取媒体时间: 冻结取"此刻所见", 恢复从"冻结处"续跑。
        // 基准重置到"现在"是必须的 —— Stopwatch 一直在走, 不重置恢复瞬间会前跳一大段。
        _dmClockMedia = DanmakuClock();
        _dmClockStamp = _dmWatch.ElapsedMilliseconds;
        _dmClockRunning = running;
    }

    /// <summary>
    /// 倍速刚被改变时立刻同步弹幕时钟的倍速(而不是等下一次 4Hz 校准)。
    ///
    /// ★ 为什么需要它: <see cref="_dmClockRate"/> 是在校准里刷新的, 而校准约 250ms 一次。
    ///   用户点"倍速"那一瞬到下一次校准之间, 弹幕仍按旧倍速外推 —— 2x 时约 250ms 的
    ///   位置偏差, 下一次校准会**突然拽回去**, 那一顿仍然看得见。
    ///   ★ 做法与暂停/恢复同款: 先把当前外推值**落定**(重置基准), 再换速率 ——
    ///     这样位置连续, 只是速度从这一刻起变了, 不会有跳变。
    ///   ★ 必须在**改 Rate 之前**调用: 否则读到的还是旧速率, 等于没换。
    /// </summary>
    private void SyncDanmakuClockRate(float newRate)
    {
        if (_closing || _mp == null) return;
        // 先把"此刻所见"落成新的校准点(位置连续), 再换速率
        _dmClockMedia = DanmakuClock();
        _dmClockStamp = _dmWatch.ElapsedMilliseconds;
        _dmClockRate = newRate > 0 ? newRate : 1.0;
    }

    /// <summary>屏幕上正在飞的一条弹幕</summary>
    private sealed class FlyingDanmaku
    {
        public Border Element = null!;
        public TranslateTransform Translate = null!;
        /// <summary>上屏那一刻的媒体时间(秒)。位移 = f(当前媒体时间 - 它)</summary>
        public double SpawnClock;
        /// <summary>滚动: 横穿总时长。固定: 停留总时长。</summary>
        public double TotalSeconds;
        /// <summary>滚动弹幕自身宽度(用于算"完全离开左侧"的位置)</summary>
        public double Width;
        /// <summary>true = 顶部/底部固定弹幕(不动横向, 只按时间淡出)</summary>
        public bool IsFixed;
    }

    // 顶部/底部固定弹幕的占用时间表(避免固定在同一个位置互相压住)
    private readonly List<(double until, int slot)> _topSlots = new();
    private readonly List<(double until, int slot)> _bottomSlots = new();

    /// <summary>
    /// 判定"弹幕游标"与播放位置之间有多大的时间错位才算失配(秒)。
    ///
    /// 取 1.5: 正常播放时游标最多落后一个心跳(TimeChanged 通常 0.25~0.5s 一跳),
    /// 远小于这个阈值, 所以绝不会误判成 seek; 而真实 seek 的错位是"几秒到几分钟"量级。
    /// </summary>
    private const double DanmakuCursorToleranceSec = 1.5;

    /// <summary>
    /// 游标是否已与播放位置失配 —— 失配了就该重新二分定位。
    ///
    /// 游标 <c>_indexInLib</c> 的不变式是"指向第一条还没发出去的弹幕", 正常播放时它总是
    /// 满足 last &lt;= current &lt;= next。失配只有两种, 都发生在位置突变之后:
    ///   · <c>next</c> 在很远的地方 ⇒ 用户**往回**跳了;
    ///   · <c>last</c> 在很远的地方 ⇒ 用户**往前**跳了。
    ///
    /// ★ 后半条以前是漏的(旧代码只判了 next), 而它恰好是会出事的那个: 往前跳之后,
    ///   游标还指着跳转前那一条, 它连同后面**整段**弹幕都满足 time &lt;= current,
    ///   DispatchDanmaku 的 while 循环会在一帧里全部发出来 —— 几百条同屏 = 糊屏 + 卡顿。
    ///   同一个坑还有第二个触发口: 弹幕是异步加载的, 等它回来时播放已经过去一段时间,
    ///   而 _indexInLib 刚被置 0, 于是从头到当前时刻的弹幕一次性全发。
    ///
    /// 纯函数(不碰实例状态), 探针/单测可以直接喂几组三元组断言, 不必起播放器。
    /// </summary>
    private static bool IsDanmakuCursorStale(double currentSeconds, double lastSentTime, double nextTime)
        => lastSentTime > currentSeconds + DanmakuCursorToleranceSec
        || nextTime < currentSeconds - DanmakuCursorToleranceSec;

    /// <param name="durationHintSec">
    /// 调用方**在起播之前**就能拿到的视频时长(详情接口的 <c>DurationSec</c>, 单位秒)。
    /// -1(默认)表示没有提示, 退回 <see cref="_totalMs"/> —— 老的"起播后再拉"那条路就是这样。
    ///
    /// ★ 为什么需要它: 现在弹幕预加载提到了起播之前(见 LoadVideoAsync 里的调用点), 那一刻
    ///   <see cref="_totalMs"/> 还是 0(它要等取播放地址回来才知道)。没有这个提示的话
    ///   GetDanmakuAsync 会走 "时长未知 ⇒ 按最多 20 段拉" 的兜底 —— 一个 3 分钟的短视频
    ///   也要发 20 个段请求, 白白浪费 19 个。传了时长就只拉真正存在的段(3 分钟 = 1 段)。
    /// </param>
    private CancellationTokenSource? _danmakuLoadCancellation;
    private async Task LoadDanmakuAsync(long cid, int durationHintSec = -1)
    {
        if (cid <= 0) return;
        _danmakuLoadCancellation?.Cancel();
        var cancellation=_danmakuLoadCancellation=new CancellationTokenSource();
        var generation=_loadGen;
        try
        {
            // 传视频总时长, 让接口只拉真正需要的分段(6 分钟一段), 长视频不再白拉几十个请求。
            // 保留最多 60000 条；超限按完整时间轴均匀采样，不能截断后半段。
            var durationSec = durationHintSec >= 0
                ? durationHintSec
                : _totalMs > 0 ? (int)(_totalMs / 1000) : 0;
            var list = await Svc.Api.GetDanmakuAsync(cid, durationSec, 60000, cancellation.Token);
            var ordered = await Task.Run(() => list.OrderBy(d => d.Time).ToArray(), cancellation.Token);
            var filtering = await Dispatcher.InvokeAsync(() =>
            {
                if (_closing || cancellation.IsCancellationRequested) return Task.CompletedTask;
                // ★ 取回来时这条弹幕还必须属于**当前这一段**: 预加载提前之后, 它和"换下一个视频"
                //   之间的窗口从几百毫秒拉长到了整段取流时间, 旧 cid 的结果会把新视频的弹幕盖掉
                //   (表现: 新视频弹幕全灭, 或者满屏上一条视频的弹幕)。
                if (_currentCid != cid || generation != _loadGen) return Task.CompletedTask;
                // 原始列表按时间排好序存下来, 供后续"改屏蔽词立即重算"使用
                _danmakuRaw.Clear();
                _danmakuRaw.AddRange(ordered);
                return RefilterDanmakuAsync(force: true);
            });
            await filtering;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { WriteLibVLCDebug($"[Danmaku] cid={cid} load failed: {ex.GetType().Name}\n"); }
        finally
        {
            if (ReferenceEquals(_danmakuLoadCancellation, cancellation)) _danmakuLoadCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// 用当前的屏蔽设置重新过滤一遍已加载的弹幕(设置页改动时调用)。
    /// 不重新请求接口, 只重算内存里的列表; 会尽量保持当前播放位置附近的索引,
    /// 避免重算后弹幕"从头再刷一遍"或者整段卡住。
    /// </summary>
    private void RefilterDanmaku()
    {
        _ = RefilterDanmakuAsync();
    }

    /// <summary>
    /// 弹幕过滤。
    /// 智能屏蔽开启时: 过滤超长弹幕 + 相同文本的刷屏(只留前 N 条) + 纯符号/空白;
    /// 关闭时只做最基本的安全过滤(去空白), 其余全部保留。
    /// 关键词屏蔽**独立生效**: 无论智能屏蔽是否开启, 用户手写的黑名单都会被应用
    /// (用户明确写了"不想看到"的词, 优先级高于"要不要自动净化"这个总开关)。
    /// </summary>
    private static IEnumerable<RawDanmaku> ApplyDanmakuFilter(IEnumerable<RawDanmaku> list)
        => FilterDanmakuSnapshot(list, CaptureDanmakuFilter());

    private static IEnumerable<RawDanmaku> FilterDanmakuSnapshot(IEnumerable<RawDanmaku> list,
        DanmakuFilterSnapshot options, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var smart = options.Smart;
        var rules = DanmakuKeywordFilter.Build(options.Keywords);

        // 类型过滤(滚动/固定/彩色/高级): 四项全开 = 不过滤。
        // ★ 用"是否全开"而不是"是否有任一项关闭"来判断能不能跳过 —— 后者写反过一次,
        //   表现是默认状态下弹幕全没了。
        var typeFilter = !(options.Scroll && options.Fixed && options.Colorful && options.Advanced);

        // 三件事都没开就原样返回, 省掉一次完整遍历
        if (!smart && rules == null && !typeFilter) return list;
        return FilterCore(list, smart, rules, typeFilter,
            options.Scroll, options.Fixed, options.Colorful, options.Advanced, cancellation);

        static IEnumerable<RawDanmaku> FilterCore(
            IEnumerable<RawDanmaku> src,
            bool smart,
            DanmakuKeywordFilter? rules,
            bool typeFilter,
            bool allowScroll,
            bool allowFixed,
            bool allowColorful,
            bool allowAdvanced, CancellationToken cancellation)
        {
            var seen = new Dictionary<string, int>();
            foreach (var d in src)
            {
                cancellation.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(d.Text)) continue;

                // 类型过滤: 用户没勾的类型直接丢弃(与智能屏蔽/关键词都解耦)
                if (typeFilter && !IsTypeAllowed(d, allowScroll, allowFixed, allowColorful, allowAdvanced))
                    continue;

                // 关键词/正则黑名单: 命中即丢弃(与智能屏蔽解耦)
                if (rules != null && rules.IsBlocked(d.Text)) continue;

                if (smart)
                {
                    // 超长弹幕(>30 字)会占满一整条泳道, 智能屏蔽下直接丢弃
                    if (d.Text.Length > 30) continue;
                    // 相同文本刷屏: 最多保留 4 条, 再多就是复读机
                    seen.TryGetValue(d.Text, out var n);
                    if (n >= 4) continue;
                    seen[d.Text] = n + 1;
                }
                yield return d;
            }
        }
    }

    /// <summary>
    /// 这条弹幕是否在用户勾选的类型里。
    ///
    /// 类型划分(与 B 站客户端的分类口径一致):
    ///   · 滚动 = mode 1/2/3/6(6 在解析时已被归一成 1)
    ///   · 固定 = mode 4(底部)/5(顶部)
    ///   · 高级 = mode 7(高级)/8(代码)/9(BAS) —— 这类会自己画图形/动画, 和普通弹幕不同源
    ///   · 彩色 = 颜色非白(0xFFFFFF)。它与"彩色弹幕"显示开关是两件事:
    ///     那个决定"彩色要不要按原色画", 这个决定"彩色要不要出现"。
    ///
    /// ★ 四个维度是**或**的关系: 只要命中的类型里有任意一项被允许就放行。
    ///   一条弹幕可能同时是"滚动 + 彩色", 关掉滚动但留着彩色时它应该还在。
    /// </summary>
    private static bool IsTypeAllowed(RawDanmaku d,
        bool allowScroll, bool allowFixed, bool allowColorful, bool allowAdvanced)
    {
        var colorful = d.Color != 0xFFFFFF && d.Color != 0;

        // 滚动类(1/2/3)与高级类(7/8/9)之外的 mode 都算固定(4/5), 未知 mode 也归固定 ——
        // 宁可多显示一条, 也别让接口新增的 mode 变成"凭空消失的弹幕"
        switch (d.Mode)
        {
            case 1: case 2: case 3: return allowScroll || (colorful && allowColorful);
            case 7: case 8: case 9: return allowAdvanced || (colorful && allowColorful);
            default: return allowFixed || (colorful && allowColorful);
        }
    }

    private void DispatchDanmaku()
    {
        if (!_danmakuOn || _closing || _mp == null || _mp.Length <= 0) return;
        var currentSeconds = _mp.Time / 1000.0;
        // ★ 校准弹幕时钟: 逐帧推进只读本地 Stopwatch, 靠这里(约 4Hz)把它拉回真实媒体时间。
        //   拖进度条/seek 之后, 下一个 TimeChanged 就会纠偏, 弹幕不会继续按旧位置飞。
        CalibrateDanmakuClock(currentSeconds);

        // 游标与播放位置必须同步, 否则下面的 while 会在一帧里把跨越的整段弹幕全发出来(刷屏)。
        // 往回跳、往前跳、以及"弹幕加载完成时播放已经过去一段时间"这三种失配都在这里收敛,
        // 判据见 IsDanmakuCursorStale 的说明。边界值用 ±∞ 兜住"还没有上一条 / 已经没有下一条"。
        var lastSentTime = _indexInLib > 0 ? _danmaku[_indexInLib - 1].Time : double.NegativeInfinity;
        var nextTime = _indexInLib < _danmaku.Count ? _danmaku[_indexInLib].Time : double.PositiveInfinity;
        if (IsDanmakuCursorStale(currentSeconds, lastSentTime, nextTime))
        {
            _indexInLib = LowerBound(_danmaku, currentSeconds);
            ResetLanes();
        }

        // Bound the work a dense timestamp can put on the UI thread. Retain the full
        // timeline for seeking, but never replay an old burst over later video frames.
        var dispatchStart = Stopwatch.GetTimestamp();
        var dispatched = 0;
        while (_indexInLib < _danmaku.Count && _danmaku[_indexInLib].Time <= currentSeconds)
        {
            var item = _danmaku[_indexInLib];
            AddDanmaku(item.Text, item.Mode, item.Time, item.Color);
            _indexInLib++;
            if (++dispatched >= 128 || Stopwatch.GetElapsedTime(dispatchStart).TotalMilliseconds >= 2)
            {
                _indexInLib = LowerBound(_danmaku, Math.BitIncrement(currentSeconds));
                break;
            }
        }
    }

    /// <summary>二分查找第一条 time >= 目标时间的弹幕下标</summary>
    private static int LowerBound(List<RawDanmaku> list, double target)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (list[mid].Time < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>画布尺寸变化或跳转后重置泳道占用状态</summary>
    private void ResetLanes()
    {
        _topSlots.Clear();
        _bottomSlots.Clear();
        _laneFreeAt = null;      // 下次渲染时按新尺寸重建
        _laneLastWidth = null;   // 与 _laneFreeAt 同生共死, 避免留下长度不匹配的残影数据
        _lastLaneCalcWidth = 0;
        _lastLaneCalcHeight = 0;
        // ★ 在飞的弹幕一并收掉(2026-10-11): 跳转之后旧的弹幕必须立刻消失, 否则它们的
        //   SpawnClock 还停在跳转前的位置, elapsed 会变成负数/极大值 —— 前者被钳成 0
        //   (满屏弹幕突然全挤在右边缘), 后者直接被判定为"已飞完"集体闪没。
        //   收掉之后 DispatchDanmaku 会按新位置重新派发, 这才是"跳过去就该看到那边的弹幕"。
        if (_flying.Count > 0)
        {
            foreach (var f in _flying) DanmakuCanvas?.Children.Remove(f.Element);
            _flying.Clear();
        }
    }

    /// <summary>
    /// 按当前画布尺寸重建泳道数组。
    /// 泳道数 = 显示区域高度 / 单条弹幕高度。字号固定 18px, 加上上下 padding
    /// 约 28px 一条, 所以一条 1080P 视频的 25% 区域大约能容纳 8~10 条泳道。
    /// </summary>
    private void EnsureLanes()
    {
        var w = DanmakuCanvas.ActualWidth;
        var h = DanmakuCanvas.ActualHeight;
        if (h <= 0) return;

        // 尺寸没变则复用现有泳道, 保留占用状态(避免每帧重建导致弹幕重叠)
        if (_laneFreeAt != null && Math.Abs(w - _lastLaneCalcWidth) < 1 &&
            Math.Abs(h - _lastLaneCalcHeight) < 1)
            return;

        var areaHeight = h * _danmakuAreaRatio;
        var laneCount = Math.Max(1, (int)(areaHeight / _laneHeight));

        _laneFreeAt = new double[laneCount];
        _laneLastWidth = new double[laneCount];
        _lastLaneCalcWidth = w;
        _lastLaneCalcHeight = h;

        // 新泳道全部立即可用
        for (var i = 0; i < laneCount; i++) _laneFreeAt[i] = 0;
    }

    /// <summary>
    /// 计算一条弹幕应该落在哪条泳道。
    ///
    /// 判定逻辑(参考主流弹幕实现):
    ///   1. 优先选"当前时间点上, 上一条弹幕已经完全离开画面右侧"的泳道;
    ///   2. 否则选"上一条弹幕虽然还在屏幕上, 但按速度算不会被后一条追上"的泳道;
    ///   3. 都不满足则返回 -1, 表示这条弹幕暂时没有位置 —— 直接丢弃(不叠在一起)。
    ///
    /// 追尾校验: 前一条弹幕在屏幕上占据 [x1, x1+w1], 后一条从右边缘 x=W 出发。
    /// 两者速度相同(都在 DanmakuScrollSeconds 内走完 W+wi), 所以只要保证
    /// 后一条出发时前一条的右边缘已经离开足够距离, 就永远不会追上。
    /// </summary>
    private int AllocateRollingLane(double width, double currentSeconds)
    {
        EnsureLanes();
        if (_laneFreeAt == null || _laneFreeAt.Length == 0) return -1;

        var canvasWidth = DanmakuCanvas.ActualWidth;
        if (canvasWidth <= 0) return -1;

        // 弹幕从左(或右)走到完全离开所需时间 = _danmakuScrollSeconds(随速度档位变)
        // 速度(像素/秒) = (canvasWidth + width) / _danmakuScrollSeconds
        var speed = (canvasWidth + width) / _danmakuScrollSeconds;

        var bestLane = -1;
        var bestFreeAt = double.MaxValue;

        for (var i = 0; i < _laneFreeAt.Length; i++)
        {
            var freeAt = _laneFreeAt[i];

            if (freeAt <= currentSeconds)
            {
                // 该泳道在"当前时刻"已经是空的 —— 立刻用它, 后面不用再看了。
                // 注意: 这里**不能**加 "freeAt >= bestFreeAt 就 continue" 这类提前剪枝:
                // 空泳道之间无从比较优劣(都是空的), 剪枝只会让本该复用的泳道被判成不可用,
                // 结果是弹幕被无谓丢弃(表现为"弹幕变稀了")。
                bestLane = i;
                bestFreeAt = freeAt;
                break;
            }

            // 泳道仍在占用中: 算一下前一条弹幕此刻的左边缘是否已经滚出足够距离。
            // 只有"当前候选中 freeAt 最早(最先腾空)"的泳道才值得继续评估,
            // 这样一趟循环就能挑出"最该复用的那条"。
            if (freeAt >= bestFreeAt) continue;

            // remaining: 前一条还要占住右侧入口多久
            var remaining = freeAt - currentSeconds;
            // 前一条弹幕此刻的左边缘位置(近似: 按自身速度反推)
            var prevWidth = _laneLastWidth != null && i < _laneLastWidth.Length ? _laneLastWidth[i] : width;
            var prevSpeed = (canvasWidth + prevWidth) / _danmakuScrollSeconds;
            var prevElapsed = _danmakuScrollSeconds - remaining;
            var prevLeft = canvasWidth - prevElapsed * prevSpeed;
            // 后一条从 canvasWidth 出发; 只要前一条的右边缘(prevLeft + prevWidth) 与
            // 后一条的左边缘(canvasWidth) 之间留有安全间距, 就不会视觉重叠
            if (prevLeft + prevWidth + 12 <= canvasWidth)
            {
                bestLane = i;
                bestFreeAt = freeAt;
            }
        }

        return bestLane;
    }

    /// <summary>分配顶部/底部固定弹幕的位置(轮转, 避免连续多条压在同一行)</summary>
    private int AllocateFixedSlot(List<(double until, int slot)> table, double currentSeconds, int slotCount)
    {
        // 清掉已过期的占用
        table.RemoveAll(x => x.until <= currentSeconds);

        for (var s = 0; s < slotCount; s++)
        {
            if (table.All(x => x.slot != s))
            {
                table.Add((currentSeconds + _danmakuFixedSeconds, s));
                return s;
            }
        }
        return -1; // 顶/底位置全满, 丢弃
    }

    /// <summary>
    /// 渲染一条弹幕。
    /// mode: 1/2/3 滚动(从右往左), 4 底部固定, 5 顶部固定。
    /// 其余少见模式(BAS/代码弹幕)统一按滚动处理, 避免出现空白占位。
    ///
    /// 性能上两条铁律(违反任何一条, 高弹幕量视频就会掉帧):
    ///   1. **不挂 Effect**: 每个带 DropShadowEffect 的元素都要独立做一次位图合成,
    ///      几十条弹幕同屏就是几十次全像素处理。★ 曾经靠"半透明黑底衬"给描边感,
    ///      现已按用户要求去掉(见下面那段的说明) —— 但**仍然不许挂 Effect**;
    ///   2. **动画只动 RenderTransform**: 动 Canvas.Left 每帧都要走一遍 Canvas 布局,
    ///      动 TranslateTransform 只发生在合成线程的变换矩阵上, 完全不碰布局。
    /// </summary>
    private void AddDanmaku(string text, int mode, double time, uint color = 0xFFFFFF)
    {
        if (DanmakuCanvas == null || string.IsNullOrEmpty(text)) return;
        if (DanmakuCanvas.ActualWidth <= 0 || DanmakuCanvas.ActualHeight <= 0) return;
        // 屏幕弹幕上限, 防止高弹幕量视频把内存和渲染拖垮
        if (DanmakuCanvas.Children.Count >= MaxOnScreenDanmaku) return;

        // ★ 用弹幕时钟(已被 DispatchDanmaku 校准过), 不再读 `_mp.Time`:
        //   这里虽是低频路径, 但它与逐帧推进必须是**同一个时间源**, 否则新弹幕的出生点
        //   和在飞弹幕算 elapsed 的基准会差一次 Stopwatch 走时, 表现为"新弹幕进场位置偏一点"。
        var currentSeconds = DanmakuClock();
        if (currentSeconds <= 0) currentSeconds = time;   // 还没起播(极少见): 退回这条弹幕自带的时间

        // Reject full lanes before constructing/measuring visual objects. The old path
        // allocated and measured thousands of elements that were immediately removed.
        var lane = -1;
        if (mode is 4 or 5)
        {
            var slots = Math.Max(1, (int)(DanmakuCanvas.ActualHeight * _danmakuAreaRatio / _laneHeight));
            var table = mode == 5 ? _topSlots : _bottomSlots;
            if (table.Count(x => x.until > currentSeconds) >= slots) return;
        }
        else
        {
            lane = AllocateRollingLane(1, currentSeconds);
            if (lane < 0) return;
        }

        // ★★★ 只留文字, 不要长方形底衬(2026-10-07 用户要求"弹幕文字有长方形背景阴影, 移除它")。
        //   原先这里给了 Background = 60% 黑 + CornerRadius=3, 屏幕上每条弹幕都压着一块
        //   深色小方块 —— 用户看到的就是"文字后面有个长方形阴影"。
        //   ⇒ Background 留 null(完全不画底), CornerRadius 随之无意义、一并去掉。
        //
        //   ★ Padding **保留**: 它不是装饰, 而是参与 width 计算(见下面的 Measure),
        //     泳道分配与"追尾校验"都拿这个宽度当"这条弹幕要占多宽"。
        //     去掉它会让相邻弹幕的横向间距归零, 密集时文字首尾相接糊成一片。
        //   ★ 不要为了"看不清"改回加底衬或挂 DropShadowEffect: 前者就是用户要去掉的东西,
        //     后者违反上面第 1 条铁律(几十条同屏 = 几十次全像素合成)。
        var border = new Border
        {
            Padding = new Thickness(6, 1, 6, 1),
            // ★ 基线不透明度: 压在亮画面上的白字太实反而晃眼, 这里给用户一个可调的档。
            //   滚动弹幕此后不会再被改 Opacity, 所以这里设一次就够;
            //   固定弹幕的淡出是"在这个基线上再乘一个系数"(见 OnDanmakuRendering)。
            Opacity = _danmakuOpacity,
            // RenderTransform 在这里预置好, 滚动弹幕直接拿它做位移动画
            RenderTransform = new TranslateTransform(0, 0),
            Child = CreateDanmakuTextElement(text, DanmakuBrush(color), _danmakuFontSize, _danmakuOutline)
        };

        DanmakuCanvas.Children.Add(border);
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(1, border.DesiredSize.Width);

        if (mode == 5 || mode == 4)
        {
            RenderFixed(border, mode, currentSeconds);
        }
        else
        {
            RenderRolling(border, width, currentSeconds, lane);
        }
    }

    // ------------------------------------------------------------ 彩色弹幕

    /// <summary>白色笔刷只建一次(绝大多数弹幕都是白的, 不必每条都 new)</summary>
    private static readonly Brush DanmakuWhiteBrush = MakeFrozen(Colors.White);

    /// <summary>
    /// 颜色 → 笔刷的缓存。
    ///
    /// 为什么必须缓存: 一个热门视频几千条弹幕, 每条都 new SolidColorBrush 会产生几千个
    /// 笔刷对象(还都要走一遍 Freeze), 而实际用到的颜色通常只有几十种 ——
    /// 命中缓存的成本接近零, 也顺带避免了大量小对象给 GC 添麻烦。
    /// 用普通 Dictionary 即可: 只在 UI 线程读写, 且颜色种类天然有限。
    /// </summary>
    private readonly Dictionary<uint, Brush> _danmakuBrushes = new();

    /// <summary>
    /// 取弹幕文字色。
    /// 0xRRGGBB 是接口原样给的十进制颜色(16777215 = 白) —— 白就是默认色,
    /// 直接复用共享笔刷; 非白即"彩色弹幕", 按值上色。
    /// 关掉"彩色弹幕"设置时一律白: 有些视频满屏五颜六色反而看不清内容。
    /// </summary>
    private Brush DanmakuBrush(uint color)
    {
        if (!_danmakuColorful || color == 0xFFFFFF || color == 0) return DanmakuWhiteBrush;

        if (_danmakuBrushes.TryGetValue(color, out var cached)) return cached;

        var brush = MakeFrozen(Color.FromRgb(
            (byte)((color >> 16) & 0xFF),
            (byte)((color >> 8) & 0xFF),
            (byte)(color & 0xFF)));
        _danmakuBrushes[color] = brush;
        return brush;
    }

    private static FrameworkElement CreateDanmakuTextElement(string text, Brush foreground, double fontSize, bool outline)
        => outline
            ? new OutlinedDanmakuText(text, foreground, fontSize)
            : new TextBlock { Text = text, Foreground = foreground, FontSize = fontSize };

    private void ApplyDanmakuOutlineToFlying()
    {
        foreach (var flying in _flying)
        {
            if (flying.Element is not Border border) continue;
            var replaced = false;
            switch (border.Child)
            {
                case TextBlock tb when _danmakuOutline:
                    border.Child = new OutlinedDanmakuText(tb.Text, tb.Foreground, tb.FontSize);
                    replaced = true;
                    break;
                case OutlinedDanmakuText ot when !_danmakuOutline:
                    border.Child = new TextBlock { Text = ot.Text, Foreground = ot.Foreground, FontSize = ot.FontSize };
                    replaced = true;
                    break;
            }
            if (replaced)
            {
                border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                flying.Width = Math.Max(1, border.DesiredSize.Width);
            }
        }
    }

    private static Brush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// 滚动弹幕: 分泳道 + 横向滚动。
    ///
    /// ★ 这里**不起任何 WPF 动画**, 只把这条弹幕登记进 <see cref="_flying"/>,
    ///   位置由 <see cref="OnDanmakuRendering"/> 按视频时钟每帧算。理由见 _flying 的说明。
    /// </summary>
    private void RenderRolling(Border border, double width, double currentSeconds, int lane)
    {
        if (lane < 0)
        {
            // 没有可用泳道 —— 宁可丢弃也不叠在一起(这正是"大量重叠"的根治点)
            DanmakuCanvas.Children.Remove(border);
            return;
        }

        // 记录该泳道的占用信息, 供后续追尾校验使用
        _laneFreeAt![lane] = currentSeconds + _danmakuScrollSeconds;
        if (_laneLastWidth != null) _laneLastWidth[lane] = width;

        Canvas.SetTop(border, 6 + lane * _laneHeight);

        var canvasWidth = DanmakuCanvas.ActualWidth;
        // 预置到"刚好在画面右侧外", 这是新弹幕的入场位置（RollingX 在 elapsed=0 时
        // 同样返回 canvasWidth）。★ 别因为怕"窗口不可见时弹幕停在这里看不见"就把主时钟
        // 降级成 16ms DispatcherTimer —— 0.1.6 正是这么修的，结果浮层帧率被钉在 ~33fps
        // （见 docs/FPS-REGRESSION-2026-11-09.md）。正解是主时钟用 CompositionTarget.Rendering
        // 跟 DWM 合成走，再加一条低频看门狗：合成停摆时补一次推进，弹幕立刻跳到
        // DanmakuClock() 外推出的正确位置。弹幕时钟基于墙钟外推，停多久都不会丢进度。
        var translate = new TranslateTransform(
            Helpers.DanmakuMotion.RollingX(canvasWidth, width, 0, _danmakuScrollSeconds), 0);
        border.RenderTransform = translate;

        TrackFlying(new FlyingDanmaku
        {
            Element = border,
            Translate = translate,
            SpawnClock = currentSeconds,
            TotalSeconds = _danmakuScrollSeconds,
            Width = width,
        });
    }

    /// <summary>顶部/底部固定弹幕: 居中停留数秒后淡出(透明度同样由视频时钟算, 不用动画)</summary>
    private void RenderFixed(Border border, int mode, double currentSeconds)
    {
        var canvasWidth = DanmakuCanvas.ActualWidth;
        var canvasHeight = DanmakuCanvas.ActualHeight * _danmakuAreaRatio;

        var slotCount = Math.Max(1, (int)(canvasHeight / _laneHeight));
        var table = mode == 5 ? _topSlots : _bottomSlots;
        var slot = AllocateFixedSlot(table, currentSeconds, slotCount);
        if (slot < 0)
        {
            DanmakuCanvas.Children.Remove(border);
            return;
        }

        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = border.DesiredSize.Width;
        Canvas.SetLeft(border, Math.Max(0, (canvasWidth - width) / 2));

        // 顶部从上往下排, 底部从下往上排
        var top = mode == 5
            ? 6 + slot * _laneHeight
            : Math.Max(6, canvasHeight - 6 - (slot + 1) * _laneHeight);
        Canvas.SetTop(border, top);

        // 横向不需要位移, 但 TranslateTransform 仍要挂一个: 逐帧代码按同一套逻辑处理两种弹幕
        var translate = new TranslateTransform(0, 0);
        border.RenderTransform = translate;
        border.Opacity = _danmakuOpacity;

        TrackFlying(new FlyingDanmaku
        {
            Element = border,
            Translate = translate,
            SpawnClock = currentSeconds,
            TotalSeconds = _danmakuFixedSeconds,
            IsFixed = true,
        });
    }

    /// <summary>登记一条在飞的弹幕, 并确保逐帧推进器已经挂上</summary>
    private void TrackFlying(FlyingDanmaku f)
    {
        _flying.Add(f);
        StartDanmakuClock();
    }

    /// <summary>
    /// 挂上逐帧推进器。★ 主时钟必须跟 DWM 合成节奏（CompositionTarget.Rendering），
    /// 不能用 16ms DispatcherTimer —— 后者受系统定时器粒度限制实际落在 ~30ms 一跳，
    /// 会把整个视频浮层窗口的呈现帧率钉在 ~33fps（实测 36.8 上屏帧/秒 vs desk 的 83.4）。
    /// 见 docs/FPS-REGRESSION-2026-11-09.md。
    ///
    /// 0.1.6 为修"窗口不可见时 Rendering 停摆导致弹幕停在右缘外"改成了定时器，
    /// 那属于修错了层：正解是 ① 弹幕初始位置直接按时钟算（见 RenderRolling）
    /// ② 加一条低频看门狗，只在合成真的停摆时补一次推进，而不是拿它当主时钟。
    /// CompositionTarget.Rendering 是**静态**事件，必须在关窗时解绑。
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _danmakuWatchdog;
    private long _lastDanmakuRenderTicks;
    private TimeSpan _lastDanmakuRenderingTime = TimeSpan.MinValue;
    private void StartDanmakuClock()
    {
        if (_danmakuClockAttached || _closing) return;
        _danmakuClockAttached = true;
        _lastDanmakuRenderTicks = Stopwatch.GetTimestamp();
        _lastDanmakuRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnDanmakuRendering;
        StartDanmakuWatchdog();
    }

    private void StopDanmakuClock()
    {
        if (!_danmakuClockAttached) return;
        _danmakuClockAttached = false;
        CompositionTarget.Rendering -= OnDanmakuRendering;
        StopDanmakuWatchdog();
    }

    /// <summary>低频兜底：合成回调停摆（窗口被遮挡/最小化/DWM 暂停）时补一次推进。</summary>
    private void StartDanmakuWatchdog()
    {
        _danmakuWatchdog ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(80), System.Windows.Threading.DispatcherPriority.Background,
            OnDanmakuWatchdogTick, Dispatcher);
        _danmakuWatchdog.Start();
    }

    private void StopDanmakuWatchdog() => _danmakuWatchdog?.Stop();

    private void OnDanmakuWatchdogTick(object? sender, EventArgs e)
    {
        if (!_danmakuClockAttached || _closing || _flying.Count == 0) return;
        // Rendering 正常跑时这里几乎零成本：距上次回调还不到 65ms 就直接返回。
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastDanmakuRenderTicks, now).TotalMilliseconds < 65) return;
        OnDanmakuRendering(sender, e);
    }

    /// <summary>
    /// 逐帧推进所有在飞的弹幕。
    ///
    /// ★ 时间的唯一来源是 _mp.Time(真实媒体时间, 毫秒精度), 不是墙钟。
    ///   于是"视频暂停"这件事**根本不需要分支**: 媒体时间不前进 ⇒ elapsed 不变 ⇒ 位置不变。
    ///   这正是原来用 DoubleAnimation 做不到的地方(那个走合成线程墙钟, 跟播放器无关)。
    ///
    /// 反过来在播放时它还顺带修好了另一件事: 弹幕现在与画面**帧同步**,
    /// 拖进度条后 DispatchDanmaku 检测到游标失效会 ResetLanes(连带清空 _flying),
    /// 所以不会看到"老弹幕从旧位置继续飞"。
    /// </summary>
    private void OnDanmakuRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (_lastDanmakuRenderingTime == frame.RenderingTime) return;
            _lastDanmakuRenderingTime = frame.RenderingTime;
        }
        _lastDanmakuRenderTicks = Stopwatch.GetTimestamp();
        if (_flying.Count == 0)
        {
            // 一条都没在飞就没必要继续挂在渲染回调上(它每帧都会被调, 空转也是成本)
            StopDanmakuClock();
            return;
        }
        if (_closing) return;

        // ★ 只读一次本地 Stopwatch, **一次原生调用都不做**(见 _dmWatch 那段说明)
        var now = DanmakuClock();
        var canvasWidth = DanmakuCanvas?.ActualWidth ?? 0;
        // 倒序删: 末尾先走完, 正好与"新弹幕追加在尾部"的写入方向相反, 不会出现漏删
        for (var i = _flying.Count - 1; i >= 0; i--)
        {
            var f = _flying[i];
            var elapsed = now - f.SpawnClock;
            if (elapsed < 0) elapsed = 0;   // 刚上屏/刚重建泳道时的取整误差, 不让它闪到画面外

            if (f.IsFixed)
            {
                // ★★★ 必须**乘**基线不透明度, 不能直接赋值(2026-10-12 加不透明度时踩到):
                //   原来这一行是把 Opacity 直接设成淡出系数(0~1)。用户一旦把"不透明度"调到
                //   80%, 固定弹幕到了淡出阶段就会被这一行**覆盖回 100%** —— 表现为
                //   "滑杆拖了没用, 只有滚动弹幕变淡, 顶/底的始终全不透明"。
                //   现在 Opacity = 用户基线 × 淡出系数, 两个设置才真正叠加。
                f.Element.Opacity = _danmakuOpacity
                    * Helpers.DanmakuMotion.FixedOpacity(elapsed, f.TotalSeconds);
            }
            else
            {
                f.Translate.X = Helpers.DanmakuMotion.RollingX(canvasWidth, f.Width, elapsed, f.TotalSeconds);
            }

            if (Helpers.DanmakuMotion.IsFinished(elapsed, f.TotalSeconds))
            {
                DanmakuCanvas?.Children.Remove(f.Element);
                _flying.RemoveAt(i);
            }
        }
    }

    /// <summary>切换弹幕开关(控制栏按钮 / 快捷键 D), 并同步到全局设置</summary>
    private void ToggleDanmaku()
    {
        if (!Svc.Settings.HasVisibleDanmakuTypes) { Svc.Settings.RestoreDanmakuTypes(); Svc.Toast.Show("已恢复显示全部弹幕类型"); return; }
        _danmakuOn = !_danmakuOn;
        Svc.Settings.UpdateDanmakuSettings(_danmakuOn, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter);
        Svc.Toast.Show(_danmakuOn ? "弹幕已开启" : "弹幕已关闭");
        if (!_danmakuOn)
        {
            DanmakuCanvas.Children.Clear();
            ResetLanes();
        }
        UpdateDanmakuButton();
    }

    /// <summary>控制栏上的弹幕开关按钮</summary>
    private void OnDanmakuToggleClick(object sender, RoutedEventArgs e) => ToggleDanmaku();
    private void OnRestoreDanmakuTypesClick(object sender, RoutedEventArgs e)
    {
        Svc.Settings.RestoreDanmakuTypes();
        Svc.Toast.Show("已恢复显示全部弹幕类型");
    }

    /// <summary>
    /// 刷新弹幕按钮的文案与可用状态。
    ///
    /// 直播和本地文件没有弹幕可放(前者不加载弹幕, 后者没有弹幕文件), 按钮置灰 ——
    /// 留一个按了没反应的按钮比没有按钮更糟。
    /// 注意这里给 TextBlock 设的是**局部值** Foreground: 全局那个隐式 TextBlock 样式会压过继承值,
    /// 所以"关"态的灰字必须显式写在元素上(见 MEMORY 里那条隐式样式的坑)。
    /// </summary>
    private void UpdateDanmakuButton()
    {
        if (DanmakuGlyph == null || BtnDanmaku == null) return;

        var usable = !_isLive && !_isLocalPlayback;
        BtnDanmaku.IsEnabled = usable;
        BtnDanmaku.Opacity = usable ? 1.0 : 0.4;
        if (DanmakuComposer != null) DanmakuComposer.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;

        var blockedByTypes = _danmakuOn && !Svc.Settings.HasVisibleDanmakuTypes;
        DanmakuOffSlash.Visibility = _danmakuOn && !blockedByTypes ? Visibility.Collapsed : Visibility.Visible;
        BtnDanmaku.ToolTip = blockedByTypes ? "全部弹幕类型已关闭，点击恢复显示" : _danmakuOn ? "关闭弹幕 (D)" : "开启弹幕 (D)";
        System.Windows.Automation.AutomationProperties.SetName(BtnDanmaku, blockedByTypes ? "恢复弹幕类型" : _danmakuOn ? "关闭弹幕" : "开启弹幕");
        // 局部值直接给刷子, 不引资源: 控制栏压在画面上, 颜色要跟主题无关(永远是白系)
        DanmakuGlyph.Stroke = new SolidColorBrush(_danmakuOn && !blockedByTypes
            ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)   // 开: 接近纯白
            : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)); // 关: 压暗, 一眼看出状态
    }

    /// <summary>全局弹幕设置变化(来自设置页): 同步播放器运行时状态</summary>
    private void OnDanmakuSettingsChanged()
    {
        if (_closing) return;
        var wasEnabled = _danmakuOn;
        var previousColorful = _danmakuColorful;
        var previousOutline = _danmakuOutline;
        var previousArea = _danmakuAreaRatio;
        _danmakuOn = Svc.Settings.DanmakuEnabled;
        // 彩色弹幕开关: 它不影响"哪些弹幕该显示", 只影响颜色 —— 但已上屏的那些是带着
        // 旧颜色的, 所以下面照样要走一遍 RefilterDanmaku 把画布清掉重画。
        _danmakuColorful = Svc.Settings.DanmakuColorful;
        _danmakuOutline = Svc.Settings.DanmakuOutline;
        UpdateDanmakuButton();
        _danmakuAreaRatio = Svc.Settings.DanmakuAreaPercent / 100.0;
        if (DanmakuAreaSlider != null) DanmakuAreaSlider.Value = Svc.Settings.DanmakuAreaPercent;
        if (DanmakuAreaValue != null) DanmakuAreaValue.Text = Svc.Settings.DanmakuAreaPercent + "%";

        // 外观三项(不透明度/字号/速度)与描边开关
        ApplyDanmakuLook();
        UpdateDanmakuLookTexts();
        if (previousOutline != _danmakuOutline && _danmakuOn) ApplyDanmakuOutlineToFlying();

        // 区域占比变了 => 可用泳道数变了, 必须重建泳道, 否则弹幕会溢出到区域外
        if (wasEnabled != _danmakuOn || previousColorful != _danmakuColorful ||
            Math.Abs(previousArea - _danmakuAreaRatio) > .001) ResetLanes();
        if (!_danmakuOn)
        {
            DanmakuCanvas.Children.Clear();
            return;
        }
        // 智能屏蔽 / 关键词屏蔽都可能刚被改过: 用新规则重算一遍已加载的弹幕,
        // 让用户在设置页边打字边看到播放器里的即时效果。
        RefilterDanmaku();
    }

    /// <summary>
    /// 把「不透明度 / 字号 / 速度」三项设置落到运行时, 并只做**必要**的重算。
    ///
    /// ★ 为什么不一律"改了就清屏重建":
    ///   · 不透明度: 就地改每个在飞弹幕的 Opacity 即可。不清屏 ——
    ///     用户是**拖着滑杆**在调的, 每动一格就闪一次是不能接受的。
    ///   · 字号/速度: 会让泳道表与追尾校验失效, 必须重建并清掉旧弹幕,
    ///     否则要么弹幕叠在一起, 要么新弹幕被旧判定整批丢弃(看起来就是"弹幕没了")。
    ///
    /// 三项都没变时直接返回 —— 设置页里改"关键词屏蔽"也会走到这里, 不该白清一次屏。
    /// </summary>
    private void OnPlayerFontChanged()
    {
        if(_closing)return;
        ResetLanes();DanmakuCanvas.Children.Clear();_flying.Clear();
        if(_danmakuOn)StartDanmakuClock();
    }

    internal static double DanmakuViewportScale(double width) => width <= 0 ? 1 : Math.Clamp(Math.Sqrt(width / 1000d), .58, 1);
    private void OnDanmakuViewportChanged(object sender, SizeChangedEventArgs e) { if (_mp != null && !_closing) ApplyDanmakuLook(); }
    private void ApplyDanmakuLook()
    {
        var s = Svc.Settings;
        var nextOpacity = Helpers.DanmakuMotion.OpacityFromPercent(s.DanmakuOpacityPercent);
        var nextFont = Helpers.DanmakuMotion.FontSizeFromPercent(s.DanmakuFontScalePercent, DanmakuBaseFontSize) * DanmakuViewportScale(DanmakuCanvas.ActualWidth);
        var nextLevel = Math.Clamp(s.DanmakuSpeedLevel,
            Helpers.DanmakuMotion.MinSpeedLevel, Helpers.DanmakuMotion.MaxSpeedLevel);

        var opacityChanged = Math.Abs(nextOpacity - _danmakuOpacity) > 0.001;
        var fontChanged = Math.Abs(nextFont - _danmakuFontSize) > 0.01;
        var speedChanged = nextLevel != _danmakuSpeedLevel;

        if (!opacityChanged && !fontChanged && !speedChanged) return;

        _danmakuOpacity = nextOpacity;
        _danmakuFontSize = nextFont;
        _danmakuSpeedLevel = nextLevel;
        _danmakuScrollSeconds = Helpers.DanmakuMotion.ScrollSeconds(nextLevel);
        _danmakuFixedSeconds = Helpers.DanmakuMotion.FixedSeconds(nextLevel);

        // 字号变了必须同步泳道高 —— 这是历史 bug 的根(DanmakuLaneRatio 那条注释)
        _laneHeight = _danmakuFontSize * DanmakuLaneRatio;

        // 不透明度不需要清屏: 基线字段改完, 在飞的弹幕在下一帧自然按新基线渲染
        // (固定弹幕的 Opacity 每帧被"基线 × 淡出系数"覆写, 所以只改基线就够)。
        if (opacityChanged && !fontChanged && !speedChanged)
            foreach (var flying in _flying)
                if (!flying.IsFixed) flying.Element.Opacity = _danmakuOpacity;

        if (fontChanged || speedChanged)
        {
            // 布局变了, 在飞的那些按旧尺寸/旧速度继续飞会突然错位, 一并清掉
            ResetLanes();
            DanmakuCanvas.Children.Clear();
            _flying.Clear();
            if (_danmakuOn) StartDanmakuClock();
        }
    }

}
