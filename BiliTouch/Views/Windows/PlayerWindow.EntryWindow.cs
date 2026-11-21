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

    private FrameworkElement SeamlessStage => (_seamless.EntryWindow != null && !_seamless.HandedOffToPlayer) ? SeamlessMorphOverlay : RootGrid;

    private void MountSeamlessEntryWindow(SeamlessTransitionDirector.SeamlessAnchor anchor)
    {
        if (anchor.SourceWindow == null || !anchor.SourceWindow.TryGetTarget(out var source) ||
            source.Content is not FrameworkElement content || !source.IsVisible) return;
        _seamless.SourceMainWindow = source as MainWindow;
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
            var host = SeamlessHeroHost ?? (FrameworkElement)SeamlessHeroCard;
            host.Width = updatedRect.Width;
            host.Height = updatedRect.Height;
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

        // 捕获源窗口单帧静态快照（限宽 960px，~2~3ms 一次完成）作为非分层 D3D9 硬件加速舞台背景。
        // ★ 绝不再给 SeamlessWindowSnapshot 外套第二层 BitmapCache(1.0)：ImageBrush(RenderTargetBitmap)
        //   本身就是静态 GPU 纹理，重复缓存 3040×2108 全屏表面会白白吞掉 25MB 显存与大量填充率。
        var snapshotBrush = anchor.SourceWindowBackdrop
            ?? SeamlessTransitionDirector.CaptureWindowSnapshotBrush(source);
        anchor.SourceWindowBackdrop = snapshotBrush;
        if (SeamlessWindowSnapshot != null)
        {
            SeamlessWindowSnapshot.CacheMode = null;
            SeamlessWindowSnapshot.Background = snapshotBrush;
            SeamlessWindowSnapshot.Opacity = snapshotBrush != null ? 1.0 : 0.0;
            SeamlessWindowSnapshot.Visibility = snapshotBrush != null ? Visibility.Visible : Visibility.Collapsed;
            SeamlessWindowSnapshot.HorizontalAlignment = HorizontalAlignment.Stretch;
            SeamlessWindowSnapshot.VerticalAlignment = VerticalAlignment.Stretch;
            SeamlessWindowSnapshot.Width = SeamlessWindowSnapshot.Height = double.NaN;
            SeamlessWindowSnapshot.Margin = new Thickness(0);
            RenderOptions.SetBitmapScalingMode(SeamlessWindowSnapshot, BitmapScalingMode.Linear);
        }
        if (SeamlessBackdrop != null)
        {
            SeamlessBackdrop.Visibility = Visibility.Visible;
        }
        if (SeamlessPlayerShell != null)
        {
            SeamlessPlayerShell.Visibility = Visibility.Visible;
        }

        // 若快照可用则使用纯 D3D9 硬件加速非透明无边框窗；仅在极端快照失败时回退透明层
        var useOpaqueHardware = snapshotBrush != null;
        var stageW = (extent.X - origin.X) / dpi.DpiScaleX;
        var stageH = (extent.Y - origin.Y) / dpi.DpiScaleY;
        var stage = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = !useOpaqueHardware,
            Background = useOpaqueHardware ? new SolidColorBrush(Color.FromRgb(0x0A, 0x0C, 0x10)) : Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Owner = source,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = origin.X / dpi.DpiScaleX, Top = origin.Y / dpi.DpiScaleY,
            Width = stageW, Height = stageH,
            IsHitTestVisible = false
        };
        RootGrid.Children.Remove(SeamlessMorphOverlay);
        stage.Content = SeamlessMorphOverlay;
        _seamless.EntryWindow = stage;
        _seamless.EntryPresented = false;
        BiliTouch.Views.Controls.LoadingRing.SetGlobalPaused(true);
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
        _seamless.ShellEndRect = new Rect(0, 0, Math.Max(1, stageW), Math.Max(1, stageH));
        var heroHost = SeamlessHeroHost ?? (FrameworkElement)SeamlessHeroCard;
        heroHost.Width = initialRect.Width;
        heroHost.Height = initialRect.Height;
        if (SeamlessPlayerShell != null)
        {
            SeamlessPlayerShell.Width = Math.Max(1, stageW);
            SeamlessPlayerShell.Height = Math.Max(1, stageH);
        }
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

    private void UnmountSeamlessEntryWindow(bool restoreOwnership = true)
    {
        BiliTouch.Views.Controls.LoadingRing.SetGlobalPaused(false);
        if (_seamless.EntryWindow is { } stage)
        {
            _seamless.EntryWindow = null;
            _seamless.PendingPresentation = null;
            _seamless.EntryPresented = false;
            stage.Content = null;
            if (!RootGrid.Children.Contains(SeamlessMorphOverlay))
                RootGrid.Children.Add(SeamlessMorphOverlay);
            stage.Close();
        }
        if (restoreOwnership && _seamlessOwnerChanged)
        {
            Owner = _seamlessPreviousOwner;
            _seamlessPreviousOwner = null;
            _seamlessOwnerChanged = false;
        }
    }
}
