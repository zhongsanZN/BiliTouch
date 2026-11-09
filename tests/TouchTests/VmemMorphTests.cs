using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Views;

/// <summary>
/// 验证 vmem 模式下的停靠动画是**画面本体变形**，不是玻璃片：
///  - VmemView 自己的 RenderTransform.ScaleX 连续变小（多个中间值）；
///  - 期间 DockTransitionCover 是 Collapsed（不再用假卡片）；
///  - 落位后进度到 1，且 VmemView 有圆角裁剪。
/// </summary>
internal static class VmemMorphTests
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
        if (clip == null) { check(true, "vmem morph probe skipped without a local clip"); return; }

        var oldVmem = Svc.Settings.UseVmemRenderer;
        var oldLayout = Svc.Settings.UseWebPlayerLayout;
        var oldScale = Svc.Settings.UiScalePercent;
        PlayerWindow? player = null;
        try
        {
            Svc.Settings.SetVmemRenderer(true);
            Svc.Settings.SetWebPlayerLayout(true);
            Svc.Settings.SetUiScalePercent(110);

            player = new PlayerWindow { Width = 1320, Height = 860, Topmost = true, ShowActivated = false };
            player.Show();
            Pump(500);
            player.PlayLocalFile(clip);
            Pump(2200);

            var comments = (ItemsControl)player.FindName("CommentList");
            comments.ItemsSource = Enumerable.Range(0, 50).Select(i => new BiliTouch.Models.CommentItem
            { Content = "vmem 变形检查", UserName = $"测试 {i}" }).ToArray();
            Pump(400);

            var scroll = (ScrollViewer)player.FindName("WebLeftScroll");
            var placeholder = (Border)player.FindName("WebVideoPlaceholder");
            var cover = (FrameworkElement)player.FindName("DockTransitionCover");
            var vmem = (FrameworkElement)player.FindName("VmemView");

            var scales = new List<double>();
            var coverVisibleDuringMorph = false;
            var trackingSamples = 0;
            EventHandler onFrame = (_, _) =>
            {
                var animating = Field<bool>(player, "_webRendering");
                var tracking = Field<bool>(player, "_vmemTracking");
                if (!animating && !tracking) return;
                if (tracking && !animating) trackingSamples++;
                if (vmem.RenderTransform is TransformGroup g && g.Children.Count > 0 && g.Children[0] is ScaleTransform st)
                    scales.Add(Math.Round(st.ScaleX, 3));
                if (cover.Visibility == Visibility.Visible) coverVisibleDuringMorph = true;
            };
            CompositionTarget.Rendering += onFrame;
            scroll.ScrollToVerticalOffset(placeholder.ActualHeight + 100);
            // 动画约 420ms：分别在 ~35% / ~65% 处截一张，作为"画面本体正在变形"的直观证据。
            Pump(150);
            SaveWindow(player, artifacts, "vmem-morph-mid35.png");
            Pump(120);
            SaveWindow(player, artifacts, "vmem-morph-mid65.png");
            Pump(1400);
            CompositionTarget.Rendering -= onFrame;

            var distinct = scales.Distinct().Where(v => v < 0.995).Count();
            var min = scales.Count > 0 ? scales.Min() : 1;
            check(distinct >= 4, $"video body scales continuously (not a hard cut): {distinct} distinct intermediate scales");
            check(min <= 0.55, $"video body actually shrinks toward the dock: min scale {min:F3}");
            check(!coverVisibleDuringMorph, "the glass cover stays collapsed in vmem mode (no fake card)");
            check(trackingSamples >= 3, $"scroll drives the morph directly (finger-tracking): {trackingSamples} tracking frames");
            check(Math.Abs(Field<double>(player, "_webDockProgress") - 1) < 0.02,
                $"docked progress settles at 1: {Field<double>(player, "_webDockProgress"):F3}");
            check(vmem.Clip is RectangleGeometry, "docked video keeps rounded-corner clipping");

            player.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)player.ActualWidth, (int)player.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(player);
            var dir = Path.Combine(artifacts, "vmem-morph");
            Directory.CreateDirectory(dir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(Path.Combine(dir, "vmem-morph-docked.png"))) enc.Save(fs);
            Console.WriteLine("VMEMMORPH saved " + Path.Combine(dir, "vmem-morph-docked.png"));
            Console.WriteLine($"VMEMMORPH scales={string.Join(",", scales.Distinct().Take(12))}");
        }
        catch (Exception ex)
        {
            check(false, "vmem morph probe threw: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            try { player?.Close(); } catch { }
            Pump(300);
            Svc.Settings.SetVmemRenderer(oldVmem);
            Svc.Settings.SetWebPlayerLayout(oldLayout);
            Svc.Settings.SetUiScalePercent(oldScale);
        }
    }

    private static void SaveWindow(Window w, string artifacts, string name)
    {
        w.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(w);
        var dir = Path.Combine(artifacts, "vmem-morph");
        Directory.CreateDirectory(dir);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, name));
        enc.Save(fs);
        Console.WriteLine("VMEMMORPH saved " + name);
    }
}
