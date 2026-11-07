using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BiliTouch.Services;

namespace BiliTouch.Helpers;

/// <summary>
/// 全站唯一的滚轮/触控板帧驱动: 累积输入包, 每个渲染帧最多写一次 offset。
///
/// ★★★ 2026-11-07 取消 IsFeed 分叉(来自用户要求的对比分析)。
///   以前"手感"由一个 Inherits 的 <c>TrackpadScroll.IsFeed</c> 决定: 挂了标记的容器用一套按灵敏度
///   标定过的参数, 没挂的用另一套。用户的观察是"动态页丝滑、评论区不跟手", 把两类表面的实际参数摆开:
///     精密包位移  动态/首页 = 0.15 px/单位   播放器评论区 = 0.6 px/单位  → 同样的手指距离评论走 4 倍
///     整格位移    动态/首页 = 45 px/格        播放器评论区 = 90 px/格     → 2 倍
///     整格时间常数 动态/首页 = 0.12 s         播放器评论区 = 160/5000=0.032 s → 首帧吃掉 39%, 观感是跳
///   现在只留一套标定: 所有像素滚动的 ScrollViewer 共用灵敏度、整格距离与时间常数。
///   逻辑滚动(ScrollUnit=Item 的列表)仍然完全交回 WPF 原语义。
/// </summary>
public static class TrackpadScroll
{
    // The touchpad slider scales precision packets; whole notches keep their own saved scale.
    internal const double NotchBasePixels=90;

    /// <summary>
    /// 整格滚轮的时间常数(秒)。
    ///
    /// ★ 为什么必须和精密输入分开: 精密包是"每 8ms 来一小口", 位移本身就是手指位移的缩放, 所以
    ///   它**不缓动** —— 下一帧直接把累积目标交出去, 再缓一道就是迟滞。整格不同, 它是"一次性甩来
    ///   120 个单位"的离散量, 目标一步跳到 45px 外, 不缓就是原样的瞬移。
    ///   整格要的是"看得见的滑动": tau 0.12 ⇒ 首帧 13%、3 帧 34%、0.3s 到 92%, 收尾靠 0.5px 吸附。
    /// ★ 为什么取区间下限 0.12 而不是 0.18: 指数逼近的尾巴很长, tau 0.18 时一格要飘到 0.9s 才停,
    ///   快速连滚会明显"追不上手"。0.12 在"滑动感"和"跟手"之间折中。
    /// ★ 它以前只对信息流生效, 现在全站生效 —— 播放器评论区当时是 DurationMs/5000=0.032,
    ///   首帧就走掉 39%, 用户看到的还是"啪"地跳过去。
    /// </summary>
    internal const double NotchTauSeconds=0.12;

    /// <summary>精密包每单位 delta 的像素数, 由设置页的滚动灵敏度缩放。</summary>
    internal const double PrecisionPixelsPerDeltaUnit=0.15;

    private const int PrecisionIdleMs = 90;
    // Releasing a render callback is not the end of device identification. A touchpad
    // tail (or the next short stroke) can pause without becoming a physical mouse wheel.
    private const int PrecisionGestureGapMs = 750;

    [StructLayout(LayoutKind.Sequential)]
    private struct InputMessageSource { public uint DeviceType, OriginId; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
    private static bool IsReportedTouchpad()
    {
        // Older Windows/drivers may report touchpad wheels as mouse input. Treat only
        // a positive touchpad identity as authoritative; keep packet-based fallback.
        return GetCurrentInputMessageSource(out var source) && source.DeviceType == 0x10;
    }

    private sealed class Motion
    {
        public double Target, Applied, Observed;
        public long LastFrame, LastInput;
        public bool Active;
        public bool Precision;
        public int Direction;
    }
    private static readonly ConditionalWeakTable<ScrollViewer,Motion> Motions=new();
    private static readonly List<ScrollViewer> Active=new();
    private static bool _rendering;
    private static TimeSpan _lastRenderingTime=TimeSpan.MinValue;
    // Instrumentation is deliberately internal: lets the regression harness measure coalescing.
    internal static long InputPackets {get;private set;}
    internal static long OffsetWrites {get;private set;}
    /// <summary>精密包每单位 delta 走多少像素: 全站一把尺子, 由设置页灵敏度缩放。</summary>
    internal static double PixelsPerDelta=>PrecisionPixelsPerDeltaUnit*Svc.Settings.TouchpadFeedSensitivity/100.0;

    /// <summary>整格滚轮一格走多少像素: 与灵敏度无关(灵敏度描述的是手指位移, 整格是离散量)。</summary>
    internal static double NotchPixels=>.5*NotchBasePixels*Svc.Settings.FeedWheelScalePercent/100.0;

    internal static bool OwnsWheel(ScrollViewer viewer,MouseWheelEventArgs e)=>ReferenceEquals(ScrollTarget(e.OriginalSource as DependencyObject),viewer);

