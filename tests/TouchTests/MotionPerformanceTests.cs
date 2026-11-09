using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Views;

internal static class MotionPerformanceTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);

    public static void Run(Action<bool, string> check)
    {
        // Equal elapsed time must produce equal motion, independently of refresh rate.
        (double p, double v) Sample(int hz)
        {
            var p = 0.0; var v = 0.0;
            for (var i = 0; i < hz / 2; i++) (p, v) = SpringMotion.Advance(p, v, 1, 1.0 / hz, 24);
            return (p, v);
        }
        var reference = Sample(60);
        foreach (var hz in new[] { 30, 120, 144, 240 })
        {
            var sample = Sample(hz);
            check(Math.Abs(reference.p - sample.p) < 1e-10 && Math.Abs(reference.v - sample.v) < 1e-10,
                $"motion has equal position and velocity after 500ms at {hz}Hz and 60Hz");
        }
        var beforeReverse = SpringMotion.Advance(0, 0, 1, .08, 24);
        var afterReverse = SpringMotion.Advance(beforeReverse.Position, beforeReverse.Velocity, 0, .00001, 24);
        check(Math.Abs(afterReverse.Position - beforeReverse.Position) < .001 &&
            Math.Abs(afterReverse.Velocity - beforeReverse.Velocity) < .01,
            "reversing preserves position and velocity at contact");
        var settled = SpringMotion.Advance(afterReverse.Position, afterReverse.Velocity, 0, 1, 24);
        check(settled.Position >= 0 && settled.Position < .0001, "reversed motion settles within its endpoint bounds");

        TestFrameOwnership(check);
        TestDenseDanmaku(check);
        TestThemeClockCleanup(check);
        TestSeamlessContinuousMorph(check);
    }

    private static void TestSeamlessContinuousMorph(Action<bool, string> check)
    {
        var player = new PlayerWindow { Width = 1280, Height = 800, Topmost = true, ShowActivated = false };
        try
        {
            player.Show();
            Pump(250);

            var cardCover = new WriteableBitmap(16, 9, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
            var sourceButton = new Border { Width = 240, Height = 135 };
            SeamlessTransitionDirector.Prime("BV1seamless4", new Rect(80, 140, 240, 135), 8.0, cardCover, sourceButton);

            check(player.TryStartSeamlessEntry("BV1seamless4"), "seamless shared-element morph starts from primed card anchor");
            var overlay = (FrameworkElement)player.FindName("SeamlessMorphOverlay");
            var hero = (Border)player.FindName("SeamlessHeroCard");
            var loadingPoster = (Border)player.FindName("LoadingPoster");
            var dockPoster = (Border)player.FindName("DockTransitionPoster");

            check(overlay.Visibility == Visibility.Visible && player.IsSeamlessMorphRunning,
                "seamless overlay becomes visible while morphing");
            check(loadingPoster.Background is System.Windows.Media.ImageBrush && dockPoster.Background is System.Windows.Media.ImageBrush,
                "shared cover artwork populates both loading poster and dock transition card without re-decoding");

            // Step forward and verify continuous geometry + corner radius interpolation without overshoot.
            var initialRect = player.SeamlessCurrentRect;
            player.StepSeamlessMorph(0.06);
            var midRect = player.SeamlessCurrentRect;
            check(midRect.Width > initialRect.Width && midRect.Height > initialRect.Height &&
                  player.SeamlessMorphProgress > 0.1 && player.SeamlessMorphProgress < 0.99,
                $"seamless morph interpolates container geometry smoothly (w: {initialRect.Width:F1} -> {midRect.Width:F1})");

            // Interrupt mid-flight with a new target rect and verify zero-jump retargeting.
            var beforeInterrupt = player.SeamlessCurrentRect;
            var newTarget = new Rect(20, 44, 880, 495);
            check(player.StartSeamlessMorph(new Rect(0, 0, 100, 60), newTarget, 8.0, 0.0),
                "in-flight seamless morph accepts retargeting");
            var afterInterrupt = player.SeamlessCurrentRect;
            check(Math.Abs(afterInterrupt.X - beforeInterrupt.X) < 0.5 &&
                  Math.Abs(afterInterrupt.Width - beforeInterrupt.Width) < 0.5,
                "interrupting an in-flight morph preserves the instantaneous screen rectangle without jumping");

            for (var i = 0; i < 60 && player.IsSeamlessMorphRunning; i++)
                player.StepSeamlessMorph(1.0 / 60.0);

            check(!player.IsSeamlessMorphRunning && overlay.Visibility == Visibility.Collapsed,
                "settled seamless morph hides the overlay and detaches its composition clock");
        }
        finally
        {
            SeamlessTransitionDirector.Clear();
            player.Close();
            Pump(200);
        }
    }

    private static void TestThemeClockCleanup(Action<bool, string> check)
    {
        var type = typeof(ThemeTransition);
        var wait = type.GetMethod("WaitFrames", BindingFlags.Static | BindingFlags.NonPublic)!;
        var detach = type.GetMethod("DetachActive", BindingFlags.Static | BindingFlags.NonPublic)!;
        var field = type.GetField("_waitingFrame", BindingFlags.Static | BindingFlags.NonPublic)!;
        var completed = 0;
        System.Windows.Media.RenderingEventArgs Frame(double ms) =>
            (System.Windows.Media.RenderingEventArgs)Activator.CreateInstance(typeof(System.Windows.Media.RenderingEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { TimeSpan.FromMilliseconds(ms) }, null)!;
        wait.Invoke(null, new object[] { 2, (Action)(() => completed++) });
        try
        {
            var callback = (EventHandler)field.GetValue(null)!;
            callback(null, Frame(10));
            callback(null, Frame(10));
            check(completed == 0, "theme transition waits for distinct composition frames");
            callback(null, Frame(20));
            check(completed == 1 && field.GetValue(null) == null, "theme frame wait unsubscribes after completion");
            wait.Invoke(null, new object[] { 2, (Action)(() => completed++) });
            detach.Invoke(null, null);
            check(completed == 1 && field.GetValue(null) == null,
                "canceled theme motion releases its static rendering handler even without another frame");
        }
        finally { detach.Invoke(null, null); }
    }

    private sealed class Format : IDisposable
    {
        private readonly VmemVideoView _view;
        internal IntPtr Opaque, Pixels;
        internal readonly int Width, Height, Pitch, Lines;
        internal Format(VmemVideoView view, int width, int height)
        {
            _view = view; Width = width; Height = height;
            var chroma = Marshal.AllocHGlobal(4);
            var planes = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                object[] args = { IntPtr.Zero, chroma, (uint)width, (uint)height, 0u, 0u };
                var result = (uint)Call(view, "OnFormat", args)!;
                if (result != 1) throw new InvalidOperationException("format negotiation rejected test fixture");
                Opaque = (IntPtr)args[0]; Pitch = (int)(uint)args[4]; Lines = (int)(uint)args[5];
                Pixels = (IntPtr)Call(view, "OnLock", Opaque, planes)!;
            }
            finally { Marshal.FreeHGlobal(chroma); Marshal.FreeHGlobal(planes); }
        }
        internal void Send(int color)
        {
            for (var row = 0; row < Height; row++)
                for (var col = 0; col < Width; col++) Marshal.WriteInt32(Pixels, row * Pitch + col * 4, color);
            Call(_view, "OnDisplay", Opaque, Pixels);
        }
        public void Dispose()
        {
            if (Opaque == IntPtr.Zero) return;
            Call(_view, "OnCleanup", Opaque);
            Opaque = IntPtr.Zero;
        }
    }

    private static void TestFrameOwnership(Action<bool, string> check)
    {
        using var view = new VmemVideoView();
        using var oldFormat = new Format(view, 13, 7); // Non-aligned width tests row-stride conversion.
        check(oldFormat.Pixels.ToInt64() % 32 == 0 && oldFormat.Pitch % 32 == 0 && oldFormat.Lines % 32 == 0,
            "native planes, pitches and lines satisfy LibVLC alignment");
        oldFormat.Send(unchecked((int)0xff000011));
        oldFormat.Send(unchecked((int)0xff000022));
        oldFormat.Send(unchecked((int)0xff000033));
        Pump(40);
        var pixels = new byte[13 * 7 * 4];
        ((BitmapSource)view.Source).CopyPixels(pixels, 13 * 4, 0);
        check(pixels[0] == 0x33 && pixels[^4] == 0x33,
            "one queued upload presents the newest frame and copies padded rows correctly");
        check(view.CoalescedFrames == 2 && view.PresentedFrames == 1,
            "three queued decoder frames use one UI upload");

        oldFormat.Send(unchecked((int)0xff000044));
        using (var nextFormat = new Format(view, 19, 9))
        {
            nextFormat.Send(unchecked((int)0xff000055));
            oldFormat.Dispose(); // Old cleanup must not free/clear the new format's buffers.
            Pump(40);
            check(view.Source is BitmapSource bitmap && bitmap.PixelWidth == 19 && bitmap.PixelHeight == 9,
                "resolution change discards stale queued frames and retains the new format");
            check(view.VideoSize == (19u, 9u), "old native cleanup does not reset the new video dimensions");
        }

        var largeView = new VmemVideoView();
        using (var largeFormat = new Format(largeView, 3840, 2160))
        {
            largeView.Dispose();
            // A decoder lock after visual disposal still receives a full-sized, live buffer.
            var planes = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                var pointer = (IntPtr)Call(largeView, "OnLock", largeFormat.Opaque, planes)!;
                Marshal.WriteInt32(pointer, largeFormat.Pitch * largeFormat.Lines - 4, 0x12345678);
                check(Marshal.ReadInt32(pointer, largeFormat.Pitch * largeFormat.Lines - 4) == 0x12345678,
                    "4K decoder memory remains valid until native cleanup, including after visual disposal");
            }
            finally { Marshal.FreeHGlobal(planes); }
        }
        Pump(40);
        check(largeView.Source == null, "disposed video view cannot be revived by a queued upload");
    }

    private static void TestDenseDanmaku(Action<bool, string> check)
    {
        var player = new PlayerWindow { Width = 1200, Height = 800 };
        try
        {
            player.Show(); Pump(250);
            Call(player, "CalibrateDanmakuClock", 10d);
            Call(player, "SetDanmakuClockRunning", false);
            Call(player, "ResetLanes");
            for (var i = 0; i < 40; i++) Call(player, "AddDanmaku", "泳道预热", 1, 10d, 0xffffffu);
            var canvas = (Canvas)player.FindName("DanmakuCanvas");
            var count = canvas.Children.Count;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var add = (Action<string, int, double, uint>)player.GetType().GetMethod("AddDanmaku", Hidden)!
                .CreateDelegate(typeof(Action<string, int, double, uint>), player);
            for (var i = 0; i < 60_000; i++) add("满泳道不再创建控件", 1, 10, 0xffffff);
            watch.Stop();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            check(canvas.Children.Count == count, "a 60,000-item dense burst does not overlap occupied lanes");
            check(bytes < 4 * 1024 * 1024, $"full lanes avoid visual allocation: {bytes:N0} bytes for 60,000 candidates ({watch.Elapsed.TotalMilliseconds:F1}ms)");

            var fixture = System.IO.Path.GetFullPath("artifacts/dock-color.mp4");
            if (!System.IO.File.Exists(fixture)) throw new System.IO.FileNotFoundException("dense-dispatch playback fixture", fixture);
            player.PlayLocalFile(fixture); Pump(800);
            var media = (LibVLCSharp.Shared.MediaPlayer)player.GetType().GetField("_mp", Hidden)!.GetValue(player)!;
            media.Mute = true;
            check(media.IsPlaying && media.Length > 0, "dense-dispatch regression uses actual local playback");
            var now = media.Time / 1000.0;
            var timeline = (List<RawDanmaku>)player.GetType().GetField("_danmaku", Hidden)!.GetValue(player)!;
            timeline.Clear();
            for (var i = 0; i < 60_000; i++) timeline.Add(new RawDanmaku(now - .01, "同一时间的大量弹幕", 1));
            timeline.Add(new RawDanmaku(now + 60, "之后的弹幕", 1));
            player.GetType().GetField("_indexInLib", Hidden)!.SetValue(player, 0);
            player.GetType().GetField("_danmakuOn", Hidden)!.SetValue(player, true);
            watch.Restart(); Call(player, "DispatchDanmaku"); watch.Stop();
            check((int)player.GetType().GetField("_indexInLib", Hidden)!.GetValue(player)! == 60_000,
                $"dense dispatch skips overdue excess without replaying a backlog ({watch.Elapsed.TotalMilliseconds:F2}ms)");
            check(timeline.Count == 60_001 && timeline[^1].Time == now + 60,
                "dispatch budget retains the full seekable timeline and all future comments");
        }
        finally { player.Close(); Pump(250); }
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
