using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.ViewModels;

/// <summary>动态页：按全部关注或单个 UP 浏览综合时间线与纯视频。</summary>
public class FollowViewModel : ObservableObject
{
    private bool _loading;
    private bool _loadingMore;
    private bool _hasMore = true;
    private string _error = "";
    private long _selfMid;
    private bool _hasMoreUsers = true;

    /// <summary>
    /// 左栏 UP 列表还有没有下一页（页面滚动加载的闸门，见 FollowPage.UpListScroll_OnScrollChanged）。
    /// ★ 2026-11-08 暴露成属性：之前它是私有字段，页面只能用"Users.Count>0"当判据，
    ///   于是列表翻完之后每次滚动到底都会再打一次"下一页"的请求 —— 白打，且在限流
    ///   敏感期里纯属添乱。
    /// </summary>
    public bool HasMoreUsers => _hasMoreUsers;
    private bool _loadingUsers;
    public bool LoadingUsers { get => _loadingUsers; private set => SetProperty(ref _loadingUsers, value); }
    private int _followingPage = 1;      // 左栏 UP 列表的翻页

    public ObservableCollection<FollowUser> Users { get; } = new();

    /// <summary>右栏内容(视频卡片)。切选择时整体换血。</summary>
    public ObservableCollection<VideoItem> FeedItems { get; } = new();

    /// <summary>综合时间线。两种模式共享数据和分页游标，切换不重新请求。</summary>
    public ObservableCollection<DynamicItem> Dynamics { get; } = new();

    /// <summary>独立视图，避免纯视频筛选影响综合时间线的默认视图。</summary>
    public ICollectionView VideoFeed { get; }

    private bool _videoOnly;
    public bool VideoOnly
    {
        get => _videoOnly;
        private set
        {
            if (!SetProperty(ref _videoOnly, value)) return;
            OnPropertyChanged(nameof(ShowDynamicFeed));
            OnPropertyChanged(nameof(ShowVideoFeed));
            OnPropertyChanged(nameof(FeedTagAll));
            OnPropertyChanged(nameof(FeedTagVideo));
        }
    }
    public bool ShowDynamicFeed => !VideoOnly;
    public bool ShowVideoFeed => VideoOnly;
    public string FeedTagAll => VideoOnly ? "off" : "on";
    public string FeedTagVideo => VideoOnly ? "on" : "off";
    public ICommand ShowAllFeedCommand { get; }
    public ICommand ShowVideoFeedCommand { get; }

    private FollowUser? _selectedUp;

    /// <summary>当前选中的 UP(null = 全部动态)</summary>
    public FollowUser? SelectedUp
    {
        get => _selectedUp;
        private set
        {
            if (_selectedUp == value) return;
            var old = _selectedUp;
            _selectedUp = value;
            // 选中态是左栏列表项的高亮依据: **必须真正赋值**(IsSelected 的 setter 自带通知)。
            // 之前只调 RaiseSelectedChanged() 发通知但不改字段值 —— WPF 收到通知回头一读
            // 还是 false, 触发器永远不亮, 这就是"选中没有粉色"的根因。
            if (old != null) old.IsSelected = false;
            if (value != null) value.IsSelected = true;
            OnPropertyChanged(nameof(IsAllSelected));
            OnPropertyChanged(nameof(SelectionTitle));

        }
    }

    /// <summary>是否选中了「全部动态」(左栏入口的高亮依据)</summary>
    public bool IsAllSelected => SelectedUp == null;

    /// <summary>右栏标题: 说明当前在看谁的内容</summary>
    public string SelectionTitle => SelectedUp == null ? "全部动态" : SelectedUp.Name + " 的动态";

    public bool Loading { get => _loading; private set { if (SetProperty(ref _loading, value)) { OnPropertyChanged(nameof(ShowFullLoading)); OnPropertyChanged(nameof(ShowContent)); } } }

    /// <summary>
    /// 整页加载遮罩只在"右栏还没有任何内容"时显示。
    /// 切 UP / 回全部动态时右栏换血, 若还整页盖 spinner 会闪烁 —— 那种情况
    /// 让旧内容留着、右栏自己呈现加载状态更稳。
    /// </summary>
    public bool ShowFullLoading => Loading && Dynamics.Count == 0 && Users.Count == 0;
    public bool ShowContent => !NotLoggedIn && !ShowFullLoading;
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public int Count => Users.Count;
    public bool NotLoggedIn => !Svc.Session.HasLogin;

    /// <summary>
    /// 发布按钮的可用性(2026-11-04)。
    /// 与 <see cref="NotLoggedIn"/> 分开是因为语义不同: 未登录时"发布"要**禁用**而不是
    /// 跟着整页藏起来 —— 用户得先看见这个功能, 才可能去登录。
    /// </summary>
    public bool IsLoggedInForPublish => Svc.Session.HasLogin;

    // ------------------------------------------------------------ 关注直播看板(2026-10-31)

    /// <summary>正在直播的关注主播(点一条直接进 TA 的直播间)。空 = 没人在播(看板整块收起)。</summary>
    public ObservableCollection<VideoItem> LiveNow { get; } = new();

    // ★ 2026-11-04 合并上游 1.5.4: 默认**收起**。
    //   理由(上游原话): 关注直播看板占着动态流首屏, 而它多数时候只有两三行 ——
    //   先让动态内容露出来, 想看谁在播再点标题展开。标题行**常驻**(见 LiveBoardTitle),
    //   人数一直可见, 不至于让人以为功能没了。
    private bool _liveBoardOpen;

