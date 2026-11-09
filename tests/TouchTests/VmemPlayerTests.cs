using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.Views;

/// <summary>
/// 验证「实验性 vmem 渲染」在**真实 PlayerWindow** 里能跑：
///  1) 窗口以 vmem 模式构建（视频宿主是 VmemView，不是 HwndHost）；
///  2) VideoAreaRoot（弹幕/控制栏/手势层）被搬回普通 WPF 树并可见；
///  3) 起播后持续交付帧；
///  4) 整窗能被 RenderTargetBitmap 截到（HwndHost 做不到，这是圆角/阴影/变形的前提）。
/// </summary>
internal static class VmemPlayerTests
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, Hidden)!.GetValue(o)!;

    private static string? FindClip()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir != null && i < 9; i++, dir = dir.Parent)
        {
            foreach (var rel in new[] { "artifacts/probes/rateaudio/fixture-muxed.mp4", "artifacts/vsr-probe/motion.mp4" })
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
        if (clip == null) { check(true, "vmem player probe skipped without a local clip"); return; }

        var old = Svc.Settings.UseVmemRenderer;
        PlayerWindow? player = null;
        try
        {
            Svc.Settings.SetVmemRenderer(true);
            player = new PlayerWindow { Width = 1280, Height = 820, Topmost = true, ShowActivated = false };
            player.Show();
            Pump(500);

            check(Field<bool>(player, "_vmemMode"), "player window builds in vmem mode");

            var vmem = (FrameworkElement)player.FindName("VmemView");
            var surface = (Grid)player.FindName("VideoSurface");
            var areaRoot = (Grid)player.FindName("VideoAreaRoot");
            check(vmem.Visibility == Visibility.Visible, "VmemVideoView host is visible");
            check(ReferenceEquals(areaRoot.Parent, surface),
                "VideoAreaRoot moved out of the HwndHost overlay into the normal WPF tree");
            check(ReferenceEquals((FrameworkElement)player.FindName("DanmakuCanvas"), areaRoot.Children[1])
                  || IsDescendantOf((FrameworkElement)player.FindName("DanmakuCanvas"), areaRoot),
                "danmaku layer still lives inside the video area");
            check(((FrameworkElement)player.FindName("ControlBar")).IsVisible,
                "control bar is still visible over the composited video");

            player.PlayLocalFile(clip);
            Pump(2500);

            var frames = (long)vmem.GetType().GetProperty("DeliveredFrames")!.GetValue(vmem)!;
            check(frames > 30, $"vmem delivers frames inside the real player window: {frames}");

            player.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)player.ActualWidth, (int)player.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(player);
            var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
            var bright = 0; var total = 0;
            for (var i = 0; i < px.Length; i += 4 * 131) { total++; if (px[i] > 60 || px[i + 1] > 60 || px[i + 2] > 60) bright++; }
            check(bright > total / 10,
                $"whole player window is WPF-compositable (video included): {bright}/{total} bright samples");

            var dir = Path.Combine(artifacts, "vmem-player");
            Directory.CreateDirectory(dir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(Path.Combine(dir, "vmem-player.png"))) enc.Save(fs);
            Console.WriteLine("VMEMPLYER saved " + Path.Combine(dir, "vmem-player.png"));
        }
        catch (Exception ex)
        {
            check(false, "vmem player probe threw: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            try { player?.Close(); } catch { }
            Pump(300);
            Svc.Settings.SetVmemRenderer(old);
        }
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (var cur = child; cur != null; cur = VisualTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, ancestor)) return true;
        return false;
    }
}
