using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BiliTouch.Helpers;

namespace BiliTouch.Views;

public partial class PlayerWindow
{
    private Window? _seamlessPreviousOwner;
    private bool _seamlessOwnerChanged;

    internal void PresentPrepared(Action present)
    {
        if (_seamless.EntryWindow == null || _seamless.EntryPresented) { present(); return; }
        _seamless.PendingPresentation = present;
    }

    private FrameworkElement SeamlessStage => _seamless.EntryWindow != null ? SeamlessMorphOverlay : RootGrid;

    private void MountSeamlessEntryWindow(SeamlessTransitionDirector.SeamlessAnchor anchor)
    {
        if (anchor.SourceWindow == null || !anchor.SourceWindow.TryGetTarget(out var source) ||
            source.Content is not FrameworkElement content || !source.IsVisible) return;
        var dpi = VisualTreeHelper.GetDpi(content);
        var origin = content.PointToScreen(new Point());
        var extent = content.PointToScreen(new Point(content.ActualWidth, content.ActualHeight));

        if (_seamless.EntryWindow is { } existingStage)
        {
            var updatedRect = new Rect((anchor.ScreenPixelRect.X - origin.X) / dpi.DpiScaleX,
                (anchor.ScreenPixelRect.Y - origin.Y) / dpi.DpiScaleY,
                anchor.ScreenPixelRect.Width / dpi.DpiScaleX, anchor.ScreenPixelRect.Height / dpi.DpiScaleY);
            _seamless.StartRect = _seamless.EndRect = updatedRect;
            _seamless.StartRadius = _seamless.EndRadius = anchor.CornerRadius;
            SeamlessHeroCard.Width = updatedRect.Width;
            SeamlessHeroCard.Height = updatedRect.Height;
            ApplySeamlessMorphFrame(0);
            return;
        }
        // This is a transition within the source app. A temporary visible owner
        // also keeps launch-animation tools (e.g. Windhawk Mobile Open Animation)
        // from replacing it with an independent app-icon splash. Restore ownership
        // afterwards so normal minimize/fullscreen behavior is unchanged.
        _seamlessPreviousOwner = Owner;
        _seamlessOwnerChanged = !ReferenceEquals(Owner, source);
        if (_seamlessOwnerChanged) Owner = source;
        // A captionless transparent tool surface is visible before the native player
        // is shown. Its first frame is the source card over the live source window.
        var stage = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Owner = source,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = origin.X / dpi.DpiScaleX, Top = origin.Y / dpi.DpiScaleY,
            Width = (extent.X - origin.X) / dpi.DpiScaleX, Height = (extent.Y - origin.Y) / dpi.DpiScaleY,
            IsHitTestVisible = false
        };
        RootGrid.Children.Remove(SeamlessMorphOverlay);
        stage.Content = SeamlessMorphOverlay;
        _seamless.EntryWindow = stage;
        _seamless.EntryPresented = false;
        stage.ContentRendered += (_, _) =>
        {
            _seamless.EntryPresented = true;
            // The source frame must reach its own compositor surface before
            // Show() can expose the native player's startup or maximize surface.
            stage.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
            {
                var present = _seamless.PendingPresentation;
                _seamless.PendingPresentation = null;
                if (!_closing) present?.Invoke();
            }));
        };
        EnsureSeamlessTransformsMounted();
        RenderOptions.SetBitmapScalingMode(SeamlessHeroCard, BitmapScalingMode.Linear);
        var initialRect = new Rect((anchor.ScreenPixelRect.X - origin.X) / dpi.DpiScaleX,
            (anchor.ScreenPixelRect.Y - origin.Y) / dpi.DpiScaleY,
            anchor.ScreenPixelRect.Width / dpi.DpiScaleX, anchor.ScreenPixelRect.Height / dpi.DpiScaleY);
        _seamless.StartRect = _seamless.EndRect = initialRect;
        _seamless.StartRadius = _seamless.EndRadius = anchor.CornerRadius;
        SeamlessHeroCard.Width = initialRect.Width;
        SeamlessHeroCard.Height = initialRect.Height;
        ApplySeamlessMorphFrame(0);
        stage.SourceInitialized += (_, _) =>
        {
            DwmInterop.SetTransitionsDisabled(stage, true);
            if (PresentationSource.FromVisual(stage) is HwndSource hwnd)
                hwnd.AddHook((System.IntPtr _, int message, System.IntPtr _, System.IntPtr _, ref bool handled) =>
                { if (message == 0x0084) { handled = true; return new System.IntPtr(-1); } return System.IntPtr.Zero; });
        };
        stage.Show();
        stage.UpdateLayout();
    }

    private void UnmountSeamlessEntryWindow()
    {
        if (_seamless.EntryWindow is not { } stage) return;
        _seamless.EntryWindow = null;
        _seamless.PendingPresentation = null;
        _seamless.EntryPresented = false;
        stage.Content = null;
        RootGrid.Children.Add(SeamlessMorphOverlay);
        stage.Close();
        if (_seamlessOwnerChanged) Owner = _seamlessPreviousOwner;
        _seamlessPreviousOwner = null;
        _seamlessOwnerChanged = false;
    }
}
