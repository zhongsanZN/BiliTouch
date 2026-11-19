using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views.Controls;
using BiliTouch.Views.Pages;

// Local diagnostic only, never shipped. Two bisections:
//   Run()      keeps one fixture and toggles global rendering settings.
//   Surfaces() keeps the user's settings and swaps the surface.
// Each PRESENT line therefore differs from the one before it by exactly one thing.
internal static partial class ScrollAbTests
{
    private enum CardCache { Xaml, Off, DeviceScale }

    private static CardCache _cardCache = CardCache.Xaml;
    private static double _dpiScale = 1;
    private const double UserScale = 1.10;   // Svc.Settings.UiScalePercent on this machine

    // ---------------------------------------------------------------- surfaces

    /// <summary>Real HomePage and the synthetic wall, both under the user's settings.</summary>
    internal static void Surfaces(string artifacts, Action<bool, string> check)
    {
        var saved = Save();
        try
        {
            ApplyUserSettings();
            var items = Covers(artifacts, 240);

            var (wallWindow, wallScroll) = WallFixture(items);
            var (homeWindow, homeScroll) = HomeFixture(items);
            wallWindow.Show();
            wallWindow.UpdateLayout();
            Pump(wallWindow, 600);
            Measure(wallWindow, wallScroll, "synthetic wall @110%");
            Measure(homeWindow, homeScroll, "real HomePage @110%");
            Measure(wallWindow, wallScroll, "synthetic wall @110% again");
            wallWindow.Close();
            homeWindow.Close();
        }
        finally { Restore(saved); }
        check(true, "scroll surface A/B completed");
    }