    /// <summary>看板展开/收起(UI 状态, 不落盘 —— 每次进动态页默认收起, 想看再点)</summary>
    public bool LiveBoardOpen
    {
        get => _liveBoardOpen;
        set => SetProperty(ref _liveBoardOpen, value);
    }

    /// <summary>看板标题: 说明有几人在播, 0 人时整个看板隐藏</summary>
    public string LiveBoardTitle => $"关注直播 · {LiveNow.Count}";

    /// <summary>看板加载中(随 RefreshAsync 并行, 不挡动态流)</summary>
    private bool _liveBoardLoading;

    /// <summary>看板请求在途(挡住定时刷新与人工刷新撞车; 不参与 UI 绑定)</summary>
    private bool _liveBoardBusy;

    public bool LiveBoardLoading
    {
        get => _liveBoardLoading;
        private set
        {
            if (SetProperty(ref _liveBoardLoading, value))
                OnPropertyChanged(nameof(ShowLiveEmptyText));
        }
    }

    /// <summary>看板有没有内容可显示(有主播在播)</summary>
    public bool HasLiveNow => LiveNow.Count > 0;

    /// <summary>
    /// 看板空态说明("关注的主播暂时都没有在直播")的可见性:
    /// 加载**完成**且没人在播才显示 —— 加载中显示它会把"正在查"误报成"没人在播"。
    /// (Style 触发器压不过属性绑定, 这种复合条件在 VM 里算好最干净。)
    /// </summary>
    public bool ShowLiveEmptyText => !LiveBoardLoading && !HasLiveNow;

