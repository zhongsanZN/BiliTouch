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
            var surface = (FrameworkElement)player.FindName("VideoSurface");

            var scales = new List<double>();
            var coverVisibleDuringMorph = false;
            var trackingSamples = 0;
            EventHandler onFrame = (_, _) =>
            {
                var animating = Field<bool>(player, "_webRendering");
                var tracking = Field<bool>(player, "_vmemTracking");
                if (!animating && !tracking) return;
                if (tracking && !animating) trackingSamples++;
                if (surface.RenderTransform is TransformGroup g && g.Children.Count > 0 && g.Children[0] is ScaleTransform st)
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
            check(surface.Clip is RectangleGeometry, "docked video keeps rounded-corner clipping");
            check(ReferenceEquals(((Border)player.FindName("WebDockAnchor")).Background, Brushes.Transparent),
                "moving video leaves no opaque black placeholder behind in the right rail");
            SaveWindow(player, artifacts, "vmem-morph-docked.png");

            var stage = (FrameworkElement)player.FindName("PlayerStage");
            var area = (FrameworkElement)player.FindName("VideoAreaRoot");
            var danmaku = (FrameworkElement)player.FindName("DanmakuCanvas");
            var maxError = 0.0;
            var returnScale = 1.0;
            var overlap = false;
            var clipReused = true;
            var danmakuVisible = true;
            RectangleGeometry? clipInstance = null;
            var returnFrames = 0;
            EventHandler watchReturn = (_, _) =>
            {
                var tracking = Field<bool>(player, "_vmemTracking");
                var animating = Field<bool>(player, "_webRendering");
                overlap |= tracking && animating;
                if ((!tracking && !animating) || !Field<bool>(player, "_vmemMorphPrepared")) return;
                returnFrames++;
                var p = Field<double>(player, "_webDockProgress");
                var inline = Field<Rect>(player, "_vmemInlineBounds");
                var dock = Field<Rect>(player, "_vmemDockBounds");
                var actual = surface.TransformToAncestor(stage).TransformBounds(new Rect(surface.RenderSize));
                var expected = new Rect(inline.X + (dock.X - inline.X) * p, inline.Y + (dock.Y - inline.Y) * p,
                    inline.Width + (dock.Width - inline.Width) * p, inline.Height + (dock.Height - inline.Height) * p);
                var error = new[] { Math.Abs(actual.X - expected.X), Math.Abs(actual.Y - expected.Y),
                    Math.Abs(actual.Width - expected.Width), Math.Abs(actual.Height - expected.Height) }.Max();
                if (error > maxError + .1)
                    Console.WriteLine($"MORPH GEOMETRY p={p:F5} actual={actual} expected={expected} source={Field<Rect>(player, "_vmemMorphSource")} size={vmem.RenderSize} tracking={tracking} animation={animating}");
                maxError = Math.Max(maxError, error);
                if (surface.RenderTransform is TransformGroup g && g.Children[0] is ScaleTransform scale)
                    returnScale = Math.Max(returnScale, scale.ScaleX);
                if (surface.Clip is RectangleGeometry clip)
                {
                    clipInstance ??= clip;
                    clipReused &= ReferenceEquals(clipInstance, clip);
                }
                danmakuVisible &= danmaku.Visibility == Visibility.Visible;
            };
            CompositionTarget.Rendering += watchReturn;
            try
            {
                scroll.ScrollToTop(); Pump(70);
                SaveWindow(player, artifacts, "vmem-morph-return.png");
                // Interrupt the return with another dock gesture before it has settled.
                scroll.ScrollToVerticalOffset(placeholder.ActualHeight + 100); Pump(60);
                Settle(player);
                check(Math.Abs(Field<double>(player, "_webDockProgress") - 1) < .001,
                    "a reversed gesture returns to the dock without competing animation drivers");
                scroll.ScrollToTop(); Pump(60); Settle(player);
            }
            finally { CompositionTarget.Rendering -= watchReturn; }
            check(returnFrames >= 3 && returnScale > 1.2, $"small-to-large expands the actual video before landing ({returnFrames} frames, max scale {returnScale:F3})");
            check(maxError < 1.5, $"both directions follow the canonical endpoint rectangles (max error {maxError:F3} DIP)");
            check(!overlap, "finger tracking and automatic settling never write the same frame");
            check(clipReused, "rounded clipping reuses one geometry across frames and interruptions");
            check(danmakuVisible && area.IsDescendantOf(surface) && vmem.IsDescendantOf(surface),
                "danmaku and brightness layers remain visible and travel with the video");
            check(surface.Clip == null && Math.Abs(Field<double>(player, "_webDockProgress")) < .001,
                "returning inline clears rounded clipping and settles at zero");
            check(!Field<bool>(player, "_webFrameAttached"), "settled motion releases its static composition subscription");

            scroll.ScrollToVerticalOffset(placeholder.ActualHeight + 100); Pump(60);
            player.Width = 1180; Pump(80); Settle(player);
            check(surface.ActualWidth > 0 && surface.ActualHeight > 0 && !Field<bool>(player, "_webFrameAttached"),
                "resizing during motion commits a valid rectangle and releases the clock");
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight); Pump(60); Settle(player);
            Svc.Settings.SetUiScalePercent(150); Pump(400); Settle(player);
            check(surface.Clip is RectangleGeometry radius && Math.Abs(radius.Rect.Width - surface.ActualWidth) < .1 &&
                Math.Abs(radius.Rect.Height - surface.ActualHeight) < .1,
                "docked clipping follows actual video dimensions after UI scaling");
            SaveWindow(player, artifacts, "vmem-morph-scaled.png");
            scroll.ScrollToTop(); Pump(60);
            player.WindowState = WindowState.Minimized; Pump(100);
            check(!Field<bool>(player, "_webFrameAttached"), "minimizing mid-gesture releases the composition clock");
            player.WindowState = WindowState.Normal; Pump(400);
            check(surface.ActualWidth > 0 && surface.ActualHeight > 0,
                "restoring a minimized transition keeps a nonzero video surface");
            Svc.Settings.SetWebPlayerLayout(false); Pump(400);
            check(surface.Clip == null && !Field<bool>(player, "_webFrameAttached"),
                "leaving web layout restores full-stage presentation and stops motion");

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

    private static void Settle(PlayerWindow player)
    {
        var deadline = Environment.TickCount64 + 8000;
        while ((Field<bool>(player, "_vmemTracking") || Field<bool>(player, "_webRendering")) && Environment.TickCount64 < deadline)
            Pump(30);
        if (Field<bool>(player, "_vmemTracking") || Field<bool>(player, "_webRendering"))
            throw new TimeoutException("vmem morph did not release its animation clock");
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
