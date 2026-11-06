using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BiliTouch.Services;

namespace BiliTouch.Helpers;

/// <summary>Accumulate precision-wheel packets and commit at most once per display frame.</summary>
public static class TrackpadScroll
{
    public static readonly DependencyProperty IsFeedProperty=DependencyProperty.RegisterAttached(
        "IsFeed",typeof(bool),typeof(TrackpadScroll),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.Inherits));
    public static void SetIsFeed(DependencyObject d,bool value)=>d.SetValue(IsFeedProperty,value);
    public static bool GetIsFeed(DependencyObject d)=>(bool)d.GetValue(IsFeedProperty);

    /// <summary>
    /// 整格滚轮(传统 ±120)一格在 **100% 灵敏度**下的基准像素。
    /// 信息流再乘灵敏度百分比(默认 75% ⇒ 67.5px/格); 其它容器不加这层缩放, 保持旧口径。
    /// </summary>
    internal const double NotchBasePixels=90;

    /// <summary>
    /// 整格滚轮的时间常数(秒)。
    ///
    /// ★ 为什么必须和精密输入分开: 精密包是"每 8ms 来一小口", 目标几乎贴着手指走,
    ///   tau 只要 0.025 就能既跟手又顺; 而整格是"一次性甩来 120 个单位", 目标一步跳到 67px 外,
    ///   若还用 0.025 的 tau, 一帧就走完 48% —— 观感依旧是一次跳跃, 只是被抹圆了一点点。
    ///   整格要的是"看得见的滑动": tau 0.12 ⇒ 首帧 13%、3 帧 34%、0.3s 到 92%, 收尾靠 0.5px 吸附。
    /// ★ 为什么取区间下限 0.12 而不是 0.18: 指数逼近的尾巴很长, tau 0.18 时一格要飘到 0.9s 才停,
    ///   快速连滚会明显"追不上手"。0.12 在"滑动感"和"跟手"之间折中。
    /// </summary>
    internal const double NotchTauSeconds=0.12;

    /// <summary>
    /// 精密输入(触控板 / 高分辨率滚轮)的时间常数(秒)。
    /// ★ 这个值**不要**跟着整格一起改: 触控板的手感已经调过并确认过, 整格是独立的一套参数。
    /// </summary>
    internal const double PrecisionTauSeconds=0.025;

    /// <summary>SmoothScroll 兜底路径(容器自己开了平滑、但不在信息流里)沿用的默认动画时长(ms)。</summary>
    private const double LegacyDurationMs=280;

    private sealed class Motion
    {
        public double Target, Applied;
        public long LastFrame, LastInput;
        public bool Active;
        public bool Precision;
    }
    private static readonly ConditionalWeakTable<ScrollViewer,Motion> Motions=new();
    private static readonly HashSet<ScrollViewer> Active=new();
    private static bool _rendering;
    private static TimeSpan _lastRenderingTime=TimeSpan.MinValue;
    // Instrumentation is deliberately internal: lets the regression harness measure coalescing.
    internal static long InputPackets {get;private set;}
    internal static long OffsetWrites {get;private set;}
    /// <summary>精密输入的像素/每单位 delta: 信息流按灵敏度缩放, 其它容器固定 0.6。</summary>
    internal static double PixelsPerDelta(ScrollViewer viewer)=>GetIsFeed(viewer)?0.3*Svc.Settings.TouchpadFeedSensitivity/100.0:0.6;

    /// <summary>
    /// 整格滚轮一格的像素数。
    ///
    /// 信息流: 90 × 灵敏度百分比。★ 与 <see cref="PixelsPerDelta"/> 同一把尺子很重要 ——
    ///   若只有精密输入吃灵敏度、整格写死 90, 用户把灵敏度调到 25% 之后, 触控板一格 18px、
    ///   鼠标一格还是 90px, 两者手感立刻割裂(这正是这次要修的核心)。
    /// 其它容器(播放器网页布局): 保持旧口径 —— SmoothScroll.Step 单页覆盖优先, 否则 90px。
    /// </summary>
    internal static double NotchPixels(ScrollViewer viewer)
    {
        if(!GetIsFeed(viewer)) return SmoothScroll.GetStep(viewer)>0?SmoothScroll.GetStep(viewer)*.5:NotchBasePixels;
        return .5*NotchBasePixels*Svc.Settings.TouchpadFeedSensitivity/100.0;
    }

    internal static bool OwnsWheel(ScrollViewer viewer,MouseWheelEventArgs e)=>ReferenceEquals(ScrollTarget(e.OriginalSource as DependencyObject),viewer);

    /// <param name="includeNotches">
    /// true = 调用方允许把整格(±120)滚轮也交给本类做累积平滑。
    /// ★ 全局类处理器(TouchSupport.OnTouchpadWheel)只对 <c>IsFeed=true</c> 的 ScrollViewer 传 true:
    ///   那个类处理器挂着**所有** ScrollViewer, 在那里一刀切会把设置页 / 播放器信息栏 / 任何
    ///   带滚动条的网格里的整格滚轮一起变慢变飘 —— 那些页面的原语义就是"一格走一段", 不该被绑走。
    /// ★ SmoothScroll 的实例处理器也传 true, 那是"这个容器我自己开了平滑"的显式选择(与 IsFeed 无关)。
    /// </param>
    public static bool Handle(ScrollViewer viewer,MouseWheelEventArgs e,bool includeNotches=false)
    {
        if(e.Handled || e.Delta==0 || viewer.ScrollableHeight<=0 || !OwnsWheel(viewer,e))return false;
        // Logical lists use row offsets, not pixels. Keep their own wheel semantics.
        if(viewer.CanContentScroll && viewer.TemplatedParent is ItemsControl owner && VirtualizingPanel.GetScrollUnit(owner)==ScrollUnit.Item)return false;
        var now=Stopwatch.GetTimestamp();
        Motions.TryGetValue(viewer,out var motion);
        // A 120-multiple packet right after precision packets is still the touchpad finishing its gesture.
        var precision=Math.Abs((long)e.Delta)<120 || (motion?.Active==true && motion.Precision && Stopwatch.GetElapsedTime(motion.LastInput,now).TotalMilliseconds<90);
        if(!precision && !includeNotches)return false;
        if(motion==null)
        {
            motion=Motions.GetOrCreateValue(viewer);
            viewer.Unloaded+=OnUnloaded;
            viewer.PreviewMouseDown+=OnMouseDown;
        }
        if(!motion.Active)
        {
            SmoothScroll.Stop(viewer);
            motion.Target=motion.Applied=viewer.VerticalOffset;
            motion.LastFrame=now;motion.Active=true;Active.Add(viewer);
        }
        var delta=precision ? -e.Delta*PixelsPerDelta(viewer) : -e.Delta/120.0*NotchPixels(viewer);
        // Reversing kills the in-flight overshoot instead of first finishing it and then coming back.
        if(Math.Sign(delta)!=Math.Sign(motion.Target-viewer.VerticalOffset))motion.Target=viewer.VerticalOffset;
        motion.Precision=precision;
        motion.Target=Math.Clamp(motion.Target+delta,0,viewer.ScrollableHeight);
        motion.LastInput=now;InputPackets++;e.Handled=true;
        if(!_rendering){_rendering=true;_lastRenderingTime=TimeSpan.MinValue;CompositionTarget.Rendering+=OnFrame;}
        return true;
    }
    private static ScrollViewer? ScrollTarget(DependencyObject? source)
    {
        while(source!=null)
        {
            if(source is ScrollViewer viewer && viewer.ScrollableHeight>0)return viewer;
            source=source is ContentElement content?ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent:
                source is Visual or System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(source):LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
    private static void OnFrame(object? sender,EventArgs e)
    {
        if(e is RenderingEventArgs frame)
        {if(frame.RenderingTime==_lastRenderingTime)return;_lastRenderingTime=frame.RenderingTime;}
        var now=Stopwatch.GetTimestamp();
        foreach(var viewer in Active.ToArray())
        {
            var motion=Motions.GetOrCreateValue(viewer);
            if(!viewer.IsLoaded || !viewer.IsVisible || Math.Abs(viewer.VerticalOffset-motion.Applied)>2)
            {Cancel(viewer);continue;}
            var dt=Math.Clamp(Stopwatch.GetElapsedTime(motion.LastFrame,now).TotalSeconds,0,0.1);
            motion.LastFrame=now;
            motion.Target=Math.Clamp(motion.Target,0,viewer.ScrollableHeight);
            var remaining=motion.Target-viewer.VerticalOffset;
            var tau=FrameTimeConstant(viewer,motion);
            var next=Math.Abs(remaining)<0.5?motion.Target:viewer.VerticalOffset+remaining*(1-Math.Exp(-dt/tau));
            // No new animation clock per input packet; all packets share this frame update.
            viewer.ScrollToVerticalOffset(next);motion.Applied=next;OffsetWrites++;
            if(Math.Abs(motion.Target-next)<0.01)Cancel(viewer);
        }
    }

    /// <summary>
    /// 这一帧用哪个时间常数:
    /// 精密 0.025s / 信息流整格 0.12s / 其它容器沿用旧的"时长÷3000"(播放器网页布局的老手感)。
    /// ★ 三条分开是刻意的: 精密要跟手, 整格要看得出滑动, 而播放器那边已经按 DurationMs 调过, 别顺手改掉。
    /// </summary>
    private static double FrameTimeConstant(ScrollViewer viewer,Motion motion)
    {
        if(motion.Precision)return PrecisionTauSeconds;
        if(GetIsFeed(viewer))return NotchTauSeconds;
        var ms=SmoothScroll.GetDurationMs(viewer);
        return Math.Max(.025,(ms>0?ms:LegacyDurationMs)/3000.0);
    }

    public static void Cancel(ScrollViewer viewer)
    {
        if(Motions.TryGetValue(viewer,out var motion))motion.Active=false;
        Active.Remove(viewer);
        if(_rendering && Active.Count==0){CompositionTarget.Rendering-=OnFrame;_rendering=false;}
    }
    private static void OnUnloaded(object sender,RoutedEventArgs e)=>Cancel((ScrollViewer)sender);
    private static void OnMouseDown(object sender,MouseButtonEventArgs e)=>Cancel((ScrollViewer)sender);
}
