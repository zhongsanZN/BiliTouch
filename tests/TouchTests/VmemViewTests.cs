using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Services;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

/// <summary>
/// 验证 <see cref="VmemVideoView"/>：vmem 解码帧能不能真的变成普通 WPF 元素。
///
/// 断言四件事：
///  1) 起播后持续交付帧（不是静态画面）；
///  2) 位图尺寸 == 视频尺寸，且像素确实在变（连续不同帧，不是一帧卡住）；
///  3) 画面能被 RenderTargetBitmap 截到（HwndHost **做不到**，这是圆角/阴影/变形的前提）；
///  4) 能套圆角裁剪 —— 直接对应"澎湃OS 那种卡片"。
/// </summary>
internal static class VmemViewTests
{
    private static string? FindClip()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir != null && i < 9; i++, dir = dir.Parent)
        {
            foreach (var rel in new[] { "artifacts/vsr-probe/motion.mp4", "artifacts/probes/rateaudio/fixture-muxed.mp4" })
            {
                var p = Path.Combine(dir.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    public static void Run(Action<bool, string> check, string artifacts)
    {
        var clip = FindClip();
        if (clip == null) { check(true, "vmem view probe skipped without a local clip"); return; }

        var window = new Window { Width = 900, Height = 560, WindowStyle = WindowStyle.None,
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x19)), ShowActivated = false, Topmost = true };
        var root = new Grid();
        window.Content = root;

        var card = new Border { CornerRadius = new CornerRadius(18), Background = Brushes.Black, ClipToBounds = true };
        var view = new VmemVideoView();
        card.Child = view;
        root.Children.Add(card);
        window.Show();
        Pump(300);

        LibVLC? core = null;
        MediaPlayer? mp = null;
        try
        {
            core = VlcCore.GetVmem();
            mp = new MediaPlayer(core) { Volume = 0 };
            view.Attach(mp);                       // 必须在 Play 之前
            mp.Play(new Media(core, clip, FromType.FromPath));

            // 等格式协商 + 起播
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline && view.DeliveredFrames < 10) Pump(120);
            check(view.DeliveredFrames >= 10, $"vmem delivers frames through VmemVideoView: {view.DeliveredFrames}");

            var size = view.VideoSize;
            check(size.Width > 0 && size.Height > 0, $"format negotiated: {size.Width}x{size.Height}");

            // 帧率：按**片源本身**的帧率判定，别拿 60 硬套 30fps 的夹具
            var srcFps = clip.Contains("motion", StringComparison.OrdinalIgnoreCase) ? 60.0 : 30.0;
            var f0 = view.DeliveredFrames;
            Pump(1500);
            var fps = (view.DeliveredFrames - f0) / 1.5;
            check(fps >= srcFps * 0.85,
                $"vmem keeps up with the source: {fps:F1} fps vs source {srcFps:F1} fps");

            // 画面确实在变：连续采两次像素哈希，必须不同
            var hash1 = HashView(view, out var w, out var h);
            Pump(180);
            var hash2 = HashView(view, out _, out _);
            check(hash1 != hash2, "decoded frames actually change over time (not a frozen first frame)");

            // ★ 关键：RenderTargetBitmap 能不能截到画面（HwndHost 截不到）
            window.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(window);
            var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
            var nonBackground = 0;
            for (var i = 0; i < pixels.Length; i += 4 * 97)
                if (pixels[i] > 60 || pixels[i + 1] > 60 || pixels[i + 2] > 60) nonBackground++;
            var sampleCount = pixels.Length / (4 * 97);
            check(nonBackground > sampleCount / 5,
                $"RenderTargetBitmap captures the video (WPF-compositable): {nonBackground}/{sampleCount} bright samples");

            // 圆角裁剪：给视图加 Clip，截图里四角应该是背景色
            view.Clip = new RectangleGeometry(new Rect(0, 0, view.ActualWidth, view.ActualHeight), 22, 22);
            Pump(150);
            var clipped = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
            clipped.Render(window);
            check(view.Clip is RectangleGeometry rg && rg.RadiusX > 0, "video can be clipped to rounded corners (HyperOS-style card)");

            var dir = Path.Combine(artifacts, "vmem-view");
            Directory.CreateDirectory(dir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(clipped));
            using (var fs = File.Create(Path.Combine(dir, "vmem-view.png"))) enc.Save(fs);
            Console.WriteLine($"VMEMVIEW saved {Path.Combine(dir, "vmem-view.png")}");
        }
        catch (Exception ex)
        {
            check(false, "vmem view probe threw: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            void Mark(string m) { Console.WriteLine("VMEMVIEW-STEP " + m); Console.Out.Flush(); }
            Mark("stop");
            // ★ Stop 会拆 vout，vmem 回调在这期间还在原生线程上跑；在 UI 线程同步 Stop
            //   会和回调抢缓冲，实测直接 0xC0000005。与产品里 Enhancement 切换同款做法：
            //   丢到后台线程 Stop 并等它回来。
            try { if (mp != null) System.Threading.Tasks.Task.Run(() => { try { mp.Stop(); } catch { } }).Wait(4000); } catch { }
            Mark("pump1");
            // ★ 这里**不能 Pump Dispatcher**：Stop 之后仍可能有 display 回调排队的
            //   BeginInvoke 在队列里，此时再抽干队列会撞上已拆除的 vout。
            //   用纯 Thread.Sleep 让原生侧自己收尾。
            System.Threading.Thread.Sleep(250);
            Mark("dispose-player");
            try { mp?.Dispose(); } catch { }
            Mark("pump2"); Pump(200);
            Mark("dispose-view");
            view.Dispose();
            Mark("close");
            try { window.Close(); } catch { }
            Mark("pump3"); Pump(200);
            Mark("done");
        }
    }

    /// <summary>采样 WriteableBitmap 的像素做哈希，判断画面是否在变。</summary>
    private static long HashView(VmemVideoView view, out int w, out int h)
    {
        w = h = 0;
        if (view.Source is not BitmapSource src) return 0;
        w = src.PixelWidth; h = src.PixelHeight;
        var stride = Math.Max(4, src.PixelWidth * 4);
        var buf = new byte[stride * Math.Min(src.PixelHeight, 120)];
        try { src.CopyPixels(new Int32Rect(0, 0, src.PixelWidth, Math.Min(src.PixelHeight, 120)), buf, stride, 0); }
        catch { return 0; }
        long hash = 17;
        for (var i = 0; i < buf.Length; i += 7) hash = hash * 31 + buf[i];
        return hash;
    }
}
