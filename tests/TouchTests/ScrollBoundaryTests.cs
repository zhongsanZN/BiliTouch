using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;

internal static class ScrollBoundaryTests
{
    public static void Run(Action<bool, string> check)
    {
        var old = Svc.Settings.TouchpadFeedSensitivity;
        Svc.Settings.TouchpadFeedSensitivity = 100;
        var hit = new Border { Height = 20000 };
        var scroll = new ScrollViewer { Content = hit };
        var window = new Window { Content = scroll, Width = 700, Height = 600, ShowActivated = false };
        try
        {
            window.Show(); Pump(100);
            void Input(int delta) => hit.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = hit });
            void Frame() => typeof(TrackpadScroll).GetMethod("OnFrame", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [null, EventArgs.Empty]);

            // ScrollToVerticalOffset queues a layout command. Another input can arrive before it applies.
            // Note: these expectations deliberately derive from the live calibration constant,
            // not from a hard-coded 0.15 — else every recalibration of the touchpad scale
            // would look like a boundary regression.
            Input(-60); Frame();
            check(scroll.VerticalOffset == 0, "fixture retains an unapplied asynchronous scroll command");
            Input(-60); Frame();
            var expectedPending = 120 * TrackpadScroll.PrecisionPixelsPerDeltaUnit;
            Pump(400);
            Console.WriteLine($"BOUNDARY pending commands: expected={expectedPending:F2} actual={scroll.VerticalOffset:F2}");
            check(Math.Abs(scroll.VerticalOffset - expectedPending) < 1, "input arriving before scroll acknowledgement keeps both packet distances");

            TrackpadScroll.Cancel(scroll); scroll.ScrollToVerticalOffset(0); Pump(100);
            Input(-20); Pump(160); Input(-120); Pump(700);
            var expectedTailNotch = 140 * TrackpadScroll.PrecisionPixelsPerDeltaUnit;
            Console.WriteLine($"BOUNDARY slow tail: expected={expectedTailNotch:F2} actual={scroll.VerticalOffset:F2}");
            check(Math.Abs(scroll.VerticalOffset - expectedTailNotch) < 1, "a paused precision tail never changes to the wheel distance scale");

            TrackpadScroll.Cancel(scroll); scroll.ScrollToVerticalOffset(0); Pump(100);
            var tail = new[] { -60, -45, -30, -20, -15, -10, -8, -6, -4, -3, -2, -1 };
            double previous = 0;
            var monotonic = true;
            for (int i = 0; i < tail.Length; i++)
            {
                Input(tail[i]); Frame();
                // Include uneven delivery and a long pause near the end of OS inertia.
                Pump(i == 8 ? 160 : 16);
                monotonic &= scroll.VerticalOffset >= previous;
                previous = scroll.VerticalOffset;
            }
            Pump(400);
            var expectedTail = -tail.Sum() * TrackpadScroll.PrecisionPixelsPerDeltaUnit;
            Console.WriteLine($"BOUNDARY deceleration: expected={expectedTail:F2} actual={scroll.VerticalOffset:F2}");
            check(monotonic && Math.Abs(scroll.VerticalOffset - expectedTail) < 1,
                "decelerating finger-off input preserves distance through uneven frame delivery");
            var settled = scroll.VerticalOffset; Pump(200);
            check(Math.Abs(scroll.VerticalOffset - settled) < .01,
                "system inertia ends without an extra application easing tail");

            TrackpadScroll.Cancel(scroll); scroll.ScrollToVerticalOffset(0); Pump(100);
            for (int i = 0; i < 20; i++) { Input(-1); Frame(); scroll.UpdateLayout(); }
            Pump(400);
            check(Math.Abs(scroll.VerticalOffset - 20 * TrackpadScroll.PrecisionPixelsPerDeltaUnit) < 1,
                "fractional touchpad tail distance accumulates without a minimum jump");

            TrackpadScroll.Cancel(scroll); scroll.ScrollToVerticalOffset(0); Pump(100);
            Input(-60); Frame(); scroll.UpdateLayout();
            scroll.ScrollToVerticalOffset(500); scroll.UpdateLayout(); Frame(); Pump(100);
            check(Math.Abs(scroll.VerticalOffset - 500) < 1, "external programmatic navigation still takes over scrolling");
            var end = scroll.VerticalOffset; Pump(250);
            check(Math.Abs(scroll.VerticalOffset - end) < .01, "finger-off tail settles with no application-added drift");

            TrackpadScroll.Cancel(scroll); scroll.ScrollToVerticalOffset(0); Pump(100);
            Input(-60); Frame(); TrackpadScroll.Cancel(scroll); Pump(200);
            check(scroll.VerticalOffset == 0, "new contact cancels scroll commands already waiting for layout");
        }
        finally { window.Close(); Svc.Settings.TouchpadFeedSensitivity = old; }
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
