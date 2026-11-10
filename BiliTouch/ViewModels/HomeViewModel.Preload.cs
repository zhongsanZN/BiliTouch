using System.Threading;

namespace BiliTouch.ViewModels;

public partial class HomeViewModel
{
    private readonly IHomeFeedSource _feedSource;
    private readonly SemaphoreSlim _firstPageSlots = new(2);
    private readonly Dictionary<PageTab, Task> _firstPages = new();
    private readonly bool[] _firstPageReady = new bool[5];
    private readonly string[] _tabErrors = new string[5];
    private readonly HashSet<PageTab> _loadingMoreTabs = new();
    public string RecommendError => _tabErrors[0] ?? "";
    public string PopularError => _tabErrors[1] ?? "";
    public string RankingError => _tabErrors[2] ?? "";
    public string LiveError => _tabErrors[3] ?? "";
    public string BangumiError => _tabErrors[4] ?? "";
    private PageTab _selectedTab = PageTab.Recommend;
    private string _myBangumiAccount = "";
    private Task? _myBangumiTask;
    private int _myBangumiGeneration;
    internal static Action<string>? PreloadTrace;
    internal Func<bool>? IsPublicationDeferred;

    private async Task ApplyFeedAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        while (!dispatcher.HasShutdownStarted)
        {
            var applied = await dispatcher.InvokeAsync(() =>
            {
                if (IsPublicationDeferred?.Invoke() == true) return false;
                action(); return true;
            }, System.Windows.Threading.DispatcherPriority.Background);
            if (applied) return;
            await Task.Delay(32);
        }
    }

    internal Task PreloadInitialTabsAsync(int selectedIndex)
    {
        var order = new[] { selectedIndex, selectedIndex + 1, selectedIndex - 1, 0, 1, 2, 3, 4 }
            .Where(i => i is >= 0 and < 5).Distinct();
        return Task.WhenAll(order.Select(EnsureFirstPageAsync));
    }

    internal Task EnsureFirstPageAsync(int index)
    {
        var tab = (PageTab)Math.Clamp(index, 0, 4);
        if (_firstPageReady[(int)tab] || HasTabItems(tab) || (tab == PageTab.Ranking && RankingBlocked)) return Task.CompletedTask;
        if (_firstPages.TryGetValue(tab, out var running)) return running;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _firstPages[tab] = completion.Task;
        if (_selectedTab == tab) Loading = true;
        _ = FetchFirstPageAsync(tab, completion);
        return completion.Task;
    }

    private async Task FetchFirstPageAsync(PageTab tab, TaskCompletionSource completion)
    {
        try
        {
            PreloadTrace?.Invoke($"{Environment.TickCount64} home.fetch.start tab={tab}");
            switch (tab)
            {
                case PageTab.Recommend: await LoadRecommendAsync(true); break;
                case PageTab.Popular: await LoadPopularAsync(true); break;
                case PageTab.Ranking: await LoadRankingAsync(); break;
                case PageTab.Live: await LoadLiveAsync(true); break;
                case PageTab.Bangumi: await LoadBangumiAsync(true); break;
            }
        }
        catch (Exception ex) { SetTabError(tab, ex.Message); }
        finally
        {
            _firstPages.Remove(tab);
            if (_selectedTab == tab) Loading = false;
            PreloadTrace?.Invoke($"{Environment.TickCount64} home.fetch.end tab={tab} ready={_firstPageReady[(int)tab]} items={HasTabItems(tab)}");
            completion.TrySetResult();
        }
    }

    private async Task<T> FetchFeedAsync<T>(Func<Task<T>> fetch)
    {
        await _firstPageSlots.WaitAsync();
        try { return await fetch(); }
        finally { _firstPageSlots.Release(); }
    }

    private bool HasTabItems(PageTab tab) => tab switch
    {
        PageTab.Recommend => RecommendItems.Count > 0,
        PageTab.Popular => PopularItems.Count > 0,
        PageTab.Ranking => RankingItems.Count > 0,
        PageTab.Live => LiveItems.Count > 0,
        _ => BangumiRecommend.Count > 0
    };

    private void SetTabError(PageTab tab, string message)
    {
        _tabErrors[(int)tab] = message;
        OnPropertyChanged(tab + "Error");
        if (_selectedTab == tab) Error = message;
    }

    private Task PrepareMyBangumiAsync()
    {
        if (!Svc.Session.HasLogin) return Task.CompletedTask;
        var account = Svc.Session.Current.DedeUserID ?? "";
        if (_myBangumiAccount == account && _myBangumiTask != null) return _myBangumiTask;
        _myBangumiAccount = account;
        var generation = ++_myBangumiGeneration;
        return _myBangumiTask = LoadMineAsync();
        async Task LoadMineAsync()
        {
            try
            {
                var mid = long.TryParse(account, out var parsed) ? parsed : await CurrentMidAsync();
                if (mid <= 0) return;
                var (ok, err, mine) = await Svc.Api.GetMyBangumiAsync(mid);
                await ApplyFeedAsync(() =>
                {
                    if (generation != _myBangumiGeneration || account != (Svc.Session.Current.DedeUserID ?? "") || !Svc.Session.HasLogin) return;
                    if (!ok && !string.IsNullOrEmpty(err)) BangumiHint = err;
                    if (mine != null) { MyBangumi.Clear(); foreach (var item in mine) MyBangumi.Add(item); }
                    OnPropertyChanged(nameof(HasMyBangumi)); OnPropertyChanged(nameof(MyBangumiHint)); OnPropertyChanged(nameof(MyBangumiCountText));
                });
            }
            catch (Exception ex)
            {
                if (generation == _myBangumiGeneration && account == (Svc.Session.Current.DedeUserID ?? "")) BangumiHint = ex.Message;
            }
        }
    }
}
