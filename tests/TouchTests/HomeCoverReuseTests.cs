using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Views.Controls;

internal static class HomeCoverReuseTests
{
    public static void Run(Action<bool, string> check)
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        var url = "https://example.invalid/home-cover-reuse.png";
        var bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null,
            new byte[640 * 360 * 4], 640 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(url) + ".jpg")))
            encoder.Save(stream);
        var cached = CoverLoader.LoadAsync(url, 1280).GetAwaiter().GetResult();
        check(cached != null && CoverLoader.TryGetCached(url, 320, out var smaller) && ReferenceEquals(cached, smaller),
            "narrower recycled cards reuse the existing larger decoded bitmap");
        check(ReferenceEquals(cached, CoverLoader.LoadAsync(url, 320).GetAwaiter().GetResult()),
            "async requests also reuse a larger cached thumbnail");

        var item = new VideoItem { Title = "回滚复用", Cover = url };
        var card = new VideoCard { DataContext = item, Width = 236 };
        var window = new Window { Content = new ScrollViewer { Content = card }, Width = 400, Height = 400, ShowActivated = false };
        var previousContext = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            var started = QueueStarted();
            window.Show();
            Pump();
            var cover = (Border)card.FindName("CoverBox");
            check(cover.Background is ImageBrush brush && ReferenceEquals(brush.ImageSource, cached),
                "card displays the same decoded bitmap after its first layout");
            check(QueueStarted() == started, "warm cover bypasses the cold-image viewport queue");

            Cover.SetSource(cover, null);
            Cover.SetSource(cover, url);
            check(cover.Background is ImageBrush && !cover.HasAnimatedProperties,
                "loaded warm card restores synchronously without another fade");

            // Force a disk-cache return without depending on GC or the machine's memory.
            var cache = (System.Collections.IDictionary)typeof(CoverLoader)
                .GetField("MemCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var key = url + "|w1280";
            var entry = cache[key];
            cache.Remove(key);
            try
            {
                Cover.SetSource(cover, null);
                check(cover.Background == null, "recycled source clears the previous video's image immediately");
                Cover.SetSource(cover, url);
                Pump();
                for (int i = 0; i < 20 && cover.Background == null; i++) Pump();
                check(cover.Background is ImageBrush && !cover.HasAnimatedProperties,
                    "previously viewed item returning from disk does not replay its cover entrance");
            }
            finally { cache[key] = entry; }

            window.Content = null;
            var returned = new VideoCard { DataContext = item, Width = 236 };
            started = QueueStarted();
            window.Content = new ScrollViewer { Content = returned };
            Pump();
            check(((Border)returned.FindName("CoverBox")).Background is ImageBrush && QueueStarted() == started,
                "recreated card restores its cached cover without scheduling cold work");

            var factory = new FrameworkElementFactory(typeof(VirtualizingCardWallPanel));
            factory.SetValue(CardWall.EnableProperty, true);
            var list = new ItemsControl
            {
                ItemsSource = Enumerable.Range(0, 80).Select(i => new VideoItem { Title = $"回滚 {i}", Cover = url }).ToArray(),
                ItemsPanel = new ItemsPanelTemplate(factory),
                ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(VideoCard)) }
            };
            var viewport = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            window.Content = viewport;
            Pump();
            viewport.ScrollToVerticalOffset(viewport.ScrollableHeight / 2);
            Pump();
            viewport.ScrollToTop();
            Pump();
            var cards = Descendants<VideoCard>(list).ToArray();
            check(cards.Length > 0 && cards.Length < 60 && cards.All(c =>
                    Descendants<Border>(c).Any(b => b.Name == "CoverBox" && b.Background is ImageBrush)),
                "virtualized wall restores visible covers after scrolling out and back");
            check(QueueStarted() == started, "warm forward and backward scrolling schedules zero cold image loads");
        }
        finally { window.Close(); System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static int QueueStarted() => (int)typeof(CoverLoader).Assembly
        .GetType("BiliTouch.Helpers.ViewportImageQueue")!
        .GetProperty("Started", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
