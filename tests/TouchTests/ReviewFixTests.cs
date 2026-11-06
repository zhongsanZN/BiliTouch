using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Views;
using BiliTouch.Models;
internal static class ReviewFixTests
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    static void Pump(int ms) { var f = new DispatcherFrame(); var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; t.Tick += (_, _) => { t.Stop(); f.Continue = false; }; t.Start(); Dispatcher.PushFrame(f); }
    public static void Run(Action<bool,string> check)
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var win = new PlayerWindow { Width = 1280, Height = 800 }; win.Show(); Pump(150);
        try {
            var popup = (Popup)win.FindName("QualityPopup");
            check(popup.Placement == PlacementMode.Custom && popup.PlacementTarget == win.FindName("BtnQuality"), "quality panel uses an explicit target and custom placement");
            var positions = popup.CustomPopupPlacementCallback(new Size(250,300),new Size(40,36),new Point());
            check(positions[0].Point == new Point(-210,-304), "quality popup aligns its right edge above its button");
            var volume = (Popup)win.FindName("VolumePopup"); volume.IsOpen = true;
            Call(win,"HideVolumePopupSoon"); Pump(80);
            check(volume.IsOpen, "volume panel survives the pointer crossing the gap");
            Pump(450); check(!volume.IsOpen, "unhovered volume popup closes after the grace period");
            Call(win,"ApplyPages",new List<VideoPage>{new(){Cid=1,Page=1,Part="第一P"},new(){Cid=2,Page=2,Part="第二P"}});
            var pages = (FrameworkElement)win.FindName("PagesPanel"); var season = (FrameworkElement)win.FindName("SeasonPanel");
            check(pages.Visibility == Visibility.Visible && VisualTreeHelper.GetParent(pages) == VisualTreeHelper.GetParent(season), "parts are inline beside the season list in the introduction");
            var parent = (Panel)VisualTreeHelper.GetParent(pages);
            check(parent.Children.IndexOf(pages) < parent.Children.IndexOf(season), "parts appear above the collection");
            Call(win,"CalibrateDanmakuClock",0d); Call(win,"SetDanmakuClockRunning",true);
            Call(win,"AddDanmaku","点播弹幕移动验证",1,0d,(uint)0xffffff); Pump(80);
            var canvas = (Canvas)win.FindName("DanmakuCanvas");
            check(canvas.Children.Count > 0,"VOD danmaku creates visible overlay elements");
            var element=(FrameworkElement)canvas.Children[0]; var transform=(TranslateTransform)element.RenderTransform; var x=transform.X;
            Pump(160); check(transform.X < x - 1,"VOD danmaku advances across the independent video overlay");
            var live = new LiveRoomWindow();
            check(live.FindName("ChatToggleButton") == null,"redundant bottom chat toggle is absent");
            check(live.FindName("LiveDim") != null && live.FindName("LiveGestureText") != null,"live gestures have brightness and volume feedback layers");
            live.Close();
        } finally { win.Close(); Pump(300); }
    }
}
