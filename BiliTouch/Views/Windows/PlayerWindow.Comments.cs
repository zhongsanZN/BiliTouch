using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

namespace BiliTouch.Views;

public partial class PlayerWindow
{
    // ------------------------------------------------------------ Tab

    private void TabIntro_Checked(object sender, RoutedEventArgs e)
    {
        if(IsWebVideoLayout){SelectWebInfoPage(recommended: false);return;}
        IntroPanel.Visibility = Visibility.Visible;
        CommentsPanel.Visibility = Visibility.Collapsed;
        // 点 tab 也走滑动入场(横滑翻页时由翻页动画接管)
        AnimatePageEntrance(IntroPanel, fromRight: false);
        CloseCommentDetail();
        // 切走评论 tab: FAB 与展开的输入面板一起收掉(它们只属于评论区)
        UpdateCommentComposer();
        // 合集现在长在简介页里: 切回简介时把列表定位到"正在播的那一集"
        if (_seasonOpen && _hasSeason) ScrollSeasonToCurrent();
    }

    private async void TabComments_Checked(object sender, RoutedEventArgs e)
    {
        if(IsWebVideoLayout){IntroPanel.Visibility=CommentsPanel.Visibility=Visibility.Visible;UpdateCommentComposer();await LoadCommentsAsync();return;}
        IntroPanel.Visibility = Visibility.Collapsed;
        CommentsPanel.Visibility = Visibility.Visible;
        AnimatePageEntrance(CommentsPanel, fromRight: true);
        // 切到评论页时顺手校准一次输入框状态(FAB 显隐跟着 tab 走):
        // 用户可能是先登录/换号再回来发的
        UpdateCommentComposer();
        await LoadCommentsAsync();
    }

    private ApiClient.VideoCommentCursor? _nextCommentPage;
    private bool _loadingCommentPage;
    private bool _commentPageFailed;

    private void UpdateCommentPageButton()
    {
        BtnMoreComments.Visibility = _nextCommentPage != null && !IsCommentDetailOpen
            ? Visibility.Visible : Visibility.Collapsed;
        BtnMoreComments.IsEnabled = !_loadingCommentPage && CommentLoadingRing.Visibility != Visibility.Visible;
        BtnMoreComments.Content = _loadingCommentPage ? "正在加载更多评论…" :
            _commentPageFailed ? "加载失败，点击重试" : "加载更多评论";
    }

    private void PrefetchCommentPage(ScrollViewer scroll, ScrollChangedEventArgs e)
    {
        if (_nextCommentPage != null && !_commentPageFailed && e.VerticalChange > 0
            && CommentList.IsVisible && ReferenceEquals(e.OriginalSource, scroll)
            && scroll.ScrollableHeight - scroll.VerticalOffset < scroll.ViewportHeight * 0.75)
            _ = LoadNextCommentPageAsync();
    }

    private async void OnMoreCommentsClick(object sender, RoutedEventArgs e) => await LoadNextCommentPageAsync();

    private async Task LoadNextCommentPageAsync()
    {
        var cursor = _nextCommentPage;
        if (_closing || _loadingCommentPage || cursor == null || IsCommentDetailOpen
            || CommentLoadingRing.Visibility == Visibility.Visible
            || CommentList.ItemsSource is not ObservableCollection<CommentItem> comments) return;
        var version = _commentRequestVersion;
        var generation = _loadGen;
        var aid = _aid;
        _loadingCommentPage = true;
        _commentPageFailed = false;
        UpdateCommentPageButton();
        try
        {
            var page = await Svc.Api.GetVideoCommentsPageAsync(aid, 30, _loadedCommentSort, cursor);
            if (_closing || version != _commentRequestVersion || generation != _loadGen || aid != _aid) return;
            if (!page.ok) { _commentPageFailed = true; return; }
            var seen = new HashSet<long>(comments.Select(c => c.Rpid));
            foreach (var item in page.items)
                if (seen.Add(item.Rpid)) comments.Add(item);
            _nextCommentPage = page.next;
        }
        finally
        {
            if (version == _commentRequestVersion && generation == _loadGen && aid == _aid && !_closing)
            {
                _loadingCommentPage = false;
                UpdateCommentPageButton();
            }
        }
    }

    private bool _commentsLoaded;

    /// <summary>
    /// 评论区排序: 3 = 热门, 2 = 时间(与 B 站接口的 mode 同值)。
    /// ★ 初值 3 与接口默认一致 —— 用户第一次进评论区看到的顺序不能因为这次改动而变。
    /// </summary>
    private int _commentSort = 3;

    /// <summary>
    /// **本次实际展示**的评论排序(3 热门 / 2 时间)。平时等于 <see cref="_commentSort"/>,
    /// 只有"热门接口返回空、本次按时间回退"时会临时变成 2。
    /// ★ 单独记一份是为了让排序按钮的点击判断跟着**眼前看到的列表**走:
    ///   热门回退成时间后 _commentSort 仍是 3, 若直接拿它判断, 用户点「热门」会被
    ///   "已经是热门"短路掉, 按钮看起来是死的。
    /// </summary>
    private int _effectiveCommentSort = 3;

    /// <summary>当前视频的根评论总数(来自接口 cursor.all_count, 不是本页条数)</summary>
    private long _commentTotal;

    private int _commentRequestVersion;
    private int _loadedCommentSort = 3;
    private Task? _commentsRequest;
    private (long aid, int sort, int generation) _commentsRequestKey;
    private Task LoadCommentsAsync(bool force = false)
    {
        var key = (_aid, _commentSort, _loadGen);
        if (_commentsRequest is { IsCompleted: false } && key == _commentsRequestKey && !force) return _commentsRequest;
        if (_commentsLoaded && !force) return Task.CompletedTask;
        _commentsRequestKey = key;
        return _commentsRequest = LoadCommentsCoreAsync(force, ++_commentRequestVersion);
    }

