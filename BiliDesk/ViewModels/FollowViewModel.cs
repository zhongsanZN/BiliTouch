using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 「动态」页 ViewModel。
///
/// 布局是左右两栏(参考 B 站客户端动态页):
///   · 左栏 = 「全部动态」入口 + 关注的 UP 主列表(有未读时头像左侧显示红点);
///   · 右栏 = 当前选中对象的内容, 两档排版由 <see cref="UseCardWall"/> 切:
///       - 全部动态: 关注的所有 UP 近期的**视频投稿**(动态时间线接口, 游标分页);
///       - 具体某 UP: 该 UP 的投稿视频(空间投稿接口, 页码分页)。
/// 动态里非视频的内容(图文/纯文字/投票)没有可播对象, 不进这个列表。
///
/// 未读红点的判断在 <see cref="UpdateUnreadBadges"/> —— 本地比对, 不额外请求接口。
/// </summary>
public class FollowViewModel : ObservableObject
{
    private bool _loading;
    private bool _loadingMore;
    private bool _hasMore = true;
    private string _error = "";
    private long _selfMid;
    private bool _hasMoreUsers = true;
    private bool _loadingUsers;
    public bool LoadingUsers { get => _loadingUsers; private set => SetProperty(ref _loadingUsers, value); }
    private int _followingPage = 1;      // 左栏 UP 列表的翻页

    public ObservableCollection<FollowUser> Users { get; } = new();

    /// <summary>右栏内容(视频卡片)。切选择时整体换血。</summary>
    public ObservableCollection<VideoItem> FeedItems { get; } = new();

    /// <summary>
    /// 「全部动态」的混合流(2026-11-04)。
    ///
    /// ★ 为什么与 FeedItems 并存而不是替换它: 单 UP 视图(点左边某个 UP)拿的是
    ///   "TA 的投稿视频"接口, 那一路只有视频、没有动态混合流的概念。
    ///   两个集合各管一条路, 页面按 SelectedUp 是否为 null 切显隐。
    /// ★ 混合流里**视频动态也在里面**(DynamicItem.Video), 不再单独进 FeedItems ——
    ///   否则同一条会显示两次。
    /// </summary>
    public ObservableCollection<DynamicItem> Dynamics { get; } = new();

    /// <summary>
    /// 当前右栏显示的是混合流(全部动态)还是单 UP 的视频列表。
    /// ★★★ 2026-11-04 修"动态页还是一堆视频卡": 原来 FollowPage 里 FeedItems 那两个
    ///   ItemsControl 的 Visibility 只绑了 UseCardWall —— 也就是说**无论是不是"全部动态"
    ///   都在显示**。而全部动态模式走的是 Dynamics(混合流), 于是两套列表叠在一起:
    ///   上面是混合流, 下面还挂着一堆视频卡(FeedItems 可能残留上一次单 UP 的内容)。
    ///   现在拆成三个互斥的可见性, 见下面两个属性。
    /// </summary>
    public bool ShowDynamics => SelectedUp == null;

    /// <summary>
    /// 混合流列表的可见性(2026-11-04): **两种模式下都显示** ——
    /// 因为单 UP 视图现在也走 feed/space 动态流(Dynamics), 不再是投稿视频。
    /// 唯一例外是用户把档位切到"卡片墙"(那时用 FeedItems 的视频墙)。
    /// </summary>
    public bool ShowDynamicFeed => !UseCardWall;

    /// <summary>单 UP 视图 + 动态卡档位(FeedItems, 单列)</summary>
    public bool ShowUpFeed => SelectedUp != null && !UseCardWall;

