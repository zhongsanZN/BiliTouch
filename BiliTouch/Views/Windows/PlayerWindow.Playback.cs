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
    // ------------------------------------------------------------ 简介折叠 + 相关视频(2026-10-12)

    /// <summary>点相关视频卡片: 换成那个视频</summary>
    public RelayCommand PlayRelatedCommand { get; }

    /// <summary>
    /// 简介是否处于"展开"态。
    ///
    /// ★★★ 2026-11-04 用户要求: **默认展开**。
    ///   原来是默认折叠(为了给下面的相关视频腾位置), 但这样一来:
    ///     · 用户进详情页第一眼看到的是被截断的简介, 还得点一下"展开简介";
    ///     · 而合集现在被挪到了相关视频**之上**(见 XAML 里那段说明), 简介下方不再紧贴
    ///       一长串推荐 —— "腾位置"这个理由本身已经不成立了。
    ///   所以默认 true; 想看推荐就自己收起, 想看全文不用再点。
    /// </summary>
    private bool _descExpanded = true;

    /// <summary>相关视频的加载代次: 换片后作废上一片的在飞请求</summary>
    private int _relatedGen;

    /// <summary>简介折叠后的最大高度(与 XAML 里 DescCollapsedHeight 同一个数)</summary>
    private const double DescCollapsedHeight = 108;

    /// <summary>
    /// 「展开简介」按钮。
    ///
    /// ★★ 判据必须是"文本**不限高**时需要多高", 所以 Measure 之前**必须先把 MaxHeight 摘掉**:
    ///   WPF 的 Measure 会遵守 MaxHeight —— 折叠态下 MaxHeight 就是 108,
    ///   于是 DesiredSize.Height 拿回来恒等于 108, 与阈值比较永远不成立
    ///   ⇒ 按钮**永远不出现**(实测: 40 行简介 + 展开按钮 Collapsed + DesiredHeight=108)。
    ///   这个坑很隐蔽: 逻辑读起来完全合理, 只有真跑一次界面才暴露。
    /// ★ 所以下面那三步的顺序不能换: 备份 → 摘掉 → 量 → 装回。
    /// ★ 另一个容易写错的替代品是读 ActualHeight: 折叠态下它也被 MaxHeight 夹住,
    ///   同样永远判不出"超没超"。必须用无上限的那次测量。
    /// ★ 宽度未知(窗口还没布局)时直接隐藏按钮: 那时的 0 宽度会算出 0 高度,
    ///   判成"不溢出"就对了, 但纯属巧合, 所以这里显式处理而不是让它自然走完。
    /// </summary>
    private void UpdateDescFold()
    {
        if (DescText == null || BtnDescFold == null) return;

        var width = DescText.ActualWidth;
        if (width <= 1)
        {
            // 还没量到宽度: 先不显示。等布局跑完由 SizeChanged 再调一次。
            BtnDescFold.Visibility = Visibility.Collapsed;
            return;
        }

        // ★ 临时摘掉 MaxHeight 再量 —— 见方法说明。try/finally 不能省:
        //   Measure 抛异常时若没装回去, 简介就会永远失去高度上限(撑满整屏)。
        var savedMax = DescText.MaxHeight;
        var needed = 0d;
        try
        {
            DescText.MaxHeight = double.PositiveInfinity;
            DescText.Measure(new Size(width, double.PositiveInfinity));
            needed = DescText.DesiredSize.Height;
        }
        finally
        {
            DescText.MaxHeight = _descExpanded
                ? (needed > DescCollapsedHeight ? needed : DescCollapsedHeight)
                : DescCollapsedHeight;
        }

        // +1 容差: 亚像素误差会让"刚好 6 行"被误判成没超, 按钮该出时不出。
        var overflows = needed > DescCollapsedHeight + 1;
        BtnDescFold.Visibility = overflows ? Visibility.Visible : Visibility.Collapsed;
        // 短简介没有"展开"可言 —— 保持展开态即可, 按钮隐藏。
        // ★ 不要在这里把它设成 false: 默认就是展开, 设 false 会让 ApplyDescFoldText
        //   把文案写成"展开简介", 而实际内容是全部显示的, 自相矛盾。
        _ = overflows;

        ApplyDescFoldText();
    }

    /// <summary>
    /// 「展开/收起」那行文字的**唯一**写法点(文案 + 符号一起换方向)。
    ///
    /// ★ 为什么单独抽出来: 符号(⌄/⌃)与文案必须在**同一次**调用里同步更新,
    ///   而它们分属两个不同的对象(TextBlock 与 Run)。留在 UpdateDescFold 里就地写没问题,
    ///   但一旦以后新增第二个入口(比如键盘快捷键/右键菜单)忘了改符号方向,
    ///   就会出现"点了展开, 文字说收起、箭头还朝下"这种自相矛盾的按钮。
    /// </summary>
    private void ApplyDescFoldText()
    {
        if (BtnDescFoldLabel != null) BtnDescFoldLabel.Text = _descExpanded ? "收起简介" : "展开简介";
        // ⌄ = 往下展开, ⌃ = 往上收起
        if (BtnDescFoldGlyph != null) BtnDescFoldGlyph.Text = _descExpanded ? "⌃" : "⌄";
        if (BtnDescFold != null) BtnDescFold.ToolTip = _descExpanded ? "收起简介" : "展开完整简介";
    }

    /// <summary>展开/收起简介</summary>
    private void OnDescFoldClick(object sender, RoutedEventArgs e)
    {
        _descExpanded = !_descExpanded;
        UpdateDescFold();
        // BtnDescFold 已设 Focusable=False: 点完焦点不会留在按钮上(浅色主题没有焦点框,
        // 空格也不会打在按钮上)。
    }

    /// <summary>
    /// 载入当前视频的相关视频。
    ///
    /// ★★ 2026-10-13 用户报"相关视频只有 8 个, 添加更多视频, 往下滑刷新":
    ///   接口一次就返 40 条且翻页无效(实测 ps/pn 都不影响, 见 GetRelatedAsync 的说明),
    ///   所以这里把**全量 40 条**存在 <see cref="_relatedAll"/>, 界面只挂前
    ///   <see cref="RelatedPageSize"/> 条; 滚到附近时由 <see cref="ShowMoreRelated"/>
    ///   再补一批。列表用 ObservableCollection, 追加即刷新, 不重排已有卡片。
    /// </summary>
    private async Task LoadRelatedAsync(string bvid)
    {
        if (_closing || string.IsNullOrEmpty(bvid)) return;

        // 直播/本地文件没有"相关视频"这回事(见 LoadLiveAsync 里对 DescText 的处理)
        if (RelatedPanel == null || RelatedList == null) return;

        var gen = ++_relatedGen;

        // 先清空再转圈: 不清的话上一次的卡片会一直挂在界面上, 而那批推荐与当前视频无关。
        _relatedAll = new List<VideoItem>();
        _relatedShown = 0;
        RelatedList.ItemsSource = null;
        RelatedPanel.Visibility = Visibility.Visible;
        UpdateWebRecommendationsHint();
        RelatedLoadingRing.Visibility = Visibility.Visible;
        RelatedHint.Text = "正在加载…";

        var (ok, err, items) = await Svc.Api.GetRelatedAsync(bvid);
        // 代次比对: 换片后这批结果就不属于当前视频了, 丢掉(与 ClearDanmaku 那套同款闸门)
        if (_closing || gen != _relatedGen) return;

        RelatedLoadingRing.Visibility = Visibility.Collapsed;
        RelatedHint.Text = "";

        if (!ok || items == null || items.Count == 0)
        {
            // ★ 失败/为空时把整块藏起来, 而不是留一个空标题 + 一句报错:
            //   "相关视频"下面是空的比不显示更糟(看着像功能坏了)。
            //   真的想给用户一个交代, 那该走 Toast, 而这属于后台补充信息, 不该打扰。
            RelatedPanel.Visibility = Visibility.Collapsed;
            _relatedEmptyMessage = ok ? "暂无推荐视频" : "推荐视频暂时无法加载";
            UpdateWebRecommendationsHint();
            // 只写 errors.txt 不弹窗: 相关视频是后台补充信息, 失败不该打断看片。
            // 包一层 Exception 是因为 App.ReportError 只收 Exception(它的唯一用途就是落盘),
            // 这里不去动那个公共签名 —— 加一个 string 重载会让人以为"失败可以不记录"。
            App.ReportError(new InvalidOperationException($"相关视频加载失败({bvid}): {err ?? "未知错误"}"));
            return;
        }

        _relatedAll = items;
        // 先挂第一批(与旧行为一致: 打开播放器先看到 8 条, 不一下铺满 40 条)
        _relatedShown = Math.Min(RelatedPageSize, items.Count);
        RelatedList.ItemsSource = items.GetRange(0, _relatedShown);
    }

    /// <summary>
    /// 信息栏滚动: 滚到相关视频附近时把下一批补上(用户要求"往下滑刷新出更多相关视频")。
    ///
    /// ★ 判据是"相关视频列表的**底部**离视口底部还有不到一屏", 不是"滚到底":
    ///   等用户真的滚到最底才补, 会先出现"列表空了半屏"的空白期, 看着像没有了。
    /// ★ 只在往下滚的方向补: 往上滚回来时不该再 append(已经补过的就在那儿)。
    ///   <see cref="ShowMoreRelated"/> 本身幂等, 这里加方向判断是省掉无谓的 ItemsSource 重挂 ——
    ///   每次重挂都会让 ItemsControl 重新生成全部容器, 一屏卡片会闪一下。
    /// </summary>
    private void OnInfoScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!UsesDetachedCommentsLayout && sender is ScrollViewer commentsScroll) PrefetchCommentPage(commentsScroll, e);
        if (_tabletPortraitLayoutMounted && IsPortraitVideoDetected && sender is ScrollViewer tabletScroll && ReferenceEquals(e.OriginalSource, tabletScroll))
        {
            if (tabletScroll.VerticalOffset > 64 && !_tabletPortraitVideoCompact)
            {
                _tabletPortraitVideoCompact = true;
                UpdateTabletPortraitMeasurements();
            }
            else if (tabletScroll.VerticalOffset < 20 && _tabletPortraitVideoCompact)
            {
                _tabletPortraitVideoCompact = false;
                UpdateTabletPortraitMeasurements();
            }
        }
        if (_closing || _webDockSpacingPrepared || _webRendering || _vmemTracking || _relatedShown >= _relatedAll.Count) return;
        // 换片后 _relatedAll 是空的, 上面的闸门已经挡住; 这里再确认面板是显示的,
        // 免得在"简介 tab"下也补(相关视频属于简介 tab, 评论 tab 下整块是隐藏的)。
        if (!ReferenceEquals(e.OriginalSource, sender) || RelatedPanel?.IsVisible != true) return;
        // 往上滚不补
        if (e.VerticalChange < 0) return;

        if (sender is not ScrollViewer sv) return;
        // 相关视频列表在可视区里的位置: 它的底部距 ScrollViewer 内容底部还有多远
        var targetBottom = RelatedList.TransformToVisual(sv)
            .Transform(new Point(0, RelatedList.ActualHeight)).Y;
        // 距"还要再滚一屏才看得见"的阈值内就补下一批
        if (targetBottom - sv.ViewportHeight < sv.ViewportHeight)
            ShowMoreRelated();
    }

    /// <summary>相关视频每批展示几条(滚动到附近时按这个量追加)</summary>
    private const int RelatedPageSize = 8;

    /// <summary>当前视频的相关视频**全量**(接口一次给 40 条; 界面只挂前 <see cref="_relatedShown"/> 条)</summary>
    private List<VideoItem> _relatedAll = new();

    /// <summary>当前已经挂到界面上的条数; 换片时归零</summary>
    private int _relatedShown;
    private string _relatedEmptyMessage = "暂无推荐视频";

    /// <summary>
    /// 再展示一批相关视频(信息栏滚到相关视频附近时调)。
    ///
    /// ★ 幂等 + 到顶即停: 已经全展示完时什么都不做, 不会重复 append。
    /// ★ 换片后 <see cref="_relatedAll"/> 是空的, 这里直接返回 —— 不会把上一片的
    ///   推荐补进新片的列表(那是"点下去跳到不相干视频"的老 bug)。
    /// </summary>
    private void ShowMoreRelated()
    {
        if (_closing || _relatedShown >= _relatedAll.Count) return;
        var next = Math.Min(_relatedShown + RelatedPageSize, _relatedAll.Count);
        var shown = _relatedAll.GetRange(0, next);
        _relatedShown = next;
        RelatedList.ItemsSource = shown;
    }

    /// <summary>相关视频卡片的点击: 切到那个 bvid</summary>
    private void PlayRelated(object? param)
    {
        // ★ 必须真的转成 string 再用: CommandParameter 从 XAML 过来是 object,
        //   直接拿它和 _currentBvid 比是**引用比较** —— 编译器会警告 CS0252,
        //   而运行时几乎永远不相等, 表现为"点了相关视频没反应"。
        var bvid = param as string;
        if (_closing || string.IsNullOrEmpty(bvid)) return;
        // 点自己没意义(接口那边已经滤掉了, 这里再挡一道是防"同一片快速点两次")
        if (bvid == _currentBvid) return;
        PlayVideo(bvid);
    }

    /// <summary>
    /// 打开一个直播间。
    /// 与点播的区别: 直播没有总时长/进度、没有弹幕回放、也没有三连与历史进度上报,
    /// 所以互动按钮全部禁用(复用 _isLocalPlayback 那套判断), 进度条置灰。
    /// </summary>
    public void PlayLive(long roomId, string? roomTitle = null)
    {
        if (_closing || roomId <= 0) return;
        if (_isLive && roomId == _liveRoomId && _mp.IsPlaying) return;
        _currentBvid = "";
        _isLocalPlayback = true;   // 没有在线上下文: 不打接口、不互动、不上报
        _isLive = true;
        _liveRoomId = roomId;
        ResetForNewMedia();
        if (!string.IsNullOrEmpty(roomTitle)) VideoTitleText.Text = roomTitle;
        _ = ReplaceNativeInputAsync(() => _ = LoadLiveAsync(roomId));
    }

    /// <summary>连接直播间: 取直播流地址 → 起播</summary>
    private async Task LoadLiveAsync(long roomId)
    {
        ShowLoading("正在连接直播间…");
        FailOverlay.Visibility = Visibility.Collapsed;

        try
        {
            if (!string.IsNullOrEmpty(VideoTitleText.Text))
            {
                Title = VideoTitleText.Text + " - BiliTouch";
                TitleText.Text = VideoTitleText.Text;
            }
            ViewCountText.Text = "--";
            DanmakuCountText.Text = "--";
            PubDateText.Text = "直播中";
            UpNameText.Text = "直播间 " + roomId;
            UpDescText.Text = "B 站直播";
            // ★ 2026-11-04 用户要求: 在线观看人数放在**上面那条 bar**(与稍后再看/听视频同排)。
            //   直播时上方那颗「稍后再看」没有意义(直播不能"稍后看"), 正好换成人数,
            //   位置固定、进直播就能看到, 不必去简介里翻。
            if (LiveOnlineBox != null) LiveOnlineBox.Visibility = Visibility.Visible;
            if (LiveOnlineText != null) LiveOnlineText.Text = "--";
            if (BtnWatchLaterTop != null) BtnWatchLaterTop.Visibility = Visibility.Collapsed;
            DescText.Text = "这是一个直播间。直播没有进度条与历史记录, 互动按钮在直播中不可用。";
            // 直播没有"相关视频"(接口只认视频稿件 bvid), 直接把整块藏掉,
            // 否则会停在上一片残留的推荐列表上 —— 那是**别的视频**的推荐。
            ClearRelatedPanel();
            UpAvatarBox.Background = (Brush)FindResource("AccentSoftFillBrush");
            UpAvatarFallback.Visibility = Visibility.Visible;

            // 直播没有总时长: 进度条禁用 + 时间位写"直播中"。
            // 注意 UpdateProgressUi 里有 `Length > 0` 的判断, 直播的 Length 一直是 0,
            // 所以它本来就不会去覆盖这里显示的文案。
            ProgressSlider.IsEnabled = false;
            TimeText.Text = "直播中";
            _qualities.Clear();
            FillQualityMenu(0);
            // ★ 2026-11-04: 直播**支持选清晰度**了(之前写死 qn=0 且把按钮藏了)。
            //   档位列表由接口的 g_qn_desc 给出, 真正拿不拿得到取决于登录态与主播推流,
            //   所以按钮文案按**实际拿到的档位**写(见下面 actualQn)。
            if (QualityText != null) QualityText.Text = "直播";
            if (CommentHint != null) CommentHint.Text = "直播没有评论区";
            UpdateButtonState();

            // 提前启动直播弹幕 WebSocket 握手，与下方取流并发减少冷启动等待(借鉴 6a6e650)
            StartLiveDanmaku(roomId);
            var (ok, err, url, actualQn, qnOptions) =
                await Svc.Api.GetLivePlayUrlExAsync(roomId, _liveQn);
            if (_closing) return;
            if (!ok || string.IsNullOrEmpty(url))
            {
                StopLiveDanmaku();
                ShowFail(err ?? "无法获取直播流地址");
                return;
            }
            // 把可选档位填进清晰度菜单, 并按实际档位更新按钮文案
            _qualities.Clear();
            foreach (var (q, label) in qnOptions) _qualities.Add((q, label));
            _liveActualQn = actualQn;
            FillQualityMenu(actualQn);
            if (QualityText != null && qnOptions.Count > 0)
            {
                var label = qnOptions.FirstOrDefault(o => o.qn == actualQn).label;
                QualityText.Text = string.IsNullOrEmpty(label) ? "直播" : label;
            }
            _lastPlayUrl = url;
            _lastAudioUrl = null;   // 直播是单流, 没有独立的音轨

            // 直播流地址同样要带 Referer / UA —— 直播 CDN 比点播校验得更严:
            // 不带 Referer 时常见表现就是"能拿到地址但播不出来"(403/直接 EOF)。
            // 之前这里只加了 :no-video-title-show, 于是直播一直播不动。
            // ★ 2026-11-01: 缓冲值不再硬编码 2000 —— 走 BufferPolicy 统一决策
            //   (手动档取 max(用户值, 1000), 自动档 2000, 规则见 Services/BufferPolicy.cs)。
            var liveCaching = BufferPolicy.Resolve(BufferMediaKind.Live, url, shortVideo: false,
                Svc.Settings.NetworkCachingMs, Svc.Settings.AutoBufferPolicy);
            var liveOptions = new List<string>
            {
                ":no-video-title-show",
                ":http-referrer=https://live.bilibili.com/" + roomId,
                ":http-user-agent=" + PlayerUserAgent,
                ":http-reconnect",
                // 直播是持续流: 加大缓存, 别让 LibVLC 因为"看起来卡住"就早早判定结束
                ":network-caching=" + liveCaching,
                ":live-caching=" + liveCaching
            };

            var media = new Media(_libVLC, new Uri(url), liveOptions.ToArray());
            // 直播是持续流, 没有可 seek 的时间轴; 标成"可靠源"让看门狗别去重建它
            _isReliableSeekSource = true;
            if (!PlayMedia(media))
            {
                StopLiveDanmaku();
                ShowFail("直播流播放失败, 可能已经下播");
            }
        }
        catch (Exception ex)
        {
            StopLiveDanmaku();
            ShowFail("打开直播间失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 清空并隐藏「相关视频」整块。
    ///
    /// ★ 必须在每条**没有在线上下文的**播放路径上调用(直播 / 本地文件 / 换片起点):
    ///   否则上一批推荐会一直挂在界面上, 而那批是**别的视频**的推荐 ——
    ///   点下去跳到不相干的视频, 却没有任何迹象说明它过期了。
    /// ★ 顺带把代次 +1: 让还在飞的请求回来时立刻作废, 不会把旧结果又灌进来。
    /// </summary>
    private void ClearRelatedPanel()
    {
        _relatedGen++;
        // 换片/切直播: 缓存也一起丢。不清的话 OnInfoScrollChanged 里的闸门
        // (_relatedShown >= _relatedAll.Count) 会看到上一片还有"没展示完"的条数,
        // 于是把上一片的推荐补进新片的列表 —— 那是"点下去跳到不相干视频"的老 bug。
        _relatedAll = new List<VideoItem>();
        _relatedShown = 0;
        if (RelatedList != null) RelatedList.ItemsSource = null;
        if (RelatedPanel != null) RelatedPanel.Visibility = Visibility.Collapsed;
        if (RelatedLoadingRing != null) RelatedLoadingRing.Visibility = Visibility.Collapsed;
        if (RelatedHint != null) RelatedHint.Text = "";
        _relatedEmptyMessage = "暂无推荐视频";
        UpdateWebRecommendationsHint();
    }

    /// <summary>
    /// 打开本地缓存文件并起播。
    ///
    /// 全程同步 —— 以前它是 <c>async Task</c> 却没有任何 await, 靠一句
    /// <c>await Task.CompletedTask;</c> 收尾。那种写法有两个坏处: ① 让"这里其实不异步"这件事
    /// 看不出来; ② 异常会进 Task 里, 而调用方是 <c>_ = LoadLocalAsync(...)</c>(丢弃返回值),
    /// 等于无人观察。方法内已有 try/catch 兜底, 所以这里直接做成同步方法最诚实。
    /// </summary>
    private void LoadLocalFile(string path)
        => LoadLocalFileWithMeta(path, Path.GetFileNameWithoutExtension(path), "本地文件", "离线缓存", "本地",
            "这是离线缓存里的本地文件, 没有在线视频的简介与评论。");

    private async Task LoadOfflineEntryAsync(OfflineEntry entry, int gen)
    {
        var displayTitle = string.IsNullOrWhiteSpace(entry.PartTitle) || entry.Title == entry.PartTitle
            ? entry.Title
            : $"{entry.Title} · {entry.PartTitle}";
        var author = string.IsNullOrWhiteSpace(entry.Author) ? "离线缓存" : entry.Author;
        var sourceDesc = entry.IsExternalClientCache ? "哔哩哔哩客户端缓存" : "离线缓存";
        var qualityLabel = string.IsNullOrWhiteSpace(entry.QualityText) ? "本地" : entry.QualityText;
        var summary = entry.IsExternalClientCache
            ? $"来自哔哩哔哩 Windows 客户端离线缓存目录：\n{entry.ExternalDirectory}"
            : (HasBilibiliOnlineIdentity(entry)
                ? "离线缓存本地播放；联网时将在后台自动补充弹幕与评论（不影响视频播放）。"
                : "这是离线缓存里的本地文件, 没有在线视频的简介与评论。");

        ApplyLocalMetadata(displayTitle, author, sourceDesc, qualityLabel, summary);
        ShowLoading(entry.IsExternalClientCache ? "正在准备客户端离线视频…" : "正在打开本地文件…");
        FailOverlay.Visibility = Visibility.Collapsed;

        var cts = new CancellationTokenSource();
        _offlinePrepCts = cts;
        try
        {
            var playablePath = entry.IsExternalClientCache
                ? await BilibiliClientCacheScanner.PreparePlayableMediaAsync(entry, cts.Token)
                : entry.FilePath;
            if (_closing || gen != _loadGen || cts.IsCancellationRequested) return;
            await ReplaceNativeInputAsync(() =>
            {
                if (_closing || gen != _loadGen) return;
                LoadLocalFileWithMeta(playablePath, displayTitle, author, sourceDesc, qualityLabel, summary);
            });
            // ★ 本地视频已开始起播后，才在后台静默尝试补充在线弹幕与评论：
            //   不 await、不阻塞起播、失败完全静默降级、绝不触发 FailOverlay 或影响播放。
            if (!_closing && gen == _loadGen && IsOfflineBiliMedia)
                _ = LoadOfflineOnlineContextAsync(entry, gen);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closing && gen == _loadGen) ShowFail("打开离线缓存失败: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_offlinePrepCts, cts)) _offlinePrepCts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// 离线缓存播放时的后台“在线增强”：在有网的情况下静默加载在线弹幕与评论（aid/cid），
    /// 全程与本地媒体起播解耦——无网或接口异常时直接静默降级，绝不中断视频播放或弹出失败遮罩。
    /// </summary>
    private async Task LoadOfflineOnlineContextAsync(OfflineEntry entry, int gen, bool userInitiated = false)
    {
        if (_closing || gen != _loadGen || !IsOfflineBiliMedia) return;
        var bvid = _offlineOnlineBvid;
        if (string.IsNullOrEmpty(bvid)) return;

        // 离线快速短路：用户主动点重试时仍允许尝试一次（部分虚拟网卡下 GetIsNetworkAvailable 可能误报）
        if (!userInitiated)
        {
            try
            {
                if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                    return;
            }
            catch { }
        }

        try { _offlineOnlineCts?.Cancel(); _offlineOnlineCts?.Dispose(); } catch { }
        var cts = new CancellationTokenSource();
        _offlineOnlineCts = cts;
        try
        {
            // 1. 若离线条目本身就记录了 cid，立刻解锁弹幕按钮并在后台并行拉取弹幕，无需等待详情接口
            if (entry.Cid > 0 && _currentCid <= 0)
            {
                _currentCid = entry.Cid;
                UpdateDanmakuButton();
                _ = LoadDanmakuAsync(entry.Cid, entry.DurationSeconds > 0 ? entry.DurationSeconds : -1);
            }

            // 2. 后台获取视频详情以拿到 aid（以及缺失时的 cid），供评论区按需加载
            var (ok, _, detail) = await Svc.Api.GetVideoAsync(bvid);
            if (_closing || gen != _loadGen || cts.IsCancellationRequested || !IsOfflineBiliMedia) return;
            if (!ok || detail == null) return;

            if (detail.Aid > 0) _aid = detail.Aid;
            if (_currentCid <= 0 && detail.Cid > 0)
            {
                _currentCid = detail.Cid;
                UpdateDanmakuButton();
                _ = LoadDanmakuAsync(detail.Cid, detail.DurationSec > 0 ? detail.DurationSec : -1);
            }
            else if (_currentCid > 0 && _danmakuRaw.Count == 0 && _danmakuLoadCancellation == null)
            {
                // 断网恢复后点击重试时顺带补拉弹幕
                UpdateDanmakuButton();
                var hintSec = detail.Pages.FirstOrDefault(p => p.Cid == _currentCid)?.DurationSec ?? detail.DurationSec;
                _ = LoadDanmakuAsync(_currentCid, hintSec > 0 ? hintSec : -1);
            }

            UpdateCommentComposer();
            // 若用户此时已经切到评论页（或处于网页布局评论常显），立刻拉取评论列表
            if (_aid > 0 && CommentsPanel != null && CommentsPanel.Visibility == Visibility.Visible)
                _ = LoadCommentsAsync(force: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            WriteLibVLCDebug($"[OfflineOnline] bvid={bvid} enrich skipped: {ex.GetType().Name}\n");
        }
        finally
        {
            if (ReferenceEquals(_offlineOnlineCts, cts)) _offlineOnlineCts = null;
            cts.Dispose();
            if (!_closing && gen == _loadGen && IsOfflineBiliMedia && _aid <= 0 &&
                CommentsPanel != null && CommentsPanel.Visibility == Visibility.Visible)
            {
                CommentLoadingRing.Visibility = Visibility.Collapsed;
                CommentHint.Text = "离线缓存未加载到在线评论（离线或网络不可用），联网后可点击重试";
                BtnRetryComments.Visibility = Visibility.Visible;
            }
        }
    }

    private void ApplyLocalMetadata(string name, string author, string subDesc, string qualityLabel, string desc)
    {
        TitleText.Text = name;
        Title = name + " - BiliTouch";
        VideoTitleText.Text = name;
        ViewCountText.Text = "--";
        DanmakuCountText.Text = "--";
        PubDateText.Text = "--";
        UpNameText.Text = author;
        UpDescText.Text = subDesc;
        DescText.Text = desc;
        UpAvatarBox.Background = (Brush)FindResource("AccentSoftFillBrush");
        UpAvatarFallback.Visibility = Visibility.Visible;
        UpdateButtonState();
        _qualities.Clear();
        FillQualityMenu(0);
        if (QualityText != null) QualityText.Text = qualityLabel;
    }

    private void LoadLocalFileWithMeta(string path, string name, string author, string subDesc, string qualityLabel, string desc)
    {
        ShowLoading("正在打开本地文件…");
        FailOverlay.Visibility = Visibility.Collapsed;
        try
        {
            ApplyLocalMetadata(name, author, subDesc, qualityLabel, desc);

            var media = new Media(_libVLC, path, FromType.FromPath);
            // 与 BuildMedia 保持一致：避免分片 MP4(fMP4) 结尾最后一个片段被 libvlc mp4 demuxer 丢弃
            media.AddOption(":demux=avformat");
            if (_closing) { media.Dispose(); return; }
            _lastPlayUrl = path;
            // 本地文件是单文件源, seek 一次到位(与 durl 合流单流同性质)
            _isReliableSeekSource = true;
            PlayMedia(media);
        }
        catch (Exception ex)
        {
            if (!_closing) ShowFail("打开本地文件失败: " + ex.Message);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (TabIntro != null && IntroPanel != null) TabIntro.IsChecked = true;
        UpdateButtonState();
        // 窗口是复用的: 按当前状态(铺满/常规)同步一次信息栏与窗口最小尺寸
        ApplyInfoPanelState();
        // 「铺满窗口」按钮的字形初值: XAML 里写的是"进入前"那个字形(E9A6), 这里按实际状态
        // 再同步一次 —— 窗口是复用的, 复用到一个"上次已铺满"的窗口时不能显示成未铺满。
        UpdateFillWindowButton();
        // ★ 这条路径(带 bvid 直接 new 出窗口)**不走 ResetForNewMedia**(与 ApplyPreferredQuality
        //   当年踩的是同一个坑), 所以「听视频」按钮的可用性也要在这里同步一次。
        //   位置在 LoadVideoAsync **之前**: UpdateListenButtonState 依赖 _isLive/_isLocalPlayback,
        //   而这两个标志由调用方在构造后、Show 前就已设好。
        UpdateListenButtonState();
        // 聆听态是跨窗口复用的窗口级模式: 复用一个"上次停在聆听态"的窗口时,
        // 顶栏按钮与遮罩都要还原成聆听态(否则按钮说"听视频"、实际播的是音轨)
        if (_isListenMode) ApplyListenMode(true);
        // 覆盖层窗口(VideoView 的 ForegroundWindow)这时才建出来 —— 把键盘处理也挂上去,
        // 否则点过一次视频/控制栏之后快捷键就全哑了(见 HookOverlayKeyboard 的说明)
        HookOverlayKeyboard();
        // ★ 不重复启动: 新窗口第一次打开时 PlayerService 已经先调过 PlayVideo(→ReplaceNativeInputAsync→LoadVideoAsync),
        //   Loaded 事件随后才到。此时 ReplaceNativeInputAsync 可能仍在后台 Stop(), _loadRunning 尚未置 true，
        //   必须同时检查 _nativeInputVersion == 0 才能挡住竞态双加载。
        if (!_loadRunning && _nativeInputVersion == 0 && !_isLocalPlayback && !_isLive && !string.IsNullOrEmpty(_currentBvid))
            await LoadVideoAsync();
    }

    // ------------------------------------------------------------ 加载流程

    /// <summary>
    /// 拉详情 → 取流 → 播放。
    ///
    /// ★ <paramref name="cid"/>: 指定要播的**分P**。0 = 走默认(pages[0])。
    ///   分P 是同一个 bvid 切成几段, 换段只换 cid(见 SwitchToPageAsync),
    ///   所以这里用参数传, 不去改 _currentBvid —— 改了会把弹幕/历史/三连的归属整个搞错。
    /// </summary>
    private async Task LoadVideoAsync(long cid = 0)
    {
        if (string.IsNullOrEmpty(_currentBvid)) return;

        // ★ 每次进入都取一个新代次: 上一次在飞的加载(点了另一条视频/另一个分P)立刻作废。
        //   没有这一道时, 连点两张卡片会跑两条完整的加载链, 后回来的那条会把画面/标题盖掉。
        var gen = ++_loadGen;
        _loadRunning = true;
        var sw = Stopwatch.StartNew();
        long tDetail = 0, tPlayUrl = 0, tRemux = 0, tSponsor = 0;

        ShowLoading("正在获取视频信息…");
        FailOverlay.Visibility = Visibility.Collapsed;
        try
        {
            var (detailOk, detailErr, detail) = await Svc.Api.GetVideoAsync(_currentBvid);
            tDetail = sw.ElapsedMilliseconds;
            // 加载期间用户可能已经关掉窗口/换了片, 此时不能再碰任何 UI/原生对象
            if (_closing || gen != _loadGen) return;
            if (detail == null)
            {
                // 现在 err 会带真实原因(接口码/异常), 不用再写死一句猜测的话
                ShowFail(detailErr ?? "获取视频信息失败, 可能视频已删除或不可用");
                return;
            }
            // ★ cid 优先级: 调用方指定的分P > pages[0]。
            //   合集换片那条路不传 cid(它是"换 bvid", 落回 pages[0] 与原行为完全一致),
            //   分P 换段那条路传具体 cid。
            _currentCid = cid > 0 ? cid : detail.Cid;
            _aid = detail.Aid;
            _ownerMid = detail.OwnerMid;
            _ownerName = detail.Owner;
            _coverUrl = detail.Pic ?? "";

            if (_currentCid <= 0)
            {
                RunOrDeferAfterSeamlessEntry(() =>
                {
                    if (_closing || gen != _loadGen) return;
                    UpdateUiFromDetail(detail);
                    ApplySeason(detail.Season);
                    ApplyPages(detail.Pages);
                });
                ShowFail("无法获取视频 cid, 播放中止");
                return;
            }

            // During active shared-element entry (~250ms), keep network/decode pipeline running at full speed
            // while deferring heavy right-rail visual tree binds until the entry surface lands.
            var pageDim = detail.Pages.FirstOrDefault(p => p.Cid == _currentCid);
            var detailW = pageDim is { VideoWidth: > 0, VideoHeight: > 0 } ? pageDim.VideoWidth : detail.VideoWidth;
            var detailH = pageDim is { VideoWidth: > 0, VideoHeight: > 0 } ? pageDim.VideoHeight : detail.VideoHeight;
            RunOrDeferAfterSeamlessEntry(() =>
            {
                if (_closing || gen != _loadGen) return;
                if (detailW > 0 && detailH > 0) UpdateDetectedVideoSize(detailW, detailH, fromMetadata: true);
                UpdateUiFromDetail(detail);
                ApplySeason(detail.Season);
                ApplyPages(detail.Pages);
                _ = LoadCommentsAsync();
            });
            WriteLibVLCDebug($"\n[Load] 详情就绪 aid={_aid} cid={_currentCid} tabComments={TabComments?.IsChecked}\n");
            // 弹幕预加载权重要高, 不能等起播: 起播之后画面已经在动, 用户第一反应就是"弹幕怎么还没出来",
            // 而弹幕接口是**按 6 分钟一段并发拉取**的, 冷启动实测比取流还慢。这里与下面的 GetPlayUrlAsync
            // 并行发出, 不 await、不阻塞起播; 带上详情里的时长(多P 取当前那一 P 的), 免得多发十几个空段请求。
            var danmakuHintSec = detail.Pages.FirstOrDefault(p => p.Cid == _currentCid)?.DurationSec ?? detail.DurationSec;
            _ = LoadDanmakuAsync(_currentCid, danmakuHintSec > 0 ? danmakuHintSec : -1);

            // ★ 片段查询**立刻发起, 与下面"取播放地址 / 合流下载"并行** ——
            // 起播**不等它**(服务端首包实测 2.6s, 等了也拿不到); 回来得早才做开场空降,
            // 否则由 500ms 心跳补跳(见 BeforePlayApplySponsorAsync)。
            var sponsorTask = FetchSponsorSegmentsAsync();

            SetLoadingText("正在获取播放地址…");
            // 默认请求 1080P(qn=80), B 站会按登录态/大会员返回实际可用的清晰度
            var (playOk, playErr, main, backups, durMs, qualities, actualQn, audioUrl, remux,
                audioTracks, actualAudio) =
                await Svc.Api.GetPlayUrlAsync(_currentBvid, _currentCid, _currentQn, Svc.Settings.PreferredAudioQuality);
            tPlayUrl = sw.ElapsedMilliseconds;
            // 记下这条视频的音轨清单与实际在播的档位: 清晰度菜单里的"音质"分区靠它们渲染
            if (_closing || gen != _loadGen) return;
            _audioTracks = audioTracks;
            _actualAudio = actualAudio;
            // 以前这里不判成败, 直接拿 main 往下走 —— 取不到地址时 main 是 null, 一路走到
            // BuildMedia(null) 才炸, 用户看到的是一句语焉不详的"打开失败"。
            // 现在把 API 层给的原因直接透出去(见 ApiClient.GetPlayUrlAsync 的说明)。
            if (!playOk && string.IsNullOrEmpty(main))
            {
                ShowFail(playErr ?? "获取播放地址失败, 请稍后重试");
                return;
            }
            _totalMs = durMs;
            UpdateTotalDurationText();
            if (remux is { VideoWidth: > 0, VideoHeight: > 0 } && !_videoSizeFromMetadata)
                UpdateDetectedVideoSize(remux.VideoWidth, remux.VideoHeight, fromMetadata: false);
            _qualities.Clear();
            _qualities.AddRange(qualities);
            FillQualityMenu(actualQn);

            // ---- 极短片(≤60s / ≤8MB): 先合流成本地文件再播; 其余一律远端双流直接起播 ----
            // 顺序: 已缓存 -> 直接播本地; 没缓存 -> 2.5 秒内下完就播本地; 下不动 -> 回落远端 DASH。
            // ★ 2026-10-30: 阈值从"3 分钟 / 150MB"收到"1 分钟 / 8MB"。旧阈值下大多数
            //   短视频都要先整片下载, 起播被拖成 10~45 秒(用户报"加载慢/卡住")。
            //   实测 15.3MB 就要 3.6 秒, 所以 8MB 才是"等得起"的体量上限。
            //
            // ★ 只在播**第一个 P** 时才碰本地缓存(2026-10-11 加分P时补的):
            //   ShortVideoCache 的键只有 bvid, 不含 cid。多P 的短视频(每段都小于体积上限,
            //   比如一个视频切成几段 vlog)切到 P2 时会命中 P1 落盘的文件 —— 表现为
            //   "换了集还是上一段的内容"。长视频本来就超预算走不到这里,
            //   所以这个坑平时碰不到, 但短视频多P 一做分P 就会踩。
            //   与其给缓存 API 加 cid 参数(要动文件命名/清理/落盘三处), 不如认准"只缓存首 P"。
            var isFirstPage = cid <= 0 || cid == detail.Cid;
            var local = isFirstPage ? ShortVideoCache.TryGet(_currentBvid, actualQn, actualAudio) : null;
            if (local == null && isFirstPage && ShortVideoCache.WorthCaching(remux))
                local = await PrepareShortVideoAsync(remux!, actualQn);
            tRemux = sw.ElapsedMilliseconds;
            if (_closing || gen != _loadGen) return;

            string? url;
            string? playAudio;
            if (local != null)
            {
                url = local;
                playAudio = null;      // 本地合流是单文件源, 带 input-slave 等于给同一文件再挂一条音轨
            }
            else
            {
                url = main;
                if (string.IsNullOrEmpty(url) && backups.Count > 0) url = backups[0];
                playAudio = audioUrl;
            }
            if (string.IsNullOrEmpty(url))
            {
                ShowFail("无法获取播放地址(可能为大会员/付费视频), 请登录后重试");
                return;
            }

            SetLoadingText("正在缓冲视频…");

            // 记录要播放的 URL(诊断 + 重播时重建 Media 用)
            _lastPlayUrl = url;
            _lastAudioUrl = playAudio;
            // ★★★ 「听视频」存的必须是 **DASH 音轨地址(audioUrl)**, 不能存 playAudio。
            //   两者的区别正是"三分钟以下视频点听视频说没有音轨"的根因:
            //     短视频走**本地合流文件**那条路时 playAudio 是 null(单文件源不能挂 input-slave),
            //     但接口给的 DASH 音轨地址依然是好的、依然能单独播。
            //     以前这里写成 `= playAudio` ⇒ 短视频的 _listenAudioUrl 恒为 null ⇒
            //     点「听视频」直接报"这条视频没有可用的音轨"(2026-10-06 用户实测)。
            //   存 audioUrl 之后: 聆听态播纯音轨(省流量), 退出时用 _lastPlayUrl(本地文件)
            //   原样拼回 —— 两条路各用各的地址, 互不干扰。
            _listenAudioUrl = audioUrl;
            RememberPlayUrlFreshness(!string.IsNullOrEmpty(main) ? main : audioUrl, backups);
            WriteLibVLCDebug(
                $"\n[LoadVideo] bvid={_currentBvid} cid={_currentCid} qn={actualQn} url={url} audio={playAudio}\n");

            // 片段数据: 只落地"已经回来"的结果 —— 绝不为它推迟起播(见 BeforePlayApplySponsorAsync)
            await BeforePlayApplySponsorAsync(sponsorTask);
            tSponsor = sw.ElapsedMilliseconds;
            if (_closing || gen != _loadGen) return;

            // 起播耗时诊断: 每段都是**本段耗时**(不是累计), 与 [Playing] 的 first-frame 对齐看
            WriteLibVLCDebug(
                $"\n[Timing] bvid={_currentBvid} detail={tDetail}ms " +
                $"playurl={tPlayUrl - tDetail}ms remux={tRemux - tPlayUrl}ms " +
                $"sponsor={tSponsor - tRemux}ms toPlay={sw.ElapsedMilliseconds}ms " +
                $"source={(local != null ? "local" : "remote")} qn={actualQn} " +
                $"cdnWait=0\n");

            // B站 CDN 对 Referer 和 User-Agent 有校验, 必须通过 Media 选项告诉 LibVLC;
            // DASH 流音视频分离, 用 input-slave 把音轨挂到视频流上一起播放。
            // ★ 「听视频」开着时(user 开着聆听态点了下一条视频)要直接起播**音轨单流** ——
            //   否则会先起视频流再被 EnterListenMode 切一次, 白拉一段视频数据、还多闪一次画面。
            if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
            {
                _listenResumeMs = 0;
                ShowLoading("正在切换到听视频…");
                PlayMedia(BuildMedia(_listenAudioUrl!, null));
            }
            else
            {
                // ★ 聆听态开着但这一片**取不到独立音轨**(本地合流是单文件源): 退回整路播放,
                //   并把聆听态收掉 —— 否则遮罩会说"正在聆听"而实际在播视频, 自相矛盾。
                if (_isListenMode)
                {
                    _isListenMode = false;
                    ApplyListenMode(false);
                }
                PlayMedia(BuildMedia(url, playAudio));
            }
            UpdateListenButtonState();
            // 查询点赞/关注/收藏状态(用于按钮状态切换)
            // ★ 弹幕**不在这里拉了**: 已提前到详情拿到 cid 之后(见上面 danmakuHintSec 那一段)。
            //   留在这里会变成第二次请求同一份弹幕(慢一倍、还可能和预加载的结果互相覆盖)。
            _ = RefreshLikeStateAsync();
            _ = RefreshFollowStateAsync();
            _ = RefreshFavStateAsync();
            _ = RefreshWatchLaterStateAsync();
        }
        catch (Exception ex)
        {
            if (!_closing && gen == _loadGen) ShowFail("加载失败: " + ex.Message);
        }
        finally
        {
            // ★ 只有"还是当前这一代"才有资格清闸门: 被新加载顶掉的旧任务清掉的话,
            //   闸门会在新任务还在飞的时候谎报空闲, OnLoaded 又会叠一条进来。
            if (gen == _loadGen) _loadRunning = false;
        }
    }

    /// <summary>
    /// 播一个 Media, 并**立刻释放我们自己那一份引用**。
    ///
    /// ★ 为什么必须释放: `MediaPlayer.Play(media)` 内部会 `set_media` —— 播放器**自己**已经
    ///   retain 了一份, 所以 Play 之后我们这份就可以放掉, 播放不受任何影响
    ///   (LibVLCSharp 官方示例就是 `using (var media = ...) mp.Play(media)`)。
    ///
    /// 反过来, 如果我们不释放, 这个 `libvlc_media_t` 就**永远**留着 —— 它握着解复用解析出来的
    /// 东西(本地 MP4 的样本表、网络流的 track / metadata), 而且全是**原生内存**,
    /// .NET 的 GC 一点都管不到。表现就是: 每看一个视频涨一块、关掉也不回落
    /// (2026-09-27 用户报的内存问题里就有这一份)。
    ///
    /// 之前 6 个播放点全都是 `_mp.Play(new Media(...))` 直接把 Media 丢掉, 一个都没释放。
    /// </summary>
    /// <summary>
    /// 切到 vmem 宿主：把 `VideoAreaRoot` 从 `VideoView.Content` 搬回普通 WPF 树。
    ///
    /// ★ 为什么能搬：`VideoAreaRoot` 之所以必须待在 `VideoView.Content` 里，是因为
    ///   `VideoView` 是 `HwndHost`（airspace）—— 普通兄弟元素会被原生视频窗口盖住。
    ///   vmem 模式下没有原生窗口，屏幕上是普通 `Image`，所以它可以直接当兄弟盖上去。
    /// ★ 失败就返回 false，由调用方退回 VideoView 模式（不能出现"内核是 vmem、
    ///   宿主还是 VideoView"的半吊子状态，那样会黑屏）。
    /// </summary>
    private bool MountVmemSurface()
    {
        try
        {
            var areaRoot = VideoAreaRoot;
            if (areaRoot == null)
            {
                WriteLibVLCDebug("[Vmem] mount skipped: VideoAreaRoot is null\n");
                return false;
            }
            // 从当前父级摘下：正常是 VideoView.Content（ContentControl），
            // 若库已经把它搬进浮层窗口的树里，就按实际父级类型处理。
            if (areaRoot.Parent is Panel panel) panel.Children.Remove(areaRoot);
            else if (areaRoot.Parent is ContentControl owner) owner.Content = null;
            else if (areaRoot.Parent is Decorator decorator) decorator.Child = null;
            try { VideoView.Content = null; } catch { }
            WriteLibVLCDebug($"[Vmem] areaRoot.Parent was {areaRoot.Parent?.GetType().Name ?? "null"}\n");

            VideoView.Visibility = Visibility.Collapsed;
            Panel.SetZIndex(VmemView, 0);
            Panel.SetZIndex(areaRoot, 10);
            VideoSurface.Children.Insert(1, areaRoot);   // 0 = VmemView
            VmemView.Visibility = Visibility.Visible;
            VmemView.VideoSizeChanged -= OnVmemVideoSizeChanged;
            VmemView.VideoSizeChanged += OnVmemVideoSizeChanged;
            WriteLibVLCDebug("[Vmem] mounted: video host = VmemVideoView\n");
            return true;
        }
        catch (Exception ex)
        {
            WriteLibVLCDebug($"[Vmem] mount failed: {ex.GetType().Name} {ex.Message}\n");
            return false;
        }
    }

    /// <summary>视频宿主显隐：vmem 模式控 VmemView，否则控 VideoView。</summary>
    private void SetVideoHostVisible(bool visible)
    {
        if (_vmemMode) VmemView.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        else VideoView.Visibility = visible && !_seamless.IsRunning && !_seamless.PreShowPrepared ? Visibility.Visible : Visibility.Hidden;
    }

    private void CreateNativeVideoPlayer()
    {
        var player = new MediaPlayer(_libVLC);
        _mp = player;
        if (_vmemMode) VmemView.Attach(player);   // ★ vmem 回调必须在 Play 之前挂好
        else VideoView.MediaPlayer = _mp;
        // libvlc_video_set_callbacks sets avcodec-hw=none on the player. Request
        // D3D11VA on each media after attachment so composited video also uses it.
        // Unsupported streams retain LibVLC's software fallback.
        player.EnableHardwareDecoding = VideoDecode.HardwareEnabled;

        _mp.TimeChanged += OnTimeChanged;
        _mp.LengthChanged += OnLengthChanged;
        _mp.EndReached += OnEndReached;
        // Playing 事件触发时隐藏加载遮罩; 若刚切完清晰度则恢复进度
        // 注意: LibVLC 事件都在其内部线程触发, 必须用 BeginInvoke 异步派发到 UI 线程。
        // 若用同步 Invoke, UI 线程可能正阻塞在 _mp 属性调用等 LibVLC 内部锁, 双方互相等待 => 死锁
        // 另外每个派发体都要先判 _closing: 窗口已开始关闭时回调可能仍在队列里, 必须丢弃。
        _mp.Playing += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || !ReferenceEquals(player,_mp)) return;
            // 首帧耗时(从 PlayMedia 算起): 与 [Timing] 那行对齐, 用来验证起播优化有没有效果
            if (_playWatch != null)
            {
                WriteLibVLCDebug($"[Timing] first-frame={_playWatch.ElapsedMilliseconds}ms\n");
                _playWatch = null;
            }
            // aout(音频输出)就绪后把应用侧记住的音量补写回去:
            // 无 aout 时 libvlc_audio_set_volume 是静默丢弃的(反编译 3.8.0 确认 setter 不看返回值),
            // 长视频远端 DASH 在起播/缓冲/换清晰度期间常出现"用户滚轮调了音量但 aout 还没建好"的窗口,
            // 不补写的话 aout 就绪后音量会回到原生侧的旧值 —— 表现就是"滚轮调音量没反应"。
            // 放在函数最前: 后面 _resumeAfterSwitchMs 的 MarkSeekIssued 可能立刻进入 seek 静音,
            // 顺序反了会把静音状态顶掉(声音先于画面出来)。
            try { _mp.Volume = _volumeCache; } catch { }
            LogAudioStateOnce();
            var willResumeSeek = _resumeAfterSwitchMs > 0;
            if (!willResumeSeek) HideLoading();
            // 把 EOF 时切黑的手势层还原成近透明(见 OnEndReached 里切黑的原因):
            // 新 input 的 vout 已经起来, 后面要能看见画面。
            // ★★ 必须走 <see cref="ReleaseFullscreenCover"/> 而不是直接赋值(2026-10-13 修):
            //   原来这里是 `VideoAreaRoot.Background = VideoAreaBackBrush;` + 顺手把
            //   `_fullscreenCoverArmed = false;` —— 两条都**绕过了 _resizeHold 闸门**。
            //   后果: 拖边缘 resize 期间 LibVLC 反复重协商 vout, 每出一帧就触发一次 Playing
            //   ⇒ 遮罩被撤掉、露出黑底, 下一轮重协商又压黑 —— 用户看到的就是
            //   "视频区边缘黑/亮交替抽动"(同一道闸本来就是为了这个才加的, 见 ArmResizeCover)。
            //   顺带还解释了"拖动时偶尔能看到一块白": 撤罩那一瞬 vout 还没帧, 白底露出来。
            ReleaseFullscreenCover();
            // ★★★ 绝对兜底(2026-10-17 修"点重播黑屏", 第三版)。
            //
            //   前两版分别判 `_fullscreenCoverArmed`(被 OnModalResizeStarted 清零 ⇒ 失效)
            //   与"底色 == Brushes.Black"(理论上无条件成立, 用户仍报黑屏)。两次都失败,
            //   说明不能再猜 —— **要么这个 Playing 压根没触发, 要么压住画面的不是这一层。**
            //   所以把现场全部记进 vlc-debug.txt: 下次复现看日志就能定位。
            //
            //   ★ 特别注意: VideoAreaRoot 是 VideoView.Content 浮层窗口里的一层, 位于
            //   vout HWND **之上**; 若真正压住画面的是 vout HWND **自身的类背景刷**
            //   (见 ApplyHwndHostBlackBackground / VideoBackdropInterop), 改这里就是改错了地方。
            WriteLibVLCDebug(
                $"\n[Playing] armed={_fullscreenCoverArmed} resizeHold={_resizeHold} " +
                $"maxArmed={_maximizeCoverArmed} ended={_ended} " +
                $"bg={DescribeVideoAreaBg()} " +
                $"time={SafeReadTime()} state={_mp.State}\n");
            if (VideoAreaRoot != null && VideoAreaRoot.Background == Brushes.Black)
            {
                _fullscreenCoverArmed = false;
                VideoAreaRoot.Background = VideoAreaBackBrush;
                WriteLibVLCDebug("[Playing] 已强制揭幕手势层\n");
            }
            // ★ 视频没填满时的白色边带(2026-10-17): 起播后要把那块原生窗口底色刷黑。
            //   ★★ 不能只调一次就完事: 实测 `Playing` 触发时 libvlc 的 `Static` 子窗口
            //      **还没建出来**(那一刻拿到的句柄是 0, 修复静默跳过), vout 建好之后才有。
            //      所以这里起一个**有限次重试**的定时器, 命中即停。
            //   ★ 用定时器而不是单个 BeginInvoke: vout 建立耗时随片源/网速变化(实测 200ms~1s),
            //     排一拍常常还是太早。重试上限 10 次 × 120ms ≈ 1.2s, 足够覆盖;
            //     命中后立刻停, 不常驻。
            StartVideoBackdropFix();
            ProbeMediaVideoSizeFromPlayer();
            UpdatePlayButton();
            StartControlsHideCountdown();   // 播放中才开始倒计时自动隐藏
            // 聆听态: 起播后把文案里的秒数对齐到真实位置(切流前后 _mp.Time 会有几十毫秒偏差),
            // 并确保声波律动已经在跑(切清晰度/换片都会重建 Media, 但定时器是窗口级的, 不会断)。
            if (_isListenMode)
            {
                UpdateListenLabel();
                StartListenWave();
            }
            if (_resumeAfterSwitchMs > 0)
            {
                var resume = _resumeAfterSwitchMs;
                _resumeAfterSwitchMs = 0;
                var byStartTime = _resumeByStartTime;
                _resumeByStartTime = false;
                if (!byStartTime) { try { _mp.Time = resume; } catch { } }
                ShowLoading(BufferingText);
                // 走一次 MarkSeekIssued: 钉住进度条/时间显示, 并启动 seek 看门狗
                MarkSeekIssued(resume);
            }
            else
            {
                _resumeByStartTime = false;
            }
            if(_pauseAfterEnhancementSwitch){_pauseAfterEnhancementSwitch=false;_mp.SetPause(true);}
        }));
        // 暂停/停止时同步按钮图标(否则起播后图标停留在初始状态, 看起来像"反了")
        _mp.Paused += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || !ReferenceEquals(player,_mp)) return;
            // ★ 弹幕时钟跟着视频冻结/解冻(用户要求"视频暂停时弹幕不动")。
            //   ★ LibVLCSharp 的 `Paused` 事件签名是 **EventHandler<EventArgs>**(参数里没有
            //     "新的暂停态"), 而 `MediaPlayer` 也**没有** `IsPaused` 属性 —— 只有 `IsPlaying`。
            //     播完(EndReached)时 IsPlaying 同样为 false, 顺带把弹幕也停了, 正合需要。
            //     这里的一次原生调用没问题 —— 暂停是低频事件, 与逐帧路径(必须零原生调用)
            //     不在一条路上。
            try { SetDanmakuClockRunning(_mp.IsPlaying); } catch { /* 原生对象可能已失效 */ }
            UpdatePlayButton();
            ShowControls();   // 暂停时控制栏常显(没有"遮挡内容"的问题, 且用户多半要操作)
        }));
        _mp.EncounteredError += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || !ReferenceEquals(player,_mp)) return;
            if (TryAutoRecoverFromError()) return;
            HideLoading();
            FailOverlay.Visibility = Visibility.Visible;
            var msg = "视频播放出错, 请检查网络或尝试其他清晰度";
            FailText.Text = msg;
            // 诊断: 把出错时正在播的 URL 也写进 debug 文件
            WriteLibVLCDebug(
                $"\n[EncounteredError] bvid={_currentBvid} cid={_currentCid} lastUrl={_lastPlayUrl}\n");
        }));

        // ★★★ vout 建立/重建的那一刻就把原生窗口底色处理掉(2026-10-17, 修"最大化/还原后白块")。
        //   为什么必须要这个事件、不能只靠定时器: libvlc 的 `Static` 子窗口是**新建**出来的,
        //   它第一次上屏时就已经是白的了 —— 定时器最快也要 120ms 之后才轮到, 那 120ms
        //   在浅色主题下就是用户看到的那一下"白块"。何况最大化/还原/换清晰度都会重建它。
        //   ⇒ 挂在这里: vout 一出现就立刻刷黑 + 起一轮短时守护。
        // ★★ 这里**故意同步执行、不走 Dispatcher**(与别的事件处理器不同):
        //   `Vout` 在 libvlc 的 vout 线程上触发, 而"改类背景刷"是纯 Win32 调用,
        //   不碰任何 WPF 对象 —— 走 Dispatcher 会把动作推迟到下一轮消息循环, 就晚了。
        //   原生回调里抛异常会直接终结进程, 所以整段包在 try 里。
        // ★ 参数 `Count` = 当前 vout 个数; 为 0 表示 vout 被销毁, 不必处理。
        _mp.Vout += (_, e) =>
        {
            if (_closing || !ReferenceEquals(player,_mp) || e.Count <= 0) return;
            try
            {
                var hwnd = FindVideoStaticWindow();
                if (hwnd != IntPtr.Zero) VideoBackdropInterop.PaintClassBackgroundBlack(hwnd);
            }
            catch { /* 原生回调里绝不能抛 */ }

            // UI 线程上再起一轮短时守护(兜住"vout 稍后才建好"与"稍后又被重绘"）
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closing || !ReferenceEquals(player,_mp)) return;
                    // ★★★ 重播黑屏的**主修**(2026-10-17): 这里才是可靠的揭幕时机。
                    //
                    //   `Playing` 那侧连修三次都没生效, 因为 vout 窗口是**独立于 MediaPlayer
                    //   状态**建起来的: 重播时 input 确实在播(Time 在走), 但若 `Playing`
                    //   因 EOF→新 input 的状态切换而没再发一次, 那一侧的兜底就一次都不跑,
                    //   手势层的不透明黑永久留着 —— 而它位于 vout HWND 之上, 于是"黑幕后面
                    //   视频正常播放"(用户原话)。
                    //   ★ `Vout` 每次重建 vout 都发, 重播必然过这里 ⇒ 揭幕不会漏。
                    //   顺带解释了"双击全屏后恢复": 全屏必然重建 vout, 于是顺带把黑幕撤了。
                    if (VideoAreaRoot != null && VideoAreaRoot.Background == Brushes.Black)
                    {
                        _fullscreenCoverArmed = false;
                        VideoAreaRoot.Background = VideoAreaBackBrush;
                        WriteLibVLCDebug("[Vout] vout 重建, 强制揭幕手势层\n");
                    }
                    StartVideoBackdropFix();
                    ProbeMediaVideoSizeFromPlayer();
                }));
            }
            catch { /* 窗口正在销毁 */ }
        };

    }

    private void OnVmemVideoSizeChanged(uint width, uint height)
    {
        if (_closing || _isLive || width == 0 || height == 0) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing || _isLive) return;
                ProbeMediaVideoSizeFromPlayer();
                if (!_videoSizeFromMetadata && _detectedVideoWidth <= 0)
                    UpdateDetectedVideoSize((int)width, (int)height, fromMetadata: false);
            }));
        }
        catch { }
    }

    private void ProbeMediaVideoSizeFromPlayer()
    {
        if (_closing || _isLive || _videoSizeFromMetadata || _mp == null) return;
        uint w = 0, h = 0;
        try
        {
            var tracks = _mp.Media?.Tracks;
            if (tracks != null)
            {
                foreach (var t in tracks)
                {
                    if (t.TrackType != TrackType.Video) continue;
                    var v = t.Data.Video;
                    if (v.Width > 0 && v.Height > 0)
                    {
                        w = v.Width;
                        h = v.Height;
                        if (v.Orientation is VideoOrientation.LeftTop or VideoOrientation.RightTop
                            or VideoOrientation.RightBottom or VideoOrientation.LeftBottom)
                            (w, h) = (h, w);
                        break;
                    }
                }
            }
            if ((w == 0 || h == 0) && _vmemMode && VmemView != null)
                (w, h) = VmemView.VideoSize;
            if (w == 0 || h == 0)
                _mp.Size(0, ref w, ref h);
        }
        catch { return; }
        if (w > 0 && h > 0)
            UpdateDetectedVideoSize((int)w, (int)h, fromMetadata: false);
    }

    private bool PlayMedia(Media media)
    {
        try
        {
            // 首帧计时只用于诊断日志: 每次起播(含切清晰度/重播/直播)都会重置。
            _playWatch = Stopwatch.StartNew();
            if (_vmemMode) VmemView.UseCompatibleColorConversion = _currentQn is 125 or 126;
            VideoDecode.BeginMedia(_libVLC);
            var ok = _mp.Play(media);
            if (!ok) _playWatch = null;   // 没播起来就别留一个永远等不到 Playing 的计时器
            return ok;
        }
        finally
        {
            try { media.Dispose(); } catch { /* 播放器持有它自己那份引用, 这里失败不影响播放 */ }
        }
    }

    /// <summary>
    /// 喂给 LibVLC 的 UA。B 站 CDN 会校验, 用浏览器 UA 最稳;
    /// 点播/直播/重播都必须带上同一份, 所以提出来共用。
    /// </summary>
    private const string PlayerUserAgent = HttpDefaults.UserAgent;

    /// <summary>
    /// 构造带 CDN 头的 Media。url 既可能是在线地址(DASH 视频流 / durl 单流), 也可能是
    /// 本地文件(离线缓存、或短视频的本地合流缓存)。
    /// DASH 流音视频分离, 用 input-slave 挂音轨; 单文件源(durl 合流 / 本地文件)传 null。
    ///
    /// ★★ 这里顺带决定两件事(2026-10-17 播放器手术):
    ///   ① demuxer —— 单文件源加 `:demux=avformat`, 带音轨的那条**绝不能**加(会 Ended 卡死);
    ///   ② <see cref="_isReliableSeekSource"/> —— 有没有挂 input-slave 就是判据。
    /// 两条都只在这里设置一次, 别处不要再改(漏一处就会让看门狗按错的类型兜底)。
    ///
    /// 本地路径必须走 FromPath: `new Uri("C:\...\x.mp4")` 会把盘符当成协议名, 抛 UriFormatException。
    /// </summary>
    private Media BuildMedia(string url, string? audioUrl, long startMs = 0)
    {
        // B站 CDN 校验 Referer 和 User-Agent
        var options = new List<string>
        {
            ":http-referrer=https://www.bilibili.com/",
            ":http-user-agent=" + PlayerUserAgent
        };

        var isRemoteHttp = url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (isRemoteHttp)
        {
            // HTTP 长连接被 CDN 静默掐断后允许 VLC 自动重连(issue #81 第一道防线)
            options.Add(":http-reconnect");
            if (startMs > 0)
                options.Add(":start-time=" +
                            (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        }

        // Upstream v1.6.3: libvlc's MP4 demux drops the last fMP4 fragment.
        // Use avformat on every VOD source; existing seek recovery remains in place.
        options.Add(":demux=avformat"); // v1.6.3: read the final Bilibili fMP4 fragment.
        var isListenAudio = _isListenMode &&
                            string.Equals(url, _listenAudioUrl, StringComparison.Ordinal);
        if (string.IsNullOrEmpty(audioUrl) && !isListenAudio)
        {

            _isReliableSeekSource = true;
        }
        else if (!string.IsNullOrEmpty(audioUrl))
        {
            // DASH 双流: 远距离 seek 实测会被 libvlc 丢掉(位置掉回开头), 保留旧路兜底
            _isReliableSeekSource = false;
            options.Add(":input-slave=" + audioUrl);
        }
        else
        {
            // 聆听音轨仍保留 seek 看门狗，与解封装器选择无关。
            _isReliableSeekSource = false;
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var local = new Media(_libVLC, url, FromType.FromPath);
            foreach (var o in options) local.AddOption(o);
            return local;
        }

        // 起播缓冲(2026-10-30 新增, 设置页「起播缓冲」): 只加给网络流。
        // ★ 用**媒体级**选项而不是重建 LibVLC: 全局实例是进程单例, 改它等于要重启内核;
        //   媒体级选项下一条视频就生效, 且能各走各的(直播也一样)。
        // ★ 2026-11-01: 值不再直接取用户设置 —— 统一走 BufferPolicy(自动/手动两档),
        //   直播/自动档的规则见 Services/BufferPolicy.cs。
        var isShortVideo = _totalMs > 0 && _totalMs <= 60_000;
        var kind = isListenAudio ? BufferMediaKind.ListenAudio
            : string.IsNullOrEmpty(audioUrl) ? BufferMediaKind.VodSingle
            : BufferMediaKind.VodDash;
        var cachingMs = BufferPolicy.Resolve(kind, url, isShortVideo,
            Svc.Settings.NetworkCachingMs, Svc.Settings.AutoBufferPolicy);
        WriteLibVLCDebug($"[BufferPolicy] kind={kind} short={isShortVideo} nc={cachingMs}ms " +
                         $"auto={Svc.Settings.AutoBufferPolicy} measured={CdnService.Instance.LastMeasuredBytesPerSecond}B/s\n");
        options.Add(":network-caching=" + cachingMs);
        return new Media(_libVLC, new Uri(url), options.ToArray());
    }

    // ------------------------------------------------------------ 短视频本地合流(边播边下)

    /// <summary>后台合流任务。换片/切清晰度/关窗都要取消它 —— 否则会白下几十兆, 还可能把旧片写进新键</summary>
    private CancellationTokenSource? _remuxCts;

    /// <summary>
    /// 本会话里"下载超时过"的 bvid —— 不再为它尝试本地合流。
    ///
    /// ★ 为什么需要: 2.5 秒预算到点后回落到远端起播, 但**下一次再点开同一个视频**又会
    ///   从头等 2.5 秒。记下来之后, 本会话内直接流式起播, 用户不会再看到第二次转圈。
    ///   不持久化: 换网络/重启后值得重新试一次。
    /// </summary>
    private readonly HashSet<string> _remuxSlow = new(StringComparer.Ordinal);

    /// <summary>
    /// 合流任务的"代次"。换片/切清晰度都要作废上一次的在飞任务 ——
    /// 直接比字段(比如"cid 还一样吗")容易漏(切清晰度时 cid 是不变的), 用单调递增的代次最省心。
    /// </summary>
    private int _remuxGen;

    /// <summary>
    /// 加载代次。换片 / 换分P 只要再次进入 <see cref="LoadVideoAsync"/>, 代次就 +1 ——
    /// 上一次还在飞的加载回来时必须立刻作废, 否则会把旧视频的详情/地址/本地文件灌进新视频
    /// (表现是"换片后标题对了、画面还是上一条")。
    /// ★ 与 _remuxGen 同款, 但管的是**整条加载链**, 不只是合流任务。
    /// </summary>
    private int _loadGen;

    /// <summary>
    /// 是否有一次 LoadVideoAsync 正在飞。
    /// ★ 用来挡住 OnLoaded 的重复启动: 新窗口第一次打开时 PlayerService 先调 PlayVideo,
    ///   Loaded 事件随后才到 —— 没有这个闸门就会跑两遍详情/取流/测速, 甚至
    ///   "先远端起播、下完再切本地"二次重启(2026-10-30 起播慢的隐藏原因之一)。
    /// </summary>
    private bool _loadRunning;

    /// <summary>
    /// 本次 <see cref="PlayMedia"/> 到首帧(Playing)的计时器, 只用于 [Timing] 诊断日志。
    /// 打完即置空, 避免 seek 后重复的 Playing 事件把首帧耗时刷成无意义的数。
    /// </summary>
    private Stopwatch? _playWatch;

    /// <summary>
    /// 短视频: 下载两路并合流成本地文件, 期间在加载遮罩上显示进度。
    /// 返回合流好的本地路径; 失败 / 超时 / 被取消返回 null(调用方回落到远端 DASH)。
    ///
    /// 为什么是"下完再播"(用户 2026-09-25 选定):
    ///   前一版是"边播边下、就绪后换源"。换源那一下真机上暴露了两个问题 —— 跳转后画面花屏、
    ///   音轨不同步(根因见 DashRemuxer 里 mfhd 序列号那条)。下完再播从头到尾都是**同一个输入**,
    ///   没有换源这一步, 问题面小得多。
    ///
    /// ★ 2026-10-30 收窄: 只有"≤60 秒 且 ≤8MB"的极短片会走到这里, 且总预算 2.5 秒。
    ///   旧版对所有 ≤3 分钟的视频都先整片下载(45 秒上限), 起播被拖成 10~45 秒。
    ///
    /// 进度是"两路已下字节 / 按码率估算的总字节": 估算值来自接口的 bandwidth, 与实际有偏差,
    /// 所以到 100% 之后还可能要等一下合流(那步是纯内存拷贝, 很快)。
    /// </summary>
    private async Task<string?> PrepareShortVideoAsync(DashStreams spec, int qn)
    {
        // 本会话已经为它白等过 2.5 秒 ⇒ 直接流式起播, 不再重试。
        // ★ 必须放在创建 CTS / 清遮罩文案之前: 早退不该留下一个没人用的 CTS。
        var bvid = _currentBvid;  // 别在后台线程上读字段: 先取到本地变量
        if (_remuxSlow.Contains(bvid)) return null;

        CancelRemuxPrefetch();
        var cts = new CancellationTokenSource();
        _remuxCts = cts;
        var gen = _remuxGen;      // CancelRemuxPrefetch 里已经自增过, 这就是"本次任务"的代次

        SetLoadingText("正在准备高清播放…");
        try
        {
            // Progress<T> 在创建它的线程(UI 线程)上回调, 所以这里可以直接碰控件
            var progress = new Progress<double>(p =>
            {
                if (_closing || gen != _remuxGen) return;
                SetLoadingText(p > 0
                    ? $"正在下载高清视频 {p * 100:0}%"
                    : "正在准备高清播放…");
            });

            var result = await ShortVideoCache.PrepareAsync(bvid, qn, spec, cts.Token, progress);
            // 期间用户可能已经换片/切清晰度/关窗: 代次变了就丢掉这次结果
            if (_closing || gen != _remuxGen) return null;
            if (result.TimedOut) _remuxSlow.Add(bvid);
            if (result.Path == null) SetLoadingText("正在缓冲视频…");   // 合流没成 -> 走远端, 文案还原
            return result.Path;
        }
        finally
        {
            if (ReferenceEquals(_remuxCts, cts))
            {
                _remuxCts = null;
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// 取消正在进行的合流任务(换片 / 切清晰度 / 关窗都调它)。
    /// 用单调递增的代次作废在飞任务: 只比"cid 还是不是同一个"会漏 —— 切清晰度时 cid 不变。
    /// </summary>
    private void CancelRemuxPrefetch()
    {
        _remuxGen++;
        try { _remuxCts?.Cancel(); } catch { /* 已经结束 */ }
        _remuxCts?.Dispose();
        _remuxCts = null;
    }

    /// <summary>
    /// 当前分P 挂在标题后面的后缀, 如 <c>" - P12 S05E08 收获"</c>。
    /// 不是多P 视频、或当前 cid 在 pages 里定位不上, 都返回空串(标题一个字不动)。
    /// </summary>
    private string CurrentPageSuffix(VideoDetail detail)
    {
        if (detail.Pages.Count <= 1 || _currentCid <= 0) return "";
        foreach (var p in detail.Pages)
            if (p.Cid == _currentCid)
            {
                var part = p.Part?.Trim();
                return string.IsNullOrEmpty(part) || string.Equals(part, detail.Title.Trim(), StringComparison.Ordinal)
                    ? $" - P{p.Page}" : $" - P{p.Page} {part}";
            }
        return "";
    }

    private void UpdateUiFromDetail(VideoDetail detail)
    {
        // ★ 多P 时标题后面挂上"当前这一段"(2026-10-11):
        //   40 段的纪录片每段标题都一样("克拉克son's农场 1-5季"), 不标出来用户根本分不清
        //   自己在第几段, 切窗口(Alt-Tab)回来也是一脸茫然。官方网页版同样在标题里带 part。
        //   单P 视频一个字符都不多写。
        var pageSuffix = CurrentPageSuffix(detail);
        var displayTitle = detail.Title + pageSuffix;

        TitleText.Text = displayTitle;
        Title = displayTitle + " - BiliTouch";
        // ★ _videoTitle 保持**不含分P**: 它是要往历史/上报里写的视频名, 那是稿件的标题,
        //   不是"我现在看到第几段"。混进去会让同一稿件在历史里出现几十条重名记录。
        _videoTitle = detail.Title;
        VideoTitleText.Text = displayTitle;
        ViewCountText.Text = VideoItem.FormatCount(detail.ViewCount);
        DanmakuCountText.Text = VideoItem.FormatCount(detail.DanmakuCount);
        PubDateText.Text = detail.Pubdate > 0
            ? DateTimeOffset.FromUnixTimeSeconds(detail.Pubdate).LocalDateTime.ToString("yyyy-MM-dd")
            : "";
        UpNameText.Text = string.IsNullOrEmpty(detail.Owner) ? "UP 主" : detail.Owner;
        UpDescText.Text = "查看个人主页";
        DescText.Text = string.IsNullOrEmpty(detail.Desc) ? "这个 UP 主很懒, 什么都没写~" : detail.Desc;
        // ★ 换片 ⇒ 折叠态复位到**默认值(展开)**: 与构造时的默认一致。
        //   不写成 false 是因为用户要求"简介默认展开" —— 换片后也应保持一致,
        //   否则"第一条展开、第二条收起"会让人以为状态丢了。
        _descExpanded = true;
        // 宽度可能还没变(信息栏固定 400px), SizeChanged 这次不会触发, 得主动判一次
        UpdateDescFold();

        // 相关视频: 与详情一起取, 走后台, 不挡起播(见 LoadRelatedAsync 的代次闸门)
        _ = LoadRelatedAsync(detail.Bvid);

        UpdateButtonState();
        // 切换视频时重置各操作按钮状态(随后的异步查询会刷新为真实值)
        _isLiked = false;
        _isFollowed = false;
        // ★ 投币态**不能**取自详情(那个字段根本不存在, 见 GetCoinCountAsync 的说明),
        //   这里先复位, 真实值由随后的 RefreshCoinStateAsync 异步补上。
        _isCoined = false;
        ThemedCoinIcon.Count = 0;
        // 计数直接来自详情(view 接口的 stat), 比旧的"点赞/投币/收藏"三个字信息量大
        _likeCount = detail.LikeCount;
        _coinCount = detail.CoinCount;
        _favCount = detail.FavoriteCount;
        UpdateActionCounts();
        SetFavorited(false);
        // 与点赞同款: 详情带不来"本人是否投过币", 换片后单独查一次, 否则投币键恒灰
        _ = RefreshCoinStateAsync();
        // 「稍后再看」的点亮态不在这里复位 —— 它统一在 ResetForNewMedia 里清,
        // 因为直播/本地文件那两条路根本不会走到这里。
        UpdateFollowButton();
        // UP 主头像优先用 owner.face(之前误用了视频封面)
        _ownerFace = detail.OwnerFace ?? "";
        // ★ 先暖缓存再走真正的贴图: 这行只是把下载+解码发出去(不碰 UI、不 await),
        //   LoadUpAvatarAsync 随后命中的就是同一张(同一份 CoverLoader 缓存键),
        //   于是头像在"标题已经出来"的那一刻就有图, 不用等它自己那趟网络。
        PrimeUpAvatar(_ownerFace);
        _ = LoadUpAvatarAsync(_ownerFace);
        // 视频封面: 「听视频」聆听态要拿它当视觉锚点(一眼知道在听什么)。
        // 顺手记下来, 不必等用户点了聆听再去请求详情接口。
        _coverUrl = detail.Pic ?? "";
        PopulateDockAndLoadingPosterAsync(_coverUrl);
        if (_isListenMode)
        {
            if (ListenOverlayTitle != null) ListenOverlayTitle.Text = detail.Title;
            LoadListenCover(_coverUrl);
        }

        // 本机观看历史已移除, 这里不再写本地记录。
        // 观看记录仍然会上报到云端 —— 见下方的 Svc.HistorySync。ReportAsync。
    }

    private async Task LoadUpAvatarAsync(string url)
    {
        if (string.IsNullOrEmpty(url) || _closing) return;
        var gen = _loadGen;
        var img = await CoverLoader.LoadAsync(url, UpAvatarDecodeWidth);
        if (img == null || _closing || gen != _loadGen || !string.Equals(_ownerFace, url, StringComparison.Ordinal)) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closing || gen != _loadGen || !string.Equals(_ownerFace, url, StringComparison.Ordinal)) return;
            var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            UpAvatarBox.Background = brush;
            UpAvatarFallback.Visibility = Visibility.Collapsed;
        });
    }

    private void ShowFail(string msg)
    {
        HideLoading();
        FailOverlay.Visibility = Visibility.Visible;
        FailText.Text = msg;
    }

    // ------------------------------------------------------------ 加载遮罩(含颜文字彩蛋)

    // 颜文字轮换的间隔。700ms 一换: 比一次典型的网络请求慢半拍, 像是"陪伴"而不是"闪烁"。
    private const int KaomojiIntervalMs = 700;
    private static readonly string[] KaomojiList =
    {
        "(´･ω･`)", "(･ω･)つ", "ヾ(≧▽≦*)o", "(๑•̀ㅂ•́)و✧", "(っ•̀ω•́)っ", "┌(・。・)┘♪"
    };
    private DispatcherTimer? _kaomojiTimer;
    private int _kaomojiIndex;

    /// <summary>
    /// 遮罩的淡入/淡出代次。
    /// 用途: 淡出动画跑完时, 如果这期间又被 ShowLoading 重新叫起(seek 卡顿反复触发就会),
    /// 不能再把遮罩藏掉 —— 用代次比对即可判断"我这一层还是不是当前的"。
    /// </summary>
    private int _loadingGen;

    /// <summary>遮罩淡入时长</summary>
    private const int LoadingFadeInMs = 130;

    /// <summary>遮罩淡出时长。比淡入稍长: 收起时慢一点, 画面过渡更自然。</summary>
    private const int LoadingFadeOutMs = 170;

    /// <summary>显示加载遮罩(顺带启动颜文字轮换)。加载遮罩统一从这里走, 不要直接碰 Visibility</summary>
    private void ShowLoading(string message)
    {
        if (_closing || LoadingOverlay == null) return;
        _loadingGen++;
        LoadingText.Text = message;

        if (LoadingOverlay.Visibility != Visibility.Visible)
        {
            // 首次显示: 基准值设 0, 再淡到 1 —— 别"啪"地糊一层黑上来
            LoadingOverlay.Opacity = 0;
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        // 取"当前透明度"(挂了动画时拿到的是动画值)。已经在 1 上就不重播,
        // 否则每次换文案("获取视频信息"→"获取播放地址")都会闪一下。
        var from = LoadingOverlay.Opacity;
        if (from < 1)
        {
            LoadingOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(LoadingFadeInMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        StartKaomoji();
    }

    /// <summary>遮罩已经可见、只是换个文案(比如"获取视频信息"→"获取播放地址")</summary>
    private void SetLoadingText(string message)
    {
        if (_closing) return;
        LoadingText.Text = message;
        StartKaomoji();
    }

    /// <summary>收起加载遮罩(淡出)。所有收起路径都必须走这里</summary>
    private void HideLoading()
    {
        _kaomojiTimer?.Stop();
        _kaomojiTimer = null;

        if (_closing || LoadingOverlay == null) return;
        if (LoadingOverlay.Visibility != Visibility.Visible) return;

        var gen = ++_loadingGen;
        var from = LoadingOverlay.Opacity;
        if (from <= 0.001)
        {
            FinishHideLoading();
            return;
        }

        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(LoadingFadeOutMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            // 淡出期间又被 ShowLoading 接管(卡顿反复触发时会遇到) -> 不能藏
            if (_closing || gen != _loadingGen) return;
            FinishHideLoading();
        };
        LoadingOverlay.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>真正把遮罩藏起来(同时把基准透明度归零, 下次显示才能从 0 淡入)</summary>
    private void FinishHideLoading()
    {
        LoadingOverlay.BeginAnimation(OpacityProperty, null);
        LoadingOverlay.Opacity = 0;
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>颜文字轮播。定时器常驻到遮罩收起为止, 避免每次 ShowLoading 都新建</summary>
    private void StartKaomoji()
    {
        if (_kaomojiTimer != null) return;
        LoadingKaomoji.Text = KaomojiList[_kaomojiIndex % KaomojiList.Length];
        _kaomojiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(KaomojiIntervalMs) };
        _kaomojiTimer.Tick += (_, _) =>
        {
            if (_closing || LoadingOverlay.Visibility != Visibility.Visible)
            {
                HideLoading();
                return;
            }
            _kaomojiIndex++;
            LoadingKaomoji.Text = KaomojiList[_kaomojiIndex % KaomojiList.Length];
        };
        _kaomojiTimer.Start();
    }

    // ------------------------------------------------------------ 播放控制

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_mp == null) return;

        // 已经播完了: 按播放键就是"重播"。LibVLC 到 EOF 之后直接 Play() 有时不会回到开头,
        // 显式把时间归零最稳。
        if (_ended)
        {
            RestartFromBeginning();
            return;
        }

        if (_mp.IsPlaying) _mp.Pause();
        else _mp.Play();
        UpdatePlayButton();

        // 暂停是一次"自然的段落结束", 补报一次让云端记录的进度与用户当下的位置一致
        // (否则用户看到一半暂停去网页端, 只能续播到最多 15 秒前的旧位置)
        if (!_mp.IsPlaying) SyncProgressNow();
    }

    /// <summary>
    /// 从头重播(播完之后点播放键 / 点遮罩上的「重播」)。
    ///
    /// 这里**不能**只写 `_mp.Time = 0; _mp.Play();` —— 那正是"点了重播没反应"的根因:
    /// LibVLC 到 EOF 之后, 那条 input 已经结束了, 对它发 seek 和 play 都会被丢掉
    /// (状态已经是 Ended, 不再处理控制指令)。所以必须**重新丢一条 Media 进去**,
    /// 让 LibVLC 起一个全新的 input —— 和"点另一个视频"走的是同一条路, 那条路一直是好的。
    ///
    /// 三个来源分别重建: 直播重新拉流、本地文件用 FromPath、在线视频重建音视频组合
    /// (DASH 的音轨是 input-slave 挂上去的, 不带上它重播就只有画面没有声音)。
    /// </summary>
    private void RestartFromBeginning()
    {
        if (_closing || _mp == null) return;
        try
        {
            // ★ 先把"能不能重播"验完, 再动界面状态(2026-10-17 修"点重播黑屏"):
            //   早退发生在验完之后 —— 原来先清 `_ended` / 收起 EndOverlay 再验地址,
            //   于是地址取不到(直播失效、本地文件已被删)时直接 return:
            //   遮罩已经收起、"重播"按钮也没了, 手势层又还压着 EOF 那层不透明黑
            //   (见 OnEndReached), 而新 input 永远不会起来 ⇒ 屏幕上一片死黑,
            //   连重试的入口都没有。验不过就什么都不改, 让用户还能再点一次。
            if (_isLive)
            {
                // 直播没有"从头"的概念: 重新连一次拿一条新的流地址
                // (LoadLiveAsync 自己会重置遮罩与加载提示, 所以这里先不管本地状态)
                _ = LoadLiveAsync(_liveRoomId);
                return;
            }

            if (_isLocalPlayback)
            {
                var path = _lastPlayUrl;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                EnterReplayState();
                ShowLoading("正在重新播放…");
                var local = new Media(_libVLC, path, FromType.FromPath);
                local.AddOption(":demux=avformat");
                PlayMedia(local);
            }
            else
            {
                var url = _lastPlayUrl;
                if (string.IsNullOrEmpty(url)) return;
                EnterReplayState();
                // 给个加载提示: 重播要重新拉一次流, 几秒内不会有画面 ——
                // 没有提示的话用户会以为"点了没反应"(这正是之前那版的观感)。
                // 遮罩由 Playing 事件负责收起。
                ShowLoading("正在重新播放…");
                // ★ 聆听态下重播仍然只拉音轨: 重播是"再听一遍", 不该突然开始下视频。
                if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
                    PlayMedia(BuildMedia(_listenAudioUrl!, null));
                else
                    PlayMedia(BuildMedia(url, _lastAudioUrl));
            }

            // ★★ 2026-10-17 删掉这里的 `MarkSeekIssued(0)`(重播曾报"无限缓冲 + 音频重复")。
            //   它是个**假跳转**: 重播并不需要 seek(新 Media 本来就从 0 开始), 却让看门狗
            //   以为"有一次跳到 0 的 seek 还没确认", 于是:
            //     ① 2.5s 内没到位就走重建分支 → RebuildMediaAndSeek(0) → **又建一次 Media**,
            //        第二次重新拉流 ⇒ 用户看到"进度条在跑、画面却一直缓冲"(无限缓冲);
            //     ② 两次 input 的音轨叠在一起 ⇒ "音频重复播放"。
            //   重播只需要把进度条/时间归零, 不需要任何 seek 机制。
            if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
            _pendingSeekMs = -1;
            _seekObservedMs = -1;
            _anchorMs = -1;
            _displayedMs = -1;
            UpdatePlayButton();
        }
        catch { /* 播放器可能已失效 */ }
    }

    /// <summary>
    /// 真正要起播一个新 input 了, 才把"已播完"这套状态收掉。
    ///
    /// ★★★ 2026-10-17 重播黑屏**真正的修法**在这里(前几次都修错了地方):
    ///   EOF 时 <see cref="OnEndReached"/> 把手势层压成不透明黑, 用来盖住已被拆掉的
    ///   vout HWND 露出的白底。**重播根本不需要那层黑** —— 新 input 的 vout 建好之前
    ///   只有几百毫秒, 而且那种情况下露出的是"背景白", 由 <see cref="LoadingOverlay"/>
    ///   (重播时已经显示"正在重新播放…")全程盖着, 手势层压根不必参与。
    ///   ⇒ 所以在**起播之前**就把手势层还原。原先指望 `Playing`/`Vout` 事后揭幕,
    ///   两者都不可靠: EOF 刻意不调 `_mp.Stop()`(见 OnEndReached), 那条 input 的 vout
    ///   可能并未被销毁, `Vout` 因而不发; 而 `Playing` 在 EOF→新 input 的状态切换里
    ///   也可能不再触发一次。结果黑层就永久留下了 —— 而它在 vout HWND **之上**,
    ///   于是"幕布后面视频正常播放"(用户原话)。
    ///   ★ 也解释了"双击全屏后恢复": 全屏必然重建 vout, 顺带把黑幕撤掉了。
    /// </summary>
    private void EnterReplayState()
    {
        _ended = false;
        EndOverlay.Visibility = Visibility.Collapsed;
        // 重播是同一片源的全新 input: 自救计数清零, 之后遇到提前 EOF 仍能续播
        _eofResumeCount = 0;
        _lastEofResumeMs = 0;
        _tailVideoOnlyResumed = false;
        // 揭掉 EOF 那层黑(见上面说明: 重播期间不需要它, 遮罩由 LoadingOverlay 负责)
        if (VideoAreaRoot != null && VideoAreaRoot.Background == Brushes.Black)
        {
            _fullscreenCoverArmed = false;
            VideoAreaRoot.Background = VideoAreaBackBrush;
            WriteLibVLCDebug("[Replay] 起播前揭掉 EOF 黑层\n");
        }
    }
    private void OnReplayClick(object sender, RoutedEventArgs e) => RestartFromBeginning();

    /// <summary>强制上报一次当前进度(暂停 / 关闭窗口时用), 绕过节流间隔判断</summary>
    private void SyncProgressNow()
    {
        if (_closing || _mp == null || _isLocalPlayback) return;
        if (_aid <= 0 || _currentCid <= 0) return;
        var progressSec = _mp.Time / 1000;
        _ = Svc.HistorySync.ReportNowAsync(_aid, _currentCid, progressSec);
    }

    private void UpdatePlayButton()
    {
        if (_closing || BtnPlay == null) return;
        // 标准语义: 正在播放时显示"暂停"图标(双竖条), 停止/暂停时显示"播放"图标(三角)
        // 用矢量 Path 而非字体图标, 彻底避免字体缺失时显示为方块的问题
        BtnPlay.Content = _mp.IsPlaying ? CreatePauseIcon() : CreatePlayIcon();
    }

    private static SkinActionIcon CreatePlayIcon() => PlayerGlyphs.Play();

    private static SkinActionIcon CreatePauseIcon() => PlayerGlyphs.Pause();


    /// <summary>进度条值变化: 点击轨道任意位置直接跳转(IsMoveToPointEnabled + 本处理器)。
    /// 拖动过程由 _isSeeking 屏蔽, 松手才生效</summary>
    private void OnProgressValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_closing || _mp == null || _isSeeking || _updatingSlider) return;
        if (_mp.Length <= 0) return;

        // 用 _lastKnownMs 而非 _mp.Time: LibVLC seek 受理后 _mp.Time 会立刻回执目标值, 会吞掉真实偏离判断
        var targetMs = (long)Math.Round(e.NewValue / 1000.0 * _mp.Length);
        var currentMs = _lastKnownMs > 0 ? _lastKnownMs : _mp.Time;
        if (Math.Abs(currentMs - targetMs) < 250) return;
        SeekTo(targetMs);
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || (sender is MediaPlayer source && !ReferenceEquals(source,_mp))) return;
            _totalMs = e.Length;
            UpdateTotalDurationText();
        }));

    /// <summary>上一次 TimeChanged 的位置(遮罩安全网用来判"位置真的在推进")。-1 = 还没收到。</summary>
    private long _lastLoadingRevealMs = -1;

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || (sender is MediaPlayer source && !ReferenceEquals(source,_mp))) return;
            // 记下最后已知位置: EOF 之后 _mp.Time 会归零(那条 input 已结束),
            // "提前结束自救"只能靠这里留底
            //
            // ★ 只收**合法值**: libvlc 在 input 重建 / 分段 seek 途中会报出 -1 或
            //   超大值(实测读到 18446744073709551 —— ulong 溢出), 一旦写进 _lastKnownMs,
            //   依赖它的分段跳/看门狗就会算出一个荒谬的目标(2026-10-17 修 seek 时踩到)。
            if (e.Time >= 0 && e.Time < 24L * 3600 * 1000)
                _lastKnownMs = e.Time;
            // ★ 全屏黑幕: 能收到时间推进 = 视频正在出帧, 这一刻撤掉最准。
            //   不能只靠 Playing 事件 —— 进全屏时视频本来就在播, Playing 不会再来一次。
            ReleaseFullscreenCover();
            // ★★ 2026-10-31 遮罩安全网(修"打开长视频黑屏"的兜底):
            //   "正在跳转…""正在缓冲视频…"这类遮罩**只由 Playing 事件收掉**。万一那次
            //   play() 没被接受(或被 LibVLC 直接忽略), Playing 永远不来, 遮罩就一直盖着
            //   画面 —— 用户看到的就是"黑屏"(其实背后可能已经在播)。
            //   这里加一道客观判据: **位置真的在推进**(连续两次 TimeChanged 且值在涨)说明
            //   画面已经在出帧, 遮罩没有存在意义, 强制收起。
            //   ★ 只收"跳转/缓冲"这一类, 不动"正在切换清晰度""正在获取播放地址" ——
            //     那些是加载链自己的生命周期(见 HideBufferingIfShown 的同款约定)。
            if (e.Time >= 0 && e.Time != _lastLoadingRevealMs)
            {
                if (_lastLoadingRevealMs >= 0 && e.Time > _lastLoadingRevealMs &&
                    LoadingOverlay != null && LoadingOverlay.Visibility == Visibility.Visible &&
                    LoadingText != null &&
                    (IsStallOverlayText(LoadingText.Text) ||
                     LoadingText.Text.StartsWith("正在缓冲", StringComparison.Ordinal) ||
                     LoadingText.Text.StartsWith("正在跳转", StringComparison.Ordinal) ||
                     LoadingText.Text.StartsWith("正在打开本地文件", StringComparison.Ordinal) ||
                     LoadingText.Text.StartsWith("正在重新播放", StringComparison.Ordinal) ||
                     LoadingText.Text.StartsWith("正在继续播放", StringComparison.Ordinal)))
                {
                    HideLoading();
                }
                _lastLoadingRevealMs = e.Time;
            }
            // ★ 这里**不**调 UpdateProgressUi —— 进度条与跳转看门狗的唯一心跳是 _progressTimer。
            //   曾经这里也在调, 于是进度 UI 有两个时钟: 等待期定时器 100ms 一跳, 而 TimeChanged
            //   会在两跳之间插进来。这不只是重复干活 —— 看门狗判"落地但偏早"要求
            //   **连续两次心跳**都成立(见 UpdateProgressUi), 那道闸的用意是挡掉 input 重启
            //   那一瞬的过渡位置; 双时钟会把"两次采样"压到 1ms 内, 过渡值照样骗过它,
            //   后果是 seek 静音提前解除、缓冲提示提前消失(画面没回来声音先到)。
            //   定时器全程在跑(构造里 Start, 等待期 100ms / 平时 500ms), 少这一个调用不丢刷新。
            DispatchDanmaku();
            MaybeSyncProgress();
        }));

    /// <summary>
    /// 播放中周期上报进度到云端历史。
    /// 节流逻辑全部在 HistorySyncService 里, 这里只是"到点就报"。
    /// 两个额外的守卫:
    ///   - 只在真正处于播放态时上报(暂停/拖动时不报, 避免上报错误的中间进度);
    ///   - 拖动进度条期间(_isSeeking)跳过, 松手后下一次心跳自然会带上新位置。
    /// 整个调用是 fire-and-forget: 上报失败绝不能影响播放。
    /// </summary>
    private void MaybeSyncProgress()
    {
        if (_closing || _mp == null || _isLocalPlayback) return;
        if (!_mp.IsPlaying || _isSeeking) return;
        if (_aid <= 0 || _currentCid <= 0) return;

        var progressSec = _mp.Time / 1000;
        // ShouldReport 内部已含"未登录不上报"的语义(ReportHistoryAsync 会静默跳过),
        // 这里先做一次本地判断, 避免无意义地起 Task。
        if (!Svc.HistorySync.ShouldReport(_aid, _currentCid, progressSec)) return;

        _ = Svc.HistorySync.ReportAsync(_aid, _currentCid, progressSec);
    }

    private void UpdateTotalDurationText()
    {
        if (_closing || _totalMs <= 0) return;
        TimeText.Text = $"00:00 / {FormatClock(TimeSpan.FromMilliseconds(_totalMs))}";
    }

    /// <summary>
    /// 播放结束。LibVLC 官方警告: 绝不能在其事件回调线程里直接调 Stop()(必死锁),
    /// 必须异步派发到 UI 线程执行, 且 Stop 包 try/catch(状态竞争时可能抛异常)
    /// </summary>
    /// <summary>
    /// 播完了。**刻意不调用 `_mp.Stop()`**:
    /// Stop 会把视频输出整个拆掉, 那个 HWND 就成一块没有内容的空白面 —— 浅色界面下看着
    /// 就是"整块全白"(用户报的就是这个)。停在那里不动、上面盖一层遮罩, 既挡住空白,
    /// 又能顺手给出"重播"入口; 下次播放走 RestartFromBeginning 把时间归零。
    ///
    /// LibVLC 官方警告: 绝不能在其事件回调线程里直接操作播放器(必死锁), 所以这里依旧
    /// 异步派发到 UI 线程。
    /// </summary>
    private void OnEndReached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closing || (sender is MediaPlayer source && !ReferenceEquals(source,_mp))) return;

        // ---- 提前结束的自救 ----
        // DASH 音轨(input-slave)先于视频到 EOF 时, LibVLC 会结束整个 input:
        // 表现就是"最后几秒没声音 + 进度条提前满格"。这不是真的播完 ——
        // 从最后已知位置重建 Media 续播(次数与推进距离双保险, 防止音频本来就短的
        // 片源无限循环续播)。
        //
        // 两个收紧(短视频"播完又跳回几秒重播"的根因都在这):
        //   1. 长度基准用 **_mp.Length**(LibVLC 实际解封装出的时长), 而不是接口的
        //      timelength —— B 站接口给的 timelength 经常比流的真实时长长几秒,
        //      按 _totalMs 判断会把"正常播完"误判成"提前结束", 于是重建 Media
        //      从"几秒前"续播 —— 用户看到的就是"明明结束了又自己倒回去放"。
        //   2. 单文件源(_lastAudioUrl == null, durl/合流)没有 input-slave,
        //      根本不存在"音轨先 EOF"这个问题, 直接跳过自救 —— 否则任何风吹草动的
        //      提前 EOF 都会在单文件源上触发一次无谓的回跳续播。
        //
        //   ★★★ 3(2026-11-01 修「4h 视频快进回到 9s 处」): **跳转进行中绝不自救**。
        //      DASH 双流远跳失效时, 音轨 seek 失败会拖死整个 input 触发本事件 ——
        //      而此刻 _lastKnownMs 已经掉回片头(用户报的就是 ~9s), 用它当断点续播
        //      等于"从坏位置再播一次", 正是"快进 → 缓冲几次 → 回到 9s"的最后一环。
        //      看门狗自己会重建到目标(_pendingSeekMs 还挂着), 这里必须让路。
        var effectiveTotalMs = _mp.Length > 0 ? _mp.Length : _totalMs;
        var canResumeEarlyEof = _isListenMode
            ? !string.IsNullOrEmpty(_listenAudioUrl)
            : !string.IsNullOrEmpty(_lastAudioUrl);
        var seekLooksUnlanded = !_eofSeekRebuildTried
            && _lastSeekTargetMs > 0
            && _seekIssuedAt > 0
            && Environment.TickCount64 - _seekIssuedAt <= SeekEofRecoverWindowMs
            && Math.Abs(_lastSeekTargetMs - _lastKnownMs) >= SeekEofRecoverMinMissMs;

        if (!_isLive && !_isLocalPlayback && !_ended && _pendingSeekMs < 0 && !seekLooksUnlanded &&
            effectiveTotalMs > 0 &&
            canResumeEarlyEof &&
            // 中段提前 EOF: 离结尾 >= TailQuietZoneMs, 带 start-time 直达断点续播双流
            _lastKnownMs > 0 && _lastKnownMs < effectiveTotalMs - TailQuietZoneMs &&
            _eofResumeCount < EofResumeMax &&
            _lastKnownMs - _lastEofResumeMs >= EofResumeMinAdvanceMs &&
            !string.IsNullOrEmpty(_lastPlayUrl))
        {
            _eofResumeCount++;
            _lastEofResumeMs = _lastKnownMs;
            var resume = _lastKnownMs;
            ShowLoading("正在继续播放…");
            // 通过 :start-time 直达断点, 避免重新从 00:00 拉流再二次 seek 导致回跳
            _resumeAfterSwitchMs = resume;
            _resumeByStartTime = true;
            try
            {
                if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
                    PlayMedia(BuildMedia(_listenAudioUrl!, null, startMs: resume));
                else
                    PlayMedia(BuildMedia(_lastPlayUrl!, _lastAudioUrl, startMs: resume));
            }
            catch { }
            return;
        }

        // 尾部音轨缺口收尾：DASH 音轨(input-slave)先于视频结束(常见提前 1.5s~10s)拖死整个 input 时，
        // 剥离已 EOF 的短音轨 slave，用纯视频单流续播完最后几秒，避免结尾被突然黑屏截断（每片最多一次）。
        var demuxedLengthMs = _mp.Length;
        var tailRemainMs = demuxedLengthMs > 0 ? demuxedLengthMs - _lastKnownMs : 0;
        if (!_isLive && !_isLocalPlayback && !_isListenMode && !_ended && !_tailVideoOnlyResumed &&
            _pendingSeekMs < 0 && !seekLooksUnlanded &&
            demuxedLengthMs >= 10_000 &&
            !string.IsNullOrEmpty(_lastPlayUrl) && !string.IsNullOrEmpty(_lastAudioUrl) &&
            _lastKnownMs > 0 &&
            tailRemainMs >= TailVideoOnlyMinRemainMs && tailRemainMs < TailQuietZoneMs)
        {
            _tailVideoOnlyResumed = true;
            var resume = _lastKnownMs;
            WriteLibVLCDebug(
                $"[EndReached] DASH 尾段音轨提前结束(remain={tailRemainMs}ms, pos={resume}ms/{demuxedLengthMs}ms) → 切单视频流补播结尾\n");
            _resumeAfterSwitchMs = resume;
            _resumeByStartTime = true;
            try { PlayMedia(BuildMedia(_lastPlayUrl!, null, startMs: resume)); }
            catch { }
            return;
        }

        _ended = true;
        RestoreVolumeAfterSeek();   // 播完若还挂在 seek 静音里, 立刻恢复(重播/下一条别哑)
        if (!_isLive && !_isLocalPlayback && !_eofSeekRebuildTried &&
            !string.IsNullOrEmpty(_lastPlayUrl) &&
            (_pendingSeekMs >= 0 || seekLooksUnlanded))
        {
            var target = _pendingSeekMs >= 0 ? _pendingSeekMs : _lastSeekTargetMs;
            _ended = false;
            _eofSeekRebuildTried = true;
            WriteLibVLCDebug(
                $"[EndReached] 疑似远跳假EOF(target={target}ms time={_lastKnownMs}ms " +
                $"pending={_pendingSeekMs}) → 重建 Media 续跳\n");
            ShowLoading("正在跳转…");
            ClearSeekWatchdog(hideBuffering: false);
            RebuildMediaAndSeek(target);
            return;
        }
        WriteLibVLCDebug(
            $"[EndReached] 播完收尾 vmem={_vmemMode} time={_lastKnownMs} bgBefore={DescribeVideoAreaBg()}\n");
        if (!_vmemMode)
        {
            // 非 vmem(HwndHost) 模式下压黑手势层以遮挡原生 vout 露出的白底；
            // vmem 模式下 WPF 图像自然定格在最后一帧，不盖纯黑，收尾更自然。
            VideoAreaRoot.Background = Brushes.Black;
            _fullscreenCoverArmed = true;
        }
        // 播完了: 跳转看门狗必须收掉 —— 它在外面的条件是 !_ended, 留着 _pendingSeekMs >= 0
        // 会让卡顿检测被永久跳过(那个判断里也有 _pendingSeekMs >= 0), 之后重播的卡顿就不再提示了。
        _pendingSeekMs = -1;
        _seekObservedMs = -1;
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
        EndOverlay.Visibility = Visibility.Visible;
        // 进度条钉在满格 + 时间显示成总时长, 否则 Position 可能报 0, 看起来像"回到开头了"
        UpdateProgressUi();
        UpdatePlayButton();

        // 播到结尾也算一次"自然的段落结束", 把进度补报到云端(下次能从结尾续上)。
        // 注意不能用 _mp.Time —— EOF 之后它已经归零, 会把云端进度错误地报成 0;
        // 用 TimeChanged 留底的 _lastKnownMs 才是真实看完的位置。离线缓存本地播放不上报云端历史。
        if (!_isLocalPlayback && _aid > 0 && _currentCid > 0)
            _ = Svc.HistorySync.ReportNowAsync(_aid, _currentCid, _lastKnownMs / 1000);
    }));

    private void OnProgressDragStart(object sender, DragStartedEventArgs e)
    {
        _progressDragStartValue = ProgressSlider.Value;
        _isSeeking = true;
    }

    private void OnProgressDragEnd(object sender, DragCompletedEventArgs e)
    {
        _isSeeking = false;
        if (e.Canceled || _closing) { UpdateProgressUi(); return; }
        var moved = Math.Abs(ProgressSlider.Value - _progressDragStartValue) > 0.01;
        if (moved && _mp.Length > 0)
        {
            var targetMs = (long)Math.Round(ProgressSlider.Value / 1000.0 * _mp.Length);
            SeekTo(targetMs);
        }
        // 拖动结束后立刻补报一次: 用户主动跳转是"我在看这个位置"的强信号,
        // 如果只等 15 秒周期上报, 此时关掉窗口会把位置记在拖动前的老地方。
        SyncProgressNow();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mp == null) return;
        var v = (int)VolumeSlider.Value;
        // 滑块是用户拖动时的入口, 程序化赋值(SetVolume)也会走到这 —— 同值回写, 无副作用。
        // 必须同步权威值: 否则拖完滑块再滚轮, 基数还是旧缓存(见 _volumeCache 的说明)。
        _volumeCache = v;
        UpdateVolumeChrome(v);
        // ★ --mute 启动参数只在本次进程内生效，绝不把 0 写回 settings.json 覆盖用户真实音量偏好
        if (!App.MuteForThisRun) Svc.Settings.SetPlayerVolume(v);
        // seek 静音期间(与 SetVolume 同一套约定): 新值记为恢复目标, 声音保持静音直到画面就绪。
        // 这里不能直接写 _mp.Volume —— 否则拖一下滑块就绕过静音, 声音又跑到画面前面去了。
        if (_seekMuteSavedVolume >= 0)
        {
            _seekMuteSavedVolume = v;
            try { _mp.Volume = 0; } catch { }
            return;
        }
        try { _mp.Volume = v; } catch { }
    }

}
