using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliTouch.Models;
using System.Windows.Input;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Pages;

internal static class GestureVisualTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args)!;
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    public static void Run(Action<bool, string> check, string artifacts, bool homeOnly = false, bool pictures = false)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var records = new List<object>();
        var frameRecords = new List<object>();
        var images = new List<(string name, BitmapSource bitmap)>();
        var cardCosts = new List<string>();
        var previousMeasureTrace = VirtualizingCardWallPanel.MeasureTrace;
        VirtualizingCardWallPanel.MeasureTrace = cardCosts.Add;
        void Picture(string name, FrameworkElement element)
        {
            if (!pictures) return;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Window.GetWindow(element)?.Background ?? Brushes.White, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, element.ActualWidth, element.ActualHeight) }, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            bitmap.Render(visual); bitmap.Freeze(); images.Add((name, bitmap));
        }
        void Exercise(string name, FrameworkElement viewport, double w, Action<Point> down, Action<Point> move, Action up,
            Func<Dictionary<int, FrameworkElement>> panels)
        {
            var gaps = new List<double>();
            var slowFrames = new List<object>();
            var requestLatencies = new List<double>();
            var slowRequests = new List<object>();
            long pendingRequest = 0;
            void RequestFrame() { if (pendingRequest == 0) pendingRequest = Stopwatch.GetTimestamp(); }
            string phase = "start";
            TimeSpan? last = null;
            EventHandler rendering = (_, e) =>
            {
                if (e is not RenderingEventArgs frame || frame.RenderingTime == last) return;
                if (pendingRequest != 0)
                {
                    var latency = Stopwatch.GetElapsedTime(pendingRequest).TotalMilliseconds;
                    requestLatencies.Add(latency);
                    if (latency > 25) slowRequests.Add(new { phase, latency });
                    pendingRequest = 0;
                }
                if (last is { } prior)
                {
                    var gap = (frame.RenderingTime - prior).TotalMilliseconds;
                    gaps.Add(gap);
                    if (gap > 40) slowFrames.Add(new { phase, gap });
                }
                last = frame.RenderingTime;
            };
            CompositionTarget.Rendering += rendering;
            Dictionary<int, Rect> Bounds() => panels().Where(p => p.Value.Visibility == Visibility.Visible)
                .ToDictionary(p => p.Key, p => p.Value.TransformToAncestor(viewport)
                    .TransformBounds(new Rect(p.Value.RenderSize)));
            for (int i = 0; i < 4; i++)
            {
                phase = $"forward.down.{i}";
                down(new Point(w * .9, 100));
                for (int k = 1; k <= 8; k++) { phase = $"forward.move.{i}.{k}"; RequestFrame(); move(new Point(w * (.9 - .7 * k / 8), 100)); Pump(16); if (i == 0 && k == 4) Picture(name + "-forward-half", viewport); }
                phase = $"forward.settle.{i}"; RequestFrame(); up(); Pump(40);
                viewport.UpdateLayout();
                var before = Bounds();
                phase = $"reverse.down.{i}"; down(new Point(w * .1, 100));
                viewport.UpdateLayout();
                var after = Bounds();
                if (i == 0) Picture(name + "-reverse-takeover", viewport);
                var common = before.Keys.Intersect(after.Keys).ToArray();
                var jump = common.Length == 0 ? double.PositiveInfinity : common.Max(key => Math.Abs(before[key].X - after[key].X));
                records.Add(new { name, iteration = i, jump, before = before.ToDictionary(x => x.Key, x => x.Value.ToString()), after = after.ToDictionary(x => x.Key, x => x.Value.ToString()) });
                File.WriteAllText(Path.Combine(artifacts, "gesture-visual-continuity.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                check(before.Count >= 2 && after.Count >= 2 && common.Length >= 2 && jump < 1,
                    $"{name} reverse takeover preserves BOTH visible pages (jump={jump:F3} DIP)");
                for (int k = 1; k <= 8; k++) { phase = $"reverse.move.{i}.{k}"; RequestFrame(); move(new Point(w * (.1 + .65 * k / 8), 100)); Pump(16); if (i == 0 && k == 4) Picture(name + "-reverse-half", viewport); }
                phase = $"reverse.settle.{i}"; RequestFrame(); up(); Pump(220);
            }
            CompositionTarget.Rendering -= rendering;
            var sorted = gaps.Order().ToArray();
            var responses = requestLatencies.Order().ToArray();
            frameRecords.Add(new { name, slowFrames, samples = sorted.Length, maxGapMs = sorted.LastOrDefault(), p95GapMs = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)],
                requestSamples = responses.Length, maxRequestLatencyMs = responses.LastOrDefault(), p95RequestLatencyMs = responses.Length == 0 ? 0 : responses[(int)((responses.Length - 1) * .95)], slowRequests });
            File.WriteAllText(Path.Combine(artifacts, "gesture-render-gaps.json"), JsonSerializer.Serialize(frameRecords, new JsonSerializerOptions { WriteIndented = true }));
        }

        var main = new MainWindow(); main.Show(); App.MainVm.Navigate(PageKey.Home); Pump(300);
        try
        {
            var home = (HomePage)Call(main, "PageOf", PageKey.Home);
            if (pictures && home.DataContext is HomeViewModel vm)
            {
                foreach (var (items, title) in new[] { (vm.PopularItems, "热门"), (vm.RankingItems, "排行榜") })
                {
                    items.Clear();
                    for (int n = 1; n <= 24; n++) items.Add(new VideoItem { Bvid = "BVvisual" + n, Title = title + " · 画面检查 " + n, Author = title, Duration = "1:00" });
                }
            }
            ((RadioButton)home.FindName("PopularTab")).IsChecked = true; Pump(pictures ? 1400 : 150);
            var host = (FrameworkElement)home.FindName("TabHost");
            Exercise("Home", home, host.ActualWidth, p => home.SwipeDown(p, host), p => home.SwipeMove(p), () => home.SwipeUp(),
                () => new[] { "RecommendScroll", "PopularScroll", "RankingScroll", "LiveScroll", "BangumiScroll" }
                    .Select((name, index) => (index, element: (FrameworkElement)home.FindName(name)))
                    .ToDictionary(x => x.index, x => x.element));

            // Same-direction contacts overlap the previous settle. At each packet
            // the visible pages must cover the viewport, including after crossing
            // a full width. A final selected-index assertion misses the blank gap.
            foreach (var direction in new[] { 1, -1 })
            {
                ((RadioButton)home.FindName(direction > 0 ? "RecommendTab" : "BangumiTab")).IsChecked = true;
                Pump(220);
                for (int contact = 0; contact < 4; contact++)
                {
                    var start = direction > 0 ? .9 : .1;
                    home.SwipeDown(new Point(host.ActualWidth * start, 100), host);
                    var covered = true;
                    double worstOverflow = 0;
                    for (int k = 1; k <= 8; k++)
                    {
                        home.SwipeMove(new Point(host.ActualWidth * (start - direction * .72 * k / 8), 100));
                        Pump(16); home.UpdateLayout();
                        if (contact == 1 && (k == 4 || k == 8)) Picture($"Home-same-{direction}-{k}", home);
                        var left = host.TranslatePoint(new Point(), home).X - Field<TranslateTransform>(home, "_tabTrans").X;
                        var ranges = new[] { "RecommendScroll", "PopularScroll", "RankingScroll", "LiveScroll", "BangumiScroll" }
                            .Select(n => (FrameworkElement)home.FindName(n)).Where(p => p.Visibility == Visibility.Visible)
                            .Select(p => p.TransformToAncestor(home).TransformBounds(new Rect(p.RenderSize)))
                            .OrderBy(r => r.Left).ToArray();
                        var end = left;
                        foreach (var range in ranges)
                            if (range.Left <= end + 1) end = Math.Max(end, range.Right);
                        covered &= end >= left + host.ActualWidth - 1;
                        worstOverflow = Math.Max(worstOverflow, Math.Abs(Field<TranslateTransform>(home, "_tabTrans").X) - host.ActualWidth);
                    }
                    check(covered && worstOverflow <= 0, $"Home same-direction contact {direction}/{contact} covers viewport without overshooting a page");
                    home.SwipeUp(); Pump(contact == 3 ? 220 : 35);
                }
                check(home.CurrentTabIndex == (direction > 0 ? 4 : 0), "Home repeated same-direction contacts reach the boundary in order");
            }
            ((RadioButton)home.FindName("PopularTab")).IsChecked = true; Pump(220);

            foreach (var direction in new[] { 1, -1 })
            {
                ((RadioButton)home.FindName(direction > 0 ? "RecommendTab" : "BangumiTab")).IsChecked = true; Pump(220);
                for (int contact = 0; contact < 4; contact++)
                {
                    for (int packet = 1; packet <= 8; packet++)
                    {
                        home.PreviewHorizontalWheel(direction * .72 * packet / 8); Pump(16);
                        check(Math.Abs(Field<TranslateTransform>(home, "_tabTrans").X) <= host.ActualWidth,
                            "Home repeated wheel streams keep both pages within one viewport displacement");
                    }
                    home.SettleHorizontalWheelPreview(true); Pump(contact == 3 ? 220 : 35);
                }
                check(home.CurrentTabIndex == (direction > 0 ? 4 : 0), "Home repeated wheel streams advance through every tab in order");
            }
            // Both promoted routes share a single origin and a single release.
            ((RadioButton)home.FindName("PopularTab")).IsChecked = true; Pump(220);
            Call(home, "BeginContact", new Point(host.ActualWidth * .9, 100), host);
            home.SwipeMove(new Point(host.ActualWidth * .5, 100));
            var beforeDuplicate = Field<TranslateTransform>(home, "_tabTrans").X;
            Call(home, "BeginContact", new Point(host.ActualWidth * .5, 100), host);
            check(Math.Abs(beforeDuplicate - Field<TranslateTransform>(home, "_tabTrans").X) < .01,
                "promoted duplicate down preserves contact origin and page geometry");
            home.SwipeMove(new Point(host.ActualWidth * .2, 100));
            Call(home, "EndContact"); Call(home, "EndContact"); Pump(220);
            check(home.CurrentTabIndex == 2, "promoted duplicate release commits only one page");
            ((RadioButton)home.FindName("PopularTab")).IsChecked = true; Pump(220);

            // Exercise WPF hit testing, bubbling capture changes, and promoted releases.
            // Calling SwipeMove directly cannot catch failures in this route.
            for (int i = 0; i < 3; i++)
            {
                var before = home.CurrentTabIndex;
                var finger = new Finger(host);
                finger.Down(host.ActualWidth * .9, 100);
                ((ScrollViewer)home.FindName("PopularScroll")).CaptureTouch(finger);
                finger.Move(host.ActualWidth * .8, 102); Pump(16);
                check(ReferenceEquals(finger.Captured, host), "horizontal touch transfers capture from the child scroller to the page host");
                finger.Move(host.ActualWidth * .2, 102); Pump(16);
                finger.Up(host.ActualWidth * .2, 102); Pump(220);
                check(home.CurrentTabIndex == before + 1, "routed touch commits exactly once after capture transfer");
                finger = new Finger(host);
                finger.Down(host.ActualWidth * .1, 100);
                finger.Move(host.ActualWidth * .8, 102); Pump(16);
                finger.Up(host.ActualWidth * .8, 102); Pump(220);
                check(home.CurrentTabIndex == before, "routed reverse touch returns one page without promoted-event duplication");
            }

            if (homeOnly) return;
            App.MainVm.Navigate(PageKey.Mine); Pump(200);
            var mine = (MinePage)Call(main, "PageOf", PageKey.Mine);
            host = (FrameworkElement)mine.FindName("SectionHost");
            Exercise("Mine", mine, host.ActualWidth, p => mine.SwipeDown(p, host), p => mine.SwipeMove(p), () => mine.SwipeUp(),
                () => Field<Dictionary<int, FrameworkElement>>(mine, "_sections"));

            App.MainVm.Navigate(PageKey.Follow); Pump(200);
            var follow = (FollowPage)Call(main, "PageOf", PageKey.Follow);
            host = (FrameworkElement)follow.FindName("FollowFeedHost");
            Exercise("Follow", follow, host.ActualWidth, p => follow.FeedSwipeDown(p, host), p => follow.FeedSwipeMove(p), () => follow.FeedSwipeUp(),
                () => new() { [0] = (FrameworkElement)follow.FindName("DynamicList"), [1] = (FrameworkElement)follow.FindName("VideoFeedScroll") });

            App.MainVm.Navigate(PageKey.Settings); Pump(200);
            var settings = (SettingsPage)Call(main, "PageOf", PageKey.Settings);
            var g = settings.ModuleGestures;
            var content = (FrameworkElement)settings.FindName("ModuleContent");
            host = (FrameworkElement)content.Parent;
            Exercise("Settings", host, content.ActualWidth, p => g.Begin(p, content), p => g.Move(p), () => g.End(),
                () => new() { [g.SelectedIndex] = content, [Field<int>(g, "_peerIndex")] = Field<Grid>(g, "_peerHost") });
        }
        finally
        {
            App.IsExiting = true; main.Close(); App.IsExiting = false;
            VirtualizingCardWallPanel.MeasureTrace = previousMeasureTrace;
            File.WriteAllText(Path.Combine(artifacts, "gesture-card-costs.json"), JsonSerializer.Serialize(cardCosts, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var (name, bitmap) in images)
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(artifacts, name + ".png")); encoder.Save(stream);
            }
        }

        var player = new PlayerWindow(); player.Show(); Pump(200);
        try
        {
            var host = (FrameworkElement)player.FindName("PageHost");
            var secondary = (FrameworkElement)Call(player, "OtherPagePanel");
            Exercise("Player", host, host.ActualWidth, p => player.InfoDown(p, host), p => player.InfoMove(p), () => player.InfoUp(),
                () => new() { [0] = (FrameworkElement)player.FindName("IntroPanel"), [1] = secondary });
        }
        finally { player.Close(); Pump(200); }
        File.WriteAllText(Path.Combine(artifacts, "gesture-visual-continuity.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Finger : TouchDevice
    {
        private static int _id = 9000;
        private readonly FrameworkElement _target;
        private readonly Visual _root;
        private Point _point;
        private TouchAction _action;
        public Finger(FrameworkElement target) : base(++_id)
        {
            _target = target;
            var source = PresentationSource.FromVisual(target)!;
            _root = source.RootVisual; SetActiveSource(source);
        }
        public override TouchPoint GetTouchPoint(IInputElement? relativeTo)
        {
            var p = relativeTo is Visual v ? _root.TransformToDescendant(v).Transform(_point) : _point;
            return new(this, p, new Rect(p, new Size(4, 4)), _action);
        }
        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo) => new() { GetTouchPoint(relativeTo) };
        private void At(double x, double y, TouchAction action) { _point = _target.TranslatePoint(new Point(x, y), (UIElement)_root); _action = action; }
        public void Down(double x, double y) { At(x, y, TouchAction.Down); Activate(); ReportDown(); }
        public void Move(double x, double y) { At(x, y, TouchAction.Move); ReportMove(); }
        public void Up(double x, double y) { At(x, y, TouchAction.Up); ReportUp(); Deactivate(); }
    }
}
