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
        int motion = 0, invalid = 0;
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
                    var transition = Field<object>(player, "_webFrameTransition");
                    var overlay = transition?.GetType().GetField("_window", Hidden)?.GetValue(transition) as Window;
                    if (overlay?.Content is Canvas canvas && canvas.Children[0] is Border picture && picture.IsArrangeValid && picture.ActualWidth > 0)
                    {
                        rect = picture.TransformToAncestor(canvas).TransformBounds(new Rect(0, 0, picture.Width, picture.Height));
                        motion++;
                    }
                    var point = stage.PointToScreen(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
                    DwmFlush();
                    var color = GetPixel(dc, (int)point.X, (int)point.Y);
                    int r = (int)(color & 255), g = (int)((color >> 8) & 255), b = (int)((color >> 16) & 255);
                    var expected = g > 120 && r < 120 && b > 60 && b < 180;
                    if (!expected) invalid++;
                    samples.Add(new { r, g, b, expected, moving = overlay != null, x=rect.X, y=rect.Y, width=rect.Width, height=rect.Height, progress=Field<double>(player,"_webDockProgress") });
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
            check(motion > 30, $"desktop samples cover both snapshot handoffs and repeated motion: {motion} moving samples");
            check(invalid == 0, $"displayed video center retains fixture color through six transitions: {samples.Count} samples, {invalid} unexpected");
            check(!Field<bool>(player, "_webFrameAnimating") && !Field<bool>(player, "_webFramePending"), "all transitions complete and release their temporary windows");
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
