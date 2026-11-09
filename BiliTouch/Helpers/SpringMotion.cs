namespace BiliTouch.Helpers;

/// <summary>Exact critically damped motion in seconds; retargeting preserves velocity.</summary>
internal static class SpringMotion
{
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
}