    public static bool Handle(ScrollViewer viewer,MouseWheelEventArgs e)
    {
        if(e.Handled || e.Delta==0 || viewer.ScrollableHeight<=0 || !OwnsWheel(viewer,e))return false;
        // Logical lists use row offsets, not pixels. Keep their own wheel semantics.
        if(viewer.CanContentScroll && viewer.TemplatedParent is ItemsControl owner && VirtualizingPanel.GetScrollUnit(owner)==ScrollUnit.Item)return false;
        var now=Stopwatch.GetTimestamp();
        Motions.TryGetValue(viewer,out var motion);
        // A 120-multiple packet right after precision packets is still the touchpad finishing its gesture.
        var precision=IsReportedTouchpad() || e.Delta % 120 != 0 || (motion?.Precision==true && Stopwatch.GetElapsedTime(motion.LastInput,now).TotalMilliseconds<PrecisionGestureGapMs);
        if(motion==null)
        {
            motion=Motions.GetOrCreateValue(viewer);
            viewer.Unloaded+=OnUnloaded;
            viewer.PreviewMouseDown+=OnMouseDown;
        }
        if(!motion.Active)
        {
            motion.Target=motion.Applied=motion.Observed=viewer.VerticalOffset;
            motion.LastFrame=now;motion.Active=true;Active.Add(viewer);
        }
        var delta=precision ? -e.Delta*PixelsPerDelta : -e.Delta/120.0*NotchPixels;
        // Reversing kills the in-flight overshoot instead of first finishing it and then coming back.
        var direction = Math.Sign(delta);
        if (direction != motion.Direction || WasMovedExternally(viewer, motion))
            motion.Target = motion.Applied = motion.Observed = viewer.VerticalOffset;
        motion.Direction = direction;
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
            if (source is ScrollViewer viewer && viewer.ScrollableHeight > 0 &&
                viewer.TemplatedParent is not BiliTouch.Views.Controls.CommentText) return viewer;
            source=source is ContentElement content?ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent:
                source is Visual or System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(source):LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
    // ScrollToVerticalOffset is asynchronous. Until layout commits it, VerticalOffset can
    // still equal the last observed position rather than our last requested position.
    // Neither state is an external move; cancelling here used to discard queued input.
    private static bool WasMovedExternally(ScrollViewer viewer, Motion motion) =>
        Math.Abs(viewer.VerticalOffset - motion.Applied) > 2 &&
        Math.Abs(viewer.VerticalOffset - motion.Observed) > 2;
    private static void OnFrame(object? sender,EventArgs e)
    {
        if(e is RenderingEventArgs frame)
        {if(frame.RenderingTime==_lastRenderingTime)return;_lastRenderingTime=frame.RenderingTime;}
        var now=Stopwatch.GetTimestamp();
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var viewer = Active[i];
            var motion=Motions.GetOrCreateValue(viewer);
            if(!viewer.IsLoaded || !viewer.IsVisible || WasMovedExternally(viewer,motion))
            {Cancel(viewer);continue;}
            var dt=Math.Clamp(Stopwatch.GetElapsedTime(motion.LastFrame,now).TotalSeconds,0,0.1);
            motion.LastFrame=now;
            motion.Target=Math.Clamp(motion.Target,0,viewer.ScrollableHeight);
            if (motion.Precision && motion.Target == motion.Applied)
            {
                // Keep one composition subscription across packets of the same gesture.
                // Retain the fractional target even if the viewport rounded the last write.
                if (Stopwatch.GetElapsedTime(motion.LastInput, now).TotalMilliseconds >= PrecisionIdleMs)
                    Stop(viewer);
                continue;
            }
            var remaining=motion.Target-viewer.VerticalOffset;
            // Precision devices have already sampled finger displacement and OS inertia.
            // Present the accumulated distance at the next render, without a second easing tail.
            var next = motion.Target;
            if (!motion.Precision && Math.Abs(remaining) >= .5)
            {
                next = viewer.VerticalOffset + remaining * (1 - Math.Exp(-dt / NotchTauSeconds));
            }
            // No new animation clock per input packet; all packets share this frame update.
            motion.Observed=viewer.VerticalOffset;
            viewer.ScrollToVerticalOffset(next);motion.Applied=next;OffsetWrites++;
            if(!motion.Precision && Math.Abs(motion.Target-next)<0.01)Stop(viewer);
        }
    }

    public static void Cancel(ScrollViewer viewer)
    {
        if(Motions.TryGetValue(viewer,out var motion))
        {
            // Removing our frame callback does not remove a ScrollViewer command that
            // is already queued. Replace that command too when contact/navigation takes over.
            if(motion.Active && Math.Abs(viewer.VerticalOffset-motion.Applied)>.01)
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset);
            motion.Precision=false;
        }
        Stop(viewer);
    }
    private static void Stop(ScrollViewer viewer)
    {
        if(Motions.TryGetValue(viewer,out var motion))motion.Active=false;
        Active.Remove(viewer);
        if(_rendering && Active.Count==0){CompositionTarget.Rendering-=OnFrame;_rendering=false;}
    }
    private static void OnUnloaded(object sender,RoutedEventArgs e)=>Cancel((ScrollViewer)sender);
    private static void OnMouseDown(object sender,MouseButtonEventArgs e)=>Cancel((ScrollViewer)sender);
}