    /// <summary>
    /// The window the user actually scrolls: real MainWindow, real HomePage, real covers.
    /// Optional toggles then remove one piece of the window's own decoration at a time.
    /// </summary>
    internal static void RealWindow(string artifacts, Action<bool, string> check)
    {
        var saved = Save();
        Window? main = null;
        try
        {
            ApplyUserSettings();
            UiScale.SetPercent((int)Math.Round(UserScale * 100));
            var items = RealCovers(artifacts, 240);
            main = new MainWindow { Width = 1380, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            main.Show();
            Pump(main, 900);
            App.MainVm.Navigate(PageKey.Home);
            Pump(main, 900);
            var home = (HomePage)((ContentControl)main.FindName("PageHost")!).Content;
            var vm = (HomeViewModel)home.DataContext;
            vm.RecommendItems.Clear();
            foreach (var item in items) vm.RecommendItems.Add(item);
            var list = (ItemsControl)home.FindName("RecommendList")!;
            var limit = Stopwatch.StartNew();
            while (list.Items.Count < items.Length && limit.ElapsedMilliseconds < 60_000) Pump(main, 100);
            var viewport = (ScrollViewer)home.FindName("RecommendScroll")!;
            Console.WriteLine($"MAIN-WINDOW items={list.Items.Count} filled in {limit.ElapsedMilliseconds}ms " +
                              $"row={(Descendants<VirtualizingCardWallPanel>(home).FirstOrDefault()?.RowHeight ?? 0):F0} " +
                              $"scrollable={viewport.ScrollableHeight:F0} scale={UiScale.Factor:F2}");
            var panel = Descendants<VirtualizingCardWallPanel>(viewport).First();
            Console.WriteLine($"MAIN-GEOMETRY window={main.ActualWidth:F0}x{main.ActualHeight:F0} viewport={viewport.ViewportWidth:F0}x{viewport.ViewportHeight:F0} " +
                $"panel={panel.ActualWidth:F0} columns={CardWall.GetColumns(panel)} fixed={CardWall.FixedColumns} aspect={CardWall.GetAspect(panel):F3}");
            var decorated = (UIElement)main.FindName("SharedHeaderSurface")!;
            Settle(main, viewport);
            Measure(main, viewport, "real MainWindow home");
            if (Environment.GetCommandLineArgs().Contains("--main-fast")) { check(true, "main-window scroll measurement completed"); return; }
            Measure(main, viewport, "no covers",
                () => SetCoversHidden(main, true),
                () => SetCoversHidden(main, false));
            Measure(main, viewport, "shared header hidden",
                () => decorated.Visibility = Visibility.Collapsed,
                () => decorated.Visibility = Visibility.Visible);
            Measure(main, viewport, "home toolbars hidden",
                () => HideHomeOverlays(home),
                () => ShowHomeOverlays(home));
            Measure(main, viewport, "skin off",
                () => SkinService.Instance.Configure("", true, true),
                () => SkinService.Instance.Configure(SkinService.PhantomBlade, true, true));
            Measure(main, viewport, "ui scale 100",
                () => UiScale.SetPercent(100),
                () => UiScale.SetPercent((int)Math.Round(UserScale * 100)));
            Measure(main, viewport, "real MainWindow home again");
        }
        finally
        {
            main?.Close();
            UiScale.SetPercent((int)Math.Round(saved.Scale * 100));
            Restore(saved);
        }
        check(true, "real window scroll A/B completed");
    }

    // ---------------------------------------------------------------- settings

    internal static void Run(string artifacts, Action<bool, string> check)
    {
        var saved = Save();
        try
        {
            ApplyUserSettings();
            var items = Covers(artifacts, 240);
            var (window, viewport) = WallFixture(items);
            var root = (FrameworkElement)window.Content;
            try
            {
                SetUiScale(root, 0);
                window.Show();
                window.UpdateLayout();
                Pump(window, 600);
                _dpiScale = VisualTreeHelper.GetDpi(window).PixelsPerDip;
                var wall = Descendants<VirtualizingCardWallPanel>(window).First();
                Console.WriteLine($"AB-FIXTURE items={items.Length} columns={CardWall.GetColumns(wall)} dpi={_dpiScale:F2} " +
                                  $"row={wall.RowHeight:F0} viewport={viewport.ViewportHeight:F0} " +
                                  $"scrollable={viewport.ScrollableHeight:F0}");

                foreach (var (name, apply, undo) in SettingsVariants(window, root, viewport))
                    Measure(window, viewport, name, apply, undo);
            }
            finally { window.Close(); }
        }
        finally { Restore(saved); }
        check(true, "scroll A/B completed");
    }

    private static IEnumerable<(string Name, Action Apply, Action Undo)> SettingsVariants(
        Window window, FrameworkElement root, ScrollViewer viewport)
    {
        yield return ("scale-100", () => { SetUiScale(root, 0); SetCardCache(window, CardCache.Xaml); }, () => { });

        // The user's real configuration: 110% software scale on top of the 225% system DPI.
        yield return ("scale-110", () => SetUiScale(root, UserScale), () => SetUiScale(root, 0));

        // 4/3 makes the combined step 2.25*1.3333 = 3.0 device px per DIP. If only the
        // non-integer factors are slow, this recovers what 110% lost.
        yield return ("scale-4over3", () => SetUiScale(root, 4.0 / 3.0), () => SetUiScale(root, 0));

        yield return ("scale-200", () => SetUiScale(root, 2.0), () => SetUiScale(root, 0));

        yield return ("scale-110-card-cache-off",
            () => { SetUiScale(root, UserScale); SetCardCache(window, CardCache.Off); },
            () => SetCardCache(window, CardCache.Xaml));

        yield return ("scale-110-device-cache",
            () => { SetUiScale(root, UserScale); SetCardCache(window, CardCache.DeviceScale); },
            () => SetCardCache(window, CardCache.Xaml));

        yield return ("scale-110-text-display",
            () => { SetUiScale(root, UserScale); window.SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display); },
            () => window.ClearValue(TextOptions.TextFormattingModeProperty));

        yield return ("scale-110-repeat", () => SetUiScale(root, UserScale), () => SetUiScale(root, 0));
    }

    // ---------------------------------------------------------------- fixtures

