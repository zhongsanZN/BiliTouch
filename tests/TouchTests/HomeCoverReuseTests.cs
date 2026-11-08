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

            // ---- 2026-11-08: 封面解码的两道新闸门 ----
            // ① 解码并发上限 = min(CPU/2, 4)。这条断言防的是"有人把它改成 32/64 图省事" ——
            //    超过 4 的并发实测只抬高单张延迟、总吞吐不变, 而 UI 线程会被拖累。
            var cap = CoverLoader.DecodeConcurrency;
            check(cap >= 1 && cap <= 4 && CoverLoader.DecodeSlotCount <= cap,
                "cover decoding is capped at min(CPU/2, 4) concurrent slots");

            // ② "还要不要"标记：作废之后, 排队中的解码必须**直接放弃**, 不能白解一张
            //    已经被回收给别人的卡片。这里用一个**没人用过的 URL**, 保证一定走冷路径。
            //    ★ 编码器实例不能 Save 两次(BitmapEncoder 会抛), 所以这里新建一个 ——
            //      复用上面那个 encoder 会让这两条断言以"测试自身报错"的形式失败。
            var abandonedUrl = "https://example.invalid/abandoned-cover.png";
            var secondEncoder = new PngBitmapEncoder();
            secondEncoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = File.Create(Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(abandonedUrl) + ".jpg")))
                secondEncoder.Save(fs);
            var interest = new CoverLoader.LoadInterest();
            interest.Abandon();
            var gaveUp = CoverLoader.LoadAsync(abandonedUrl, 320, interest).GetAwaiter().GetResult();
            check(gaveUp == null,
                "an abandoned cover load skips decoding instead of burning a decode slot");

            // ③ 没作废时必须正常解出来 —— 否则上面那条会被"永远返回 null"蒙混过关。
            var live = new CoverLoader.LoadInterest();
            var decoded = CoverLoader.LoadAsync(abandonedUrl, 320, live).GetAwaiter().GetResult();
            check(decoded != null,
                "a cover load that is still wanted decodes normally through the slot gate");
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
