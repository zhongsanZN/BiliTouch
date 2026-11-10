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
        var card = new Border
        {
            Width = 320, Height = 180, CornerRadius = new CornerRadius(8),
            Background = new ImageBrush(bmp), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(220, 360, 0, 0)
        };
        var source = new Window
        {
            WindowState = WindowState.Maximized, Background = new SolidColorBrush(Color.FromRgb(236, 240, 246)),
            Content = new Grid { Children = { card } }
        };
        source.Show(); Pump(500);
        Application.Current.MainWindow = source;

        Svc.Player.WarmUp(); Pump(300);
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
        SeamlessTransitionDirector.Capture(card, "BV1seamlessProbe", 8.0, card);
        Svc.Player.PlayVideo("BV1seamlessProbe", "probe");
        player = (PlayerWindow?)typeof(PlayerService).GetField("_window", Hidden)!.GetValue(Svc.Player);
        Pump(1400);
        var entryDeadline = Environment.TickCount64 + 8000;
        while (player?.IsSeamlessMorphRunning == true && Environment.TickCount64 < entryDeadline) Pump(20);
        check(player is { IsSeamlessMorphRunning: false }, "seamless entry finishes on its actual rendering clock");
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
        source.Close(); Pump(200);
    }
}
