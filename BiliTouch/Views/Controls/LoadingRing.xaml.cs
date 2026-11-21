using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BiliTouch.Views.Controls;

public partial class LoadingRing : UserControl
{
    private static readonly DoubleAnimation Spin = CreateSpin();
    private static readonly System.Collections.Generic.HashSet<LoadingRing> LiveRings = new();
    private static bool _globalPaused;

    private static DoubleAnimation CreateSpin()
    {
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        spin.Freeze();
        return spin;
    }

    /// <summary>
    /// 在跨窗「一镜到底」期间暂停底层被遮挡窗口上的所有旋转菊花，
    /// 避免 WPF 共享渲染线程每帧对被遮挡窗口执行多余的 D3D9 Present()，使入场帧率翻倍。
    /// </summary>
    internal static void SetGlobalPaused(bool paused)
    {
        if (_globalPaused == paused) return;
        _globalPaused = paused;
        foreach (var ring in LiveRings)
            ring.UpdateAnimation();
    }

    public LoadingRing()
    {
        InitializeComponent();
        Loaded += (_, _) => { LiveRings.Add(this); UpdateAnimation(); };
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => { LiveRings.Remove(this); Rot.BeginAnimation(RotateTransform.AngleProperty, null); };
    }

    private void UpdateAnimation()
    {
        var running = !_globalPaused && IsLoaded && IsVisible;
        if (running != Rot.HasAnimatedProperties)
            Rot.BeginAnimation(RotateTransform.AngleProperty, running ? Spin : null);
    }
}