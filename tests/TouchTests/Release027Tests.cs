using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.ViewModels;
using BiliTouch.Views.Pages;

internal static class Release027Tests
{
    public static void Run(Action<bool, string> check)
    {
        var store = new BiliTouch.Services.SettingsStore();
        var sanitize = typeof(BiliTouch.Services.SettingsStore).GetMethod("ApplySanitized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        sanitize.Invoke(store, [new AppSettings()]);
        check(store.HardwareAccelerationEnabled, "old settings keep hardware acceleration enabled");
        sanitize.Invoke(store, [new AppSettings { HardwareAccelerationEnabled = false }]);
        var snapshot = (AppSettings)typeof(BiliTouch.Services.SettingsStore).GetMethod("Snapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(store, null)!;
        check(!store.HardwareAccelerationEnabled && snapshot.HardwareAccelerationEnabled == false, "explicit software preference survives settings round trip");
        var startup = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../BiliTouch/App.xaml.cs"));
        check(startup.IndexOf("Svc.Settings.Load();") < startup.IndexOf("RenderPolicy.ApplyProcessRenderMode();") &&
            startup.IndexOf("RenderPolicy.ApplyProcessRenderMode();") < startup.IndexOf("var main = new MainWindow();"),
            "startup applies saved render preference after loading settings and before opening main window");
        var vm = new HomeViewModel(new EmptyFeeds());
        for (int i = 0; i < 600; i++)
            vm.RecommendItems.Add(new VideoItem { Bvid = $"BV{i:D10}", Title = $"Card {i}", Author = "UP" });
        var page = new HomePage { DataContext = vm };
        var window = new Window { Content = page, Width = 1280, Height = 800, ShowActivated = false };
        try
        {
            window.Show();
            Pump();
            var list = (ItemsControl)page.FindName("RecommendList");
            var wall = Descendants<VirtualizingCardWallPanel>(list).Single();
            check(wall.RealizedCount > 0 && wall.RealizedCount < 60,
                $"actual home XAML bounds realized cards: {wall.RealizedCount}/600");
            var scroll = (ScrollViewer)page.FindName("RecommendScroll");
            check(!Descendants<ScrollViewer>(list).Any(), "home wall shares the page scroll owner");
            // Data publication is incremental. Jump into the completed fixture's
            // middle, rather than the middle of its first incomplete viewport.
            var waitStarted = Environment.TickCount64;
            while ((list.Items.Count != 600 || scroll.ScrollableHeight < 10000)
                && Environment.TickCount64 - waitStarted < 10000) Pump();
            check(list.Items.Count == 600 && vm.RecommendItems.Count == 600, "the virtualized fixture finishes publication without real-network additions");
            var middle = scroll.ScrollableHeight / 2;
            scroll.ScrollToVerticalOffset(middle);
            Pump();
            check(wall.RealizedCount > 0 && wall.RealizedCount < 60, "actual home remains virtualized after scrolling");
            var items = Descendants<BiliTouch.Views.Controls.VideoCard>(wall).ToArray();
            check(Math.Abs(scroll.VerticalOffset - middle) < wall.RowHeight, "the completed wall retains its requested middle scroll position");
            check(items.Any(c => c.DataContext is VideoItem item && int.Parse(item.Bvid[2..]) > 200),
                "actual home realizes middle rows through the page viewport");
            check(items.All(c => c.ActualWidth > 150 && c.ActualWidth < 600), "actual home card widths remain usable");
            // ★ 2026-11-08: 这里原来把 "0.2.9" 写死成断言的一部分，每发一版就得改一次。
            //   版本一致性由 Program.cs 里那条「程序集 == App.AppVersion」负责；
            //   这里只保证"它是个非空的三段版本号"，不再为发版制造无意义的改动点。
            check(App.AppVersion.StartsWith(typeof(App).Assembly.GetName().Version!.ToString(3))
                  && App.AppVersion.Split('.').Length == 3,
                "application and assembly report the same released version");
        }
        finally { window.Close(); }
    }

    private sealed class EmptyFeeds : IHomeFeedSource
    {
        private static Task<(bool ok, string? err, List<VideoItem>? items)> Empty() => Task.FromResult((true, (string?)null, (List<VideoItem>?)new List<VideoItem>()));
        public Task<(bool ok, string? err, List<VideoItem>? items)> Recommend(bool reset) => Empty();
        public Task<(bool ok, string? err, List<VideoItem>? items)> Popular(int page) => Empty();
        public Task<(bool ok, string? err, List<VideoItem>? items)> Ranking() => Empty();
        public Task<(bool ok, string? err, List<VideoItem>? items)> Live(int page) => Empty();
        public Task<(bool ok, string? err, List<BangumiItem>? items, bool hasNext)> Bangumi(int page)
            => Task.FromResult((true, (string?)null, (List<BangumiItem>?)new List<BangumiItem>(), false));
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
