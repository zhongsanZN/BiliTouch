using System.Diagnostics;
using System.IO;
using BiliTouch;
using BiliTouch.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Views.Controls;

internal static class ScrollRealizationTests
{
    public static void Run(Action<bool, string> check, bool costOnly)
    {
        var video = new VideoItem { Title = "首页 · 跨行生成卡片", Author = "作者" };
        var comment = new CommentItem { Content = "带昵称前缀和 @用户名 的楼中楼评论", UserName = "评论者" };
        Measure("video cards", () => new VideoCard { DataContext = video }, 252);
        Measure("comment replies", () => new CommentText
        {
            Comment = comment, Prefix = "回复者：", PrefixForeground = Brushes.Navy, MentionForeground = Brushes.Green
        }, 440);
        if (costOnly) return;

        var card = new VideoCard { DataContext = video, ExtraMenuItems = [new CardMenuItem { Header = "额外操作" }] };
        var window = new Window { Content = card, ShowActivated = false, Width = 340, Height = 320 };
        try
        {
            window.Show();
            var menu = card.ContextMenu!;
            check(menu.Items.Count == 0, "scrolling into view leaves video actions unconstructed");
            menu.PlacementTarget = card;
            menu.IsOpen = true;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(menu.Items.OfType<MenuItem>().Count() == 7 && ReferenceEquals(menu.DataContext, video), "first menu opening builds six video actions and the page action");
            menu.IsOpen = false;
            card.ExtraMenuItems.Clear();
            menu.IsOpen = true;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(menu.Items.OfType<MenuItem>().Count() == 6, "reopened menu reflects the current page actions");
            menu.IsOpen = false;

            var text = new CommentText { Comment = comment, Prefix = "作者：" };
            window.Content = text;
            window.UpdateLayout();
            check(new TextRange(text.Document.ContentStart, text.Document.ContentEnd).Text.Contains("作者：" + comment.Content), "measured reply has its selectable prefix and complete text");
            text.Comment = new CommentItem { Content = "新正文" };
            text.Prefix = "新作者：";
            window.UpdateLayout();
            check(new TextRange(text.Document.ContentStart, text.Document.ContentEnd).Text.Trim() == "新作者：新正文", "recycled reply updates text and prefix together");

            var list = new StackPanel();
            window.Content = null;
            list.Children.Add(text);
            list.Children.Add(new Border { Height = 1800 });
            var viewport = new ScrollViewer { Content = list };
            window.Content = viewport;
            window.UpdateLayout();
            var editor = (ScrollViewer)text.Template.FindName("PART_ContentHost", text);
            check(editor.PanningMode == PanningMode.None, "selectable comment body leaves touch panning to the outer viewport");
            var packet = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -30)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            var run = ((Paragraph)text.Document.Blocks.FirstBlock).Inlines.FirstInline;
            run.RaiseEvent(packet);
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            check(packet.Handled && viewport.VerticalOffset > 0 && editor.VerticalOffset == 0,
                "precision input over an actual inline moves the page, including when it hits selectable text");

            var ring = new LoadingRing();
            window.Content = ring;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var rot = (RotateTransform)ring.FindName("Rot");
            check(rot.HasAnimatedProperties, "visible loading ring spins");
            ring.Visibility = Visibility.Collapsed;
            check(!rot.HasAnimatedProperties, "hidden ring releases its animation clock");
            ring.Visibility = Visibility.Visible;
            check(rot.HasAnimatedProperties, "restored ring spins again");
            window.Content = null;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(!rot.HasAnimatedProperties, "unmounted ring releases its clock");
        }
        finally { window.Close(); }
    }

    public static void ImageWall(string artifacts, Action<bool, string> check)
    {
        // Measure the UI layout cost of crossing fresh rows separately from presentation.
        var source = Path.Combine(artifacts, "../../../theme-research/phantom-blade-zero/assets/media/screenshots");
        var oldSkin = Svc.Settings.SkinId;
        var effects = Svc.Settings.SkinEffects;
        var accent = Svc.Settings.SkinUseAccent;
        var window = new Window { Width = 1180, Height = 780, ShowActivated = false };
        try
        {
            SkinService.Instance.Configure(SkinService.PhantomBlade, effects, accent);
            Directory.CreateDirectory(AppPaths.CacheDir);
            var items = Enumerable.Range(0, 180).Select(i =>
            {
                var url = $"https://example.invalid/local-wall-{i}.jpg";
                File.Copy(Path.Combine(source, $"{i % 6 + 3:000}.jpg"),
                    Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(url) + ".jpg"), true);
                return new VideoItem { Title = $"高速滚动实测 {i} · 横向封面和换行标题", Author = "测试投稿", Cover = url, ViewCount = 12000 };
            }).ToArray();
            var panel = new FrameworkElementFactory(typeof(VirtualizingCardWallPanel));
            panel.SetValue(CardWall.EnableProperty, true);
            var list = new ItemsControl { ItemsSource = items, ItemsPanel = new ItemsPanelTemplate(panel),
                ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(VideoCard)) } };
            var viewport = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var grid = new Grid();
            grid.Children.Add(new SkinSurface { Kind = "Content" });
            grid.Children.Add(viewport);
            window.Content = grid;
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            PresentedScrollProbe.Run(viewport, "themed image wall");
            var elapsed = new List<double>();
            for (int i = 1; i <= 32; i++)
            {
                var start = Stopwatch.GetTimestamp();
                viewport.ScrollToVerticalOffset(i * 260);
                window.UpdateLayout();
                elapsed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            elapsed.Sort();
            check(viewport.VerticalOffset > 6000, "image wall crosses previously unrealized rows");
            Console.WriteLine($"IMAGE-WALL 180 covers, 32 steps: layout median={elapsed[16]:F2}ms p95={elapsed[30]:F2}ms max={elapsed[^1]:F2}ms dpi={VisualTreeHelper.GetDpi(window).PixelsPerInchX:F0}");
        }
        finally { window.Close(); SkinService.Instance.Configure(oldSkin, effects, accent); }
    }

    private static void Measure(string label, Func<FrameworkElement> create, double width)
    {
        // Cold containers with warm framework templates, excluding HTTP, image decoding and compositing.
        var times = new List<double>();
        var allocations = new List<long>();
        for (int round = 0; round < 6; round++)
        {
            var startBytes = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 32; i++)
            {
                var element = create();
                element.Measure(new Size(width, double.PositiveInfinity));
                element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
            }
            if (round == 0) continue;
            times.Add(watch.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - startBytes);
        }
        times.Sort();
        allocations.Sort();
        Console.WriteLine($"REALIZE 32 {label}: median={times[2]:F1}ms allocated={allocations[2] / 1024}KiB (five batches)");
    }
}
