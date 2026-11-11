using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.Views;
using BiliTouch.Views.Controls;
using BiliTouch.Views.Pages;
using BiliTouch.ViewModels;
using BiliTouch.Models;

/// <summary>
/// 应用内形变画面探针：主窗口卡片 → 一镜到底打开播放器 → 关闭收回，记录几何及少量场景快照，
/// 用来肉眼确认"是否从屏幕中间展开"、关闭是否抖动/报错。
/// </summary>
internal static class SeamlessCaptureProbe
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    public static void Run(Action<bool, string> check, string artifacts)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Svc.Session.Load();
        var realHome = Environment.GetCommandLineArgs().Contains("--real-home");
        Svc.Settings.SetWebPlayerLayout(!Environment.GetCommandLineArgs().Contains("--legacy-layout") &&
            (realHome || Environment.GetCommandLineArgs().Contains("--web-layout")));
        if (realHome) UiScale.SetPercent(110);
        var dir = Path.Combine(artifacts, "seamless-capture");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(dir)) File.Delete(f);

        var bmp = new WriteableBitmap(64, 36, 96, 96, PixelFormats.Bgra32, null);
        var px = new byte[64 * 36 * 4];
        for (int y = 0; y < 36; y++) for (int x = 0; x < 64; x++)
        {
            var i = (y * 64 + x) * 4;
            px[i] = (byte)(x * 4); px[i + 1] = (byte)(y * 7); px[i + 2] = 230; px[i + 3] = 255;
        }
        bmp.WritePixels(new Int32Rect(0, 0, 64, 36), px, 64 * 4, 0);
        bmp.Freeze();
        FrameworkElement card = new Border
        {
            Width = 320, Height = 180, CornerRadius = new CornerRadius(8),
            Background = new ImageBrush(bmp), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(220, 360, 0, 0)
        };
        Window source = new Window
        {
            WindowState = WindowState.Maximized, Background = new SolidColorBrush(Color.FromRgb(236, 240, 246)),
            Content = new Grid { Children = { card } }
        };
        VideoCard? homeCard = null;
        if (realHome)
        {
            source = new MainWindow { WindowState = WindowState.Maximized };
            var home = new HomePage { DataContext = new HomeViewModel(new FixtureHomeSource()) };
            ((Dictionary<PageKey, FrameworkElement>)typeof(MainWindow).GetField("_pages", Hidden)!.GetValue(source)!).Add(PageKey.Home, home);
        }
        source.Show(); Pump(500);
        Application.Current.MainWindow = source;
        if (realHome)
        {
            Pump(800);
            homeCard = Descendants<VideoCard>(source).First(c => c.IsVisible);
            card = (Border)homeCard.FindName("CoverBox");
            ((Border)card).Background = new ImageBrush(bmp);
            Console.WriteLine($"REAL HOME scale={UiScale.Factor} card={card.TransformToAncestor(source).TransformBounds(new Rect(card.RenderSize))}");
        }

        if (!Environment.GetCommandLineArgs().Contains("--cold"))
        {
            Svc.Player.WarmUp(); Pump(300);
        }
        Process? desktop = null;
        if (Environment.GetCommandLineArgs().Contains("--desktop"))
        {
            desktop = Process.Start(new ProcessStartInfo("ffmpeg",
                $"-y -hide_banner -loglevel error -f lavfi -i ddagrab=framerate=60:draw_mouse=0 -t 6 -vf hwdownload,format=bgra,scale=960:-2 -c:v libx264 -preset ultrafast -crf 22 \"{Path.Combine(dir, "desktop.mp4")}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            Pump(600);
        }
        var records = new List<object>();
        var screenshots = new List<(string path, BitmapSource bitmap)>();
        var capturedPhases = new HashSet<string>();
        var sw = Stopwatch.StartNew();
        double last = -1000; string phase = "open";
        PlayerWindow? player = null;
        EventHandler onFrame = (_, _) =>
        {
            var t = sw.Elapsed.TotalMilliseconds;
            if (t - last < 90 || records.Count >= (phase == "open" ? 12 : 24)) return;
            last = t;
            var path = Path.Combine(dir, $"{phase}-{records.Count:00}.png");
            var captureThisFrame = player is { IsVisible: true, SeamlessMorphProgress: >= .4 } && capturedPhases.Add(phase);
            if (captureThisFrame)
            {
                // Capture only this fixture's WPF scene. Desktop-region copies can
                // silently photograph an unrelated user player covering this window.
                var target = player is { IsVisible: true } ? (FrameworkElement)player.Content : (FrameworkElement)source.Content;
                var width = Math.Max(1, target.ActualWidth);
                var height = Math.Max(1, target.ActualHeight);
                var scale = Math.Min(1, 800 / width);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                    dc.DrawRectangle(new VisualBrush(target) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
                }
                bitmap.Render(drawing); bitmap.Freeze();
                screenshots.Add((path, bitmap));
            }
            IntPtr ph = player != null ? new WindowInteropHelper(player).Handle : IntPtr.Zero;
            RECT pr = default; if (ph != IntPtr.Zero) GetWindowRect(ph, out pr);
            records.Add(new
            {
                phase, t = Math.Round(t), file = captureThisFrame ? Path.GetFileName(path) : null,
                running = player?.IsSeamlessMorphRunning, p = player?.SeamlessMorphProgress,
                rect = player?.SeamlessCurrentRect.ToString(),
                win = $"{pr.Left},{pr.Top},{pr.Right - pr.Left}x{pr.Bottom - pr.Top}",
                state = player?.WindowState.ToString(), visible = player?.IsVisible,
                rootW = (player?.FindName("RootGrid") as FrameworkElement)?.ActualWidth,
            });
        };
        var errorFile = Path.Combine(AppPaths.DataDir, "errors.txt");
        var errorsBefore = File.Exists(errorFile) ? new FileInfo(errorFile).Length : 0;
        CompositionTarget.Rendering += onFrame;
        if (homeCard != null) homeCard.OpenVideo();
        else
        {
            SeamlessTransitionDirector.Capture(card, "BV1seamlessProbe", 8.0, card);
            Svc.Player.PlayVideo("BV1seamlessProbe", "probe");
        }
        player = (PlayerWindow?)typeof(PlayerService).GetField("_window", Hidden)!.GetValue(Svc.Player);
        var state = typeof(PlayerWindow).GetField("_seamless", Hidden)!.GetValue(player!)!;
        check(!player.IsVisible && state.GetType().GetField("EntryWindow")!.GetValue(state) is Window { IsVisible: true },
            "source card surface is visible before native player Show");
        var entry = (Window)state.GetType().GetField("EntryWindow")!.GetValue(state)!;
        check(ReferenceEquals(player!.Owner, source) && ReferenceEquals(entry.Owner, source),
            "detail and transition surface belong to the source during entry, avoiding independent launch animations");
        var content = (FrameworkElement)source.Content;
        var contentExtent = content.PointToScreen(new Point(content.ActualWidth, content.ActualHeight)) - content.PointToScreen(new Point());
        var entryExtent = entry.PointToScreen(new Point(entry.ActualWidth, entry.ActualHeight)) - entry.PointToScreen(new Point());
        check((contentExtent - entryExtent).Length < 3,
            "transition surface covers the actual source bounds including application UI scaling");
        var expected = new Rect(entry.PointFromScreen(card.PointToScreen(new Point())),
            entry.PointFromScreen(card.PointToScreen(new Point(card.ActualWidth, card.ActualHeight))));
        Console.WriteLine($"ENTRY geometry initial={player!.SeamlessCurrentRect} expected={expected} sourceDpi={VisualTreeHelper.GetDpi(card).DpiScaleX} stageDpi={VisualTreeHelper.GetDpi(entry).DpiScaleX} stageSize={entry.ActualWidth}x{entry.ActualHeight} screen={card.PointToScreen(new Point())} anchor={SeamlessTransitionDirector.CurrentAnchor?.ScreenPixelRect}");
        check((player!.SeamlessCurrentRect.TopLeft - expected.TopLeft).Length < 1 &&
            Math.Abs(player.SeamlessCurrentRect.Width - expected.Width) < 1,
            "entry first frame starts at the clicked card rather than screen center");
        if (state.GetType().GetField("EntryWindow")!.GetValue(state) is Window entryStage)
        { entryStage.Topmost = false; entryStage.Topmost = true; }
        Console.WriteLine($"ENTRY immediate running={player?.IsSeamlessMorphRunning} progress={player?.SeamlessMorphProgress} rect={player?.SeamlessCurrentRect}");
        foreach (Window w in Application.Current.Windows) Console.WriteLine($"ENTRY window {w.GetType().FullName} visible={w.IsVisible} title={w.Title} size={w.ActualWidth}x{w.ActualHeight}");
        Pump(1400);
        var entryDeadline = Environment.TickCount64 + 8000;
        while (player?.IsSeamlessMorphRunning == true && Environment.TickCount64 < entryDeadline) Pump(20);
        check(player is { IsSeamlessMorphRunning: false }, "seamless entry finishes on its actual rendering clock");
        check(state.GetType().GetField("EntryWindow")!.GetValue(state) == null,
            "entry surface is released after revealing the player");
        check(player!.Owner == null, "entry restores independent player ownership for normal window controls");
        phase = "close"; sw.Restart(); last = -1000;
        var closeBtn = player != null ? player.TryBeginSeamlessClose() : false;
        Pump(1400);
        var closeDeadline = Environment.TickCount64 + 8000;
        while (player is { IsVisible: true } && Environment.TickCount64 < closeDeadline) Pump(20);
        CompositionTarget.Rendering -= onFrame;
        foreach (var (path, bitmap) in screenshots)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        File.WriteAllText(Path.Combine(dir, "index.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        check(closeBtn, "seamless close started");
        check(player is { IsVisible: false }, "seamless close completes on the actual fixture player");
        check(!File.Exists(errorFile) || new FileInfo(errorFile).Length == errorsBefore, "open and close add no reported application exception");
        Console.WriteLine($"SEAMLESS {records.Count} geometry records, {screenshots.Count} WPF scene snapshots -> {dir}");
        if (desktop != null) { while (!desktop.HasExited) Pump(100); desktop.Dispose(); }

        // Exercise the deferred Show path twice in the same UI turn. Only the
        // latest click may start playback when the source surface is presented.
        PlayerWindow Open(string bvid, string title)
        {
            SeamlessTransitionDirector.Capture(card, bvid, 8.0, card);
            Svc.Player.PlayVideo(bvid, title);
            var result = (PlayerWindow)typeof(PlayerService).GetField("_window", Hidden)!.GetValue(Svc.Player)!;
            return result;
        }
        Window? Entry(PlayerWindow window)
        {
            var morph = typeof(PlayerWindow).GetField("_seamless", Hidden)!.GetValue(window)!;
            return (Window?)morph.GetType().GetField("EntryWindow")!.GetValue(morph);
        }
        var first = Open("BV1rapidFirst", "first");
        var latest = Open("BV1rapidLatest", "latest");
        var visibleDeadline = Environment.TickCount64 + 5000;
        while (!latest.IsVisible && Environment.TickCount64 < visibleDeadline) Pump(10);
        check(ReferenceEquals(first, latest) && latest.Title == "latest - BiliTouch" &&
            Equals(typeof(PlayerWindow).GetField("_currentBvid", Hidden)!.GetValue(latest), "BV1rapidLatest"),
            "rapid clicks start only the latest requested video");

        var motionDeadline = Environment.TickCount64 + 3000;
        while ((!latest.IsSeamlessMorphRunning || latest.SeamlessMorphProgress < .05) && Environment.TickCount64 < motionDeadline) Pump(10);
        check(latest.IsSeamlessMorphRunning && latest.SeamlessMorphProgress < 1 && Entry(latest) != null,
            "interruption fixture reaches the actual entry animation");
        var beforeInterrupt = latest.SeamlessCurrentRect;
        check(latest.TryBeginSeamlessClose() && latest.SeamlessCurrentRect == beforeInterrupt,
            "closing during entry reverses from the current geometry without jumping");
        var interruptedDeadline = Environment.TickCount64 + 5000;
        while (latest.IsVisible && Environment.TickCount64 < interruptedDeadline) Pump(20);
        check(!latest.IsVisible && Entry(latest) == null,
            "interrupted entry closes and releases its temporary surface");
        check(!File.Exists(errorFile) || new FileInfo(errorFile).Length == errorsBefore,
            "rapid clicks and interrupted entry add no reported exception");
        var ownerShutdown = Open("BVownerShutdown", "owner shutdown");
        var shutdownDeadline = Environment.TickCount64 + 5000;
        while (!ownerShutdown.IsVisible && Environment.TickCount64 < shutdownDeadline) Pump(10);
        source.Close(); Pump(500);
        check(!ownerShutdown.IsVisible && Entry(ownerShutdown) == null,
            "closing the source during entry releases its owned player and transition surface");
        check(!File.Exists(errorFile) || new FileInfo(errorFile).Length == errorsBefore,
            "source-window shutdown during entry adds no reported exception");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class FixtureHomeSource : IHomeFeedSource
    {
        private static List<VideoItem> Rows() => Enumerable.Range(0, 36).Select(i => new VideoItem { Bvid="BV1seamlessProbe", Title=$"真实首页卡片 · {i}", Author="动效检查", Duration="1:00" }).ToList();
        public Task<(bool ok,string? err,List<VideoItem>? items)> Recommend(bool reset) => Task.FromResult((true,(string?)null,(List<VideoItem>?)Rows()));
        public Task<(bool ok,string? err,List<VideoItem>? items)> Popular(int page) => Recommend(false);
        public Task<(bool ok,string? err,List<VideoItem>? items)> Ranking() => Recommend(false);
        public Task<(bool ok,string? err,List<VideoItem>? items)> Live(int page) => Recommend(false);
        public Task<(bool ok,string? err,List<BangumiItem>? items,bool hasNext)> Bangumi(int page) => Task.FromResult((true,(string?)null,(List<BangumiItem>?)new List<BangumiItem>(),false));
    }
}