    private async Task LoadCommentsCoreAsync(bool force, int requestVersion)
    {
        if (_closing) return;
        // ★ 记住发起时的 aid: 网络回来时用户可能已经换片了。没有这道的话,
        //   上一条视频的评论会灌进新视频(列表/条数/排序入口全是旧的)。
        var aidAtStart = _aid;
        var generationAtStart = _loadGen;
        var sortAtStart = _commentSort;
        bool IsCurrent() => !_closing && _aid == aidAtStart && generationAtStart == _loadGen
            && sortAtStart == _commentSort && requestVersion == _commentRequestVersion;
        void ShowLoadError(string message)
        {
            CommentLoadingRing.Visibility = Visibility.Collapsed;
            CommentHint.Text = message;
            BtnRetryComments.Visibility = Visibility.Visible;
            UpdateCommentPageButton();
            if (_commentsLoaded)
            {
                _effectiveCommentSort = _loadedCommentSort;
                if (IsCommentDetailOpen) SortDetailReplies();
            }
            UpdateCommentSortUi(IsCommentDetailOpen || CommentList.Items.Count > 0, _effectiveCommentSort);
        }

        WriteLibVLCDebug($"\n[Comments] start aid={aidAtStart} loaded={_commentsLoaded} force={force}\n");
        // force: 刚发表完评论要重新拉一次(rpid/楼层/审核状态本地编不出来)。
        // 但"看过就不再拉"这个短路必须保留 —— 切 Tab 会反复调到这里, 不短路就是白刷接口。
        // ★ 切换排序必须走 force=true: 它要的就是"同一份数据换个顺序重拉"。
        // ★★ 2026-11-05 修"评论详情页点 热门/时间 被退回评论区":
        //    这里原来无条件 CloseCommentDetail() —— 用户明明是在详情页里点的排序,
        //    结果刚点亮按钮就被弹回顶层列表, 看起来像"点一下退出了详情"。
        //    force 的两个来源要区别对待:
        //      · 切换排序: 详情页必须留在原地(详情页的 DataContext 是 _detailThreadRoot
        //        这个**对象**本身, 重拉只替换 CommentList.ItemsSource, 不碰它);
        //      · 发表评论后刷新: 在详情页里发表走的是"回复"分支(ReloadThreadRepliesAsync),
        //        走不到这里; 真走到这里时详情页本来就没开, 所以保留现场也不会留旧内容。
        //    换视频/切 tab/主动返回这些需要关详情的地方, 由各自调用方显式 CloseCommentDetail()。
        if (_commentsLoaded && !force) return;
        if (aidAtStart <= 0)
        {
            CommentHint.Text = _isLocalPlayback ? "本地文件没有评论" : "未获取到视频 aid";
            UpdateCommentSortUi(visible: false);
            return;
        }
        BtnRetryComments.Visibility = Visibility.Collapsed;
        CommentLoadingRing.Visibility = Visibility.Visible;
        _loadingCommentPage = false;
        UpdateCommentPageButton();
        CommentHint.Text = force ? "正在刷新评论…" : "加载评论中…";
        // 重拉期间先把两个词收起来: 否则"正在刷新评论…"右边还挂着可点的排序词, 会出现
        // "点一下又刷新一次"的连点窗口期。
        UpdateCommentSortUi(visible: false);
        // 本次排序先按用户偏好; 若后面回退成时间, 在回调里改 _effectiveCommentSort
        _effectiveCommentSort = _commentSort;
        try
        {
            var (cmtOk, cmtErr, list, total, next) = await Svc.Api.GetVideoCommentsPageAsync(aidAtStart, 30, sortAtStart, null);
            if (!IsCurrent()) return;
            // 实际拿到的列表按哪个顺序排(热门的"空列表回退"会把它改成 2)。
            // ★ 不回写 _commentSort: 那是用户跨视频记住的偏好, 不能因为一个视频热门为空
            //   就永久改成时间; 排序入口只按本次实际顺序点亮。
            var displaySort = _commentSort;
            // ★★ 2026-11-04 修"评论区只显示置顶一条"(BV1Nxxr6oES2 实测):
            //   该视频 mode=3(热门) 的 data.replies 是**空数组**, 只剩 data.top 里的置顶;
            //   同一时刻 mode=2(时间) 能正常返回 14 条。接口不给热门列表, 不是我们解析错。
            //   这里在"热门 + 只剩置顶"时按时间重拉一次, 排序入口切到「时间」——
            //   否则热门一直亮着、列表却只有置顶, 用户会以为评论区坏了。
            var hotRootCount = list.Count(c => !c.IsPinned);
            if (cmtOk && _commentSort == 3 && total > hotRootCount &&
                ((total >= 10 && hotRootCount <= 3) || list.Count <= 1))
            {
                var (fbOk, _, fbList, fbTotal, fbNext) = await Svc.Api.GetVideoCommentsPageAsync(aidAtStart, 30, 2, null);
                if (fbOk && fbList.Count > list.Count && _commentSort == 3)
                {
                    list = fbList;
                    next = fbNext;
                    if (fbTotal > 0) total = fbTotal;
                    cmtOk = true;
                    cmtErr = null;
                    displaySort = 2;
                }
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent()) return;
                if (!cmtOk)
                {
                    ShowLoadError(cmtErr ?? "评论加载失败");
                    return;
                }
                CommentList.ItemsSource = new ObservableCollection<CommentItem>(list.DistinctBy(c => c.Rpid));
                _nextCommentPage = next;
                _commentPageFailed = false;
                // ★ 立刻暖前 5 条评论的头像(纯暖缓存, 不碰任何控件)。放在 ItemsSource 之后:
                //   此时评论容器还没生成、Cover.Source 绑定还没跑 —— 由我们先把下载+解码发出去,
                //   等容器真的生成时 CoverLoader 直接命中(它自带 in-flight 去重), 头像一上屏就有图,
                //   而不是"先灰一片、滚到哪儿加载到哪儿"。
                PrimeCommentAvatars(list, aidAtStart);
                _commentsLoaded = cmtOk;
                _commentTotal = total;
                _loadedCommentSort = _effectiveCommentSort = displaySort;
                // 热门没数据回退成时间时, 详情页里的楼中楼也要跟着换回"时间"顺序:
                // 否则按钮亮的是「时间」, 眼前回复还是「热门」序。
                if (IsCommentDetailOpen) SortDetailReplies();
                CommentLoadingRing.Visibility = Visibility.Collapsed;
                UpdateCommentPageButton();
                // ★ 原来"接口失败"和"这视频真的一条评论都没有"都显示"暂无评论" ——
                //   网络失败被伪装成了正常状态。现在失败时说清原因。
                // ★ 条数用接口给的**总数**(_commentTotal), 不再拿 list.Count —— 一页只拉 30 条,
                //   拿它当总数的话几千条评论的视频会显示成"共 30 条评论"。总数缺失(0)时才回落到本页条数。
                CommentHint.Text = list.Count == 0 ? "暂无评论"
                    : $"共 {VideoItem.FormatCount(total > 0 ? total : list.Count)} 条评论";
                // 只有真的加载成功且有评论, 排序入口才出现
                // ★ 详情页开着时不受"列表为空/失败"影响: 用户还在详情页里,
                //   此时把排序入口收起来等于把"切回另一个排序"的退路也一起藏了。
                // ★ 传 displaySort: 热门回退成时间时, 让「时间」亮起来与列表实际顺序一致
                UpdateCommentSortUi(visible: IsCommentDetailOpen || (cmtOk && list.Count > 0), sortOverride: displaySort);
            });
        }
        catch
        {
            if (IsCurrent()) ShowLoadError("评论加载失败");
        }
    }

    /// <summary>
    /// 预取**前 N 条评论的头像**(默认 5 条), 让它们不必等滚动进视口才下载。
    ///
    /// 为什么值得单独做: 评论区一打开就是"先灰一片、慢慢出图", 而那几条头像加起来不到 40KB ——
    /// 这是纯粹的**排序**问题, 不是带宽问题: 用户眼前第一屏的头像, 和滚到第 300 条时的头像
    /// 在缓存里没有任何优先级差别。这里只是在"评论刚落地"这个唯一确定的时刻, 把最该先有的
    /// 那几张先发出去。走的是同一个 CoverLoader(自带内存缓存 + in-flight 去重), 所以:
    ///   · 用户先滚到某条, Cover.Source 先发了请求 → 这里直接复用同一个 Task, 不会下两遍;
    ///   · 这里先发 → Cover.Source 稍后命中缓存或同一个 in-flight Task。
    ///
    /// ★ 代次校验(<paramref name="aidAtStart"/> / <see cref="_loadGen"/> / <see cref="_closing"/>):
    ///   评论是异步回来的, 回来的路上用户可能已经换片或关窗。若不管, 上一条视频的头像会灌进
    ///   当前窗口的缓存与后续判断。这里每个 await 之后都重新确认一次, 一旦变了就**原地停止**,
    ///   连剩下的图都不再发。
    ///
    /// ★ 全程不 await 在 UI 线程上: <see cref="CoverLoader.LoadAsync(string?,int)"/> 的网络与解码
    ///   都在后台(它内部 ConfigureAwait(false) + Task.Run 解码), 这里只是 `_ =` 发出去就返回。
    ///   任何异常都吞掉 —— 头像取不到是"图不显示", 绝不该冒泡成弹窗或者挡住评论渲染。
    /// </summary>
    private void PrimeCommentAvatars(IReadOnlyList<CommentItem>? list, long aidAtStart)
    {
        if (list == null || list.Count == 0) return;
        // 解码宽度跟着**列表里那个头像的真实尺寸**走(28 DIP × DPI × 1.25, 下限 160),
        // 与 Cover.Source 那条链用同一个函数算 —— 缓存键必须一致, 否则预取等于白做一次解码。
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var decodeWidth = CoverLoader.DecodeWidthFor(CommentAvatarDip, dpi);
        var gen = _loadGen;
        for (var i = 0; i < list.Count && i < CommentAvatarPrimeCount; i++)
        {
            _ = PrimeAvatarAsync(list[i]?.UserFace, decodeWidth, aidAtStart, gen);
        }
    }

    /// <summary>预取头像的那几张(用户明确要求"至少前五条")。</summary>
    private const int CommentAvatarPrimeCount = 5;

    /// <summary>评论列表里头像的显示边长(DIP), 与 PlayerWindow.xaml 里评论模板的 28×28 对齐。</summary>
    private const double CommentAvatarDip = 28;

    /// <summary>
    /// UP 主头像的解码宽度。
    /// ★ 必须与 <see cref="LoadUpAvatarAsync"/> 里那个字面量 **80** 保持一致 ——
    ///   UP 头像不走 Cover.Source, 它有自己的加载方法(还要贴 ImageBrush / 收占位图标);
    ///   缓存键是 url+量化档, 这里写 160 就会先落到 192 档、上屏时又按 128 档再解一次,
    ///   预取等于白做。40 DIP 显示 × 1.5 DPI = 60px, 80 已经够。
    /// </summary>
    private const int UpAvatarDecodeWidth = 80;

    /// <summary>
    /// 取一张头像并丢掉结果(只为暖缓存)。失败静默。
    /// ★ 代次在**每个 await 之后**都重新确认: 这里虽然只有一个 await, 但把判断写成循环外的
    ///   统一出口, 以后要加"顺带写回某处"的逻辑时不会忘了它。
    /// </summary>
    private async Task PrimeAvatarAsync(string? url, int decodeWidth, long aidAtStart, int genAtStart)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (_closing || _loadGen != genAtStart) return;
            var img = await CoverLoader.LoadAsync(url, decodeWidth).ConfigureAwait(false);
            // 结果不使用: 只要位图进了 CoverLoader 的内存缓存, 后面 Cover.Source 就能同步命中。
            // 这里保留一次代次确认, 是为了让"已经换片了"这条路径明确退出, 而不是继续往下走。
            if (img == null || _closing || _aid != aidAtStart || _loadGen != genAtStart) return;
        }
        catch { /* 头像取不到 = 图不显示, 静默 */ }
    }

    /// <summary>
    /// 预取 UP 主头像。
    ///
    /// ★ 和 <see cref="LoadUpAvatarAsync"/> 不重复下载: 两者都走 CoverLoader.LoadAsync(同一份
    ///   内存缓存 + 同一张 in-flight 表)。谁先发谁下载, 后到的直接复用 —— 所以这里可以放心地在
    ///   LoadUpAvatarAsync 之前跑; 真正贴到 UpAvatarBox 上的仍然是 LoadUpAvatarAsync(它要碰 UI)。
    /// </summary>
    private void PrimeUpAvatar(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _ = PrimeAvatarAsync(url, UpAvatarDecodeWidth, _aid, _loadGen);
    }

    /// <summary>
    /// 同步排序入口的可见性与选中态。
    ///
    /// ★ 选中态只在这里写 Tag(与 ContentActionTabStyle 同一套约定: 字符串 "on"/"off")。
    ///   分散着改两个按钮的 Tag 必然出现"两边都亮"或"两边都不亮"。
    /// ★ sortOverride: 热门接口返回空、本次实际按时间拉了一次时传 2, 让高亮跟着真实顺序走;
    ///   不传就用用户记住的 _commentSort。
    /// </summary>
    private void UpdateCommentSortUi(bool visible, int? sortOverride = null)
    {
        var sort = sortOverride ?? _commentSort;
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        if (BtnCommentSortHot != null)
        {
            BtnCommentSortHot.Visibility = vis;
            BtnCommentSortHot.Tag = sort == 3 ? "on" : "off";
        }
        if (BtnCommentSortTime != null)
        {
            BtnCommentSortTime.Visibility = vis;
            BtnCommentSortTime.Tag = sort == 2 ? "on" : "off";
        }
    }

    private async void OnRetryCommentsClick(object sender, RoutedEventArgs e) => await LoadCommentsAsync(force: true);

    private void OnCommentSortHotClick(object sender, RoutedEventArgs e) => SetCommentSort(3);

    private void OnCommentSortTimeClick(object sender, RoutedEventArgs e) => SetCommentSort(2);

    /// <summary>
    /// 切换评论排序。已经**实际展示**的就是这个顺序就直接返回 —— 否则点一下会白刷一次接口,
    /// 而重拉期间入口是收起的, 用户看到的是"点了闪一下什么都没有"。
    /// ★ 比较的是 <see cref="_effectiveCommentSort"/>, 不是 _commentSort:
    ///   热门回退成时间后两者不同, 用户点「热门」应该真的重拉一次, 不能被短路。
    /// </summary>
    private void SetCommentSort(int mode)
    {
        if (_closing || _effectiveCommentSort == mode) return;
        _commentSort = mode;
        _effectiveCommentSort = mode;
        UpdateCommentSortUi(visible: true, sortOverride: mode);
        // ★★ 2026-11-05: 详情页开着时先把**眼前的楼中楼**按新顺序排一遍。
        //   根评论列表在详情页背后, 重拉它用户看不见 —— 不排这边的回复,
        //   点了「热门 / 时间」就是"按钮亮了, 内容一动不动"。
        if (IsCommentDetailOpen) SortDetailReplies();
        _ = LoadCommentsAsync(force: true);
    }

    /// <summary>
    /// 按当前排序重排详情页里**已加载**的楼中楼回复。
    ///
    /// ★ 为什么必须本地排: 根评论列表有接口的 mode(热门=3 / 时间=2),
    ///   但楼中楼接口 /x/v2/reply/reply **没有排序参数**, 永远按时间正序返回。
    ///   详情页里点排序时若不重排, 眼前的顺序不会有任何变化。
    /// ★ 热门 = 点赞数降序(再按时间新→旧); 时间 = 发布时间降序,
    ///   与接口 mode=2 的"最新在前"保持一致。
    /// ★ 用 Move 而不是 Clear+Add: 后者会重建所有回复容器, 长楼中楼会闪一下并丢滚动位置。
    /// </summary>
    private void SortDetailReplies()
    {
        if (_detailThreadRoot != null) SortRepliesInPlace(_detailThreadRoot);
    }

    private void SortRepliesInPlace(CommentItem root)
    {
        if (root == null || root.Replies.Count < 2) return;

        var ordered = _effectiveCommentSort == 2
            ? root.Replies.OrderByDescending(r => r.Ctime)
                .ThenByDescending(r => r.Rpid)
                .ToList()
            : root.Replies.OrderByDescending(r => r.LikeCount)
                .ThenByDescending(r => r.Ctime)
                .ThenByDescending(r => r.Rpid)
                .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var idx = root.Replies.IndexOf(ordered[i]);
            if (idx != i) root.Replies.Move(idx, i);
        }
        root.SyncPreviewReplies();
    }

    /// <summary>
    /// 展开某条评论下的全部回复(楼中楼)。
    /// 首页接口只内联前 3 条, 这里按需把剩下的补齐 —— 补完 HasMoreReplies 会变 false,
    /// 按钮自己就消失了, 不需要额外的状态管理。
    /// </summary>
    // ------------------------------------------------------------ 评论详情(点楼中楼方框进入)

    private CommentItem? _detailThreadRoot;   // 楼中楼的线程根(拉取回复时当 root 参数)
    private CommentItem? _detailFocus;        // 详情卡片上展示的那一条
    private CommentItem? _replyTarget;        // 详情页输入框当前要回复谁; null = 发表普通评论

    /// <summary>
    /// 点评论卡片里的**楼中楼方框** → 进详情页看这条评论的全部回复。
    /// 方框长在顶层评论下面, 所以线程根就是这条评论自己。
    /// (上一版是"查看详情"文字链接 + 楼中楼条目各自可点, 都已并入方框这一个入口。)
    /// </summary>
    private void OnReplyBoxClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        ShowCommentDetail(threadRoot: item, focus: item);
    }

    private void ShowCommentDetail(CommentItem threadRoot, CommentItem focus)
    {
        _detailThreadRoot = threadRoot;
        _detailFocus = focus;
        // Host 的 DataContext = 线程根: "相关回复共 N 条"/回复列表/加载更多都从它取
        CommentDetailHost.DataContext = threadRoot;
        CommentDetailCard.Content = focus;
        // 进详情时按当前排序先排一次: 楼中楼接口固定按时间正序返回,
        // 而排序入口上亮着的可能是「热门」—— 不排的话两者对不上。
        SortRepliesInPlace(threadRoot);
        // 进入详情后更新 tab 文案，右下角显示返回按钮。
        TabComments.Content = "评论详情";
        // 评论区只有一个输入框(悬浮面板), 进详情页它自动切成"回复 @这层楼"
        // (用户规则: 在详情页评论 = 给这层楼评论); 点提示条上的"取消"才改回发新楼层。
        // **不动用户已经写了一半的草稿** —— 只是想看看回复, 不该把评论内容清掉。
        // focus: false —— 一进门就抢焦点容易误触输入法。
        // ★ openPanel: false(2026-11-01 修"每次进详情页都弹输入框"): 进详情页只切语义,
        //   不弹面板 —— 想回复时点某条的"回复"按钮或右下角 FAB, 面板自然会展开,
        //   且语义已经是对的(回复这层楼)。
        SetReplyTarget(threadRoot, focus: false);
        CommentList.Visibility = Visibility.Collapsed;
        CommentDetailPanel.Visibility = Visibility.Visible;
        UpdateCommentPageButton();
        UpdateCommentFabVisibility();
        // "楼中楼自动展开所有评论": 进来就把这条线程剩下的回复补全, 不再要用户点"展开更多"
        if (threadRoot.HasMoreReplies) _ = AutoCompleteThreadRepliesAsync(threadRoot);
        // 滚动交给外层右面板的 ScrollViewer: 让详情面板滚进视野
        CommentDetailPanel.BringIntoView();
    }

    /// <summary>
    /// 把一条线程的回复**自动补全**(用户要求"楼中楼自动展开所有评论")。
    ///
    /// ★ 2026-11-04 上限从 5 页降到 3 页(约 60 条): 每条回复都是一个 RichTextBox,
    ///   100 条一口气实例化 + 排版实测要 6 秒上下, 用户看到的就是"详情页卡死"。
    ///   60 条仍在 1 秒级, 剩下的用底部"展开更多"按钮按需拉。
    /// ★ 每页之间 Dispatcher.Yield 一次, 让窗口有机会处理输入/重绘 —— 否则 3 页连拉
    ///   仍然是一次长阻塞。
    /// 死循环保护: 服务端若重复返回同一页(去重后一条没新增)立即收手。
    /// </summary>
    private async Task AutoCompleteThreadRepliesAsync(CommentItem root)
    {
        try
        {
            // ★ 记住发起时的 aid: 自动补全可能跨几秒, 期间用户可能换片。
            //   不记的话会用**新视频的 aid** 去拉**旧评论的 rpid**, 拿到一堆无关回复。
            var aidAtStart = _aid;
            var guard = 0;
            while (!_closing && root.HasMoreReplies && guard++ < 3)
            {
                var (_, _, more) = await Svc.Api.GetReplyRepliesAsync(aidAtStart, root.Rpid, pn: root.RepliesNextPage, ps: 20);
                if (_closing || _aid != aidAtStart) return;
                if (!IsCommentDetailOpen || !ReferenceEquals(_detailThreadRoot, root)) return;
                if (more.Count == 0)
                {
                    // 服务端取不全: 用实际条数收口, 否则按钮会一直挂着
                    root.ReplyCount = root.Replies.Count;
                    break;
                }
                root.RepliesNextPage++;
                var seen = new HashSet<long>(root.Replies.Select(x => x.Rpid));
                var added = 0;
                foreach (var r in more)
                    if (r.Rpid > 0 && seen.Add(r.Rpid)) { root.Replies.Add(r); added++; }
                if (added == 0) break;
                // 让出一帧: 20 条富文本模板的实例化/排版会占满 UI 线程
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            // 自动补全的几页是按时间正序回来的: 补完再按"当前排序"归位,
            // 否则「热门」模式下新补进来的旧回复会全堆在列表末尾。
            SortRepliesInPlace(root);
            root.NotifyRepliesChanged();
        }
        catch (Exception ex)
        {
            // 自动补全是"锦上添花": 失败不能影响已经渲染出来的回复
            App.ReportError(ex);
        }
    }

    private void OnCommentDetailBack(object sender, RoutedEventArgs e) => CloseCommentDetail();

    /// <summary>评论详情页当前是否开着(排序刷新失败时据此决定要不要留下排序入口)</summary>
    private bool IsCommentDetailOpen =>
        CommentDetailPanel != null && CommentDetailPanel.Visibility == Visibility.Visible;

    /// <summary>关掉详情视图(返回列表 / 切 tab / 换视频时都要调, 防止残留旧内容)</summary>
    private void CloseCommentDetail()
    {
        if (CommentDetailPanel.Visibility != Visibility.Visible) return;
        CloseCommentPicture();
        // ★ 记下"刚才在看哪一条", 供返回后滚回去用(见 _commentReturnRpid)。
        //   必须在清空 _detailThreadRoot **之前**取, 否则信息就没了。
        var closingRoot = _detailThreadRoot;
        _commentReturnRpid = closingRoot?.Rpid ?? 0;
        CommentDetailPanel.Visibility = Visibility.Collapsed;
        CommentDetailCard.Content = null;
        CommentDetailHost.DataContext = null;
        closingRoot?.TrimRepliesToPreview();
        _detailThreadRoot = _detailFocus = null;
        ClearReplyTarget();
        // tab 文案与返回链接一起还原
        TabComments.Content = "评论";
        BtnBackToComments.Visibility = Visibility.Collapsed;
        CommentList.Visibility = Visibility.Visible;
        UpdateCommentPageButton();
        // ★ 用户报"从某条评论的回复返回后, 列表回到顶部, 得重新下拉找那条评论"
        //   (2026-10-13): 默认回到**刚才那条评论**所在的位置。
        //   ★ 走 Dispatcher 排队: 上面刚把 CommentList 从 Collapsed 恢复成 Visible,
        //     此刻容器还没生成(ContainerFromItem 会返回 null), 直接找必然落空。
        if (_commentReturnRpid > 0)
            Dispatcher.BeginInvoke(new Action(ScrollCommentIntoView),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 从评论详情返回列表后要滚回去的那条评论(rpid); 0 = 不需要滚动。
    ///
    /// ★ 为什么单独存 rpid 而不是留着 CommentItem 引用: 重新拉列表(换排序/发表后刷新)时
    ///   会换成一批**新的** CommentItem 实例, 引用会失效; rpid 是服务端主键, 跨刷新仍然对得上。
    /// </summary>
    private long _commentReturnRpid;

    /// <summary>
    /// 把 <see cref="_commentReturnRpid"/> 对应的那条评论滚进视野并闪一下。
    ///
    /// ★ 找不到就**静默放弃**: 排序换过、或那条已被删除时它确实不在列表里,
    ///   这时弹提示反而打扰用户 —— 回到顶部是合理退路。
    /// ★ 闪一下是必要的: 光滚过去, 用户仍不知道"是哪一条"(与"查看对话"同一个理由)。
    /// </summary>
    private void ScrollCommentIntoView()
    {
        var rpid = _commentReturnRpid;
        _commentReturnRpid = 0;      // 一次性: 用完即清, 免得下次切 tab 又滚一次
        if (rpid <= 0 || _closing || CommentList == null) return;

        var target = (CommentList.ItemsSource as IEnumerable<CommentItem>)
            ?.FirstOrDefault(c => c.Rpid == rpid);
        if (target == null) return;

        if (CommentList.ItemContainerGenerator.ContainerFromItem(target) is not FrameworkElement fe) return;
        fe.BringIntoView();
        // 闪两下: 告诉用户"就是这一条"
        // ★ FillBehavior=Stop 是必须的: 默认 Hold 会让动画把 Opacity 停在末值(0.35),
        //   用户看到的就是"这条评论变透明了, 而且再也回不来"—— 因为它从此被动画接管,
        //   连可见性切换都拉不回 1.0。Stop 表示动画结束后属性**回到本地值**(1)。
        var flash = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(140))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        fe.BeginAnimation(OpacityProperty, flash);
    }

    // ------------------------------------------------------------ 头像跳转 / 踩 / 回复 / 详情页发表

    /// <summary>
    /// 点评论头像 → 进 TA 的个人主页。
    /// 走的是和"播放器里点 UP 主头像"完全相同的一条路(NavigationDispatcher):
    /// 在主窗口内容区盖一层带返回按钮的主页, 而不是另开独立窗口。
    /// </summary>
    private void OnCommentAvatarClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Mid <= 0)
        {
            // 极少数情况接口没给 mid(已注销账号等), 给个明确反馈而不是"点了没反应"
            Svc.Toast.Show("没拿到 TA 的 UID, 打不开主页");
            return;
        }
        new NavigationDispatcher(this).OpenUserSpace(item.Mid.ToString());
    }

    /// <summary>
    /// 点评论缩略图 → 看大图(2026-10-31 评论图片功能)。
    /// DataContext 是 <see cref="CommentPicture"/>(模板直接绑图片项), 原图地址就在 Url 里。
    /// </summary>
    private void OnCommentPictureClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentPicture pic) return;
        e.Handled=true;OpenCommentPicture((FrameworkElement)sender,pic);
    }

    /// <summary>
    /// 点"回复" → 打开详情页并把输入框切到"回复 @某人"模式。
    ///
    /// 线程根要分清两种情况: 在详情页里点某条回复 → 根是详情页的线程根;
    /// 在列表里点顶层评论 → 根就是它自己(发出去的是给楼主的第一层回复)。
    /// </summary>
    private void OnCommentReplyClick(object sender, RoutedEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再回复");
            return;
        }
        var root = _detailThreadRoot != null && _detailThreadRoot.Replies.Contains(item)
            ? _detailThreadRoot
            : item;
        ShowCommentDetail(threadRoot: root, focus: root);
        // 点"回复"按钮: 用户明确要打字 → 弹出输入面板并抢焦点
        SetReplyTarget(item, focus: true, openPanel: true);
    }

    /// <summary>
    /// 把详情页输入框切到"回复 @XXX"模式(顶部冒出提示条, 发布时带 root/parent)。
    /// focus: 是否顺手把光标放进输入框 —— 点"回复"按钮时该给(用户接着就要打字),
    /// 进详情页时不给(一进门就抢焦点容易误触输入法)。
    /// openPanel: 是否**弹出输入面板**(2026-11-01 加)。
    /// ★ 只有"点回复按钮"才弹 —— 进详情页只是切回复**语义**, 每次都弹面板是打扰
    ///   (用户点进详情页多半只是想看回复, 上一版因此变成"一进详情页就弹输入框")。
    /// </summary>
    private void SetReplyTarget(CommentItem target, bool focus = true, bool openPanel = false)
    {
        _replyTarget = target;
        if (CommentDetailReplyHint != null) CommentDetailReplyHint.Visibility = Visibility.Visible;
        if (CommentDetailReplyTarget != null) CommentDetailReplyTarget.Text = "回复 @" + target.UserName;
        // 面板要开着才能打字; focus=false(进详情页)时展开但不抢键盘焦点
        if (openPanel && !_composerOpen) OpenComposer();
        if (focus) CommentDraft?.Focus();
        // 提示文案("Enter 发表" → "Enter 发送回复")与按钮可用性都跟着回复目标变
        UpdateCommentComposer();
        UpdatePostCommentButton();
    }

    private void ClearReplyTarget()
    {
        _replyTarget = null;
        if (CommentDetailReplyHint != null) CommentDetailReplyHint.Visibility = Visibility.Collapsed;
        if (CommentDetailReplyTarget != null) CommentDetailReplyTarget.Text = "";
        // 退出回复态 → 提示文案要退回"Enter 发表"
        UpdateCommentComposer();
    }

    /// <summary>退出"回复 @某人"模式, 退回发表普通评论</summary>
    private void OnCommentReplyCancel(object sender, RoutedEventArgs e)
    {
        ClearReplyTarget();
        CommentDraft?.Focus();
        UpdatePostCommentButton();
    }


    /// <summary>重新拉一条线程的第一页回复(发表成功后调用)。失败就保持原样, 不清空已有内容。</summary>
    private async Task ReloadThreadRepliesAsync(CommentItem root)
    {
        try
        {
            var (_, _, list) = await Svc.Api.GetReplyRepliesAsync(_aid, root.Rpid, pn: 1, ps: 20);
            if (_closing || list.Count == 0) return;
            // 重拉只覆盖第一页: 页码复位, "展开更多"重新可用(已加载的第 2 页会被第一页取代)
            root.RepliesNextPage = 2;
            var seen = new HashSet<long>();
            var merged = new List<CommentItem>();
            foreach (var r in list)
                if (r.Rpid > 0 && seen.Add(r.Rpid)) merged.Add(r);
            root.Replies.Clear();
            foreach (var r in merged) root.Replies.Add(r);
            if (root.ReplyCount < root.Replies.Count) root.ReplyCount = root.Replies.Count;
            SortRepliesInPlace(root);
            root.SyncPreviewReplies();
            root.NotifyRepliesChanged();
        }
        catch (Exception ex)
        {
            // 重拉失败不影响"已发布成功"这个结论, 记日志即可
            App.ReportError(ex);
        }
    }

    /// <summary>
    /// 点"查看对话" → 定位到被回复的那条(滚进视野 + 闪两下)。
    /// 目标还没加载出来(楼中楼只内联了前几条)时给明确提示, 而不是静默无反应。
    /// </summary>
    private void OnViewConversationClick(object sender, RoutedEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem reply) return;
        var root = _detailThreadRoot;
        if (root == null || reply.ParentRpid <= 0) return;

        var parent = root.Replies.FirstOrDefault(r => r.Rpid == reply.ParentRpid);
        if (parent == null)
        {
            Svc.Toast.Show("被回复的那条还没加载, 先点下方\u201c展开更多回复\u201d");
            return;
        }
        if (DetailRepliesList?.ItemContainerGenerator.ContainerFromItem(parent) is not FrameworkElement fe) return;
        fe.BringIntoView();
        // 闪一下: 告诉用户"就是这一条", 不然滚过去了也对不上号
        var flash = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(140))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2)
        };
        fe.BeginAnimation(UIElement.OpacityProperty, flash);
    }

    private async void OnLoadMoreRepliesClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _aid <= 0) return;
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Rpid <= 0 || item.IsLoadingReplies) return;

        item.IsLoadingReplies = true;
        try
        {
            var (_, _, more) = await Svc.Api.GetReplyRepliesAsync(_aid, item.Rpid, pn: item.RepliesNextPage, ps: 20);
            item.RepliesNextPage++;
            if (_closing) return;

            // 内联的那几条也在返回结果里, 用 rpid 去重再追加
            var seen = new HashSet<long>(item.Replies.Select(x => x.Rpid));
            foreach (var r in more)
                if (r.Rpid > 0 && seen.Add(r.Rpid)) item.Replies.Add(r);

            // 服务端也有取不全的情况(风控/已删除), 用实际条数收口,
            // 否则按钮会永远停在"展开 N 条回复"上点不动
            if (more.Count == 0 || item.Replies.Count >= item.ReplyCount)
                item.ReplyCount = item.Replies.Count;
            // 新补进来的一页按"当前排序"归位(接口固定按时间正序返回)
            SortRepliesInPlace(item);
            item.NotifyRepliesChanged();
        }
        finally
        {
            item.IsLoadingReplies = false;
        }
    }

    // ------------------------------------------------------------ 评论点赞

    /// <summary>
    /// 给一条评论点赞 / 取消点赞。
    ///
    /// 先**乐观更新**界面(数字立刻动), 请求失败再回滚 —— 点赞是高频小动作,
    /// 等一次网络往返再变数字会有明显的"点了没反应"感。
    /// LikeBusy 防连点: 连点会让 赞 / 取消 两个请求交叉, 服务端的最终状态就不确定了。
    /// </summary>
    private async void OnCommentLikeClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _aid <= 0) return;
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Rpid <= 0 || item.LikeBusy) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再点赞");
            return;
        }

        var target = !item.IsLiked;
        var oldCount = item.LikeCount;
        // 服务端行为: 点赞成功会自动消掉踩 → 本地状态也要跟着互斥
        var wasDisliked = item.IsDisliked;

        item.LikeBusy = true;
        item.IsLiked = target;
        item.LikeCount = Math.Max(0, oldCount + (target ? 1 : -1));
        if (target) item.IsDisliked = false;
        try
        {
            var (ok, err) = await Svc.Api.CommentLikeAsync(_aid, item.Rpid, target);
            if (_closing) return;
            if (ok && target) ThemeAudio.PlayLike();
            if (!ok)
            {
                // 回滚
                item.IsLiked = !target;
                item.LikeCount = oldCount;
                item.IsDisliked = wasDisliked;
                Svc.Toast.Show(err ?? "点赞失败");
            }
        }
        finally
        {
            item.LikeBusy = false;
        }
    }

    /// <summary>
    /// 点踩 / 取消点踩。与点赞对称(乐观更新 + 失败回滚), 区别是踩**没有计数** ——
    /// 接口不下发 dislike 总数, 所以只有图标的状态变化。
    ///
    /// ★ 踩走的是**另一个接口**(`/x/v2/reply/hate`), 不是点赞接口的 action=2 ——
    ///   2026-09-27 修: 之前发 action=2 给 /reply/action, 服务端回 "12011 不合法的赞或踩",
    ///   表现就是"点踩点了报错"。
    ///   两者在服务端互相覆盖(点踩成功会自动消掉赞), 所以本地状态也必须互斥。
    /// </summary>
    private async void OnCommentDislikeClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _aid <= 0) return;
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Rpid <= 0 || item.LikeBusy) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再点踩");
            return;
        }

        var target = !item.IsDisliked;       // true = 现在要点踩
        var wasLiked = item.IsLiked;
        var oldCount = item.LikeCount;

        item.LikeBusy = true;
        item.IsDisliked = target;
        // 点踩会顶掉赞(服务端行为), 本地那个数字也要跟着减回去
        if (target && wasLiked)
        {
            item.IsLiked = false;
            item.LikeCount = Math.Max(0, oldCount - 1);
        }
        try
        {
            var (ok, err) = await Svc.Api.CommentDislikeAsync(_aid, item.Rpid, target);
            if (_closing) return;
            if (!ok)
            {
                item.IsDisliked = !target;
                if (target && wasLiked)
                {
                    item.IsLiked = true;
                    item.LikeCount = oldCount;
                }
                Svc.Toast.Show(err ?? "点踩失败");
            }
        }
        finally
        {
            item.LikeBusy = false;
        }
    }

    // ------------------------------------------------------------ 发表评论

    private bool _postingComment;

    /// <summary>
    /// 输入框的可用状态与提示。三个前提缺一个就不能发: 已登录 / 有 aid / 不是本地文件播放。
    /// 直接把它们做进"输入框能不能打字"里, 比让用户敲完一大段再点按钮报错要好得多。
    /// </summary>
    private void UpdateCommentComposer()
    {
        if (_closing || CommentDraft == null) return;

        var canCompose = Svc.Session.HasLogin && _aid > 0 && !_isLocalPlayback && !_isLive;
        CommentDraft.IsEnabled = canCompose;
        // 提示文案要跟着"当前是发表还是回复"走, 否则回复态下还写着"Enter 发表"会让人困惑
        CommentPostHint.Text = _isLive
            ? "直播没有评论区"
            : !Svc.Session.HasLogin
                ? "登录后才能发表评论"
                : _aid <= 0
                    ? "没拿到视频 aid, 暂时不能评论"
                    : _replyTarget != null
                        ? "Enter 发送回复 · Shift+Enter 换行 · 点提示条上的“取消”改发评论"
                        : "Enter 发表 · Shift+Enter 换行";
        UpdatePostCommentButton();
        // FAB 的显隐跟着同一套前提走(2026-11-01 评论输入改为悬浮按钮)
        UpdateCommentFabVisibility();
    }

    // ------------------------------------------------------------ 评论输入 FAB(悬浮按钮 + 展开面板)

    /// <summary>面板是否已展开。true = 显示输入面板、FAB 变收起图标</summary>
    private bool _composerOpen;

    /// <summary>
    /// 输入框是否处于"展开高度"(2026-11-01 加, 写长评用)。
    /// ★ 它与 <see cref="_composerOpen"/> 是两回事: 面板收起后再打开, 展开**高度**保持
    ///   —— 用户写了长文收起面板看一眼列表再回来, 不该被打回 66px 的"摸黑"模式。
    ///   只有发成功(草稿清空)才回到普通高度。
    /// </summary>
    private bool _composerExpanded;

    /// <summary>输入框普通高度(两三行, 与旧版内嵌输入框一致)</summary>
    private const double ComposerNormalHeight = 66;

    /// <summary>输入框展开高度(约 9 行, 1000 字长评能整篇通读)</summary>
    private const double ComposerExpandedHeight = 200;

    /// <summary>
    /// 展开/收回输入框高度。
    /// ★ 直接赋值 Height, 不做动画: 试过 DoubleAnimation, 探针/真机上都出现过"动画时钟
    ///   未推进、Height 弹回原值"的不稳(WPF 高度动画依赖渲染节拍, 在面板刚展开/布局
    ///   未稳时不可靠)。这里是功能性切换不是装饰, 行为确定性优先 —— 高度瞬变虽不如
    ///   动画顺滑, 但 66↔200 的变化只发生在用户主动点击的那一下, 视觉可接受。
    /// </summary>
    private void ApplyComposerSize()
    {
        if (_closing || CommentDraft == null || ComposerExpandIcon == null) return;
        var target = _composerExpanded ? ComposerExpandedHeight : ComposerNormalHeight;
        ComposerExpandIcon.Text = _composerExpanded ? "\uE70E" : "\uE70D";  // 上下箭头互换
        var btn = BtnComposerExpand;
        if (btn != null) btn.ToolTip = _composerExpanded ? "收回输入框" : "展开输入框, 写长评时能通读全文";

        // 先摘掉可能残留的旧高度动画(历史遗留防御: 早期版本用过动画), 再落本地值
        CommentDraft.BeginAnimation(HeightProperty, null);
        CommentDraft.Height = target;
    }

    /// <summary>面板头行的"展开/收回"按钮</summary>
    private void OnComposerExpandClick(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _composerExpanded = !_composerExpanded;
        ApplyComposerSize();
        // 展开后把光标挪回输入框(用户就是要看/写内容)
        if (_composerOpen) CommentDraft?.Focus();
    }

    /// <summary>
    /// FAB 的显隐。只在「评论」tab 且有"能评论的前提"时出现 ——
    /// 直播/本地文件/简介 tab 下整颗收起来, 别让用户点了个寂寞。
    /// ★ tab 状态从 TabComments.IsChecked 读, 换片/切 tab 后由 UpdateCommentComposer 统一刷新。
    /// </summary>
    private void UpdateCommentFabVisibility()
    {
        if (_closing || CommentFab == null) return;
        var usable = !_isLive && !_isLocalPlayback && _aid > 0 &&
                     CommentsPanel != null && CommentsPanel.Visibility == Visibility.Visible;
        CommentFab.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        BtnBackToComments.Visibility = CommentsPanel?.Visibility == Visibility.Visible && IsCommentDetailOpen
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateAlternateVideoControls();
        // 前提没了(切到直播/本地文件)时把展开的面板一起收掉
        if (!usable && _composerOpen) CloseComposer();
    }

    /// <summary>FAB 点击: 展开 ↔ 收起。未登录等"不能评论"的情形给 Toast 说明, 不展开空面板</summary>
    private void OnCommentFabClick(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_composerOpen) { CloseComposer(); return; }

        // 与 UpdateCommentComposer 同一套前提; 不满足时说明原因(不展开空面板)
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("登录后才能发表评论"); return; }
        if (_isLive) { Svc.Toast.Show("直播没有评论区"); return; }
        if (_isLocalPlayback) { Svc.Toast.Show("本地文件没有评论区"); return; }
        if (_aid <= 0) { Svc.Toast.Show("没拿到视频 aid, 暂时不能评论"); return; }

        OpenComposer();
    }

    /// <summary>
    /// 展开输入面板: 面板从底部滑入(位移 + 淡入), FAB 图标换成收起箭头, 焦点进输入框。
    /// ★ 动画用 RenderTransform(TranslateY)而不是改 Margin: 不触发布局重排, 顺滑且零开销。
    /// </summary>
    private void OpenComposer()
    {
        if (_composerOpen || CommentComposerPanel == null || CommentFabIcon == null) return;
        _composerOpen = true;
        CommentFabIcon.Text = "\uE74A";   // ChevronDown: 收起
        CommentFab.ToolTip = "收起评论框";

        var panel = CommentComposerPanel;
        panel.Visibility = Visibility.Visible;
        // 初始态: 压在底下 24px + 全透明, 再动画回来。
        // ★ Opacity 必须先显式归 0: 收起路径(FinishCloseComposer)会把基准值复位成 1,
        //   不归 0 的话"收起 → 立刻再打开"这次淡入是 1→1, 看不到过渡。
        panel.Opacity = 0;
        var translate = new TranslateTransform(0, 24);
        panel.RenderTransform = translate;
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        // 焦点进输入框(等可见性生效之后; 走 Dispatcher 排一轮)
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            new Action(() => { if (!_closing && _composerOpen) CommentDraft?.Focus(); }));

        // 输入框高度按"展开"状态对齐(上次展开过高、收起面板后再开, 仍保持大框)
        ApplyComposerSize();
    }

    /// <summary>收起输入面板: 淡出后 Visibility=Collapsed。草稿**不清**(只是收起, 不是放弃)</summary>
    private void CloseComposer()
    {
        if (!_composerOpen || CommentComposerPanel == null || CommentFabIcon == null) return;
        _composerOpen = false;
        CommentFabIcon.Text = "\uE70F";   // Edit: 笔
        CommentFab.ToolTip = "写评论";

        var panel = CommentComposerPanel;
        var anim = new DoubleAnimation(panel.Opacity, 0, TimeSpan.FromMilliseconds(140))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => FinishCloseComposer(panel);
        panel.BeginAnimation(OpacityProperty, anim);
        // ★ 兜底(2026-11-01): Completed 回调依赖渲染节拍, 个别环境(布局刚抖过/窗口后台)会不回调,
        //   面板就"看不见但还占着命中测试"。300ms 后(动画早已该结束)再落一次 ——
        //   FinishCloseComposer 幂等, 重复调无副作用; 若期间面板又被打开(_composerOpen=true)则跳过。
        _ = Task.Delay(300).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            FinishCloseComposer(panel))));
    }

    /// <summary>把面板真正落回 Collapsed(幂等: 淡出动画回调与兜底定时器都会调它)</summary>
    private void FinishCloseComposer(Border panel)
    {
        if (_closing || _composerOpen) return;   // 期间又被打开就不收
        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = 1;                        // 复位基准值, 下次 OpenComposer 从 0 淡入
        panel.Visibility = Visibility.Collapsed;
    }

    private void UpdatePostCommentButton()
    {
        if (_closing || PostCommentButton == null || CommentDraft == null) return;
        PostCommentButton.IsEnabled = CommentDraft.IsEnabled && !_postingComment
                                      && !string.IsNullOrWhiteSpace(CommentDraft.Text);
    }

    private void OnCommentDraftChanged(object sender, TextChangedEventArgs e)
        => UpdatePostCommentButton();

    /// <summary>
    /// Enter 发表, Shift+Enter 换行。用 Preview 阶段拦: 输入框开着 AcceptsReturn,
    /// 换行是在 TextBox 自己的 KeyDown 里插进去的, 冒泡到我们的处理器时已经晚了。
    /// </summary>
    private void OnCommentDraftPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;   // 换行, 放它过去
        e.Handled = true;
        _ = PostCommentAsync();
    }

    private void OnPostCommentClick(object sender, RoutedEventArgs e) => _ = PostCommentAsync();

    /// <summary>
    /// 发表。**同一个输入框两种语义**(用户规则: 在列表页评论 = 自己开新楼层; 在详情页评论 = 回复这层楼):
    /// 有回复目标(_replyTarget) 就带 root/parent 发成楼中楼回复, 否则发普通评论。
    ///
    /// 失败**不清空输入框** —— 网络抖一下就把用户写的字抹掉, 是最让人恼火的那种"智能"。
    ///
    /// 成功后重新拉一次列表, 而不是本地插一条假的: 服务端返回的 rpid / 楼层 / 是否要审核
    /// 都不是本地能猜的, 拼一条出来看着即时, 一旦用户去点赞就会发现那条根本不存在。
    /// </summary>
    private async Task PostCommentAsync()
    {
        if (_closing || _postingComment) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("发表评论需要先登录"); return; }
        if (_aid <= 0) { Svc.Toast.Show("没拿到视频 aid, 暂时不能评论"); return; }

        var text = CommentDraft.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        var target = _replyTarget;                     // null = 发表新楼层
        var root = _detailThreadRoot;
        // 回复必须**同时**带 root 与 parent: 少了 root 服务端会把回复挂到根楼层上(回错位置)
        var rootRpid = root != null && target != null ? root.Rpid : 0;
        var parentRpid = target?.Rpid ?? 0;
        var isReply = rootRpid > 0 && parentRpid > 0;

        _postingComment = true;
        PostCommentButton.Content = isReply ? "回复中…" : "发表中…";
        UpdatePostCommentButton();
        try
        {
            var (ok, err) = await Svc.Api.PostCommentAsync(_aid, text, rootRpid, parentRpid);
            if (_closing) return;
            if (!ok)
            {
                Svc.Toast.Show(err ?? (isReply ? "回复发送失败" : "评论发表失败"));
                return;
            }

            CommentDraft.Clear();
            ClearReplyTarget();     // 发完退回"发表"模式, 免得下一条手滑又回给同一个人
            Svc.Toast.Show(isReply ? "回复已发布" : "评论已发表");
            // 发完把面板收起来: 输入使命完成, 别挡着用户看列表。
            // ★ 展开高度一并归位: 内容已清空, 大框没有存在的意义(下次写长评再点展开)。
            if (_composerExpanded)
            {
                _composerExpanded = false;
                ApplyComposerSize();
            }
            CloseComposer();

            if (isReply && root != null)
                await ReloadThreadRepliesAsync(root);   // 回复: 只重拉这条线程
            else
                await LoadCommentsAsync(force: true);   // 评论: 重拉整页列表

            if (!_closing && CommentHint != null)
            {
                // 说实话: 列表是"按热度"排的、回复还要过审排队 ——
                // 不说这句, 用户会以为内容没发出去(或发了但被吞了)。
                CommentHint.Text = isReply
                    ? "回复已发布 · 服务端要过审排队, 可能不会立刻出现在回复列表里"
                    : "评论已发表 · 列表按热度排序, 刚发的可能不在前几条";
            }
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            if (!_closing) Svc.Toast.Show(isReply ? "回复发送失败" : "评论发表失败");
        }
        finally
        {
            _postingComment = false;
            if (!_closing)
            {
                PostCommentButton.Content = "发表";
                UpdatePostCommentButton();
            }
        }
    }

}
