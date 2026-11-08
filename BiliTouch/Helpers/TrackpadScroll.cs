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
    /// ★ 整格是"一次性甩来 120 个单位"的离散量, 目标一步跳到 45px 外, 不缓就是原样的瞬移。
    ///   整格要的是"看得见的滑动": tau 0.12 ⇒ 首帧 13%、3 帧 34%、0.3s 到 92%, 收尾靠 0.5px 吸附。
    /// ★ 为什么取区间下限 0.12 而不是 0.18: 指数逼近的尾巴很长, tau 0.18 时一格要飘到 0.9s 才停,
    ///   快速连滚会明显"追不上手"。0.12 在"滑动感"和"跟手"之间折中。
    /// ★ 它以前只对信息流生效, 现在全站生效 —— 播放器评论区当时是 DurationMs/5000=0.032,
    ///   首帧就走掉 39%, 用户看到的还是"啪"地跳过去。
    /// </summary>
    internal const double NotchTauSeconds=0.12;

    /// <summary>
    /// 精密包(触控板)的时间常数(秒)。**2026-11-08 起精密包不再直达, 也走指数逼近。**
    ///
    /// ★ 为什么推翻"精密包不缓动"这条旧结论:
    ///   旧理由写的是"精密包每 8ms 来一小口, 位移本身就是手指位移的缩放, 再缓一道就是迟滞"。
    ///   这个理由的**前提**是"驱动已经把手指轨迹采样成足够密的包", 而实测不成立:
    ///     · 触控板包率与渲染帧率不同频(常见 60Hz 包 / 120Hz 屏, 或反过来),
    ///       每帧消费掉 1~2 个包 ⇒ 位移序列是"6px、12px、6px..."的阶梯, 不是连续曲线;
    ///     · 一些触控板/驱动把精密手势折成 ±120 的整格包, 那种情况下它和鼠标滚轮完全同质。
    ///   于是"直达"表现为: 手指匀速划, 画面按包的节奏一顿一顿 —— 用户原话"不丝滑"。
    ///   ★ 参照实现(BiliDesk WheelScroll)对**所有**包统一用 tau=0.05 的指数逼近, 且实测顺滑;
    ///     它的问题只有"距离太大"(见 PrecisionPixelsPerDeltaUnit), 与缓动无关。
    ///   所以这里只借它的**曲线**, 不借它的**幅度**。
    /// ★ 取 0.05: 60fps 下每帧推进 28%(3 帧 62%、5 帧 78%), 120fps 下每帧 15%。
    ///   比 NotchTauSeconds(0.12) 快一倍多 —— 精密输入本身是连续的, 拖尾必须短, 否则就成了迟滞。
    /// </summary>
    internal const double PrecisionTauSeconds=0.05;

    /// <summary>
    /// 精密包每单位 delta 的像素数, 由设置页的滚动灵敏度缩放。**2026-11-08: 0.15 → 0.45。**
    ///
    /// ★ 为什么原来的 0.15 是 bug 而不是设计:
    ///   WPF 原生滚轮在 WheelScrollLines=3 时一格 = 3×16 = 48px, 即精密包 **0.4 px/单位**。
    ///   我们整格那一路是 45px/格(`NotchPixels`)—— 和原生 48px 基本对齐;
    ///   而精密那一路是 0.15, **只有原生的 37.5%**。
    ///   ⇒ 两条路的"同一根手指走多远"差了 2.7 倍: 滚轮一格跨 45px, 触控板同样距离只走 17px。
    ///     这不是"灵敏度调得保守", 是**两条路径的换算基准不一致**, 表现为"触控板像拖着沙袋"。
    /// ★ 取 0.45: 原生的 112.5%, 与整格 45/48 = 94% 同一个量级, 两条路终于用一把尺子。
    ///   ★ 不要照抄参照实现的 1.0(= 原生 250%): 那是它"太灵敏"的来源, 用户明确要的那个缺点。
    ///   用户仍可在设置页用灵敏度滑杆(25%~175%)在这把尺子上再缩。
    /// </summary>
    internal const double PrecisionPixelsPerDeltaUnit=0.45;

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
        // Reversing truncates the in-flight overshoot instead of first finishing it and then coming back.
        //
        // ★ 2026-11-08 改: 旧写法 `motion.Target = motion.Applied = motion.Observed = VerticalOffset`
        //   一反转就**抹掉全部历史**(目标、已应用、观测值全清)。精密输入的包很小(0.45px/单位),
        //   而人手天然有 1~2 个单位的抖动, 加上掌根误触和 X 轴噪声, 高频流里反向小包很常见 ——
        //   每来一个就把 Target 归零重来, 结果是一整段手势被切碎成若干短段, 每一段都要重新
        //   从静止加速, 观感是"划不动 / 一顿一顿"。
        //   参照实现(BiliDesk WheelScroll.OnWheel)对反向的处理只是 `Target = Current`
        //   (把残余截断到当前位置), 不动 Current 记账 —— 这也是"丝滑"的一部分。
        //   现在同样只截断: 反向时把目标拉回**当前实际偏移**, 历史记账交给
        //   WasMovedExternally(它才是"别人动过滚动条"的判据)。
        var direction = Math.Sign(delta);
        if (WasMovedExternally(viewer, motion))
            motion.Target = motion.Applied = motion.Observed = viewer.VerticalOffset;
        else if (direction != motion.Direction && direction != 0)
            motion.Target = viewer.VerticalOffset;
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
            // ★ 2026-11-08: 精密包**不再直达**, 改成和整格同一套指数逼近, 只换一个更短的 tau。
            //   旧写法 `var next = motion.Target;` 是"本帧把累积目标一次写完"。它的问题不是
            //   "写得不够快", 而是"写得**太**快": 触控板的包率和渲染帧率不同频, 每帧消费 1~2 个包,
            //   于是画面按包的节奏走阶梯(6px、12px、6px…), 手指匀速但画面一顿一顿。
            //   加一层短拖尾之后, 每一帧都从当前偏移往目标推进一段, 阶梯被抹平成连续曲线。
            //   tau 取 0.05(整格是 0.12): 精密输入本身连续, 拖尾必须短, 否则就是迟滞。
            //   ★ 残留 < 0.5px 时直接吸附到目标, 不做无限逼近 —— 否则精密包会因为
            //     "永远差一点点"而永不满足 Target == Applied, 空转烧帧。
            var tau = motion.Precision ? PrecisionTauSeconds : NotchTauSeconds;
            var next = Math.Abs(remaining) >= .5
                ? viewer.VerticalOffset + remaining * (1 - Math.Exp(-dt / tau))
                : motion.Target;
            // No new animation clock per input packet; all packets share this frame update.
            motion.Observed=viewer.VerticalOffset;
            viewer.ScrollToVerticalOffset(next);motion.Applied=next;OffsetWrites++;
            // ★ 精密包不在这里收摊: 它要留着这一次手势的订阅(见上面 PrecisionIdleMs 那段),
            //   收摊由 `motion.Target == motion.Applied` + 空闲 90ms 那条路负责。
            //   否则每个包都会退订再重订 CompositionTarget.Rendering, 白白抖动。
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
