using System;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using BiliTouch.Helpers;

namespace BiliTouch.Views;

/// <summary>
/// 播放器窗口的触控板横向双指滑动 / 鼠标横向滚轮（全幅双页并排中间态 + 自然过半阈值）。
/// </summary>
public partial class PlayerWindow
{
    /// <summary>触控板完整翻一页对应的累计 delta</summary>
    private const double PageFullSwipeWheelDelta = 420.0;

    /// <summary>判定"滚动流已结束"的空闲时长(ms), 与首页保持一致</summary>
    private const int PageWheelIdleMs = 180;

    private enum HorizontalWheelRegion { None, Info, Video }
    private HorizontalWheelRegion _horizontalWheelRegion;
    private DispatcherTimer? _pageWheelIdle;
    private int _pageWheelAccum;
    private long _videoWheelStartMs, _videoWheelTargetMs;
    private PrecisionTouchpadInput? _playerTouchpad, _overlayTouchpad;
    private double _precisionVideoProgress;

    private void AttachPlayerTouchpadInput(HwndSource? source)
    {
        if (source != null) _playerTouchpad ??= new PrecisionTouchpadInput(source, ResolvePrecisionTouchpadTarget);
    }

    internal PrecisionTouchpadInput.Target? ResolvePrecisionTouchpadTarget(Point point)
    {
        if (_closing || _isLive || _videoTouches.Count > 0 || AnyPlayerPopupOpen) return null;
        if (ContainsScreenPoint(VideoSurface, point))
            return new(BeginPrecisionVideo, PreviewPrecisionVideo, EndPrecisionVideo, "Player.Video");
        if (!_isFullscreen && !_isFillWindow && ContainsScreenPoint(InfoPanel, point))
            return new(() => { ResetLegacyPageWheel(); _infoWheelPreviewActive = false; },
                PreviewPrecisionInfo, EndPrecisionInfo, "Player.Info");
        return null;
    }

    private void ResetLegacyPageWheel()
    {
        _pageWheelIdle?.Stop(); _pageWheelAccum = 0;
        _horizontalWheelRegion = HorizontalWheelRegion.None;
    }

    private void PreviewPrecisionInfo(double progress)
    {
        if (_closing || _infoSwipeArmed || _pendingCaptureDevice != null) return;
        if (!_infoWheelPreviewActive) { _infoWheelOrigin = TakeOverInfoSettle(); _infoWheelPreviewActive = true; }
        var width = Math.Max(1, PageHost.ActualWidth);
        _infoWheelContactDx = -Math.Clamp(progress, -1, 1) * width;
        _infoDragDx = Math.Clamp(_infoWheelOrigin + _infoWheelContactDx, -width * .999, width * .999);
        if (!_infoDraggingPage) _infoDraggingPage = BeginPageDrag();
        if (_infoDraggingPage) ApplyPageDrag(_infoDragDx);
    }

    private void EndPrecisionInfo(bool commit)
    {
        _infoWheelPreviewActive = false;
        if (_closing || !_infoDraggingPage) return;
        var target = PageIndex + (_infoDragDx < 0 ? 1 : -1);
        var shouldSwitch = commit && target is 0 or 1 && Math.Sign(_infoWheelContactDx) == Math.Sign(_infoDragDx)
            && Math.Abs(_infoWheelContactDx) >= PageHost.ActualWidth * PageSwitchRatio;
        FinishPageDrag(shouldSwitch, shouldSwitch ? target : PageIndex);
    }

    private void BeginPrecisionVideo()
    {
        ResetLegacyPageWheel();
        _precisionVideoProgress = 0;
        _videoWheelStartMs = _videoWheelTargetMs = Math.Max(0, _mp.Time);
    }

