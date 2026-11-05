using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Models;
using BiliTouch.Views;

/// <summary>
/// 复现"点进评论详情再返回后, 触屏拖不动"(2026-11-04) —— 第二轮, 走**真实触摸事件**。
///
/// 上一轮 CommentSwipeProbe 只验证了布局数值, 全部正常, 说明问题不在"高度/可见性",
/// 而在**触摸事件链**。真实链路是:
///   手指按下 → TouchSupport 的 PreviewTouchDown 里 ConfigureScrollViewer + MarkTouchPanning
///            → WPF 原生 Manipulation 开始平移
///            → InfoPanel 的 OnInfoTouchDownCapture 也抢一次 CaptureTouch
/// 只要其中一环在"进出详情"之后失效, 表现就是"手指上下拖不动"。
///
/// 这个探针做三件事:
///   1. 记录进出详情前后, 触摸事件的**实际到达情况**(谁 Handled 了);
///   2. 检查 TouchSupport 的 _touchViewer 是否残留(它是 static, 跨页面共享);
///   3. 检查 PageHost.Height 是否卡在固定值(它会压死 InfoScroll 的视口)。
/// </summary>
internal static class CommentTouchChainProbe
{
    public static void Run(PlayerWindow player, string output)
    {
        var report = new List<string>();
        void Line(string s) { report.Add(s); Console.WriteLine(s); Console.Out.Flush(); }

        var infoScroll = Find<ScrollViewer>(player, "InfoScroll");
        var infoPanel = Find<FrameworkElement>(player, "InfoPanel");
        var pageHost = Find<FrameworkElement>(player, "PageHost");
        var commentList = Find<ItemsControl>(player, "CommentList");
        var tabComments = Find<RadioButton>(player, "TabComments");
        if (infoScroll == null || infoPanel == null || pageHost == null || commentList == null || tabComments == null)
        { Line("PROBE elements missing"); return; }

        // ---- 探针: 数一数 InfoPanel 上真正到达的触摸事件 ----
        var reached = new Dictionary<string, int>();
        void Bump(string key) => reached[key] = reached.GetValueOrDefault(key) + 1;

        infoPanel.AddHandler(UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>((_, e) => Bump($"down handled={e.Handled}")), true);
        infoPanel.AddHandler(UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>((_, e) => Bump($"move handled={e.Handled}")), true);
        infoPanel.AddHandler(UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>((_, e) => Bump($"up handled={e.Handled}")), true);

        tabComments.IsChecked = true;
        Pump(700);

        // 造评论
        var seeded = new System.Collections.ObjectModel.ObservableCollection<CommentItem>();
        for (var i = 0; i < 15; i++)
        {
            var c = new CommentItem
            {
                Rpid = 910000 + i,
                UserName = $"触摸链用户{i}",
                Content = $"第 {i} 条评论, 用于让列表足够长。正文再写长一点, 保证每条占两三行, 这样才有可滚动空间。",
                LikeCount = i,
                ReplyCount = i % 2 == 0 ? 2 : 0,
            };
            if (i % 2 == 0)
                for (var k = 0; k < 2; k++)
                    c.Replies.Add(new CommentItem { Rpid = 920000 + i * 10 + k, UserName = "楼中楼", Content = "回复内容" });
            seeded.Add(c);
        }
        commentList.ItemsSource = seeded;
        Pump(900);

        Line($"--- 基线(评论列表) ---");
        Dump("base", Line, infoScroll, pageHost, commentList, seeded);

        // ---- 真触摸: 按下-移动-抬起, 看是否被吞 ----
        var finger = new Finger(infoScroll);
        var before = infoScroll.VerticalOffset;
        finger.Down(200, 400);
        reached.Clear();
        finger.Move(200, 300);
        finger.Move(200, 200);
        finger.Up(200, 200);
        Pump(500);
        Line($"  基线一次竖滑: {before:0} → {infoScroll.VerticalOffset:0}  " +
             $"事件={string.Join(", ", reached.OrderBy(k => k.Key).Select(k => $"{k.Key}×{k.Value}"))}");

        // ---- 进详情 ----
        Call(player, "ShowCommentDetail", seeded[0], seeded[0]);
        Pump(900);
        Line($"--- 进入详情 ---");
        Dump("detail", Line, infoScroll, pageHost, commentList, seeded);

        // ---- 返回 ----
        Call(player, "CloseCommentDetail");
        Pump(1200);
        Line($"--- 返回列表 ---");
        Dump("back", Line, infoScroll, pageHost, commentList, seeded);

        // ---- 返回后再真触摸一次 ----
        // ★ 关键: 基线那次竖滑已经把列表推到底(1879 → 之后又到底), 到底之后往下滑当然不动。
        //   所以这里先回到顶部, 再测"往下滑能不能动", 否则测的是"边界"不是"失效"。
        infoScroll.ScrollToTop();
        Pump(300);
        var before2 = infoScroll.VerticalOffset;
        finger = new Finger(infoScroll);
        finger.Down(200, 400);
        reached.Clear();
        finger.Move(200, 300);
        finger.Move(200, 200);
        finger.Up(200, 200);
        Pump(600);
        var after2 = infoScroll.VerticalOffset;
        var delta2 = after2 - before2;
        Line($"  返回后一次竖滑(从顶部): {before2:0} → {after2:0} (Δ{delta2:0})  " +
             $"事件={string.Join(", ", reached.OrderBy(k => k.Key).Select(k => $"{k.Key}×{k.Value}"))}");

        // ---- 再测一次"往上滑"(滚动到底部方向) ----
        infoScroll.ScrollToTop();
        Pump(200);
        var before3 = infoScroll.VerticalOffset;
        finger = new Finger(infoScroll);
        finger.Down(200, 200);
        finger.Move(200, 300);
        finger.Move(200, 400);
        finger.Up(200, 400);
        Pump(600);
        var after3 = infoScroll.VerticalOffset;
        Line($"  返回后反向竖滑: {before3:0} → {after3:0} (Δ{after3 - before3:0})");

        // ---- TouchSupport 的静态残留 ----
        var touchViewerField = typeof(BiliTouch.Helpers.TouchSupport).GetField("_touchViewer",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var resident = touchViewerField?.GetValue(null) as ScrollViewer;
        Line($"  TouchSupport._touchViewer = " +
             (resident == null ? "null(正常)" : $"残留: {resident.Name} ({resident.GetType().Name})"));
        Line($"  TouchSupport.IsTouchPanning(InfoScroll) = " +
             BiliTouch.Helpers.TouchSupport.IsTouchPanning(infoScroll));

        if (Math.Abs(delta2) < 1 && infoScroll.ScrollableHeight > 0)
            Line("FAIL 返回后从顶部往下滑也不动 —— 触摸链被破坏");
        else if (Math.Abs(delta2) >= 1)
            Line($"PASS 返回后仍能滚动(Δ{delta2:0})");
        else
            Line("SKIP 没有可滚动空间");

        File.WriteAllLines(Path.Combine(output, "comment-touch-chain-report.txt"), report);
    }

    private static void Dump(string stage, Action<string> line,
        ScrollViewer infoScroll, FrameworkElement pageHost, ItemsControl commentList,
        System.Collections.ObjectModel.ObservableCollection<CommentItem> items)
    {
        line($"  [{stage}] InfoScroll PanningMode={infoScroll.PanningMode} " +
             $"Scrollable={infoScroll.ScrollableHeight:0} Extent={infoScroll.ExtentHeight:0} " +
             $"Viewport={infoScroll.ViewportHeight:0} ActualH={infoScroll.ActualHeight:0}");
        line($"  [{stage}] PageHost Height={(double.IsNaN(pageHost.Height) ? "NaN(auto)" : pageHost.Height.ToString("0"))} " +
             $"Actual={pageHost.ActualHeight:0}");
        line($"  [{stage}] CommentList Visibility={commentList.Visibility} " +
             $"Items={commentList.Items.Count}/{items.Count} ActualH={commentList.ActualHeight:0}");
    }

    private static T? Find<T>(FrameworkElement root, string name) where T : FrameworkElement
        => root.FindName(name) as T ?? Descendants<T>(root).FirstOrDefault(e => e.Name == name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T m) yield return m;
            foreach (var n in Descendants<T>(child)) yield return n;
        }
    }

    private static void Call(object target, string name, params object[] args)
        => target.GetType().GetMethod(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public)!.Invoke(target, args);

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>合成触摸(与 Program.Finger 同一套做法, 单独放一份便于本探针独立使用)</summary>
    private sealed class Finger : System.Windows.Input.TouchDevice
    {
        private static int _nextId = 5000;
        private readonly FrameworkElement _target;
        private readonly Visual _root;
        private Point _point;
        private System.Windows.Input.TouchAction _action;

        public Finger(FrameworkElement target) : base(++_nextId)
        {
            _target = target;
            var source = PresentationSource.FromVisual(target)!;
            _root = source.RootVisual;
            SetActiveSource(source);
        }

        public override System.Windows.Input.TouchPoint GetTouchPoint(System.Windows.IInputElement? relativeTo)
        {
            var p = relativeTo is Visual v ? _root.TransformToDescendant(v).Transform(_point) : _point;
            return new System.Windows.Input.TouchPoint(this, p, new Rect(p, new Size(4, 4)), _action);
        }

        public override System.Windows.Input.TouchPointCollection
            GetIntermediateTouchPoints(System.Windows.IInputElement? relativeTo)
            => new() { GetTouchPoint(relativeTo) };

        private void At(double x, double y, System.Windows.Input.TouchAction a)
        {
            _point = _target.TranslatePoint(new Point(x, y), (UIElement)_root);
            _action = a;
        }

        public void Down(double x, double y) { At(x, y, System.Windows.Input.TouchAction.Down); Activate(); ReportDown(); Pump(30); }
        public void Move(double x, double y) { At(x, y, System.Windows.Input.TouchAction.Move); ReportMove(); Pump(30); }
        public void Up(double x, double y) { At(x, y, System.Windows.Input.TouchAction.Up); ReportUp(); Deactivate(); Pump(30); }
    }
}
