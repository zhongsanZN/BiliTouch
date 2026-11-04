using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using BiliDesk.Views;
using BiliDesk.Views.Pages;

internal static class Program
{
    private static int _checks;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Artifacts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/touch-tests"));

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Artifacts);
        typeof(AppPaths).GetProperty(nameof(AppPaths.DataDir))!.SetValue(null, Artifacts);
        typeof(AppPaths).GetProperty(nameof(AppPaths.CacheRoot))!.SetValue(null, Artifacts);
        var app = new Application();
        var markup = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "../../../../../BiliDesk/App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(markup.Descendants(presentation + "ResourceDictionary").First());
        resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        resources.SetAttributeValue(XNamespace.Xmlns + "helpers", "clr-namespace:BiliDesk.Helpers;assembly=BiliDesk");
        foreach (var element in resources.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:BiliDesk.Helpers"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:BiliDesk.Helpers;assembly=BiliDesk");
        foreach (var source in resources.Descendants().Attributes("Source"))
            source.Value = "/BiliDesk;component/" + source.Value;
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        TouchSupport.Install();
        var exit = 0;
        try
        {
            TestSessions();
            TestWpfControls();
            TestHome();
            TestPlayer(args.FirstOrDefault());
            Console.WriteLine($"PASS: {_checks} checks");
            File.WriteAllText(Path.Combine(Artifacts, "result.txt"), $"PASS: {_checks} checks\n{DateTimeOffset.Now:O}\n");
        }
        catch (Exception ex)
        {
            exit = 1;
            Console.Error.WriteLine(ex);
            File.WriteAllText(Path.Combine(Artifacts, "result.txt"), ex.ToString());
        }
        finally
        {
            try { app.Shutdown(); } catch { }
        }
        // The bare Application host has no App.OnExit; release the native VLC instance here so the
        // process does not fault in its CLR-exit finalizer path (the real app does this in OnExit).
        try { VlcCore.Shutdown(); } catch { }
        Console.Out.Flush();
        Environment.Exit(exit);
        return exit;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
        Console.WriteLine("PASS " + description);
    }

    private static void TestSessions()
    {
        var tap = new PlayerTouchSession(100, 100, 800, 500);
        tap.Move(107, 106);
        Check(tap.Action == PlayerTouchAction.Pending, "small finger jitter remains a tap");
        Check(tap.TryHold() && !tap.TryHold(), "hold fires once");
        tap.Move(250, 100);
        Check(tap.Action == PlayerTouchAction.Hold, "hold never converts into a seek");
        tap.Cancel();
        Check(!tap.TryHold(), "canceled touch cannot start hold");

        var seek = new PlayerTouchSession(100, 100, 800, 500);
        seek.Move(500, 103);
        Check(seek.Action == PlayerTouchAction.Seek && !seek.TryHold(), "horizontal swipe cancels long press");
        Check(seek.SeekTarget(100_000, 600_000) == 130_000, "seek sensitivity follows duration");
        seek.Move(500, -200);
        Check(seek.Action == PlayerTouchAction.Seek, "gesture axis stays locked");
        seek.Move(-2000, 100);
        Check(seek.SeekTarget(10_000, 600_000) == 0, "seek clamps to beginning");
        seek.Move(10000, 100);
        Check(seek.SeekTarget(599_000, 600_000) == 599_750, "seek clamps before EOF");
        var left = new PlayerTouchSession(100, 100, 800, 500);
        left.Move(101, 350);
        Check(left.Action == PlayerTouchAction.Brightness && left.BrightnessTarget(1) == 0.5, "left vertical swipe dims picture");
        left.Move(101, 2000);
        Check(left.BrightnessTarget(1) == 0.2, "brightness never dims picture to black");
        var right = new PlayerTouchSession(700, 300, 800, 500);
        right.Move(703, 50);
        Check(right.Action == PlayerTouchAction.Volume && right.VolumeTarget(20) == 70, "right vertical swipe adjusts volume");
        right.Move(703, -2000);
        Check(right.VolumeTarget(80) == 100, "volume clamps to 100");
        Check(UpdateChecker.ParseVersion(BiliDesk.App.AppVersion) == new Version(1, 5, 3), "touch build version works with updater");
    }

    private static void TestHome()
    {
        var vm = new HomeViewModel();
        for (var i = 0; i < 8; i++)
            vm.RecommendItems.Add(new VideoItem { Title = "首页测试 " + i, Author = "UP" + i, Duration = "1:00" });
        var page = new HomePage { DataContext = vm };
        var window = new Window { Content = page, Width = 1280, Height = 800 };
        window.Show(); Pump(600);

        var host = (Grid)page.FindName("TabHost")!;
        var recommend = (RadioButton)page.FindName("RecommendTab")!;
        var popular = (RadioButton)page.FindName("PopularTab")!;
        Check(recommend.IsChecked == true, "home starts on the recommend tab");

        // 左滑 → 下一个 tab
        var finger = new Finger(host);
        var x = host.ActualWidth / 2; var y = host.ActualHeight / 2;
        finger.Down(x, y);
        for (var i = 1; i <= 5; i++) finger.Move(x - i * 20, y);
        finger.Up(x - 100, y); Pump(200);
        Check(popular.IsChecked == true, "horizontal finger swipe moves to the next home tab");

        // 右滑 → 回到上一个 tab
        finger = new Finger(host);
        finger.Down(x - 100, y);
        for (var i = 1; i <= 5; i++) finger.Move(x - 100 + i * 20, y);
        finger.Up(x, y); Pump(200);
        Check(recommend.IsChecked == true, "horizontal finger swipe moves back to the previous home tab");

        // 竖滑不切 tab
        finger = new Finger(host);
        finger.Down(x, y);
        for (var i = 1; i <= 5; i++) finger.Move(x, y - i * 20);
        finger.Up(x, y - 100); Pump(200);
        Check(recommend.IsChecked == true, "vertical finger swipe does not switch home tabs");

        window.Close(); Pump(120);
    }

    private static void TestWpfControls()
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 10, Width = 400, Height = 48 };
        var rows = new StackPanel();
        for (var i = 0; i < 40; i++) rows.Children.Add(new Button { Content = $"Row {i}", Height = 60 });
        var viewer = new ScrollViewer { Content = rows, Height = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var horizontal = new ScrollViewer { Content = new Border { Width = 1200, Height = 50 }, Height = 60,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var explicitViewer = new ScrollViewer { PanningMode = PanningMode.None, Height = 20 };
        var episodes = new ListBox { Height = 160 };
        for (var i = 0; i < 100; i++) episodes.Items.Add(new Border { Height = 30 });
        VirtualizingPanel.SetScrollUnit(episodes, ScrollUnit.Pixel);
        var editor = new TextBox { Text = "comment draft", Height = 44 };
        var panel = new StackPanel();
        panel.Children.Add(slider); panel.Children.Add(viewer); panel.Children.Add(horizontal);
        panel.Children.Add(explicitViewer); panel.Children.Add(episodes); panel.Children.Add(editor);
        var window = new Window { Title = "BiliDesk touch control tests", Content = panel, Width = 650, Height = 560 };
        window.Show(); Pump(200);
        Check(viewer.PanningMode == PanningMode.VerticalFirst, "default scroll viewers enable native finger panning");
        Check(horizontal.PanningMode == PanningMode.HorizontalOnly, "horizontal lists enable horizontal finger panning");
        Check(explicitViewer.PanningMode == PanningMode.None, "explicit editor panning mode is preserved");
        // End-to-end: a synthetic finger must actually scroll the list, including native inertia.
        var pan = new Finger(viewer);
        pan.Down(300, 170);
        for (var step = 1; step <= 6; step++) pan.Move(300, 170 - step * 25);
        pan.Up(300, 20); Pump(400);
        Check(viewer.VerticalOffset > 100, "finger drag pans the list through native touch manipulation");
        viewer.ScrollToTop(); Pump(60);
        // Virtualized lists (episodes/messages) must pan per pixel, not per row.
        var episodeScroller = FindDescendant<ScrollViewer>(episodes);
        Check(episodeScroller != null && episodeScroller.ExtentHeight > 1000,
            "pixel scroll unit keeps a virtualized list in pixel units");
        Check(VirtualizingPanel.GetScrollUnit(episodes) == ScrollUnit.Pixel,
            "virtualized lists are configured for smooth pixel panning");
        var ep = new Finger(episodeScroller!);
        ep.Down(150, 130); ep.Move(150, 90); ep.Move(150, 50); ep.Up(150, 20); Pump(400);
        Check(episodeScroller!.VerticalOffset > 30, "finger drag pans a virtualized episode list");
        var textScroller = FindDescendant<ScrollViewer>(editor);
        Check(textScroller != null && textScroller.PanningMode != PanningMode.None,
            "text editors keep WPF finger scrolling");
        var started = 0; var completed = 0; var canceled = false;
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => started++));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) => { completed++; canceled = e.Canceled; }));
        var finger = new Finger(slider);
        finger.Down(90, 24); finger.Move(300, 24); finger.Up(300, 24);
        Check(slider.Value > 70 && started == 1 && completed == 1 && !canceled, "finger drags slider with exactly one start/end pair");
        var before = slider.Value;
        finger = new Finger(slider); finger.Down(120, 24); finger.Move(160, 24); finger.LoseCapture();
        Check(canceled && Math.Abs(slider.Value - before) < 0.01, "lost slider capture restores value and reports canceled drag");
        slider.IsSnapToTickEnabled = true; slider.TickFrequency = 25;
        finger = new Finger(slider); finger.Down(245, 24); finger.Up(245, 24);
        Check(slider.Value % 25 == 0, "touch slider respects discrete tick settings");
        window.Close(); Pump(40);
    }

    private static void TestPlayer(string? videoPath)
    {
        var player = new PlayerWindow { Title = "BiliDesk touch verification" };
        player.Show(); Pump(300);
        var root = Element<Grid>(player, "VideoAreaRoot");
        var controls = Element<Border>(player, "ControlBar");
        var help = Element<Border>(player, "TouchHelp");
        var x = root.ActualWidth / 2; var y = root.ActualHeight / 2;
        Check(root.ActualWidth > 200 && root.ActualHeight > 150, "real VLC foreground overlay has a usable gesture area");
        var info = Element<ScrollViewer>(player, "InfoScroll");
        Check(info.PanningMode == PanningMode.VerticalFirst,
            "right-side info/comments/season panel accepts finger panning");
        Check(Math.Abs(info.PanningRatio - 0.75) < 0.01, "touch panning ratio is softened for touchpads");
        Check(VirtualizingPanel.GetScrollUnit(Element<ListBox>(player, "SeasonList")) == ScrollUnit.Pixel,
            "player episode list is configured for per-pixel touch panning");
        var finger = new Finger(root);
        finger.Down(x, y); finger.Up(x, y); Pump(500);
        Check(!controls.IsHitTestVisible, "single finger tap hides controls without toggling playback");
        Check(((Button)player.FindName("TouchHelpButton")!).Visibility == Visibility.Visible,
            "the first real finger reveals the gesture help entry point");
        finger = new Finger(root); finger.Down(x, y); finger.Up(x, y); Pump(500);
        Check(controls.IsHitTestVisible, "single finger tap restores hidden controls");
        var initialVolume = Field<int>(player, "_volumeCache");
        finger = new Finger(root); finger.Down(root.ActualWidth * .8, y); finger.Move(root.ActualWidth * .8, y + 50);
        finger.Up(root.ActualWidth * .8, y + 50);
        Check(Field<int>(player, "_volumeCache") < initialVolume, "real player finger swipe adjusts volume");
        finger = new Finger(root); finger.Down(root.ActualWidth * .2, y); finger.Move(root.ActualWidth * .2, y + 50); finger.Up(root.ActualWidth * .2, y + 50);
        Check(Element<Border>(player, "TouchDimmer").Opacity > 0, "real player left swipe dims only picture overlay");
        Call(player, "OnTouchHelpClick", player, new RoutedEventArgs());
        Check(help.Visibility == Visibility.Visible, "gesture help can be opened");
        Call(player, "OnTouchHelpClose", player, new RoutedEventArgs());
        Check(help.Visibility == Visibility.Collapsed, "gesture help can be dismissed");
        if (!string.IsNullOrEmpty(videoPath))
        {
            player.PlayLocalFile(Path.GetFullPath(videoPath)); Pump(1800);
            var mp = Field<LibVLCSharp.Shared.MediaPlayer>(player, "_mp");
            Check(mp.IsPlaying && mp.Length > 10000, "real local video starts through packaged VLC libraries");
            // Long press only arms while playing: before playback 2x would be a no-op.
            finger = new Finger(root); finger.Down(x, y); Pump(800);
            Check(Field<bool>(player, "_isFastForwarding"), "long touch starts temporary fast forward");
            finger.Up(x, y);
            Check(!Field<bool>(player, "_isFastForwarding"), "lifting long touch always restores playback rate");
            finger = new Finger(root); finger.Down(x, y); Pump(800); finger.LoseCapture();
            Check(!Field<bool>(player, "_isFastForwarding"), "losing capture cancels fast forward");
            Call(player, "ShowControls");
            var a = new Finger(root); var b = new Finger(root);
            a.Down(x, y); b.Down(x + 40, y); Pump(550); b.Up(x + 40, y); a.Up(x, y); Pump(400);
            Check(!Field<bool>(player, "_isFastForwarding") && controls.IsHitTestVisible, "two fingers cancel hold without hiding controls");
            finger = new Finger(root); finger.Down(x, y); finger.Move(x + 100, y);
            Check(Field<bool>(player, "_isSeeking"), "horizontal touch previews seek while finger is down");
            finger.Up(x + 100, y); Pump(700);
            Check(!Field<bool>(player, "_isSeeking") && mp.Time > 3000, "horizontal touch commits seek after release");
            a = new Finger(root); b = new Finger(root);
            a.Down(x, y); a.Move(x + 150, y); b.Down(x + 40, y); b.Up(x + 40, y); a.Up(x + 150, y);
            Check(!Field<bool>(player, "_isSeeking"), "multi-touch cancels seek and unfreezes progress UI");
        }
        // Horizontal swipe on the right info panel switches 简介 / 评论 (and must not select text).
        var infoPanel = Element<Grid>(player, "InfoPanel");
        var ix = infoPanel.ActualWidth / 2; var iy = 140;
        var swipe = new Finger(infoPanel);
        swipe.Down(ix, iy);
        for (var i = 1; i <= 4; i++) swipe.Move(ix - i * 20, iy);
        swipe.Up(ix - 80, iy); Pump(400);
        Check(((RadioButton)player.FindName("TabComments")!).IsChecked == true,
            "horizontal finger swipe switches to the comments tab");
        swipe = new Finger(infoPanel);
        swipe.Down(ix - 80, iy);
        for (var i = 1; i <= 4; i++) swipe.Move(ix - 80 + i * 20, iy);
        swipe.Up(ix, iy); Pump(400);
        Check(((RadioButton)player.FindName("TabIntro")!).IsChecked == true,
            "horizontal finger swipe switches back to the intro tab");
        player.Close(); Pump(1400);
    }

    private static T Element<T>(PlayerWindow player, string name) => (T)player.FindName(name)!;
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Private)!.GetValue(obj)!;
    private static void Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Private)!.Invoke(obj, args);
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    // WPF TouchDevice injection exercises actual routed handlers/capture, without a physical digitizer.
    private sealed class Finger : TouchDevice
    {
        private static int _nextId = 1000;
        private readonly FrameworkElement _target;
        private readonly Visual _root;
        private Point _point;
        private TouchAction _action;
        public Finger(FrameworkElement target) : base(++_nextId)
        {
            _target = target;
            var source = PresentationSource.FromVisual(target)!;
            _root = source.RootVisual;
            SetActiveSource(source);
        }
        public override TouchPoint GetTouchPoint(IInputElement? relativeTo)
        {
            var p = relativeTo is Visual visual ? _root.TransformToDescendant(visual).Transform(_point) : _point;
            return new TouchPoint(this, p, new Rect(p, new Size(4, 4)), _action);
        }
        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo) => new() { GetTouchPoint(relativeTo) };
        private void At(double x, double y, TouchAction action) { _point = _target.TranslatePoint(new Point(x, y), (UIElement)_root); _action = action; }
        public void Down(double x, double y) { At(x, y, TouchAction.Down); Activate(); ReportDown(); Pump(25); }
        public void Move(double x, double y) { At(x, y, TouchAction.Move); ReportMove(); Pump(25); }
        public void Up(double x, double y) { At(x, y, TouchAction.Up); ReportUp(); Deactivate(); Pump(25); }
        public void LoseCapture() { Capture(null); Deactivate(); Pump(25); }
    }
}
