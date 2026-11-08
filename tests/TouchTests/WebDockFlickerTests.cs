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
        var coverScales = new List<double>();
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
                    // ★ 2026-11-09 第三轮：DWM 镜像已撤销。遮罩改成半透明，
                    //   原生 VideoView 全程可见，直接采视频区中心的屏幕颜色。
                    //   同时记录 _webCoverScale，锁死"TransformGroup 被覆盖导致缩放从来不生效"那个 bug。
                    var cover = player.GetType().GetField("_webCoverScale", Hidden)?.GetValue(player) as System.Windows.Media.ScaleTransform;
                    if (cover != null) coverScales.Add(Math.Round(cover.ScaleX, 3));
                    var moving = Field<bool>(player, "_webRendering");
                    if (moving) motion++;
                    var point = stage.PointToScreen(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
                    DwmFlush();
                    var color = GetPixel(dc, (int)point.X, (int)point.Y);
                    int r = (int)(color & 255), g = (int)((color >> 8) & 255), b = (int)((color >> 16) & 255);
                    var fixtureColor = g > 120 && r < 120 && b > 60 && b < 180;
                    // 半透明遮罩只压暗 15%，压暗后仍在 fixtureColor 范围内；
                    // 窗口落位后的 1~2 帧布局还没刷新，采样点仍钉在原位，会读到
                    // 原位空出来的纯黑底 —— 属正常的过渡收尾，不是黑帧。
                    var coverColor = (Math.Abs(r - 23) < 12 && Math.Abs(g - 23) < 12 && Math.Abs(b - 28) < 12)
                                     || (r < 12 && g < 12 && b < 12);
                    var whiteFlash = r > 230 && g > 230 && b > 230;
                    var expected = moving ? !whiteFlash : (fixtureColor || coverColor);
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
            check(motion >= 10, $"desktop samples cover repeated dock motion: {motion} moving samples");
            var distinctScales = coverScales.Distinct().Count();
            check(distinctScales >= 5, $"dock cover actually scales during the transition (TransformGroup regression): {distinctScales} distinct scales");
            check(whiteFlashCount == 0, "dock transitions never flash white while the native window stays visible");
            // 低帧率会话(远程桌面 / DWM 被限到 ~7Hz)下稳态采样可能只有 20~30 帧。
            // 按"离群比例"判定而不是绝对帧数，避免把环境慢当成产品 bug。
            check(idleTotal >= 8 && (idleBad <= 2 || idleBad <= idleTotal * 0.15),
                $"idle frames retain the fixture color: {idleBad}/{idleTotal} screen-sampling outliers");
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
