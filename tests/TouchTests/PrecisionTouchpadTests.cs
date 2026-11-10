using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.ViewModels;
using BiliTouch.Views.Pages;

internal static class PrecisionTouchpadTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> ready, int timeout = 1200)
    {
        var start = Environment.TickCount64;
        while (!ready())
        {
            if (Environment.TickCount64 - start > timeout) break;
            Pump(20);
        }
    }
    internal static void Run(Action<bool, string> check)
    {
        check(Marshal.SizeOf(typeof(PrecisionTouchpadInput).GetNestedType("PointerInfo", BindingFlags.NonPublic)!) == 96,
            "physical touchpad POINTER_INFO matches the native x64 layout");
        check(Marshal.SizeOf(typeof(PrecisionTouchpadInput).GetNestedType("TouchInfo", BindingFlags.NonPublic)!) == 144,
            "physical touchpad contact frames match the native x64 layout");
        var motion = new PrecisionTouchpadInput.ContactMotion();
        void Start()
        {
            motion.Reset(10000);
            motion.Update(1, 7000, 2000, true); motion.Update(2, 7200, 3000, true);
        }
        Start(); motion.Update(1, 6000, 2000, true); motion.Update(2, 6200, 3000, true);
        check(Math.Abs(motion.Displacement.x + .1) < 1e-6, "short physical movement stays at ten percent regardless of packet speed");
        motion.Update(1, 6000, 2000, false);
        check(Math.Abs(motion.Displacement.x + .1) < 1e-6, "lifting one finger does not jump the contact centroid");
        var lifetime = new PrecisionTouchpadInput.ContactLifetime();
        check(lifetime.Update(true, 2), "two contacts start one physical session");
        check(!lifetime.Update(false, 1) && !lifetime.Update(true, 2), "lifting and replacing one finger cannot start another session while the other remains down");
        check(!lifetime.Update(false, 0) && lifetime.Update(true, 2), "a second physical session requires every previous finger to lift");

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var main = new MainWindow(); main.Show(); App.MainVm.Navigate(PageKey.Home); Pump(300);
        try
        {
            var input = (PrecisionTouchpadInput)typeof(MainWindow).GetField("_precisionTouchpad", Hidden)!.GetValue(main)!;
            check(input.Registered, "this machine accepts the physical touchpad window registration");
            int cancelled = 0;
            typeof(PrecisionTouchpadInput).GetField("_target", Hidden)!.SetValue(input,
                new PrecisionTouchpadInput.Target(() => { }, _ => { }, committed => { if (!committed) cancelled++; }));
            typeof(PrecisionTouchpadInput).GetField("_eligible", Hidden)!.SetValue(input, true);
            typeof(PrecisionTouchpadInput).GetField("_claimed", Hidden)!.SetValue(input, true);
            typeof(PrecisionTouchpadInput).GetField("_ended", Hidden)!.SetValue(input, false);
            object[] failedPacket = { IntPtr.Zero, 0x245, new IntPtr(65535), IntPtr.Zero, false };
            typeof(PrecisionTouchpadInput).GetMethod("WndProc", Hidden)!.Invoke(input, failedPacket);
            check(cancelled == 1 && Equals(failedPacket[4], true)
                && !(bool)typeof(PrecisionTouchpadInput).GetField("_eligible", Hidden)!.GetValue(input)!
                && input.SuppressHorizontalWheel,
                "an unreadable native pointer cancels once, releases the contact and drains its generated wheel tail");
            input.Cancel();
            check(cancelled == 1, "cleanup cannot end the failed native contact twice");
            var home = (HomePage)typeof(MainWindow).GetMethod("PageOf", Hidden)!.Invoke(main, new object[] { PageKey.Home })!;
            var popular = (RadioButton)home.FindName("PopularTab");
            popular.IsChecked = true; Pump(220);
            foreach (var progress in new[] { 1d, 12d, -12d, 20d, -.8d, .9d })
            {
                home.PreviewHorizontalWheel(progress); Pump(16);
                check(home.CurrentTabIndex == 1, "unbounded displacement and reversals never commit while the same session remains held");
            }
            home.SettlePrecisionTouchpadPreview(true); Until(() => home.CurrentTabIndex == 2);
            check(home.CurrentTabIndex == 2, "one held session commits at most its original adjacent page");
            foreach (var packets in new[] { 1, 12 })
            foreach (var distance in new[] { .1, .35, .40 })
            {
                popular.IsChecked = true;
                Until(() => home.CurrentTabIndex == 1 && Math.Abs(((TranslateTransform)home.TabHost.RenderTransform).X) < 1);
                Start();
                for (int k = 1; k <= packets; k++)
                {
                    motion.Update(1, 7000 - distance * 10000 * k / packets, 2000, true);
                    motion.Update(2, 7200 - distance * 10000 * k / packets, 3000, true);
                    home.PreviewHorizontalWheel(-motion.Displacement.x);
                    if (packets > 1) Pump(16);
                }
                check(home.CurrentTabIndex == 1, "physical contact does not commit a tab before lift");
                home.SettlePrecisionTouchpadPreview(true);
                var expected = distance >= HomePage.SwipePageRatio ? 2 : 1;
                Until(() => home.CurrentTabIndex == expected && Math.Abs(((TranslateTransform)home.TabHost.RenderTransform).X) < 1);
                check(home.CurrentTabIndex == expected,
                    $"{packets}-packet physical swipe of {distance:P0} obeys distance rather than speed");
            }
            popular.IsChecked = true; Until(() => home.CurrentTabIndex == 1);
            home.PreviewHorizontalWheel(.7); home.SettlePrecisionTouchpadPreview(true);
            // Reverse before the old settle has moved. The previous target is
            // preserved, but a new short reverse cannot borrow its old offset.
            home.PreviewHorizontalWheel(-.1); home.SettlePrecisionTouchpadPreview(true);
            Until(() => home.CurrentTabIndex == 2);
            check(home.CurrentTabIndex == 2, "short reverse contact cannot borrow an unfinished animation to trigger a page change");
            home.PreviewHorizontalWheel(-.7); home.SettlePrecisionTouchpadPreview(true);
            Until(() => home.CurrentTabIndex == 1);
            check(home.CurrentTabIndex == 1, "over-threshold reverse contact returns exactly one page");
            home.PreviewHorizontalWheel(.8); home.SettlePrecisionTouchpadPreview(false);
            Pump(280);
            check(home.CurrentTabIndex == 1, "cancelled physical contact cannot commit a page");
        }
        finally { App.IsExiting = true; main.Close(); App.IsExiting = false; }
    }
}