    private static (Window, ScrollViewer) WallFixture(VideoItem[] items)
    {
        var panelFactory = new FrameworkElementFactory(typeof(VirtualizingCardWallPanel));
        panelFactory.SetValue(CardWall.EnableProperty, true);
        var list = new ItemsControl
        {
            ItemsSource = items,
            ItemsPanel = new ItemsPanelTemplate(panelFactory),
            ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(VideoCard)) }
        };
        var viewport = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list
        };
        var root = new Grid();
        root.Children.Add(new SkinSurface { Kind = "Content" });
        root.Children.Add(viewport);
        var window = Host(root);
        return (window, viewport);
    }

    private static (Window, ScrollViewer) HomeFixture(VideoItem[] items)
    {
        var vm = new HomeViewModel { IsRecommendTab = true };
        foreach (var item in items) vm.RecommendItems.Add(item);
        var page = new HomePage { DataContext = vm };
        var window = Host(page);
        window.Show();
        window.UpdateLayout();
        Pump(window, 500);
        // DeferredFill feeds the wall at Background priority; wait for the full set before measuring.
        var list = (ItemsControl)page.FindName("RecommendList")!;
        var limit = Stopwatch.StartNew();
        while (list.Items.Count < items.Length && limit.ElapsedMilliseconds < 60_000) Pump(window, 100);
        Console.WriteLine($"HOME-FIXTURE filled={list.Items.Count}/{items.Length} in {limit.ElapsedMilliseconds}ms");
        var viewport = (ScrollViewer)page.FindName("RecommendScroll")!;
        Settle(window, viewport);
        return (window, viewport);
    }

    private static Window Host(FrameworkElement content) => new()
    {
        Content = content,
        Width = 1180,
        Height = 780,
        ShowActivated = false,
        ShowInTaskbar = false
    };

    // ---------------------------------------------------------------- measuring

    private static void Measure(Window window, ScrollViewer viewport, string name,
                               Action? apply = null, Action? undo = null)
    {
        for (int pass = 1; pass <= 2; pass++)
        {
            apply?.Invoke();
            Settle(window, viewport);
            PresentedScrollProbe.Run(viewport, $"{name} #{pass}");
            undo?.Invoke();
        }
    }

    private static void Settle(Window window, ScrollViewer viewport)
    {
        // Realize the probe's whole travel range first, so the timed window is steady-state scrolling.
        var low = Math.Min(700, viewport.ScrollableHeight * .15);
        var high = Math.Min(low + 2700, viewport.ScrollableHeight - 30);
        viewport.ScrollToVerticalOffset(0);
        Pump(window, 150);
        var step = Descendants<VirtualizingCardWallPanel>(viewport).FirstOrDefault()?.RowHeight ?? 260;
        for (var offset = 0.0; offset <= high; offset += step)
        {
            viewport.ScrollToVerticalOffset(offset);
            window.UpdateLayout();
        }
        viewport.ScrollToVerticalOffset(low);
        window.UpdateLayout();
        Pump(window, 1200);
    }

    private static void SetUiScale(FrameworkElement root, double factor) =>
        root.LayoutTransform = factor <= 0 ? null : new ScaleTransform(factor, factor);

    private static void SetCardCache(Window window, CardCache mode)
    {
        _cardCache = mode;
        foreach (var card in Descendants<VideoCard>(window)) ApplyCache(card);
    }

    /// <summary>Cards realized while a variant is active have to get that variant's cache mode too.</summary>
    private static void ApplyCache(VideoCard card)
    {
        if (card.FindName("CoverBox") is Border image && card.DataContext is VideoItem item)
        {
            var source = _coversHidden ? null : item.Cover;
            if (Cover.GetSource(image) != source) Cover.SetSource(image, source);
        }
        if (_coverScaling.HasValue && card.FindName("CoverBox") is Border cover)
            RenderOptions.SetBitmapScalingMode(cover, _coverScaling.Value);
        switch (_cardCache)
        {
            case CardCache.Off:
                card.CacheMode = null;
                break;
            case CardCache.DeviceScale:
                card.CacheMode = new BitmapCache
                {
                    EnableClearType = true,
                    RenderAtScale = _dpiScale,
                    SnapsToDevicePixels = false
                };
                break;
            default:
                card.ClearValue(UIElement.CacheModeProperty);
                break;
        }
    }

    private static bool _coversHidden;
    private static void SetCoversHidden(Window window, bool hidden)
    {
        _coversHidden = hidden;
        foreach (var card in Descendants<VideoCard>(window)) ApplyCache(card);
    }

    // ---------------------------------------------------------------- settings snapshot

    private sealed record Snapshot(string Skin, AppTheme Theme, int Columns, double Scale,
                                   bool Effects, bool Accent);

    private static Snapshot Save() => new(Svc.Settings.SkinId, Svc.Theme.Mode, CardWall.FixedColumns,
        UiScale.Factor, Svc.Settings.SkinEffects, Svc.Settings.SkinUseAccent);

    private static void ApplyUserSettings()
    {
        EventManager.RegisterClassHandler(typeof(VideoCard), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyCache((VideoCard)sender)));
        Svc.Theme.SetMode(AppTheme.Light);
        CardWall.SetFixedColumns(4);
        SkinService.Instance.Configure(SkinService.PhantomBlade, true, true);
    }

    private static void Restore(Snapshot saved)
    {
        SkinService.Instance.Configure(saved.Skin, saved.Effects, saved.Accent);
        Svc.Theme.SetMode(saved.Theme);
        CardWall.SetFixedColumns(saved.Columns);
        UiScale.SetPercent((int)Math.Round(saved.Scale * 100));
        _cardCache = CardCache.Xaml;
    }

    // ---------------------------------------------------------------- helpers

    private static VideoItem[] Covers(string artifacts, int count)
    {
        // Local screenshot files behind synthetic URLs, so the wall is textured like the real page.
        var source = Path.Combine(artifacts, "../../../theme-research/phantom-blade-zero/assets/media/screenshots");
        Directory.CreateDirectory(AppPaths.CacheDir);
        return Enumerable.Range(0, count).Select(i =>
        {
            var url = $"https://example.invalid/ab-wall-{i}.jpg";
            var target = Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(url) + ".jpg");
            if (!File.Exists(target))
                File.Copy(Path.Combine(source, $"{i % 6 + 3:000}.jpg"), target, true);
            return new VideoItem
            {
                Bvid = $"BV{i:D10}",
                Title = $"首页卡片 {i} · 测试较长的标题文字与换行布局",
                Author = "示例 UP",
                Cover = url,
                ViewCount = 12000,
                Duration = "12:34"
            };
        }).ToArray();
    }

    /// <summary>Real B-site feed rows with real cover URLs, served from the test image cache.</summary>
    private static VideoItem[] RealCovers(string artifacts, int count, bool blankCovers = false)
    {
        var feed = Path.Combine(artifacts, "release021-feed.json");
        var pool = System.Text.Json.JsonSerializer.Deserialize<List<VideoItem>>(File.ReadAllText(feed))!;
        return Enumerable.Range(0, count).Select(i =>
        {
            var source = pool[i % pool.Count];
            return new VideoItem
            {
                Bvid = $"{source.Bvid}-{i}",
                Title = source.Title,
                Author = source.Author,
                Cover = blankCovers ? "" : source.Cover,
                Duration = source.Duration,
                ViewCount = source.ViewCount,
                DanmakuCount = source.DanmakuCount,
                Pubdate = source.Pubdate
            };
        }).ToArray();
    }

    private static void Reload(HomeViewModel vm, VideoItem[] items)
    {
        vm.RecommendItems.Clear();
        foreach (var item in items) vm.RecommendItems.Add(item);
    }

    private static void HideHomeOverlays(UserControl home) => SetHomeOverlays(home, Visibility.Collapsed);
    private static void ShowHomeOverlays(UserControl home) => SetHomeOverlays(home, Visibility.Visible);

    /// <summary>The floating column plus the two canvas buttons at the top right of the home page.</summary>
    private static void SetHomeOverlays(UserControl home, Visibility visibility)
    {
        foreach (var name in new[] { "ToolBar", "BtnTop", "HomePartitionButton", "HomeRefreshButton" })
            if (home.FindName(name) is UIElement element) element.Visibility = visibility;
    }

    private static void Pump(Window window, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Thread.Sleep(5);
        }
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is T match) found.Add(match);
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(current, i));
        }
        return found;
    }
}
