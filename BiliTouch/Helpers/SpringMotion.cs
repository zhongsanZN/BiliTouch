using System;
using System.Windows;

namespace BiliTouch.Helpers;

/// <summary>
/// 澎湃 OS (HyperOS) 风格解析物理弹簧内核：
/// 支持临界阻尼（ζ = 1.0，零过冲单调收敛）与欠阻尼微弹性弹簧（0 &lt; ζ &lt; 1，丝滑跟手带轻微呼吸回弹），
/// 封闭解与刷新率完全无关（30 / 60 / 120 / 144 / 240Hz 轨迹严格一致），打断时连续继承位置与速度向量。
/// </summary>
internal static class SpringMotion
{
    /// <summary>一镜到底容器展开的临界阻尼固有频率（~360ms 澎湃 OS 利落收敛，零过冲）。</summary>
    internal const double HyperOmegaContainer = 18.5;

    /// <summary>中途打断或逆向一镜到底收拢的固有频率（~310ms 柔顺落位，追手不拖沓）。</summary>
    internal const double HyperOmegaInterrupt = 20.5;

    /// <summary>卡片按压/回弹与轻弹层微动效的固有频率。</summary>
    internal const double HyperOmegaMicro = 28.0;

    /// <summary>
    /// 临界阻尼弹簧步进（ζ = 1.0，结果钳位在 [0, 1]），用于一镜到底与停靠几何进度。
    /// </summary>
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

    /// <summary>
    /// 通用解析阻尼弹簧步进（支持欠阻尼 ζ &lt; 1、临界阻尼 ζ = 1、过阻尼 ζ &gt; 1）。
    /// 使用闭式解析解，任意刷新率（30~240Hz）步进累积误差为浮点机器精度级。
    /// </summary>
    internal static (double Position, double Velocity) AdvanceDamped(
        double position,
        double velocity,
        double target,
        double seconds,
        double omega,
        double dampingRatio = 1.0)
    {
        if (seconds <= 0 || omega <= 0) return (position, velocity);
        var zeta = Math.Max(0.05, dampingRatio);
        var x0 = position - target;
        var v0 = velocity;

        double x, v;
        if (Math.Abs(zeta - 1.0) < 1e-4)
        {
            // Critically damped
            var c2 = v0 + omega * x0;
            var decay = Math.Exp(-omega * seconds);
            x = (x0 + c2 * seconds) * decay;
            v = (v0 - omega * c2 * seconds) * decay;
        }
        else if (zeta < 1.0)
        {
            // Underdamped (HyperOS elastic feel: e.g. ζ = 0.72 ~ 0.88)
            var wd = omega * Math.Sqrt(1.0 - zeta * zeta);
            var decay = Math.Exp(-zeta * omega * seconds);
            var cos = Math.Cos(wd * seconds);
            var sin = Math.Sin(wd * seconds);
            var a = x0;
            var b = (v0 + zeta * omega * x0) / wd;
            x = decay * (a * cos + b * sin);
            v = decay * ((b * wd - a * zeta * omega) * cos - (a * wd + b * zeta * omega) * sin);
        }
        else
        {
            // Overdamped
            var root = omega * Math.Sqrt(zeta * zeta - 1.0);
            var r1 = -omega * zeta + root;
            var r2 = -omega * zeta - root;
            var c1 = (v0 - r2 * x0) / (r1 - r2);
            var c2 = x0 - c1;
            var e1 = Math.Exp(r1 * seconds);
            var e2 = Math.Exp(r2 * seconds);
            x = c1 * e1 + c2 * e2;
            v = c1 * r1 * e1 + c2 * r2 * e2;
        }

        return (target + x, v);
    }

    /// <summary>
    /// 计算一镜到底容器非等比缩放时的内层画面等比补偿系数，
    /// 使封面纹理在卡片(16:9) → 播放器舞台(任意高宽比)全程保持 UniformToFill 零拉伸畸变。
    /// </summary>
    internal static (double ArtScaleX, double ArtScaleY) ComputeAspectPreservingInnerScale(double containerScaleX, double containerScaleY)
    {
        var sx = Math.Max(0.02, containerScaleX);
        var sy = Math.Max(0.02, containerScaleY);
        var sMax = Math.Max(sx, sy);
        return (sMax / sx, sMax / sy);
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
    /// 让背景遮罩、主画面容器与侧栏在同一个合成时钟下呈现澎湃 OS 的层次秩序。
    /// </summary>
    internal static double StageProgress(double p, double start, double end)
    {
        if (end <= start) return p >= end ? 1 : 0;
        return Math.Clamp((p - start) / (end - start), 0, 1);
    }

    /// <summary>C1 连续平滑阶跃（3t² - 2t³），用于淡入淡出与阴影包络，消除分段拐点硬切。</summary>
    internal static double SmoothStep(double t)
    {
        var x = Math.Clamp(t, 0, 1);
        return x * x * (3.0 - 2.0 * x);
    }
}
