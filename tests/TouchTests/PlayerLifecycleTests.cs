using System.Reflection;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Services;
using BiliTouch.Views;

internal static class PlayerLifecycleTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static LibVLCSharp.Shared.MediaPlayer Media(PlayerWindow window) =>
        (LibVLCSharp.Shared.MediaPlayer)typeof(PlayerWindow).GetField("_mp", Hidden)!.GetValue(window)!;
    internal static void Run(Action<bool, string> check)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var oldVolume = Svc.Settings.PlayerVolume;
        Svc.Settings.SetPlayerVolume(0);
        var first = new PlayerWindow { Width = 1000, Height = 700 };
        var second = new PlayerWindow { Width = 1100, Height = 760 };
        var firstClosed = false; var secondClosed = false;
        first.Closed += (_, _) => firstClosed = true;
        second.Closed += (_, _) => secondClosed = true;
        try
        {
            var clip = System.IO.Path.GetFullPath("artifacts/dock-color.mp4");
            first.Show(); first.PlayLocalFile(clip);
            second.Show(); second.PlayLocalFile(clip); Pump(900);
            check(Media(first).IsPlaying && Media(second).IsPlaying && VlcCore.HasActivePlayers,
                "two independent players are registered and playing");
            var before = Media(second).Time;
            first.Close(); Until(() => firstClosed);
            Pump(1200); // Includes the delayed memory-trim window after the first player closes.
            check(VlcCore.HasActivePlayers && Media(second).IsPlaying && Media(second).Time > before + 500,
                "closing and trimming one player leaves the other player advancing");
            var surface = (System.Windows.FrameworkElement)second.FindName("VideoSurface");
            check(surface.ActualWidth > 0 && surface.ActualHeight > 0,
                "surviving player retains a nonzero video surface after peer teardown");
            second.Close(); Until(() => secondClosed);
            check(!VlcCore.HasActivePlayers, "closing the last player releases its lifetime registration");
        }
        finally { first.Close(); second.Close(); Pump(400); Svc.Settings.SetPlayerVolume(oldVolume); }
    }
    private static void Until(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 8000;
        while (!condition() && Environment.TickCount64 < deadline) Pump(40);
        if (!condition()) throw new TimeoutException("Player did not close");
    }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
