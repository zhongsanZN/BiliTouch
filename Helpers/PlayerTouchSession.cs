namespace BiliDesk.Helpers;

internal enum PlayerTouchAction { Pending, Seek, Volume, Brightness, Hold, Canceled }

/// <summary>One finger, one locked gesture. Coordinates and thresholds are in WPF DIPs.</summary>
internal sealed class PlayerTouchSession(double x, double y, double width, double height)
{
    public const double Slop = 12;
    public PlayerTouchAction Action { get; private set; }
    public double DeltaX { get; private set; }
    public double DeltaY { get; private set; }
    public double Width { get; } = Math.Max(1, width);
    public double Height { get; } = Math.Max(1, height);

    public void Move(double nextX, double nextY)
    {
        DeltaX = nextX - x;
        DeltaY = nextY - y;
        if (Action != PlayerTouchAction.Pending || Math.Max(Math.Abs(DeltaX), Math.Abs(DeltaY)) < Slop) return;
        Action = Math.Abs(DeltaX) >= Math.Abs(DeltaY) ? PlayerTouchAction.Seek
            : x < Width / 2 ? PlayerTouchAction.Brightness : PlayerTouchAction.Volume;
    }

    public bool TryHold()
    {
        if (Action != PlayerTouchAction.Pending) return false;
        Action = PlayerTouchAction.Hold;
        return true;
    }

    public void Cancel() => Action = PlayerTouchAction.Canceled;

    // A full-width swipe moves 10% of a video, bounded to a useful 30..120 seconds.
    public long SeekTarget(long startMs, long durationMs) => (long)Math.Clamp(
        startMs + DeltaX / Width * Math.Clamp(durationMs * 0.1, 30_000, 120_000),
        0, Math.Max(0, durationMs - 250));

    public int VolumeTarget(int initial) => (int)Math.Round(Math.Clamp(initial - DeltaY / Height * 100, 0, 100));
    public double BrightnessTarget(double initial) => Math.Clamp(initial - DeltaY / Height, 0.2, 1);
}
