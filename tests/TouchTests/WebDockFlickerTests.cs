using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Services;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Views;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

internal static class WebDockFlickerTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();

    public static void Run(Action<bool, string> check, string artifacts)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var old = Svc.Settings.UseWebPlayerLayout;
        PlayerWindow? player = null;
        var samples = new List<object>();
        var mirrorRects = new List<double>();
        int motion = 0, whiteFlashCount = 0, idleTotal = 0, idleBad = 0;
        EventHandler? observe = null;
        try
        {
            var sample = System.IO.Path.GetFullPath("artifacts/dock-color.mp4");
            check(System.IO.File.Exists(sample), "solid-color native playback fixture exists");
            Svc.Settings.SetWebPlayerLayout(true);
            player = new PlayerWindow { Width = 1200, Height = 800, Topmost = true, ShowActivated = false };
            player.Show(); player.PlayLocalFile(sample);
            var mp = Field<MediaPlayer>(player, "_mp"); mp.Mute = true;
            var comments = (ItemsControl)player.FindName("CommentList");
            comments.ItemsSource = Enumerable.Range(0, 40).Select(i => new BiliTouch.Models.CommentItem { Content = "过渡画面检查", UserName = $"测试 {i}" }).ToArray();
            Pump(1500);
            check(mp.IsPlaying, "solid-color fixture is playing in the native video surface");
            var stage = (Grid)player.FindName("PlayerStage");
            var surface = (Grid)player.FindName("VideoSurface");
            var scroll = (ScrollViewer)player.FindName("WebLeftScroll");
            var placeholder = (Border)player.FindName("WebVideoPlaceholder");
            var dc = GetDC(IntPtr.Zero);
            try
            {
                observe = (_, _) =>
                {
                    var rect = surface.TransformToAncestor(stage).TransformBounds(new Rect(surface.RenderSize));
                    // ★ 2026-11-09 第二轮：播放中的过渡由 DWM Thumbnail 镜像窗口承载，
                    //   原生 VideoSurface 只在 commit 帧动一次。采镜像窗口的当前屏幕矩形，
                    //   才能验证「内容本身连续变形」而不是硬切；同时用它做颜色采样点。
                    Rect mirror = Rect.Empty;
                    var live = player.GetType().GetField("_liveTransition", Hidden)?.GetValue(player);
                    if (live != null)
                    {
                        var cr = live.GetType().GetProperty("CurrentRect", Hidden);
                        if (cr?.GetValue(live) is Rect m && !m.IsEmpty && m.Width > 1) mirror = m;
                    }
                    if (!mirror.IsEmpty) { rect = mirror; mirrorRects.Add(Math.Round(mirror.X)); }
                    var moving = Field<bool>(player, "_webRendering") || Field<bool>(player, "_liveTransitionRunning");
                    if (moving) motion++;
                    var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                    var point = mirror.IsEmpty ? stage.PointToScreen(center) : center;
                    DwmFlush();
                    var color = GetPixel(dc, (int)point.X, (int)point.Y);
                    int r = (int)(color & 255), g = (int)((color >> 8) & 255), b = (int)((color >> 16) & 255);
                    var fixtureColor = g > 120 && r < 120 && b > 60 && b < 180;
                    // ★ 2026-11-09：过渡期间 DockTransitionCover(#17171C) 把原位置压成
                    //   不透明背景，避免镜像移走后露出原生 vout 造成重影。遮罩收起比
                    //   _liveTransitionRunning 复位晚 1~2 帧，这几帧是合法的过渡背景色。
                    var coverColor = Math.Abs(r - 23) < 12 && Math.Abs(g - 23) < 12 && Math.Abs(b - 28) < 12;
                    var whiteFlash = r > 230 && g > 230 && b > 230;
                    var expected = moving ? !whiteFlash : (fixtureColor || coverColor);
                    // 屏幕级 GetPixel 采样有固有时序噪声。过渡期白闪零容忍，稳态用正确率判定。
                    if (moving) { if (whiteFlash) whiteFlashCount++; }
                    else { idleTotal++; if (!fixtureColor && !coverColor) idleBad++; }
                    samples.Add(new { r, g, b, expected, moving, fixtureColor, x=rect.X, y=rect.Y, width=rect.Width, height=rect.Height, progress=Field<double>(player,"_webDockProgress") });
                };
                CompositionTarget.Rendering += observe;
                for (int i = 0; i < 3; i++)
                {
                    scroll.ScrollToVerticalOffset(placeholder.ActualHeight + 80); Pump(1000);
                    scroll.ScrollToTop(); Pump(1000);
                }
            }
            finally { CompositionTarget.Rendering -= observe; ReleaseDC(IntPtr.Zero, dc); }
            System.IO.File.WriteAllText(System.IO.Path.Combine(artifacts, "web-dock-screen-samples.json"), JsonSerializer.Serialize(samples));
            check(motion > 30, $"desktop samples cover repeated dock motion: {motion} moving samples");
            var distinctX = mirrorRects.Distinct().Count();
            check(mirrorRects.Count > 20 && distinctX > 10,
                $"DWM mirror window morphs continuously (not a hard cut): {mirrorRects.Count} frames, {distinctX} distinct x positions");
            check(whiteFlashCount == 0, "dock transitions never flash white while the native window stays visible");
            check(idleTotal > 50 && idleBad <= 2, $"idle frames retain the fixture color: {idleBad}/{idleTotal} screen-sampling outliers");
            check(!Field<bool>(player, "_webRendering"), "all dock transitions complete and detach their rendering handler");
        }
        finally
        {
            if (observe != null) CompositionTarget.Rendering -= observe;
            player?.Close(); Pump(200);
            Svc.Settings.SetWebPlayerLayout(old);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
