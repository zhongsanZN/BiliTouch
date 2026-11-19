using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Services;
using BiliTouch.Views;
using LibVLCSharp.Shared;

internal static class PopupLifetimeTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static MouseEventArgs MouseEvent(RoutedEvent routed) => new(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = routed };

    internal static void PointerReview(string artifacts)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Svc.Settings.SetWebPlayerLayout(false); Svc.Settings.SetVmemRenderer(false); Svc.Settings.SetPlayerVolume(0);
        using var playback = new PlaybackWindow();
        var player = playback.Window;
        player.Title = "音量悬停验收 · BiliTouch";
        var popup = (Popup)player.FindName("VolumePopup");
        var samples = new List<object>();
        var closed = false;
        player.Closed += (_, _) => closed = true;
        var end = Environment.TickCount64 + 240_000;
        while (!closed && Environment.TickCount64 < end)
        {
            samples.Add(new { time = DateTimeOffset.Now, open = popup.IsOpen, panelHover = popup.Child.IsMouseOver,
                captured = popup.Child.IsMouseCaptureWithin, controlsHidden = Field<bool>(player, "_controlsHidden") });
            System.IO.File.WriteAllText(System.IO.Path.Combine(artifacts, "popup-pointer-review.json"),
                System.Text.Json.JsonSerializer.Serialize(samples));
            Pump(150);
        }
    }

    internal static void Run(Action<bool, string> check)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var oldLayout = Svc.Settings.UseWebPlayerLayout;
        var oldVmem = Svc.Settings.UseVmemRenderer;
        var oldVolume = Svc.Settings.PlayerVolume;
        try
        {
            Svc.Settings.SetPlayerVolume(0);
            Svc.Settings.SetWebPlayerLayout(false);
            Svc.Settings.SetVmemRenderer(false);
            using var context = new PlaybackWindow();
            var player = context.Window;
            Exercise(player, "BtnVolume", "VolumeSlider", "OnVolumeHoverEnter", "OnVolumeHoverLeave", "OnVideoAreaMouseLeave", check, "VOD");
            var volumeSlider = (Slider)player.FindName("VolumeSlider");
            var volumeText = (TextBlock)player.FindName("VolumePopupText");
            var volumeIcon = (TextBlock)player.FindName("VolumeIcon");
            volumeSlider.Value = 42;
            check(volumeText.Text == "42%" && volumeIcon.Text == BiliTouch.Helpers.PlayerGlyphs.VolumeGlyph(false),
                "dragging volume updates its percentage and audible icon immediately");
            volumeSlider.Value = 0;
            check(volumeText.Text == "0%" && volumeIcon.Text == BiliTouch.Helpers.PlayerGlyphs.VolumeGlyph(true),
                "dragging volume to zero immediately updates the muted icon");
            player.GetType().GetField("_seekMuteSavedVolume", Hidden)!.SetValue(player, 80);
            Call(player, "SetVolume", 35, false);
            check(volumeText.Text == "35%" && Field<int>(player, "_seekMuteSavedVolume") == 35,
                "volume changes during a seek update both the restore target and visible percentage");
            player.GetType().GetField("_seekMuteSavedVolume", Hidden)!.SetValue(player, -1);
            volumeSlider.Value = 0;

            var speed = (Popup)player.FindName("SpeedPopup");
            Call(player, "OnSpeedClick", player, new RoutedEventArgs()); Pump(80);
            Call(player, "OnVideoAreaMouseLeave", player, MouseEvent(Mouse.MouseLeaveEvent));
            check(speed.IsOpen && !Field<bool>(player, "_controlsHidden"), "speed editor keeps controls visible across its native popup boundary");
            speed.IsOpen = false;

            Call(player, "OnVolumeHoverEnter", player, MouseEvent(Mouse.MouseEnterEvent));
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
            Call(player, "OnVideoAreaMouseLeftDown", player.FindName("VideoAreaRoot"), down);
            check(!((Popup)player.FindName("VolumePopup")).IsOpen && !Field<bool>(player, "_videoGestureActive"),
                "clicking the video dismisses volume without arming a playback gesture");
            Call(player, "OnVideoAreaMouseLeftUp", player.FindName("VideoAreaRoot"),
                new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            Pump(300);
            check(Field<LibVLCSharp.Shared.MediaPlayer>(player, "_mp").IsPlaying, "volume dismissal does not pause playback on the trailing mouse-up");

            Call(player, "OnVolumeHoverEnter", player, MouseEvent(Mouse.MouseEnterEvent));
            Call(player, "OnVolumeHoverLeave", player, MouseEvent(Mouse.MouseLeaveEvent));
            player.Close(); Pump(1000);
            check(Field<DispatcherTimer>(player, "_volumeCloseTimer").IsEnabled == false,
                "closing VOD releases its pending volume dismissal timer");

            var live = new LiveRoomWindow { Width = 1200, Height = 800, Left = 0, Top = 0, ShowActivated = false };
            try
            {
                live.Show(); Pump(200);
                var mp = Field<LibVLCSharp.Shared.MediaPlayer>(live, "_player");
                mp.Volume = 0;
                using var media = new Media(VlcCore.Get(), System.IO.Path.GetFullPath("artifacts/dock-color.mp4"), FromType.FromPath);
                mp.Play(media); Pump(700);
                ((FrameworkElement)live.FindName("PlaybackState")).Visibility = Visibility.Collapsed;
                Exercise(live, "MuteButton", "LiveVolume", "VolumeHoverEnter", "VolumeHoverLeave", "VideoMouseLeave", check, "live");
                Call(live, "VolumeHoverEnter", live, MouseEvent(Mouse.MouseEnterEvent));
                Call(live, "VolumeHoverLeave", live, MouseEvent(Mouse.MouseLeaveEvent));
                live.Close(); Pump(1000);
                check(!Field<DispatcherTimer>(live, "_volumeCloseTimer").IsEnabled, "closing live playback releases the volume dismissal timer");
            }
            finally { live.Close(); Pump(500); }
        }
        finally { Svc.Settings.SetWebPlayerLayout(oldLayout); Svc.Settings.SetVmemRenderer(oldVmem); Svc.Settings.SetPlayerVolume(oldVolume); }
    }

    private static void Exercise(Window window, string triggerName, string sliderName, string enter, string leave,
        string videoLeave, Action<bool, string> check, string label)
    {
        var popup = (Popup)window.FindName("VolumePopup");
        var trigger = (FrameworkElement)window.FindName(triggerName);
        var root = (Border)popup.Child;
        var slider = (Slider)window.FindName(sliderName);
        Call(window, enter, trigger, MouseEvent(Mouse.MouseEnterEvent)); Pump(60);
        check(popup.IsOpen, label + " hover opens volume");
        check(root.Background == System.Windows.Media.Brushes.Transparent && root.Padding.Bottom >= 8,
            label + " transparent hover bridge covers the former gap");
        var buttonTop = trigger.PointToScreen(new Point());
        var bridgeBottom = root.PointToScreen(new Point(0, root.ActualHeight));
        check(Math.Abs(bridgeBottom.Y - buttonTop.Y) <= 2,
            label + " popup hover bridge touches the button in screen coordinates");
        Call(window, leave, trigger, MouseEvent(Mouse.MouseLeaveEvent));
        Call(window, videoLeave, window, MouseEvent(Mouse.MouseLeaveEvent));
        Pump(60);
        check(popup.IsOpen && !Field<bool>(window, "_controlsHidden"), label + " entering the popup HWND does not hide its parent controls");

        root.RaiseEvent(MouseEvent(Mouse.MouseEnterEvent)); Pump(430);
        check(popup.IsOpen, label + " entering the panel cancels pending dismissal");
        root.RaiseEvent(MouseEvent(Mouse.MouseLeaveEvent)); Pump(180);
        root.RaiseEvent(MouseEvent(Mouse.MouseEnterEvent)); Pump(50);
        root.RaiseEvent(MouseEvent(Mouse.MouseLeaveEvent)); Pump(190);
        check(popup.IsOpen, label + " an older leave cannot close a newly entered-and-left panel early");
        UntilClosed(popup);
        check(!popup.IsOpen, label + " leaving the panel closes it after the latest grace interval");

        Call(window, enter, trigger, MouseEvent(Mouse.MouseEnterEvent)); Pump(50);
        check(slider.CaptureMouse(), label + " volume slider can capture a drag");
        root.RaiseEvent(MouseEvent(Mouse.MouseLeaveEvent)); Pump(430);
        check(popup.IsOpen, label + " dragging beyond the panel keeps volume open");
        slider.ReleaseMouseCapture(); UntilClosed(popup);
        check(!popup.IsOpen, label + " releasing an outside drag resumes dismissal");
    }

    private sealed class PlaybackWindow : IDisposable
    {
        internal readonly PlayerWindow Window = new() { Width = 1280, Height = 800, Left = 0, Top = 0, ShowActivated = false };
        internal PlaybackWindow()
        {
            Window.Show(); Pump(200);
            Window.PlayLocalFile(System.IO.Path.GetFullPath("artifacts/dock-color.mp4")); Pump(800);
            Field<LibVLCSharp.Shared.MediaPlayer>(Window, "_mp").Volume = 0;
        }
        public void Dispose() { Window.Close(); Pump(500); }
    }

    private static void UntilClosed(Popup popup)
    {
        // The early-close assertion above checks the grace boundary. Allow layout,
        // fade completion and Background-priority timer scheduling to settle here.
        var deadline = Environment.TickCount64 + 3000;
        while (popup.IsOpen && Environment.TickCount64 < deadline) Pump(20);
        if (popup.IsOpen)
            Console.WriteLine($"POPUP closing stalled: hover={popup.Child.IsMouseOver}, capture={popup.Child.IsMouseCaptureWithin}");
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