    private void PreviewPrecisionVideo(double progress)
    {
        if (_closing || _mp.Length <= 0) return;
        _precisionVideoProgress = progress;
        var range = Math.Clamp(_mp.Length * .1, 30_000, 120_000);
        _videoWheelTargetMs = (long)Math.Clamp(_videoWheelStartMs - progress * range, 0, Math.Max(0, _mp.Length - 250));
        if (!TouchUiDue()) return;
        _updatingSlider = true;
        ProgressSlider.Value = _videoWheelTargetMs * 1000.0 / _mp.Length;
        _updatingSlider = false;
        ShowSpeedHint($"{TouchTime(_videoWheelTargetMs)} / {TouchTime(_mp.Length)} · 松手跳转", true);
    }

    private void EndPrecisionVideo(bool commit)
    {
        if (_closing) return;
        if (commit && Math.Abs(_precisionVideoProgress) > .00001 && _mp.Length > 0) SeekTo(_videoWheelTargetMs);
        SyncProgressNow(); ShowSpeedHint("进度手势结束", false);
    }

    protected override void OnHorizontalWheel(int delta)
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        HandleHorizontalWheelAt(delta, new Point(cursor.X, cursor.Y));
    }

    protected override void OnHorizontalWheelAt(int delta, Point screenPoint) => HandleHorizontalWheelAt(delta, screenPoint);

    internal void HandleHorizontalWheelAt(int delta, Point screenPoint)
    {
        if (_playerTouchpad?.SuppressHorizontalWheel == true || _overlayTouchpad?.SuppressHorizontalWheel == true) return;
        if (delta == 0 || _closing || _isLive || _videoTouches.Count > 0 || AnyPlayerPopupOpen) return;
        if (_horizontalWheelRegion == HorizontalWheelRegion.None)
        {
            // The web video overlaps the upper part of the right column. Hit-test it first.
            if (ContainsScreenPoint(VideoSurface, screenPoint))
            {
                _horizontalWheelRegion = HorizontalWheelRegion.Video;
                _videoWheelStartMs = _videoWheelTargetMs = Math.Max(0, _mp.Time);
            }
            else if (!_isFullscreen && !_isFillWindow && ContainsScreenPoint(InfoPanel, screenPoint))
                _horizontalWheelRegion = HorizontalWheelRegion.Info;
            else return;
        }
        _pageWheelAccum += delta;
        RestartPageWheelIdle();
        if (_horizontalWheelRegion == HorizontalWheelRegion.Video)
        {
            if (_mp.Length <= 0) return;
            var range = Math.Clamp(_mp.Length * 0.1, 30_000, 120_000);
            _videoWheelTargetMs = (long)Math.Clamp(_videoWheelStartMs - _pageWheelAccum / PageFullSwipeWheelDelta * range, 0, Math.Max(0, _mp.Length - 250));
            if (TouchUiDue())
            {
                _updatingSlider = true;
                ProgressSlider.Value = _videoWheelTargetMs * 1000.0 / _mp.Length;
                _updatingSlider = false;
                ShowSpeedHint($"{TouchTime(_videoWheelTargetMs)} / {TouchTime(_mp.Length)} · 松手跳转", true);
            }
            return;
        }
        if (_infoSwipeArmed || _pendingCaptureDevice != null) return;
        if (!_infoWheelPreviewActive) { _infoWheelOrigin = TakeOverInfoSettle(); _infoWheelPreviewActive = true; }
        var progress = Math.Clamp(_pageWheelAccum / PageFullSwipeWheelDelta, -1.0, 1.0);
        if (PageHost != null && PageHost.ActualWidth > 1)
        {
            _infoDragDx = Math.Clamp(_infoWheelOrigin - progress * PageHost.ActualWidth, -PageHost.ActualWidth * .999, PageHost.ActualWidth * .999);
            if (!_infoDraggingPage) _infoDraggingPage = BeginPageDrag();
            if (_infoDraggingPage) ApplyPageDrag(_infoDragDx);
        }
    }

    private static bool ContainsScreenPoint(FrameworkElement element, Point screenPoint)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        try { return new Rect(element.RenderSize).Contains(element.PointFromScreen(screenPoint)); }
        catch { return false; }
    }

    private HwndSource? _overlayWheelSource;

    private void AttachVideoOverlayWheelHook(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized -= OnVideoOverlaySourceInitialized;
            window.SourceInitialized += OnVideoOverlaySourceInitialized;
            return;
        }
        var source = HwndSource.FromHwnd(handle);
        if (ReferenceEquals(source, _overlayWheelSource)) return;
        _overlayWheelSource?.RemoveHook(OnVideoOverlayHorizontalWheel);
        _overlayTouchpad?.Dispose();
        _overlayWheelSource = source;
        _overlayTouchpad = source != null ? new PrecisionTouchpadInput(source, ResolvePrecisionTouchpadTarget) : null;
        source?.AddHook(OnVideoOverlayHorizontalWheel);
    }

    private void OnVideoOverlaySourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= OnVideoOverlaySourceInitialized;
        AttachVideoOverlayWheelHook(window);
    }

    private IntPtr OnVideoOverlayHorizontalWheel(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x020E || (message == 0x020A && (wParam.ToInt64() & 4) != 0))
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xffff);
            HandleHorizontalWheelAt(message == 0x020A ? -delta : delta, WheelScreenPoint(lParam));
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RestartPageWheelIdle()
    {
        if (_pageWheelIdle == null)
        {
            _pageWheelIdle = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(PageWheelIdleMs)
            };
            _pageWheelIdle.Tick += (_, _) =>
            {
                _pageWheelIdle.Stop();
                var region = _horizontalWheelRegion;
                _horizontalWheelRegion = HorizontalWheelRegion.None;
                var distance = _pageWheelAccum;
                _pageWheelAccum = 0;
                if (region == HorizontalWheelRegion.Video)
                {
                    if (distance != 0 && !_closing && _mp.Length > 0) SeekTo(_videoWheelTargetMs);
                    SyncProgressNow();
                    ShowSpeedHint("进度手势结束", false);
                    return;
                }
                _infoWheelPreviewActive = false;
                if (_infoDraggingPage && _pendingCaptureDevice == null)
                {
                    var target = PageIndex + (_infoDragDx < 0 ? 1 : -1);
                    FinishPageDrag((target is 0 or 1) && Math.Abs(_infoDragDx) >= PageHost.ActualWidth * PageSwitchRatio, target);
                }
            };
        }
        _pageWheelIdle.Stop();
        _pageWheelIdle.Start();
    }

    /// <summary>测试入口: 直接驱动滚轮翻页(不经原生消息)</summary>
    internal void SwitchInfoPageByWheelForTest(int direction) => SwitchInfoPageByWheel(direction);

    private void SwitchInfoPageByWheel(int direction)
    {
        if (direction == 0) return;
        if (_infoPageAnimating) CompleteInfoPageSettle();
        if (PageHost == null || PageHost.ActualWidth <= 1) return;

        var target = PageIndex + direction;
        if (target is not (0 or 1))
        {
            if (_infoDraggingPage && _pendingCaptureDevice == null)
                FinishPageDrag(switchPage: false, target);
            return;
        }

        if (_infoDraggingPage && _pendingCaptureDevice == null)
        {
            FinishPageDrag(switchPage: true, target);
            return;
        }
        if (_infoDraggingPage) return;

        var width = Math.Max(1, PageHost.ActualWidth);
        _infoDragDx = direction > 0 ? -width : width;
        if (!BeginPageDrag())
        {
            _infoPageSwitching = true;
            try
            {
                if (target == 1) SecondaryPageButton().IsChecked = true;
                else TabIntro.IsChecked = true;
            }
            finally { _infoPageSwitching = false; }
            return;
        }
        FinishPageDrag(switchPage: true, target);
    }
}
