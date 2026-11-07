using System.Reflection;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Views;
using BiliTouch.Views.Controls;

internal static class PlayerCommentsScrollTests
{
    static void Pump(int ms)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
    static MouseWheelEventArgs Wheel(UIElement source,int delta)
    {
        var e=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,delta){RoutedEvent=Mouse.PreviewMouseWheelEvent,Source=source};
        source.RaiseEvent(e);return e;
    }
    internal static void Trace(ScrollViewer scroll, UIElement hit, string label, Action<bool,string> check)
    {
        // Layout cost is the other half of "跟手": the driver can commit every packet on time and
        // still feel late if each committed pixel pays a measure/arrange pass. Measured from a
        // clean offset so the input loop below starts exactly where this surface's user would.
        var baseline = scroll.VerticalOffset;
        Cost(scroll, label);
        scroll.ScrollToVerticalOffset(baseline);
        Pump(80);
        foreach (bool wheel in new[]{false,true})
        {
            var clock=Stopwatch.StartNew();
            var frames=new List<double>();
            var lastFrame=TimeSpan.MinValue;
            double first=-1, previous=0;
            var start=scroll.VerticalOffset;
            ScrollChangedEventHandler changed=(_,e)=>{
                if(ReferenceEquals(e.OriginalSource,scroll) && e.VerticalChange!=0 && first<0)first=clock.Elapsed.TotalMilliseconds;
            };
            EventHandler rendered=(_,e)=>{
                var time=((RenderingEventArgs)e).RenderingTime;
                if(time==lastFrame)return;
                lastFrame=time;
                var now=clock.Elapsed.TotalMilliseconds;
                if(previous>0)frames.Add(now-previous);
                previous=now;
            };
            scroll.ScrollChanged+=changed;CompositionTarget.Rendering+=rendered;
            bool handled=true;
            try
            {
                for(int frame=0;frame<24;frame++)
                {
                    // Prefetch can recycle the captured container mid-gesture; re-resolve the
                    // live hit target every packet instead of driving a detached node.
                    var target = LiveTarget(scroll, hit);
                    if(wheel){if(frame%4==0)handled &= Wheel(target,-120).Handled;}
                    else for(int packet=0;packet<6;packet++)handled &= Wheel(target,-7).Handled;
                    Pump(16);
                }
            }
            finally{scroll.ScrollChanged-=changed;CompositionTarget.Rendering-=rendered;}
            Pump(500);
            var distance=scroll.VerticalOffset-start;
            frames.Sort();
            var p95=frames.Count==0?0:frames[(int)((frames.Count-1)*.95)];
            Console.WriteLine($"INPUT {label} {(wheel?"wheel":"precision")} first-change={first:F1}ms frame-p95={p95:F1}ms frames={frames.Count} distance={distance:F1}");
            check(handled && first>=0 && frames.Count>2 && distance>10,label+" handles routed "+(wheel?"wheel":"precision")+" input across frames");
        }
    }
    private static UIElement LiveTarget(ScrollViewer scroll, UIElement hit)
    {
        for (DependencyObject? node = hit; node != null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, scroll)) return hit;
        return scroll;
    }

    /// <summary>
    /// Layout cost per pixel of scrolling. Routed input is only half of "跟手": every offset
    /// change also pays a measure/arrange pass, and on a slow surface that pass is what makes
    /// the content lag behind the finger. Reported in milliseconds per committed pixel step.
    /// </summary>
    internal static void Cost(ScrollViewer scroll, string label)
    {
        if (scroll.ScrollableHeight <= 40) return;
        var watch = Stopwatch.StartNew();
        var start = scroll.VerticalOffset;
        const int steps = 40;
        for (int i = 1; i <= steps; i++)
        {
            scroll.ScrollToVerticalOffset(start + i * 3);
            scroll.UpdateLayout();
        }
        watch.Stop();
        Console.WriteLine($"COST {label}: {(watch.Elapsed.TotalMilliseconds / steps):F2} ms per 3px step (offset {start:F0}->{scroll.VerticalOffset:F0})");
    }

    public static void Run(Action<bool,string> check)
    {
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var old=Svc.Settings.UseWebPlayerLayout;PlayerWindow? player=null;
        try
        {
            Svc.Settings.SetWebPlayerLayout(false);player=new PlayerWindow{Width=1320,Height=860};player.Show();Pump(200);
            var comments=(ItemsControl)player.FindName("CommentList");
            comments.ItemsSource=Enumerable.Range(1,80).Select(i=>new CommentItem{Rpid=i,UserName="评论用户 "+i,Content="滚动跟手测试。连续快速滚轮应保留完整距离，反向和点击立即接管。",LikeCount=i}).ToArray();
            ((StackPanel)player.FindName("IntroPanel")).Visibility=Visibility.Collapsed;
            ((StackPanel)player.FindName("CommentsPanel")).Visibility=Visibility.Visible;Pump(250);
            var scroll=(ScrollViewer)player.FindName("InfoScroll");
            check(scroll.ScrollableHeight>1000,"actual right-side comment viewport is a long pixel-scrolling thread");
            scroll.ScrollToVerticalOffset(100);Pump(100);
            var from=scroll.VerticalOffset;var notch=Wheel(comments,-120);Pump(35);var intermediate=scroll.VerticalOffset;
            // 评论区现在吃的是参照系(动态页)那一套: 一格 45px、tau 0.12s 滑过去。
            // 旧的 90px/0.032s 是"啪"地跳, 实测同一段手指位移在评论列走出 4 倍距离 —— 那就是"不跟手"。
            check(notch.Handled && intermediate>from && intermediate<from+44,"one comment-wheel notch visibly advances through intermediate offsets");
            Pump(650);check(Math.Abs(scroll.VerticalOffset-from-45*Svc.Settings.FeedWheelScalePercent/100.0)<2,"a comment-wheel notch finishes at the shared one-notch distance");
            TrackpadScroll.Cancel(scroll);scroll.ScrollToVerticalOffset(100);Pump(60);
            for(var i=0;i<3;i++)Wheel(comments,-120);Pump(900);
            check(Math.Abs(scroll.VerticalOffset-100-3*45*Svc.Settings.FeedWheelScalePercent/100.0)<3,"rapid wheel notches retain all unconsumed scrolling distance");
            Wheel(comments,-120);Pump(25);var reverseFrom=scroll.VerticalOffset;Wheel(comments,120);Pump(35);
            check(scroll.VerticalOffset<reverseFrom,"reversing direction immediately follows the new input");
            TrackpadScroll.Cancel(scroll);scroll.ScrollToVerticalOffset(300);Pump(60);
            var before=(long)typeof(TrackpadScroll).GetProperty("OffsetWrites",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            for(var i=0;i<30;i++)Wheel(comments,-5);
            var immediate=(long)typeof(TrackpadScroll).GetProperty("OffsetWrites",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            Pump(400);check(immediate==before && Math.Abs(scroll.VerticalOffset-322.5)<2,"precision comment packets track the finger at the shared pixels-per-delta");
            Wheel(comments,-120);Pump(25);scroll.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent,Source=scroll});
            var canceled=scroll.VerticalOffset;Pump(240);check(Math.Abs(scroll.VerticalOffset-canceled)<2,"clicking comments takes over without a delayed wheel drift");
            Trace(scroll,comments,"split comments",check);
            ScrollBench(scroll,comments,"split comments");
            Svc.Settings.SetWebPlayerLayout(true);Pump(350);
            var web=(ScrollViewer)player.FindName("WebLeftScroll");
            Svc.Settings.SetTouchpadFeedSensitivity(100);
            check(ReferenceEquals(((ContentControl)player.FindName("WebCommentsHost")).Content,player.FindName("CommentsPanel"))
                  && Math.Abs(TrackpadScroll.PixelsPerDelta-0.15)<0.001 && Math.Abs(TrackpadScroll.NotchPixels-45)<0.001,
                  "browser layout keeps the same comments on the single global calibration");
            web.ScrollToVerticalOffset(650);Pump(750);
            Trace(web,comments,"web comments",check);
            ScrollBench(web,comments,"web comments");
        }
        finally{player?.Close();Svc.Settings.SetWebPlayerLayout(old);SynchronizationContext.SetSynchronizationContext(context);}
    }

    /// <summary>
    /// 渲染侧的 A/B: 同一个 700px 视口、同样 80 条中文评论、同一段精密滚轮手势, 唯一变量是
    /// 正文用的文字承载(CommentText=RichTextBox 流文档 vs TextBlock)。
    /// 布局已经排除(COST ≤0.6ms/3px), 所以这里的帧间隔差只可能来自渲染帧要重建什么。
    /// </summary>
    public static void TextHostCost(Action<bool,string> check)
    {
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            foreach (bool rich in new[]{false,true})
            {
                var panel=new StackPanel{Width=760};
                for(int i=0;i<80;i++)
                {
                    var text=$"评论 {i}：滚动跟手测试。连续快速滚轮应保留完整距离，反向和点击立即接管。";
                    FrameworkElement host = rich
                        ? new CommentText{Comment=new CommentItem{Rpid=i,UserName="评论用户",Content=text},FontSize=12.5}
                        : new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=12.5};
                    panel.Children.Add(host);
                }
                var scroll=new ScrollViewer{Width=780,Height=700,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=panel};
                var window=new Window{Width=820,Height=760,Content=scroll,ShowInTaskbar=false,WindowStyle=WindowStyle.None};
                window.Show();Pump(400);
                Console.WriteLine($"TEXTHOST {(rich?"richtextbox":"textblock")} scrollable={scroll.ScrollableHeight:F0} content={panel.ActualHeight:F0}");
                Trace(scroll,panel,(rich?"richtextbox":"textblock")+" column",check);
                ScrollBench(scroll,panel,(rich?"richtextbox":"textblock")+" column");
                window.Close();Pump(150);
            }
        }
        finally{SynchronizationContext.SetSynchronizationContext(context);}
    }

    /// <summary>
    /// 稳态帧率: 在一个固定的 1.5 秒墙上时间里持续来回滚动, 数实际渲染出的帧。
    ///
    /// ★ 为什么不用上面 Trace 的 p95 横比: Trace 的手势是"注入固定包数", 快的表面 0.4s 就滚完、
    ///   慢的表面被排版反馈拖到几秒 —— 分母不一样, 帧间隔根本不能跨表面比。这里改成墙上时间做分母,
    ///   并且在撞到端点时反向, 保证整个窗口内驱动一直在动, 数出来的就是用户手上的实际 fps。
    /// ★ 判据参照系: 动态页(用户确认"丝滑")。评论区/首页若明显低于它, 差的那部分就是渲染侧的问题。
    /// </summary>
    internal static void ScrollBench(ScrollViewer scroll, UIElement hit, string label)
    {
        PresentedScrollProbe.Run(scroll, label);
        if (scroll.ScrollableHeight < 500)
        {
            Console.WriteLine($"BENCH {label}: skipped (scrollable {scroll.ScrollableHeight:F0}px)");
            return;
        }
        TrackpadScroll.Cancel(scroll);
        var start = 20.0;
        scroll.ScrollToVerticalOffset(start);
        Pump(150);
        var clock = Stopwatch.StartNew();
        var intervals = new List<double>();
        var lastFrame = TimeSpan.MinValue;
        var previous = 0.0;
        EventHandler rendered = (_, e) =>
        {
            var time = ((RenderingEventArgs)e).RenderingTime;
            if (time == lastFrame) return;
            lastFrame = time;
            var now = clock.Elapsed.TotalMilliseconds;
            if (previous > 0) intervals.Add(now - previous);
            previous = now;
        };
        var direction = 1;
        var target = start + 400;
        CompositionTarget.Rendering += rendered;
        var stop = false;
        try
        {
            while (!stop && clock.ElapsedMilliseconds < 1500)
            {
                if (Math.Abs(scroll.VerticalOffset - target) < 5)
                {
                    direction = -direction;
                    target = Math.Clamp(direction > 0 ? start + 400 : start, 0, scroll.ScrollableHeight);
                }
                var live = LiveTarget(scroll, hit);
                for (int packet = 0; packet < 6; packet++) Wheel(live, direction > 0 ? -7 : 7);
                Pump(8);
                if (clock.ElapsedMilliseconds >= 1500) stop = true;
            }
        }
        finally { CompositionTarget.Rendering -= rendered; }
        clock.Stop();
        intervals.Sort();
        var elapsed = clock.Elapsed.TotalMilliseconds;
        if (intervals.Count > 0) intervals.RemoveAt(intervals.Count - 1);   // 收尾那帧被 Pump 的唤醒延迟污染
        var median = intervals.Count == 0 ? 0 : intervals[intervals.Count / 2];
        var p95 = intervals.Count == 0 ? 0 : intervals[(int)((intervals.Count - 1) * .95)];
        Console.WriteLine($"BENCH {label}: fps={1000.0 / Math.Max(median, .001):F1} median={median:F1}ms p95={p95:F1}ms frames={intervals.Count + 1} in {elapsed:F0}ms");
    }
}
