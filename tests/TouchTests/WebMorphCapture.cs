using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Services;
using BiliTouch.Views;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

/// <summary>
/// 把用户的真实配置（网页版布局 + 110% UI 缩放 + 带图案视频）下的小窗过渡
/// 一帧帧截屏存下来，用来肉眼判断内容对齐、重影、黑边、镜像是否真的生效。
/// 纯色夹具测不出 rcSource 裁切偏移，所以这里必须用带图案的片源。
/// 截屏走纯 P/Invoke(BitBlt/GetDIBits) + WPF BitmapSource，不给测试项目引入
/// System.Drawing / WindowsForms 的全局 using 冲突。
/// </summary>
internal static class WebMorphCapture
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, Hidden)!.GetValue(o)!;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out WINDOWRECT rect);
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWRECT { public int Left, Top, Right, Bottom; }
    private const uint SRCCOPY = 0x00CC0020;

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static string? FindClip()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir != null && i < 9; i++, dir = dir.Parent)
        {
            foreach (var rel in new[] { "artifacts/vsr-probe/motion.mp4", "artifacts/vsr-probe/motion-640.mp4", "artifacts/probes/rateaudio/fixture-muxed.mp4" })
            {
                var p = Path.Combine(dir.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private static void Capture(int x0, int y0, int w, int h, string path)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, x0, y0, SRCCOPY);
            var stride = w * 4;
            var buffer = new byte[stride * h];
            var bi = new BITMAPINFOHEADER
            {
                biSize = 40, biWidth = w, biHeight = -h,   // 负值 = top-down BGRA
                biPlanes = 1, biBitCount = 32, biCompression = 0
            };
            if (GetDIBits(mem, bmp, 0, (uint)h, buffer, ref bi, 0) == 0)
                throw new InvalidOperationException("GetDIBits failed");
            var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static void Run(string artifacts)
    {
        var oldLayout = Svc.Settings.UseWebPlayerLayout;
        var oldScale = Svc.Settings.UiScalePercent;
        PlayerWindow? player = null;
        try
        {
            // ★ 复刻用户 settings.json 的关键两项
            Svc.Settings.SetWebPlayerLayout(true);
            Svc.Settings.SetUiScalePercent(110);

            player = new PlayerWindow { Width = 1320, Height = 860, Topmost = true, ShowActivated = false };
            player.Show(); Pump(400);

            var clip = FindClip();
            if (clip == null) { Console.WriteLine("MORPHCAPTURE no clip"); return; }
            Console.WriteLine($"MORPHCAPTURE clip={clip}");
            player.PlayLocalFile(clip);
            var mp = Field<MediaPlayer>(player, "_mp"); mp.Mute = true;
            Pump(2200);

            var comments = (ItemsControl)player.FindName("CommentList");
            comments.ItemsSource = Enumerable.Range(0, 60).Select(i => new BiliTouch.Models.CommentItem
            { Content = "网页布局滚动测试", UserName = $"测试 {i}" }).ToArray();
            Pump(500);

            var scroll = (ScrollViewer)player.FindName("WebLeftScroll");
            var placeholder = (Border)player.FindName("WebVideoPlaceholder");
            var stage = (Grid)player.FindName("PlayerStage");
            var surface = (Grid)player.FindName("VideoSurface");

            // ★ 定位坐标换算：DPI / PointToScreen / TransformToAncestor 各层到底是什么单位
            var dpi = VisualTreeHelper.GetDpi(player);
            var stageOrigin = stage.PointToScreen(new Point(0, 0));
            var playerOrigin = player.PointToScreen(new Point(0, 0));
            var fromDip = surface.TransformToAncestor(stage).TransformBounds(new Rect(surface.RenderSize));
            var converter = player.GetType().GetMethod("WebStageToScreenPixels", Hidden);
            var fromPx = converter != null ? (Rect)converter.Invoke(player, new object[] { fromDip })! : Rect.Empty;
            Console.WriteLine($"DIAG dpi={dpi.DpiScaleX:F3},{dpi.DpiScaleY:F3} stageOrigin={stageOrigin} playerOrigin={playerOrigin}");
            Console.WriteLine($"DIAG renderSize={surface.RenderSize} fromDip={fromDip}");
            Console.WriteLine($"DIAG fromPx={fromPx}");
            Console.WriteLine($"DIAG screenDIP={SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}");

            var dir = Path.Combine(artifacts, "web-morph");
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.EnumerateFiles(dir, "*.png")) File.Delete(f);

            // ★ 用 GetWindowRect 拿**物理像素**窗口区域。SystemParameters.PrimaryScreenWidth
            //   是 DIP，225% 缩放下拿它当物理像素只会截到屏幕左上角一小块(误导过一次)。
            var playerHwnd = new System.Windows.Interop.WindowInteropHelper(player).Handle;
            GetWindowRect(playerHwnd, out var wr);
            var capX = wr.Left; var capY = wr.Top;
            var w = Math.Max(1, wr.Right - wr.Left);
            var h = Math.Max(1, wr.Bottom - wr.Top);
            Console.WriteLine($"DIAG capture rect={capX},{capY},{w}x{h} (physical px)");
            var records = new List<object>();
            var sw = Stopwatch.StartNew();
            double last = -1000;
            EventHandler? onFrame = null;
            onFrame = (_, _) =>
            {
                var t = sw.Elapsed.TotalMilliseconds;
                if (t - last < 55) return;
                last = t;
                var runningObj = player.GetType().GetField("_webRendering", Hidden)?.GetValue(player);
                var running = runningObj is bool b && b;
                var legacy = running;
                var path = Path.Combine(dir, $"f{records.Count:00}.png");
                Capture(capX, capY, w, h, path);
                records.Add(new
                {
                    t = Math.Round(t), running, legacy,
                    coverScale = (player.GetType().GetField("_webCoverScale", Hidden)?.GetValue(player) as ScaleTransform)?.ScaleX ?? 1,
                    path = Path.GetFileName(path)
                });
                if (records.Count >= 20) CompositionTarget.Rendering -= onFrame;
            };

            CompositionTarget.Rendering += onFrame;
            scroll.ScrollToVerticalOffset(placeholder.ActualHeight + 80);
            Pump(2600);
            CompositionTarget.Rendering -= onFrame;

            File.WriteAllText(Path.Combine(dir, "index.json"),
                JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"MORPHCAPTURE {records.Count} frames -> {dir}");
        }
        finally
        {
            player?.Close(); Pump(250);
            Svc.Settings.SetWebPlayerLayout(oldLayout);
            Svc.Settings.SetUiScalePercent(oldScale);
        }
    }
}