    /// <summary>单 UP 视图 + 卡片墙档位(FeedItems, 网格)</summary>
    public bool ShowUpFeedCardWall => SelectedUp != null && UseCardWall;

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
            // 右栏三套列表按它切显隐(混合流 / 单UP动态卡 / 单UP卡片墙)
            OnPropertyChanged(nameof(ShowDynamics));
            OnPropertyChanged(nameof(ShowDynamicFeed));
            OnPropertyChanged(nameof(ShowUpFeed));
            OnPropertyChanged(nameof(ShowUpFeedCardWall));
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
    /// </summary>
    private bool _checkingNew;
    public async Task CheckNewDynamicsAsync()
    {
        if (!Svc.Session.HasLogin || _checkingNew || Loading) return;
        _checkingNew = true;
        var generation = _selectionGeneration;
        var forUp = SelectedUp;
        try
        {
            var result = forUp == null
                ? await Svc.Api.GetDynamicFeedFullAsync()
                : await Svc.Api.GetSpaceDynamicsAsync(forUp.Mid);
            if (generation != _selectionGeneration || !result.ok || result.items == null) return;
            if (forUp == null) UpdateUnreadBadges(result.items);
            var fresh = result.items.TakeWhile(d => !_seenDynamicIds.Contains(d.Id))
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
            OnPropertyChanged(nameof(Count));
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
            }
            _selfMid = me.Mid;

            var (ok, err, list) = await Svc.Api.GetFollowingsAsync(_selfMid, 1, 30);
            if (refreshGeneration != _refreshGeneration) return;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (list != null)
                {
                    Users.Clear();
                    foreach (var u in list)
                    {
                        u.IsSelected = u.Mid == SelectedUp?.Mid;
                        Users.Add(u);
                    }
                    _hasMoreUsers = list.Count >= 30;
                    _followingPage = 1;
                    OnPropertyChanged(nameof(Count));
                }
                else if (Users.Count == 0)
                {
                    Error = err ?? "加载失败";
                }
                Loading = false;
            });
            await LoadSelectionAsync(reset: true);
            // 关注直播看板: 与动态流并行(不 await 到关键路径里), 慢一点没关系
            _ = RefreshLiveBoardAsync();
        }
        catch (Exception ex)
        {
            Loading = false;
            Error = ex.Message;
        }
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
                    OnPropertyChanged(nameof(Count));

                    // ★ 左栏翻出新 UP 后要立刻按已缓存的动态流补算一次红点。
                    //   以前只 Add 用户、不跑 UpdateUnreadBadges —— 关注了 30+ 人的账号
                    //   往下滚出来的 UP 永远 NewCount=0, 明明有新投稿也不亮红点。
                    //   只在「全部动态」下算: 单 UP 视图的 FeedItems 只有该 UP 的投稿,
                    //   拿来给别的 UP 判新旧会把基准写错。
                    if (SelectedUp == null) UpdateUnreadBadges();
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
                : await Svc.Api.GetSpaceDynamicsAsync(forUp.Mid, offset);
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
            if (forUp == null) UpdateUnreadBadges();
            else ClearUnreadBadge(forUp);

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

    /// <summary>
    /// 右栏排版: true = 卡片墙(多列网格), false = 动态卡(单列: 头像 + 发布时间 + 视频)。
    ///
    /// ★ 初值直接读设置(用户偏好, 重启/切页都保持); 改一次就落盘 —— 与"卡片列数"同一类,
    ///   这不是"每片状态", 不该跟着数据刷新走。
    /// ★ 切换只改这个 bool: 界面上两套 ItemsControl 各自绑 Its Visibility(见 FollowPage),
    ///   所以不需要重建列表, 滚动位置也不会丢。
    /// </summary>
    public bool UseCardWall
    {
        get => _useCardWall;
        set
        {
            if (!SetProperty(ref _useCardWall, value)) return;
            Svc.Settings.FollowCardWall = value;
            Svc.Settings.Save();
            // 两个"单 UP 档位"可见性跟着档位走(见 ShowUpFeed 的说明)
            OnPropertyChanged(nameof(ShowDynamicFeed));
            OnPropertyChanged(nameof(ShowUpFeed));
            OnPropertyChanged(nameof(ShowUpFeedCardWall));
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
    public Task RefreshUnreadBadgesAsync() => CheckNewDynamicsAsync();

    /// <summary>
    /// 「全部动态」加载完 → 按这一页的条目刷新左栏各 UP 的未读红点。
    ///
    /// 规则(基准存在 <see cref="DynamicSeenStore"/>, 本地判断、不请求接口):
    ///   · 没有基准的 UP: 只把 TA 当前最新一条记成基准, **不显示红点**(见该类的说明);
    ///   · 基准与最新一条相同: 红点消失;
    ///   · 不同: NewCount = 这一页里排在基准那条**之前**的该 UP 投稿数
    ///     (基准那条已经翻出本页时, 本页所有该 UP 的条目都算新的)。
    ///     界面只拿它判断红点亮不亮, 具体数字放在红点的悬浮提示里。
    /// ★ 本页没有出现的 UP **不动**: 动态接口只给近期条目, 一个 UP 最近没投稿就无从判断,
    ///   这时把红点清掉是凭空断言"没更新"。宁可留着旧红点, 也不给错的信息。
    /// </summary>
    private void UpdateUnreadBadges() => UpdateUnreadBadges(Dynamics);

    /// <summary>用指定的动态条目集合刷新角标(定时刷新时传临时列表, 不碰 FeedItems)</summary>
    private void UpdateUnreadBadges(IReadOnlyList<DynamicItem> items)
    {
        // 每个 UP 在这一页里最新的一条(动态流是按时间倒序拼的, 先遇到的就是最新)
        var newest = new Dictionary<long, string>();
        foreach (var it in items)
        {
            if (it.AuthorMid <= 0 || string.IsNullOrEmpty(it.Id)) continue;
            if (!newest.ContainsKey(it.AuthorMid)) newest[it.AuthorMid] = it.Id;
        }

        foreach (var u in Users)
        {
            if (!newest.TryGetValue(u.Mid, out var top)) continue;

            if (!DynamicSeenStore.TryGet(u.Mid, out var seen))
            {
                DynamicSeenStore.Set(u.Mid, top);
                u.NewCount = 0;
                continue;
            }
            if (seen == top)
            {
                u.NewCount = 0;
                continue;
            }

            var n = 0;
            foreach (var it in items)
            {
                if (it.AuthorMid != u.Mid) continue;
                if (it.Id == seen) break;   // 追到上次看过的那条为止
                n++;
            }
            u.NewCount = n;
        }
        DynamicSeenStore.Flush();
    }

    /// <summary>点开某个 UP(右栏只看 TA 的投稿) = 已读: 清红点, 并把基准推到 TA 当前最新一条</summary>
    private void ClearUnreadBadge(FollowUser u)
    {
        u.NewCount = 0;
        foreach (var it in Dynamics)
        {
            if (it.AuthorMid != u.Mid || string.IsNullOrEmpty(it.Id)) continue;
            DynamicSeenStore.Set(u.Mid, it.Id);
            break;   // FeedItems 是时间倒序, 第一条就是最新
        }
        DynamicSeenStore.Flush();
    }

    /// <summary>右栏滚动到底: 追加下一页</summary>
    public async Task LoadMoreFeedAsync()
    {
        if (Loading || LoadingMore || !HasMore) return;
        await LoadSelectionAsync(reset: false);
    }
}
