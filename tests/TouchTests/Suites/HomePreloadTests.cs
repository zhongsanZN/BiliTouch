using System.Collections.Concurrent;
using System.IO;
using BiliTouch.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.ViewModels;
using BiliTouch.Views.Controls;
using BiliTouch.Views.Pages;

internal static class HomePreloadTests
{
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> done, string label, int timeout = 10000)
    {
        var started = Environment.TickCount64;
        while (!done() && Environment.TickCount64 - started < timeout) Pump(20);
        if (!done()) throw new InvalidOperationException("Timed out: " + label);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    internal static void Run(Action<bool, string> check, string artifacts)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var source = new FeedSource { Delay = index => index == 0 ? 300 : 20 };
        var vm = new HomeViewModel(source);
        var pending = vm.PreloadInitialTabsAsync(0);
        var popular = vm.SwitchToPopularAsync();
        check(source.Calls[1] == 1 && !popular.IsCompleted, "selecting an in-flight preload joins the same first-page request");
        Until(() => pending.IsCompleted && popular.IsCompleted, "all first pages");
        check(source.Calls.All(count => count == 1) && source.MaxConcurrent == 2, "all five first pages load ahead with at most two network requests at once");
        check(vm.IsPopularTab && !vm.Loading && vm.Error == "", "background completion preserves the selected tab and its loading state");
        var liveFirst = vm.LiveItems[0];
        vm.SwitchToLiveAsync().GetAwaiter().GetResult(); vm.SwitchToRecommendAsync().GetAwaiter().GetResult(); vm.SwitchToLiveAsync().GetAwaiter().GetResult();
        check(source.Calls[3] == 1 && ReferenceEquals(liveFirst, vm.LiveItems[0]), "returning to Live reuses its first page instead of clearing and fetching it again");

        source = new FeedSource { Delay = index => index == 0 ? 400 : 15, FailRanking = true };
        vm = new HomeViewModel(source); pending = vm.PreloadInitialTabsAsync(0);
        Pump(180);
        check(vm.Loading && vm.RecommendItems.Count == 0 && vm.PopularItems.Count > 0 && vm.Error == "", "background errors and loading completions do not overwrite the still-loading foreground feed");
        Until(() => pending.IsCompleted, "isolated background error");
        vm.SwitchToRankingAsync().GetAwaiter().GetResult();
        check(vm.RankingBlocked && vm.Error.Contains("排行榜") && source.Calls[2] == 1, "a prefetched ranking failure appears only on Ranking without duplicate retries");

        source = new FeedSource { Empty = true }; vm = new HomeViewModel(source);
        pending = vm.PreloadInitialTabsAsync(0); Until(() => pending.IsCompleted, "empty first pages");
        vm.SwitchToPopularAsync().GetAwaiter().GetResult(); vm.SwitchToLiveAsync().GetAwaiter().GetResult();
        check(source.Calls.All(count => count == 1), "successful empty feeds are still cached as completed first pages");

        source = new FeedSource(); vm = new HomeViewModel(source);
        bool held = true; vm.IsPublicationDeferred = () => held;
        pending = vm.PreloadInitialTabsAsync(0); Pump(200);
        check(source.Calls.All(count => count == 1) && !pending.IsCompleted && vm.PopularItems.Count == 0,
            "network preloading continues while collection publication waits for the gesture");
        held = false; Until(() => pending.IsCompleted, "publication after lift");

        source = new FeedSource { FailPopularMore = true }; vm = new HomeViewModel(source);
        pending = vm.PreloadInitialTabsAsync(0); Until(() => pending.IsCompleted, "pagination fixture first pages");
        source.Delay = index => index == 1 ? 150 : 600;
        vm.SwitchToPopularAsync().GetAwaiter().GetResult();
        var popularMore = vm.LoadMoreAsync();
        vm.SwitchToRecommendAsync().GetAwaiter().GetResult();
        var recommendMore = vm.LoadMoreAsync();
        vm.SwitchToPopularAsync().GetAwaiter().GetResult();
        var duplicateMore = vm.LoadMoreAsync();
        check(vm.LoadingMore && !vm.Loading && source.Calls[1] == 2 && duplicateMore.IsCompleted,
            "returning during pagination restores that tab's progress and cannot duplicate its request");
        vm.SwitchToRecommendAsync().GetAwaiter().GetResult(); Pump(250);
        check(popularMore.IsCompleted && !recommendMore.IsCompleted && vm.LoadingMore && vm.Error == "" && vm.PopularError == "分页失败",
            "a late background page error cannot stop or overwrite the current tab's pagination");
        vm.SwitchToPopularAsync().GetAwaiter().GetResult();
        check(vm.Error == "分页失败" && !vm.LoadingMore, "the failed page retains its own retry message");
        Until(() => recommendMore.IsCompleted, "foreground pagination completion");
        vm.SwitchToRankingAsync().GetAwaiter().GetResult();
        vm.LoadMoreAsync().GetAwaiter().GetResult();
        check(!vm.LoadingMore, "non-paginated Ranking never enters a stuck loading-more state");

        using var images = new ImageCacheFixture();
        source = new FeedSource { CoverRoot = images.Root };
        vm = new HomeViewModel(source);
        var home = new HomePage { DataContext = vm };
        var window = new Window { Content = home, Width = 1150, Height = 820 };
        var foregroundImagesBefore = ViewportImageQueue.Started;
        window.Show();
        try
        {
            var names = new[] { "RecommendScroll", "PopularScroll", "RankingScroll", "LiveScroll", "BangumiScroll" };
            var scrolls = names.Select(name => (TabViewportScrollViewer)home.FindName(name)).ToArray();
            bool Ready(int index)
            {
                var cards = Find<VideoCard>(scrolls[index]).ToArray();
                return cards.Length >= 8 && cards.Take(8).All(card => card.Opacity >= .99 && Find<Border>(card)
                    .Any(border => !string.IsNullOrWhiteSpace(Cover.GetSource(border)) && border.Background is ImageBrush));
            }
            bool PostersReady() => Find<BangumiCard>(scrolls[4]).Take(4).Count(card => card.Opacity >= .99 && Find<Border>(card)
                .Any(border => !string.IsNullOrWhiteSpace(Cover.GetSource(border)) && border.Background is ImageBrush)) == 4;
            try { Until(() => source.Calls.All(count => count == 1) && Enumerable.Range(0, 4).All(Ready) && PostersReady(), "hidden first-screen cards and decoded covers", 15000); }
            catch
            {
                foreach (var scroll in scrolls)
                {
                    var cards = Find<VideoCard>(scroll).ToArray();
                    Console.WriteLine($"PREP {scroll.Name}: visibility={scroll.Visibility} size={scroll.ActualWidth}/{scroll.ActualHeight} viewport={scroll.ViewportWidth}/{scroll.ViewportHeight} cards={cards.Length} opacity={cards.FirstOrDefault()?.Opacity}");
                    foreach (var border in cards.Take(1).SelectMany(card => Find<Border>(card)).Where(border => !string.IsNullOrWhiteSpace(Cover.GetSource(border))))
                        Console.WriteLine($"COVER url={Cover.GetSource(border)} disk={File.Exists(Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(Cover.GetSource(border)!) + ".jpg"))} w={border.ActualWidth} bg={border.Background?.GetType().Name} cached={CoverLoader.TryGetCached(Cover.GetSource(border), 320, out _)}");
                }
                throw;
            }
            check(vm.IsRecommendTab && home.CurrentTabIndex == 0 && scrolls.Skip(1).All(scroll => scroll.Visibility == Visibility.Hidden),
                "unvisited tabs prepare without selecting or displaying themselves");
            check(PostersReady(), "unvisited Bangumi prepares real poster cards and decoded covers too");
            check(ViewportImageQueue.Started - foregroundImagesBefore <= Find<VideoCard>(scrolls[0]).Count(),
                "hidden tab layouts do not start foreground image requests outside the bounded preloader");
            check(scrolls.All(scroll => BindingOperations.IsDataBound(scroll, UIElement.VisibilityProperty)), "hidden preparation preserves every tab visibility binding");
            check(Find<VirtualizingCardWallPanel>(scrolls[2]).First().RealizedCount < vm.RankingItems.Count,
                "preparation realizes only the first viewport rather than the entire ranking list");
            var beforeCalls = source.Calls.ToArray();
            var firstCard = Find<VideoCard>(scrolls[1]).First();
            home.PreviewHorizontalWheel(.35);
            check(Ready(1) && ReferenceEquals(firstCard, Find<VideoCard>(scrolls[1]).First()) && source.Calls.SequenceEqual(beforeCalls),
                "the first unvisited neighbour is revealed with real cards and covers already attached, without a new API request");
            home.SettlePrecisionTouchpadPreview(false); Pump(240);
            check(scrolls[1].Visibility == Visibility.Hidden && vm.IsRecommendTab, "cancelling the swipe keeps the prepared neighbour hidden and cached");

            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(artifacts, "home-preloaded-first-screen.png")); encoder.Save(output);
        }
        finally { window.Close(); Pump(100); }
    }

    private sealed class FeedSource : IHomeFeedSource
    {
        internal readonly int[] Calls = new int[5];
        internal int Concurrent, MaxConcurrent;
        internal Func<int, int> Delay = _ => 20;
        internal bool FailRanking, FailPopularMore, Empty;
        internal string CoverRoot = "";
        private async Task<List<VideoItem>> Fetch(int index)
        {
            Calls[index]++; Concurrent++; MaxConcurrent = Math.Max(MaxConcurrent, Concurrent);
            try
            {
                await Task.Delay(Delay(index));
                return Empty ? new() : Enumerable.Range(1, index == 2 ? 80 : 36).Select(n => new VideoItem
                {
                    Bvid = $"BVpreload{index}_{n}", RoomId = index == 3 ? n : 0,
                    Title = $"预加载检查 · 页签 {index} · {n}", Author = "预加载夹具", Duration = "1:00",
                    Cover = CoverRoot.Length == 0 ? "" : $"{CoverRoot}{index}/{n}.png"
                }).ToList();
            }
            finally { Concurrent--; }
        }
        public async Task<(bool ok, string? err, List<VideoItem>? items)> Recommend(bool reset) => (true, null, await Fetch(0));
        public async Task<(bool ok, string? err, List<VideoItem>? items)> Popular(int page)
        { var rows = await Fetch(1); return FailPopularMore && page > 1 ? (false, "分页失败", null) : (true, null, rows); }
        public async Task<(bool ok, string? err, List<VideoItem>? items)> Ranking()
        { var rows = await Fetch(2); return FailRanking ? (false, "-352 blocked", null) : (true, null, rows); }
        public async Task<(bool ok, string? err, List<VideoItem>? items)> Live(int page) => (true, null, await Fetch(3));
        public async Task<(bool ok, string? err, List<BangumiItem>? items, bool hasNext)> Bangumi(int page)
        {
            await Fetch(4);
            return (true, null, Empty ? new() : Enumerable.Range(1, 12).Select(n => new BangumiItem
            { SeasonId = n, Title = "番剧预加载 " + n, Cover = CoverRoot.Length == 0 ? "" : $"{CoverRoot}4/{n}.png" }).ToList(), false);
        }
    }

    private sealed class ImageCacheFixture : IDisposable
    {
        private readonly List<string> _files = new();
        internal string Root { get; } = "https://home-preload-" + Guid.NewGuid().ToString("N") + ".invalid/";
        internal ImageCacheFixture()
        {
            var pixels = new byte[640 * 360 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 84; pixels[i + 1] = 145; pixels[i + 2] = 186; pixels[i + 3] = 255; }
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4)));
            using var output = new MemoryStream(); encoder.Save(output);
            Directory.CreateDirectory(AppPaths.CacheDir);
            for (int tab = 0; tab < 5; tab++)
            for (int n = 1; n <= (tab == 2 ? 80 : 36); n++)
            {
                var url = $"{Root}{tab}/{n}.png";
                var path = Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(url) + ".jpg");
                File.WriteAllBytes(path, output.ToArray()); _files.Add(path);
            }
        }
        public void Dispose()
        {
            foreach (var path in _files) try { File.Delete(path); } catch (IOException) { }
        }
    }
}
