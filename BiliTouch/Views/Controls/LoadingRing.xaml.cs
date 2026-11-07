using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BiliTouch.Views.Controls;

public partial class LoadingRing : UserControl
{
    private static readonly DoubleAnimation Spin = CreateSpin();

    private static DoubleAnimation CreateSpin()
    {
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        spin.Freeze();
        return spin;
    }

    public LoadingRing()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => Rot.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void UpdateAnimation()
    {
        var running = IsLoaded && IsVisible;
        if (running != Rot.HasAnimatedProperties)
            Rot.BeginAnimation(RotateTransform.AngleProperty, running ? Spin : null);
    }
}