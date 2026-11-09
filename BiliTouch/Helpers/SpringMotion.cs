using System;
using System.Windows;

namespace BiliTouch.Helpers;

/// <summary>
/// 澎湃 OS 4 (HyperOS 4) 风格临界阻尼物理弹簧内核（ζ = 1.0，零过冲，单调收敛，打断时连续继承速度向量）。
/// </summary>
internal static class SpringMotion
{
    /// <summary>一镜到底容器展开的临界阻尼固有频率（~380ms 收敛，初段敏锐、末段柔顺零过冲）。</summary>
    internal const double HyperOmegaContainer = 26.0;

    /// <summary>中途打断或反向收拢的固有频率（~240ms 快速重定向，追手不迟滞）。</summary>
    internal const double HyperOmegaInterrupt = 34.0;

    /// <summary>卡片按压/回弹微动效的固有频率。</summary>
    internal const double HyperOmegaMicro = 38.0;

    internal static (double Position, double Velocity) Advance(
        double position, double velocity, double target, double seconds, double omega)
    {
        if (seconds <= 0) return (position, velocity);
        var displacement = position - target;
        var coefficient = velocity + omega * displacement;
        var decay = Math.Exp(-omega * seconds);
        var next = target + (displacement + coefficient * seconds) * decay;
        var nextVelocity = (velocity - omega * coefficient * seconds) * decay;
        // Progress is also used for geometry and clipping, so it must stay within the surface.
        if (next <= 0) return (0, Math.Max(0, nextVelocity));
        if (next >= 1) return (1, Math.Min(0, nextVelocity));
        return (next, nextVelocity);
    }

    internal static Rect InterpolateRect(Rect from, Rect to, double p)
    {
        var t = Math.Clamp(p, 0, 1);
        return new Rect(
            from.X + (to.X - from.X) * t,
            from.Y + (to.Y - from.Y) * t,
            Math.Max(1, from.Width + (to.Width - from.Width) * t),
            Math.Max(1, from.Height + (to.Height - from.Height) * t));
    }

    internal static double InterpolateRadius(double from, double to, double p)
        => Math.Max(0, from + (to - from) * Math.Clamp(p, 0, 1));

    /// <summary>
    /// 分层错峰进度：将主弹簧进度 <paramref name="p"/> 映射到子阶段 <c>[start, end]</c>，
    /// 让背景遮罩、主画面容器与侧栏在同一个合成时钟下呈现澎湃 OS 4 的层次秩序。
    /// </summary>
    internal static double StageProgress(double p, double start, double end)
    {
        if (end <= start) return p >= end ? 1 : 0;
        return Math.Clamp((p - start) / (end - start), 0, 1);
    }
}
