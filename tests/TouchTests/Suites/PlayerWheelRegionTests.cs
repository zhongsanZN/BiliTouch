using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.Views;
using LibVLCSharp.Shared;

internal static class PlayerWheelRegionTests
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private static void Wheel(Window window, int delta, Point point)
    {
        var packed = ((long)(ushort)(short)point.Y << 16) | (ushort)(short)point.X;
        SendMessage(new WindowInteropHelper(window).Handle, 0x020E, new IntPtr((long)(ushort)(short)delta << 16), new IntPtr(packed));
    }
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
    public static void Run(Action<bool, string> check)
    {
        var media = Path.GetFullPath("../artifacts/vmem-probe/motion-1080.mp4");
        if (!File.Exists(media)) throw new FileNotFoundException("Offline playback fixture missing", media);
        bool oldVmem = Svc.Settings.UseVmemRenderer, oldWeb = Svc.Settings.UseWebPlayerLayout;
        try
        {
            foreach (bool vmem in new[] { false, true })
            foreach (bool web in new[] { false, true })
            {
                Svc.Settings.SetVmemRenderer(vmem); Svc.Settings.SetWebPlayerLayout(web);
                var player = new PlayerWindow { Width = 1100, Height = 760 };
                try
                {
                    player.Show(); player.PlayLocalFile(media); Pump(1400);
                    var mp = Field<MediaPlayer>(player, "_mp"); mp.Mute = true; mp.SetPause(true); Pump(100);
                    var surface = (FrameworkElement)player.FindName("VideoSurface");
                    var info = (FrameworkElement)player.FindName("InfoPanel");
                    var intro = (RadioButton)player.FindName("TabIntro");
                    var videoPoint = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
                    var infoPoint = info.PointToScreen(new Point(info.ActualWidth / 2, info.ActualHeight - 30));
                    var start = mp.Time;
                    var wheelWindow = (Window)player;
                    if (!vmem)
                    {
                        var videoView = player.FindName("VideoView");
                        wheelWindow = videoView.GetType().GetProperty("ForegroundWindow", Hidden)?.GetValue(videoView) as Window ?? player;
                    }
                    Wheel(wheelWindow, -100, videoPoint);
                    Console.WriteLine($"REGION first={typeof(PlayerWindow).GetField("_horizontalWheelRegion", Hidden)!.GetValue(player)} overlay={wheelWindow.GetType().Name} handle={new WindowInteropHelper(wheelWindow).Handle} hook={Field<HwndSource?>(player, "_overlayWheelSource") != null}");
                    // Moving the pointer across the rail does not change an ongoing gesture's owner.
                    Wheel(player, -100, infoPoint);
                    check(intro.IsChecked == true && !Field<bool>(player, "_infoDraggingPage"),
                        $"{vmem}/{web}: video wheel never starts an info-page drag");
                    Pump(500);
                    check(mp.Time > start + 1000 && intro.IsChecked == true,
                        $"{vmem}/{web}: video wheel seeks playback once and keeps the info tab");

                    var top = (FrameworkElement)player.FindName("TopBar");
                    Wheel(player, 300, top.PointToScreen(new Point(100, 10))); Pump(400);
                    check(intro.IsChecked == true && Field<int>(player, "_pageWheelAccum") == 0,
                        $"{vmem}/{web}: title-bar wheel does not switch the info panel");

                    Wheel(player, 240, infoPoint); Pump(420);
                    check(intro.IsChecked != true, $"{vmem}/{web}: right-rail wheel still switches its own tabs");

                    var infoTarget = player.ResolvePrecisionTouchpadTarget(infoPoint)!;
                    check(infoTarget.Name == "Player.Info", $"{vmem}/{web}: physical contact resolves to the right rail only");
                    infoTarget.Begin(); infoTarget.Preview(-.1); infoTarget.End(true); Pump(240);
                    check(intro.IsChecked != true, $"{vmem}/{web}: short physical rail swipe cannot use wheel acceleration to change tabs");
                    infoTarget.Begin(); infoTarget.Preview(-.7); infoTarget.End(true); Pump(25);
                    infoTarget.Begin(); infoTarget.Preview(.1); infoTarget.End(true); Pump(250);
                    check(intro.IsChecked == true, $"{vmem}/{web}: short takeover preserves the previous valid rail release");
                    var videoTarget = player.ResolvePrecisionTouchpadTarget(videoPoint)!;
                    check(videoTarget.Name == "Player.Video", $"{vmem}/{web}: video surface has its own physical contact owner");
                    mp.Time = 1000; Pump(150);
                    var beforePhysicalSeek = mp.Time;
                    videoTarget.Begin(); videoTarget.Preview(-.2); videoTarget.End(true); Pump(400);
                    check(mp.Time > beforePhysicalSeek + 500 && intro.IsChecked == true,
                        $"{vmem}/{web}: physical video swipe seeks once without changing the right rail");
                    check(player.ResolvePrecisionTouchpadTarget(top.PointToScreen(new Point(100, 10))) == null,
                        $"{vmem}/{web}: physical contact on the title bar has no page owner");

                    // A live vmem frame must remain writable while close snapshots are frozen.
                    if (vmem)
                    {
                        var frame = ((VmemVideoView)player.FindName("VmemView")).Source as System.Windows.Media.Imaging.WriteableBitmap;
                        var source = new Window { Width = 400, Height = 300, Content = new Border { Width = 160, Height = 90 } };
                        source.Show(); Pump(50);
                        SeamlessTransitionDirector.Capture((FrameworkElement)source.Content, "BVcloseRegionTest");
                        check(player.TryBeginSeamlessClose() && frame is { IsFrozen: false },
                            $"{vmem}/{web}: closing snapshots do not freeze the live decoder bitmap");
                        source.Close();
                    }
                }
                finally { player.Close(); Pump(500); SeamlessTransitionDirector.Clear(); }
            }
        }
        finally { Svc.Settings.SetVmemRenderer(oldVmem); Svc.Settings.SetWebPlayerLayout(oldWeb); }
    }
}