    /// <summary>
    /// 拉"关注的主播正在直播"列表。与动态流**并行**加载(不占 RefreshAsync 的关键路径),
    /// 失败静默 —— 看板是锦上添花, 不该因为它给整页报错。
    ///
    /// ★ 进入动态页时总会调用一次(见 InitAsync 与 FollowPage 的定时刷新),
    ///   所以必须有**并发闸**: 定时器与服务端响应慢时的人工刷新会撞在一起,
    ///   两路结果先后 Clear+Add 会让列表闪一下、条数也可能对不上。
    ///
    /// ★ showLoading: 后台轮询不要点亮标题行那只 LoadingRing ——
    ///   否则没人在播时, 空态文案每分钟消失半秒, 看着像在抽风。
    /// </summary>
    public async Task RefreshLiveBoardAsync(bool showLoading = true)
    {
        if (!Svc.Session.HasLogin || _liveBoardBusy) return;
        _liveBoardBusy = true;
        var session = Svc.Session.Current.SessData;
        if (showLoading) LiveBoardLoading = true;
        try
        {
            var (ok, _, items) = await Svc.Api.GetFollowingLiveAsync();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LiveBoardLoading = false;
                if (!ok || session != Svc.Session.Current.SessData) return;   // 静默: 风控/网络抖动时看板保持原样
                LiveNow.Clear();
                if (items != null)
                    foreach (var it in items) LiveNow.Add(it);
                OnPropertyChanged(nameof(LiveBoardTitle));
                OnPropertyChanged(nameof(HasLiveNow));
                OnPropertyChanged(nameof(ShowLiveEmptyText));
            });
        }
        catch
        {
            LiveBoardLoading = false;
            // 静默(同上)
        }
        finally
        {
            _liveBoardBusy = false;
        }
    }

    /// <summary>点看板里的一条 → 直接进 TA 的直播间</summary>
    public void OpenLive(VideoItem item)
    {
        if (item == null || item.RoomId <= 0) return;
        Svc.Player.PlayLive(item.RoomId, item.Title);
    }

    // ------------------------------------------------------------ 左栏排序与未读汇总(2026-11-05)

    /// <summary>
    /// 左栏的排序模式。**只改展示顺序, 不重新请求接口**(用户可解释、可切换, 见 FollowSorting)。
    ///
    /// ★ 落盘走 Svc.Settings.SetFollowSortMode —— 与 FollowCardWall 同一类用户偏好:
    ///   点一次就记住, 重启/切页回来还是他选的那档。
    /// ★ 切模式立即生效: setter 里直接重排 Users(ObservableCollection.Move, 不动数据)。
    /// </summary>
    public FollowSortMode FollowSortMode
    {
        get => _followSortMode;
        set
        {
            if (!FollowSorting.IsDefined(value)) return;
            if (!SetProperty(ref _followSortMode, value)) return;
            Svc.Settings.SetFollowSortMode(value);
            OnPropertyChanged(nameof(SortTagRecent));
            OnPropertyChanged(nameof(SortTagUnread));
            OnPropertyChanged(nameof(SortTagOrder));
            OnPropertyChanged(nameof(SortModeTip));
            SortUsers();
        }
    }

    private FollowSortMode _followSortMode = FollowSorting.DefaultMode;

    /// <summary>
    /// chip 的选中态: 只有被选中的那枚 Tag="on"(见 FollowPage 的 FeedLayoutChipStyle DataTrigger)。
    /// ★ 由 ViewModel 一个地方算, 而不是 code-behind 逐个写 Tag ——
    ///   分散着写必然出现"两枚都亮"或"一枚都不亮"(播放页评论排序踩过这条)。
    /// </summary>
    public string SortTagRecent => _followSortMode == FollowSortMode.RecentUpdate ? "on" : "off";
    public string SortTagUnread => _followSortMode == FollowSortMode.UnreadFirst ? "on" : "off";
    public string SortTagOrder => _followSortMode == FollowSortMode.FollowOrder ? "on" : "off";

    /// <summary>排序控件的悬浮说明(说清当前这档到底怎么排)</summary>
    public string SortModeTip =>
        "当前排序：" + FollowSorting.Label(_followSortMode) + "（" + FollowSorting.Description(_followSortMode) + "）";

    /// <summary>
    /// 关注序号: mid → 第一次见到该 UP 时的序号(接口原序)。
    /// ★ 这就是"关注顺序"在多次刷新/翻页之后仍然稳定的依据 —— 每次刷新都会重建 Users,
    ///   但序号只增不改, 所以同一批 UP 的相对次序永远一致。
    /// ★ 账号切换(_selfMid 变了)时整张表清空。
    /// </summary>
    private readonly Dictionary<long, int> _followOrder = new();
    private int _nextFollowOrder;

    /// <summary>
    /// 每个 UP **已知的最新发布时间**(只增不减)。
    /// ★ 为什么累计而不是每页重算: 排序依据要在"切到单 UP 视图 → 切回全部动态"、
    ///   "翻了一页 → 又翻回第一页"之间保持一致。只看当前这一页的话, 一换视图
    ///   整列顺序就会重排一次 —— 那正是要避免的"每次刷新都抖动"。
    /// ★ 只增不减的含义: 一个 UP 昨天发过、今天没发, 它记的仍是昨天那条的时间,
    ///   所以它不会因为"这一页里没出现"而被当成"从没发过"踢到最后。
    /// </summary>
    private readonly Dictionary<long, long> _latestTs = new();

    /// <summary>最近一次见到的每个 UP 最新一条动态 id(只用于"全部标记已读"找不到条目时兜底)</summary>
    private readonly Dictionary<long, string> _latestDynamicId = new();

    /// <summary>左栏汇总徽标文案("N 个 UP 有更新"); 0 个时为空串</summary>
    private string _unreadSummary = "";
    public string UnreadSummary
    {
        get => _unreadSummary;
        private set => SetProperty(ref _unreadSummary, value);
    }

    /// <summary>汇总徽标可见性(没有未读时不占位)</summary>
    private bool _hasUnreadSummary;
    public bool HasUnreadSummary
    {
        get => _hasUnreadSummary;
        private set => SetProperty(ref _hasUnreadSummary, value);
    }

    /// <summary>"全部标记已读"的按钮文案(点完短暂变"已标记")</summary>
    private string _markAllReadText = "全部标记已读";
    public string MarkAllReadText
    {
        get => _markAllReadText;
        private set => SetProperty(ref _markAllReadText, value);
    }

    /// <summary>刚标记完(用于给按钮上一层强调色, 制造"确实点到了"的确认感)</summary>
    private bool _markAllReadFlash;
    public bool MarkAllReadFlash
    {
        get => _markAllReadFlash;
        private set => SetProperty(ref _markAllReadFlash, value);
    }

    /// <summary>左栏有 UP 可操作(没登录/还没加载出来时禁用"全部标记已读")</summary>
    public bool HasFollowUsers => Users.Count > 0;

    public ICommand SortRecentCommand { get; private set; } = null!;
    public ICommand SortUnreadCommand { get; private set; } = null!;
    public ICommand SortOrderCommand { get; private set; } = null!;
    public ICommand MarkAllReadCommand { get; private set; } = null!;

    /// <summary>刷新命令之前的部分(构造函数里挂 Command 用)</summary>
    private void InitSortCommands()
    {
        _followSortMode = Svc.Settings.FollowSortMode;
        SortRecentCommand = new RelayCommand(() => FollowSortMode = FollowSortMode.RecentUpdate);
        SortUnreadCommand = new RelayCommand(() => FollowSortMode = FollowSortMode.UnreadFirst);
        SortOrderCommand = new RelayCommand(() => FollowSortMode = FollowSortMode.FollowOrder);
        MarkAllReadCommand = new AsyncRelayCommand(MarkAllReadAsync);
    }

    /// <summary>
    /// 按当前模式重排左栏。用 ObservableCollection.Move 做**原地**重排:
    ///   · 不 Clear+重填 —— 那会让左栏滚动位置归零、选中项闪一下;
    ///   · Move 产生的 CollectionChanged 是原生 Move 动作, ItemsControl 只挪容器。
    /// 已经是目标顺序时**一个动作都不发**(逐个比对后短路), 所以可以放心频繁调用。
    /// ★ internal 而不是 private: 离线测试(FollowSortTests)要直接驱动这条链路;
    ///   生产侧的入口只有 UpdateUnreadBadges / RefreshServerUnreadAsync / 切模式。
    /// </summary>
    internal void SortUsers()
    {
        // Reading changes badges immediately, but the visible contact list stays put
        // until an explicit page refresh. In-flight polls/pages obey the same gate.
        if (_keepContactOrder || Users.Count < 2) return;
        var ordered = FollowSorting.Arrange(Users, _latestTs, _followOrder, _followSortMode);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i >= Users.Count || ReferenceEquals(Users[i], ordered[i])) continue;
            var from = IndexOfUser(ordered[i], i);
            if (from < 0) continue;
            if (from != i) Users.Move(from, i);
        }
    }

    private int IndexOfUser(FollowUser user, int hint)
    {
        if (hint >= 0 && hint < Users.Count && ReferenceEquals(Users[hint], user)) return hint;
        for (var i = 0; i < Users.Count; i++)
            if (ReferenceEquals(Users[i], user)) return i;
        return -1;
    }

    /// <summary>给新出现的 UP 分配关注序号(已有的不动)。internal 是为了让离线测试能直接驱动这条链路</summary>
    internal void EnsureFollowOrder(IEnumerable<FollowUser> users)
    {
        if (users == null) return;
        foreach (var u in users)
        {
            if (u == null || u.Mid <= 0) continue;
            if (_followOrder.ContainsKey(u.Mid)) continue;
            _followOrder[u.Mid] = _nextFollowOrder++;
        }
    }

    /// <summary>
    /// 清空排序/红点用的本地缓存(切换账号时调用)。
    /// ★ 不清的表现: 换个人登录后, 新账号的关注列表会沿用上一个人的关注序号,
    ///   左栏顺序看起来完全是乱的, 而"关注顺序"本来应该是可预期的。
    /// 服务端未读快照和请求代次也必须随账号一起重置。
    /// </summary>
    private void ResetSortCaches()
    {
        _keepContactOrder = false;
        ++_unreadRequestVersion;
        _serverUnread.Clear();
        _followOrder.Clear();
        _latestTs.Clear();
        _latestDynamicId.Clear();
        _nextFollowOrder = 0;
    }


    /// <summary>
    /// 把一批动态的 PubTs 记进"已知最新时间"(只增不减, 见 _latestTs 的说明)。
    /// internal: 离线测试要构造"左栏顺序跟着动态时间走"的场景(生产入口是 UpdateUnreadBadges)。
    /// </summary>
    internal void RememberTimestamps(IEnumerable<DynamicItem> items)
    {
        if (items == null) return;
        foreach (var it in items)
        {
            if (it == null || it.AuthorMid <= 0) continue;
            var mid = it.AuthorMid;
            if (it.PubTs > 0)
            {
                if (!_latestTs.TryGetValue(mid, out var cur) || it.PubTs > cur)
                {
                    _latestTs[mid] = it.PubTs;
                    // ★ id 必须与"最大时间戳"配套更新:
                    //   无条件写的话, 后面再遇到一条更旧的条目会把 "全部标记已读" 的兜底 id
                    //   覆盖成旧的 —— 那等于把基准往回推, 下次刷新红点又亮, 看着像消不掉。
                    if (!string.IsNullOrEmpty(it.Id)) _latestDynamicId[mid] = it.Id;
                }
            }
            else if (!string.IsNullOrEmpty(it.Id) && !_latestDynamicId.ContainsKey(mid))
            {
                // 没有时间戳的条目: 只在还没有任何记录时占个位(排序用不到它, 但兜底要有个 id)
                _latestDynamicId[mid] = it.Id;
            }
        }
    }

    /// <summary>重算左栏汇总徽标("N 个 UP 有更新")</summary>
    private void RaiseUnreadSummary()
    {
        var n = 0;
        foreach (var u in Users) if (u.NewCount > 0) n++;
        UnreadSummary = n > 0 ? $"{n} 个 UP 有更新" : "";
        HasUnreadSummary = n > 0;
    }

    /// <summary>
    /// 左栏集合变化 → 维护"每个 UP 的 NewCount 事件订阅"。
    ///
    /// ★ 为什么订阅而不是等 UpdateUnreadBadges 手动刷汇总:
    ///   汇总徽标("N 个 UP 有更新")和"未读优先"的排序都依赖 NewCount,
    ///   而 NewCount 会被**四个地方**改(算红点 / 点开清红点 / 全部标记已读 / 取消关注后的重算)。
    ///   逐个调用点去记得刷汇总, 迟早漏一处 —— 那处的表现就是"红点灭了但徽标还写着 2 个"。
    ///   订阅之后: 谁改都对, 汇总与排序自动跟上。
    /// </summary>
    private void OnUsersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if(e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move) return;
        if (e.OldItems != null)
            foreach (var item in e.OldItems)
                if (item is FollowUser old) old.PropertyChanged -= OnUserPropertyChanged;
        if (e.NewItems != null)
            foreach (var item in e.NewItems)
                if (item is FollowUser fresh) fresh.PropertyChanged += OnUserPropertyChanged;

        if (_batchUsers) return;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasFollowUsers));
        RaiseUnreadSummary();
    }

    private void OnUserPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FollowUser.NewCount) && e.PropertyName != nameof(FollowUser.HasNew)) return;
        if (_suppressSort || _batchUsers) return;
        RaiseUnreadSummary();
        // 未读优先模式下, 红点一变就要重排 —— 否则"点开一个 UP"之后它还赖在最前面。
        if (_followSortMode == FollowSortMode.UnreadFirst) SortUsers();
    }

    /// <summary>
    /// 批量改 NewCount 期间**禁止**自动排序。
    ///
    /// ★★★ 这不是优化, 是正确性: 算红点(ApplyBadges)和"全部标记已读"都是
    ///   `foreach (var u in Users) u.NewCount = ...` —— 如果此时 OnUserPropertyChanged 里
    ///   顺手 SortUsers(), 那就是**遍历中改集合**(ObservableCollection.Move),
    ///   .NET 的集合枚举器会当场抛 InvalidOperationException。
    ///   在「未读优先」模式下必现(别的模式因为不重排所以侥幸不炸), 而且它发生在
    ///   定时轮询路径里 —— 表现就是"过两分钟动态页崩一次"。
    /// </summary>
    private bool _suppressSort;
    private bool _keepContactOrder;

    /// <summary>批量改 NewCount 用: 期间不自动排序, 退出时统一重排一次</summary>
    private void WithoutAutoSort(Action body)
    {
        var prev = _suppressSort;
        _suppressSort = true;
        try { body(); }
        finally { _suppressSort = prev; }
        RaiseUnreadSummary();
        SortUsers();
    }

    /// <summary>Use the official UP-selection requests, then confirm read state from the server.</summary>
    private async Task MarkAllReadAsync()
    {
        if (!Svc.Session.HasLogin) { Error = "请先登录后同步已读状态"; return; }
        var account = Svc.Session.Current.DedeUserID;
        var targets = Users.Where(u => u.HasNew).Select(u => u.Mid).Distinct().ToArray();
        if (targets.Length == 0) return;
        _keepContactOrder = true;
        ++_markAllReadVersion;
        MarkAllReadFlash = false;
        MarkAllReadText = "正在同步…";
        Error = "";
        ++_unreadRequestVersion; // Discard a poll that started before this read action.
        var failed = false;
        var confirmed = false;
        try
        {
            foreach (var mid in targets)
            {
                if (account != Svc.Session.Current.DedeUserID) return;
                var result = await Svc.Api.GetDynamicFeedFullAsync(hostMid: mid);
                if (!result.ok) failed = true;
            }
            if (account != Svc.Session.Current.DedeUserID) return;
            var synced = await RefreshServerUnreadAsync();
            confirmed = !failed && synced && targets.All(mid => !_serverUnread.GetValueOrDefault(mid));
            if (confirmed) FlashMarkAllReadDone();
            else Error = "部分动态已读状态尚未同步到 B 站，请稍后刷新或重试";
        }
        finally
        {
            if (!confirmed) MarkAllReadText = "全部标记已读";
        }
    }

    /// <summary>按钮文案短暂变"已标记"再恢复 —— 给一次明确的"点到了"反馈</summary>
    private int _markAllReadVersion;
    private async void FlashMarkAllReadDone()
    {
        var version = ++_markAllReadVersion;
        try
        {
            MarkAllReadFlash = true;
            MarkAllReadText = "已标记";
            await Task.Delay(1600);
            if (version != _markAllReadVersion) return;
            MarkAllReadFlash = false;
            MarkAllReadText = "全部标记已读";
        }
        catch
        {
            // 只是提示态: 任何异常都退回到静态文案, 不影响已读基准
            MarkAllReadFlash = false;
            MarkAllReadText = "全部标记已读";
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand OpenUpCommand { get; }

    // 分页游标(两种来源各自一套)。
    // "快速连点导致结果串台"用 LoadSelectionAsync 里的 forUp 快照 + SelectedUp 比对兜住。
    private string? _feedOffset;         // 全部动态: 游标
    /// <summary>单 UP 动态流的分页游标(feed/space 用 offset 而不是页码)</summary>
    private string? _upDynOffset;        // 单 UP: 页码

    /// <summary>
    /// 动态流里同一视频会出现多次(转发 + 原投稿 + 多人合作视频各自的动态),
    /// 按跨页累积的 bvid 去重, 否则"全部动态"里能看到两张一模一样的卡片。
    /// reset 时清空。
    /// </summary>
    private readonly HashSet<string> _seenBvid = new(StringComparer.Ordinal);

    /// <summary>混合流已收录的动态 id(翻页去重)</summary>
    private readonly HashSet<string> _seenDynamicIds = new(StringComparer.Ordinal);

    public FollowViewModel()
    {
        VideoFeed = new ListCollectionView(Dynamics);
        FilterService.Instance.Attach(VideoFeed,
            item => item is DynamicItem { Kind: DynamicKind.Video, Video: { } video }
                && (video.Bvid.Length > 0 || video.Aid > 0));
        ShowAllFeedCommand = new RelayCommand(() => VideoOnly = false);
        ShowVideoFeedCommand = new RelayCommand(() => VideoOnly = true);
        InitSortCommands();
        // 左栏集合变化 → 维护 NewCount 订阅(汇总徽标/未读优先排序据此自动跟上, 见 OnUsersChanged)
        Users.CollectionChanged += OnUsersChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
        LoginCommand = new RelayCommand(() =>
        {
            var win = new Views.LoginWindow
            {
                Owner = System.Windows.Application.Current.MainWindow,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
            };
            win.ShowDialog();
        });
        OpenUpCommand = new RelayCommand(p =>
        {
            if (p is FollowUser u)
            {
                Svc.Navigate?.OpenUserSpace(u.Mid.ToString());
            }
        });
        Svc.Session.Changed += OnSessionChanged;
    }

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(NotLoggedIn));
        OnPropertyChanged(nameof(ShowContent));
        _ = RefreshAsync();
    }

    public async Task InitAsync()
    {
        // ★ 页面实例是长期缓存的(MainWindow._pages): 从别的页面切回来时 Loaded 会再响一次,
        //   但 Users.Count 不为 0, 以前这里直接返回 —— 看板/未读红点就永远停在
        //   "第一次进动态页"那一刻。现在: 已加载过就轻量刷新看板与红点,
        //   让"刚刚开播"和"刚刚投稿"都能立刻出现在页面上。
        if (Users.Count == 0)
        {
            await RefreshAsync();
            return;
        }

        await Task.WhenAll(
            RefreshLiveBoardAsync(showLoading: LiveNow.Count == 0),
            RefreshUnreadBadgesAsync());
    }

    /// <summary>从列表移除一个 UP 主(取消关注成功后调用)。移除的若是选中项, 回到全部动态</summary>
    public void RemoveUser(FollowUser u)
    {
        Users.Remove(u);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasFollowUsers));
        RaiseUnreadSummary();
        if (SelectedUp?.Mid == u.Mid) _ = SelectAllAsync();
    }

    /// <summary>选中「全部动态」</summary>
    public Task SelectAllAsync()
    {
        SelectedUp = null;
        return LoadSelectionAsync(reset: true);
    }

    /// <summary>选中某个 UP</summary>
    public Task SelectUpAsync(FollowUser u)
    {
        if (u == null || u.Mid <= 0) return Task.CompletedTask;
        _keepContactOrder = true;
        SelectedUp = u;
        return LoadSelectionAsync(reset: true);
    }

    // ------------------------------------------------------------ 新动态提示(2026-11-04)

    /// <summary>顶部「有 N 条新动态」提示条是否可见</summary>
    private bool _hasNewDynamics;
    public bool HasNewDynamics
    {
        get => _hasNewDynamics;
        private set => SetProperty(ref _hasNewDynamics, value);
    }

    /// <summary>提示条文案(如「3 条新动态」)</summary>
    private string _newDynamicsText = "";
    public string NewDynamicsText
    {
        get => _newDynamicsText;
        private set => SetProperty(ref _newDynamicsText, value);
    }

    /// <summary>
    /// 检查是否有新动态(2026-11-04)。
    ///
    /// 官方 PC 客户端的动态页顶部会浮一条「N 条新动态」, 点一下才把新内容插到最前面 ——
    /// 不打断用户正在看的滚动位置。这里照做:
    ///   · 拉第一页, 与本地已显示的**最新一条 id** 比对;
    ///   · 有更新的就记下条数并显示提示条, **不动列表**;
    ///   · 用户点提示条才真正刷新(见 ApplyNewDynamics)。
    ///
    /// ★ 失败静默: 这是锦上添花, 不该弹错误。
    ///
    /// ★★★ 2026-11-08 为"刷动态刷一会就限流"做了三处收敛(方法名不变, 行为按调用方区分):
    ///   <paramref name="polling"/> = true 时走**省额度**模式(定时轮询每 45 秒一次):
    ///     ① 未读快照只读第一页, 不翻完整个关注列表;
    ///     ② feed 只拉第一页, 不再为"找新动态"往下翻最多 4 页(那是 4 倍的请求量);
    ///     ③ 结论只需要"有没有新东西", 条数够提示条用就行 —— 精确条数由全量档给。
    ///   进页面 / 手动刷新 / 标记已读后走 False(全量), 红点覆盖所有人。
    /// </summary>
    private long _lastUnreadScanTs;
    private bool _checkingNew;
    public async Task CheckNewDynamicsAsync(bool polling = false)
    {
        if (!Svc.Session.HasLogin || _checkingNew || Loading) return;
        _checkingNew = true;
        var generation = _selectionGeneration;
        var forUp = SelectedUp;
        try
        {
            await RefreshServerUnreadAsync(includeOverflowPages: !polling);
            // Always scan the global feed, even while an individual UP is selected.
            var result = await Svc.Api.GetDynamicFeedFullAsync();
            if (generation != _selectionGeneration || !result.ok || result.items == null) return;
            var all = new List<DynamicItem>(result.items);
            var ids = new HashSet<string>(all.Select(d => d.Id));
            var cursor = result.nextOffset;
            var cursors = new HashSet<string>();
            while (!polling && cursors.Count < 4 && result.hasMore && !string.IsNullOrEmpty(cursor) && cursors.Add(cursor))
            {
                // Scan until reaching a previously seen page; first visit covers the last week.
                if ((_lastUnreadScanTs > 0 && all.Any(d => d.PubTs < _lastUnreadScanTs)) ||
                    all.Any(d => d.PubTs > 0 && d.PubTs < DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds())) break;
                var next = await Svc.Api.GetDynamicFeedFullAsync(cursor);
                if (generation != _selectionGeneration || !next.ok) break;
                foreach (var item in next.items ?? new List<DynamicItem>()) if (ids.Add(item.Id)) all.Add(item);
                result = next; cursor = next.nextOffset;
            }
            UpdateUnreadBadges(all);
            _lastUnreadScanTs = all.Count > 0 ? all.Max(d => d.PubTs) : _lastUnreadScanTs;
            var fresh = all.TakeWhile(d => !_seenDynamicIds.Contains(d.Id))
                .Where(d => d.Id.Length > 0).ToList();
            _pendingNewDynamics = fresh;
            NewDynamicsText = $"{fresh.Count} 条新动态";
            HasNewDynamics = fresh.Count > 0;
        }
        finally { _checkingNew = false; }
    }

    private List<DynamicItem>? _pendingNewDynamics;
    public void ApplyNewDynamics()
    {
        // Refresh the first page as well as its cursor, so a gap larger than one page cannot lose posts.
        _ = LoadSelectionAsync(reset: true);
    }

    private int _refreshGeneration;
    public async Task RefreshAsync()
    {
        _keepContactOrder = false;
        var refreshGeneration = ++_refreshGeneration;
        ++_selectionGeneration;
        Loading = true;
        Error = "";
        if (!Svc.Session.HasLogin)
        {
            Users.Clear();
            Dynamics.Clear();
            LiveNow.Clear();
            SelectedUp = null;
            HasNewDynamics = false;
            HasMore = false;
            // 排序/红点用的本地缓存也要一起清: 换个账号登录后沿用上一个人的
            // 关注序号与时间轴, 左栏会按陌生人的顺序排(看起来像排序彻底乱了)。
            ResetSortCaches();
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasFollowUsers));
            RaiseUnreadSummary();
            Loading = false;
            Error = "请先登录后查看动态";
            return;
        }
        try
        {
            // 先拿到自己的 mid
            var (_, _, me) = await Svc.Api.GetUserStateAsync();
            if (refreshGeneration != _refreshGeneration) return;
            if (me == null || me.Mid <= 0)
            {
                Loading = false;
                Error = "未登录";
                return;
            }
            if (_selfMid > 0 && _selfMid != me.Mid)
            {
                SelectedUp = null;
                Users.Clear();
                Dynamics.Clear();
                LiveNow.Clear();
                ResetSortCaches();
                OnPropertyChanged(nameof(Count));
                OnPropertyChanged(nameof(HasFollowUsers));
                RaiseUnreadSummary();
            }
            _selfMid = me.Mid;

            var (ok, err, list) = await Svc.Api.GetFollowingsAsync(_selfMid, 1, 50);
            var followingPage = 1;
            if (!ok) { Error = err ?? "关注列表获取失败"; return; }
            if (refreshGeneration != _refreshGeneration) return;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (list != null)
                {
                    _batchUsers = true;
                    Users.Clear();
                    foreach (var u in list)
                    {
                        u.IsSelected = u.Mid == SelectedUp?.Mid;
                        Users.Add(u);
                    }
                    if (SelectedUp != null) SelectedUp = Users.FirstOrDefault(u => u.Mid == SelectedUp.Mid);
                    _batchUsers = false;
                    RaiseUnreadSummary();
                    _hasMoreUsers = false;
                    _followingPage = followingPage;
                    // ★ 关注序号: 第一次见到的 UP 才分配, 已有的保持原值 ——
                    //   这是"关注顺序"模式在多次刷新后仍然稳定、不抖动的依据。
                    EnsureFollowOrder(Users);
                    OnPropertyChanged(nameof(Count));
                    OnPropertyChanged(nameof(HasFollowUsers));
                }
                else if (Users.Count == 0)
                {
                    Error = err ?? "加载失败";
                }
                Loading = false;
            });
            await LoadSelectionAsync(reset: true);
            _ = CompleteFollowingsAsync(refreshGeneration, list?.Count == 50);
            _ = CheckNewDynamicsAsync();
            // ★ 拉完第一页动态后按当前排序模式重排左栏 —— 这一步必须在 UpdateUnreadBadges
            //   之后(它在 LoadSelectionAsync 里已经跑过), 否则排的是没有时间轴的空序。
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(SortUsers);
            // 关注直播看板: 与动态流并行(不 await 到关键路径里), 慢一点没关系
            _ = RefreshLiveBoardAsync();
        }
        catch (Exception ex)
        {
            Loading = false;
            Error = ex.Message;
        }
        finally { if (refreshGeneration == _refreshGeneration) Loading = false; }
    }

    private bool _batchUsers;
    private async Task CompleteFollowingsAsync(int generation, bool more)
    {
        if (!more) return;
        LoadingUsers = true;
        try {
            var mids = new HashSet<long>(Users.Select(u => u.Mid));
            var page = 2;
            while (more && generation == _refreshGeneration) {
                var tasks = Enumerable.Range(page,4).Select(p => Svc.Api.GetFollowingsAsync(_selfMid,p,50)).ToArray();
                var results = await Task.WhenAll(tasks);
                if (generation != _refreshGeneration) return;
                _batchUsers = true;
                try {
                    foreach (var result in results) {
                        if (!result.ok || result.list == null) { Error = result.err ?? "关注列表加载中断，请刷新重试"; more=false; break; }
                        var added=0;
                        foreach(var user in result.list) if(mids.Add(user.Mid)) { Users.Add(user); added++; }
                        _followingPage++;
                        if(result.list.Count < 50 || added==0) { more=false; break; }
                    }
                } finally { _batchUsers=false; }
                OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(HasFollowUsers));
                EnsureFollowOrder(Users); UpdateUnreadBadges(); RaiseUnreadSummary();
                page += 4;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        } catch(Exception ex) { if(generation==_refreshGeneration) Error=ex.Message; }
        finally { if(generation==_refreshGeneration) LoadingUsers=false; }
    }

    /// <summary>左栏翻页: 关注列表一页 30 个。只补列表, 不动右栏。</summary>
    public async Task LoadMoreAsync()
    {
        if (LoadingUsers || Loading || !_hasMoreUsers || _selfMid <= 0) return;
        var generation = _refreshGeneration;
        LoadingUsers = true;
        try
        {
            var (ok, err, list) = await Svc.Api.GetFollowingsAsync(_selfMid, _followingPage + 1, 30);
            if (generation != _refreshGeneration) return;
            _hasMoreUsers = list == null || list.Count >= 30;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LoadingUsers = false;
                if (list != null && list.Count > 0)
                {
                    _followingPage++;
                    var seen = new HashSet<long>();
                    foreach (var u in Users) seen.Add(u.Mid);
                    foreach (var u in list)
                        if (u.Mid > 0 && seen.Add(u.Mid)) Users.Add(u);
                    EnsureFollowOrder(Users);
                    OnPropertyChanged(nameof(Count));
                    OnPropertyChanged(nameof(HasFollowUsers));

                    // Newly loaded rows also inherit the global server snapshot
                    // while the right-hand pane shows an individual author.
                    UpdateUnreadBadges();
                }
            });
        }
        catch (Exception ex)
        {
            LoadingUsers = false;
            Error = ex.Message;
        }
        finally { LoadingUsers = false; }
    }

    // ------------------------------------------------------------ 右栏内容

    /// <summary>按当前选择加载右栏。reset = 清空重来; false = 追加下一页</summary>
    private int _selectionGeneration;
    private async Task LoadSelectionAsync(bool reset)
    {
        if (!Svc.Session.HasLogin) return;
        if (!reset && (Loading || LoadingMore || !HasMore)) return;
        var generation = reset ? ++_selectionGeneration : _selectionGeneration;
        var forUp = SelectedUp;
        if (reset)
        {
            Loading = true;
            LoadingMore = false;
            FeedItems.Clear();
            Dynamics.Clear();
            _seenDynamicIds.Clear();
            _feedOffset = _upDynOffset = null;
            HasMore = true;
            HasNewDynamics = false;
            _pendingNewDynamics = null;
            Error = "";
        }
        else { LoadingMore = true; Error = ""; }
        var offset = forUp == null ? _feedOffset : _upDynOffset;
        try
        {
            var result = forUp == null
                ? await Svc.Api.GetDynamicFeedFullAsync(offset)
                : await Svc.Api.GetDynamicFeedFullAsync(offset, hostMid: forUp.Mid);
            if (generation != _selectionGeneration) return;
            if (!result.ok || result.items == null)
            {
                Error = result.err ?? "动态加载失败，请重试";
                return;
            }
            if (forUp == null) _feedOffset = result.nextOffset;
            else _upDynOffset = result.nextOffset;
            HasMore = result.hasMore && !string.IsNullOrEmpty(result.nextOffset) && result.nextOffset != offset;
            var added = new List<DynamicItem>();
            foreach (var d in result.items)
            {
                if (d.Id.Length > 0 && !_seenDynamicIds.Add(d.Id)) continue;
                Dynamics.Add(d);
                added.Add(d);
            }
            if (Dynamics.Count == 0 && !HasMore)
                Error = forUp == null ? "关注的 UP 最近没有动态" : "该 UP 最近没有动态";
            UpdateUnreadBadges();
            if (reset)
            {
                // This is the same UP-selection request as the official dynamic home.
                // Re-read the server state; do not call a local clear "synced".
                if (forUp != null) ++_unreadRequestVersion;
                await RefreshServerUnreadAsync();
            }

        }
        catch (Exception ex)
        {
            if (generation == _selectionGeneration) Error = ex.Message;
        }
        finally
        {
            if (generation == _selectionGeneration)
            {
                Loading = false;
                LoadingMore = false;
                OnPropertyChanged(nameof(ShowFullLoading));
            }
        }
    }

    // ------------------------------------------------------------ 右栏排版

    private bool _useCardWall = Svc.Settings.FollowCardWall;

    /// <summary>旧版配置兼容；当前综合 / 视频模式由 VideoOnly 控制。</summary>
    public bool UseCardWall
    {
        get => _useCardWall;
        set
        {
            if (!SetProperty(ref _useCardWall, value)) return;
            Svc.Settings.FollowCardWall = value;
            Svc.Settings.Save();

        }
    }

    // ------------------------------------------------------------ 左栏未读红点

    /// <summary>角标轻量刷新在途(定时器与页面切回可能同时触发)</summary>

    /// <summary>
    /// 只刷新左栏未读红点, **不动右栏列表**。
    ///
    /// 用于两个场景:
    ///   · 从别的页面切回动态页(页面实例被缓存, FeedItems 还是上次的);
    ///   · FollowPage 的定时轮询。
    ///
    /// ★ 为什么不直接复用 LoadSelectionAsync: 那会清空 FeedItems 并弹整页 loading,
    ///   用户只是切回来瞄一眼红点, 不该被强制回到列表顶部; 这里单独拉第一页动态就够了。
    /// </summary>
    public Task RefreshUnreadBadgesAsync() => CheckNewDynamicsAsync(polling: true);

    /// <summary>Remember posting times for sorting; red dots come exclusively from the server snapshot.</summary>
    private void UpdateUnreadBadges() => UpdateUnreadBadges(Dynamics);

    /// <summary>用指定的动态条目集合刷新角标(定时刷新时传临时列表, 不碰 Dynamics)</summary>
    private void UpdateUnreadBadges(IReadOnlyList<DynamicItem> items)
    {
        RememberTimestamps(items);
        ApplyServerUnreadBadges();
    }

    private readonly Dictionary<long, bool> _serverUnread = new();
    private int _unreadRequestVersion;

    internal async Task<bool> RefreshServerUnreadAsync(bool includeOverflowPages = true)
    {
        if (!Svc.Session.HasLogin) return false;
        var account = Svc.Session.Current.DedeUserID;
        var version = ++_unreadRequestVersion;
        var state = await Svc.Api.GetDynamicUnreadStateAsync(includeOverflowPages);
        if (version != _unreadRequestVersion || account != Svc.Session.Current.DedeUserID || !state.ok) return false;
        _serverUnread.Clear();
        foreach (var pair in state.authors) _serverUnread[pair.Key] = pair.Value;
        ApplyServerUnreadBadges();
        return true;
    }

    private void ApplyServerUnreadBadges()
    {
        // A local post-ID comparison cannot know what was read on another device.
        // The official response supplies a boolean, not an unread post count.
        WithoutAutoSort(() =>
        {
            foreach (var user in Users) user.NewCount = _serverUnread.GetValueOrDefault(user.Mid) ? 1 : 0;
        });
    }

    /// <summary>右栏滚动到底: 追加下一页</summary>
    public async Task LoadMoreFeedAsync()
    {
        if (Loading || LoadingMore || !HasMore) return;
        await LoadSelectionAsync(reset: false);
    }
}
