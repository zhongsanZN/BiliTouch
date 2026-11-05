using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Services;

namespace BiliDesk.Helpers;

/// <summary>Accumulate precision-wheel packets and commit at most once per display frame.</summary>
public static class TrackpadScroll
{
    public static readonly DependencyProperty IsFeedProperty=DependencyProperty.RegisterAttached(
        "IsFeed",typeof(bool),typeof(TrackpadScroll),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.Inherits));
    public static void SetIsFeed(DependencyObject d,bool value)=>d.SetValue(IsFeedProperty,value);
    public static bool GetIsFeed(DependencyObject d)=>(bool)d.GetValue(IsFeedProperty);

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
    internal static double PixelsPerDelta(ScrollViewer viewer)=>GetIsFeed(viewer)?0.6*Svc.Settings.TouchpadFeedSensitivity/100.0:0.6;
    internal static bool OwnsWheel(ScrollViewer viewer,MouseWheelEventArgs e)=>ReferenceEquals(ScrollTarget(e.OriginalSource as DependencyObject),viewer);

    public static bool Handle(ScrollViewer viewer,MouseWheelEventArgs e,bool includeNotches=false)
    {
        if(e.Handled || e.Delta==0 || viewer.ScrollableHeight<=0 || !OwnsWheel(viewer,e))return false;
        // Logical lists use row offsets, not pixels. Keep their own wheel semantics.
        if(viewer.CanContentScroll && viewer.TemplatedParent is ItemsControl owner && VirtualizingPanel.GetScrollUnit(owner)==ScrollUnit.Item)return false;
        var now=Stopwatch.GetTimestamp();
        Motions.TryGetValue(viewer,out var motion);
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
        var delta=precision ? -e.Delta*PixelsPerDelta(viewer) : -e.Delta/120.0*(SmoothScroll.GetStep(viewer)>0?SmoothScroll.GetStep(viewer)*.5:90);
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
            var tau=motion.Precision?0.025:Math.Max(.025,(SmoothScroll.GetDurationMs(viewer)>0?SmoothScroll.GetDurationMs(viewer):280)/3000.0);
            var next=Math.Abs(remaining)<0.5?motion.Target:viewer.VerticalOffset+remaining*(1-Math.Exp(-dt/tau));
            // No new animation clock per input packet; all packets share this frame update.
            viewer.ScrollToVerticalOffset(next);motion.Applied=next;OffsetWrites++;
            if(Math.Abs(motion.Target-next)<0.01)Cancel(viewer);
        }
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
