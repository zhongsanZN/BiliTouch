using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk.Helpers;

namespace BiliDesk.Views;

public partial class PlayerWindow
{
    private readonly HashSet<TouchDevice> _videoTouches = new();
    private PlayerTouchSession? _touchSession;
    private DispatcherTimer? _touchHoldTimer, _touchTapTimer;
    private long _touchStartMs, _touchTargetMs, _touchLastTapAt;
    private Point _touchLastTapPoint, _touchStartPoint;
    private int _touchStartVolume;
    private double _touchBrightness = 1, _touchStartBrightness;
    private bool _touchDoubleTap, _touchOwnsSeek;
    private bool _lastPlayerInputWasTouch;
    /// <summary>Next time the touch gesture may repaint the overlay (see TouchUiDue).</summary>
    private long _touchUiNextAt;
    private const int TouchUiIntervalMs = 45;


    private void InitializeTouch()
    {
        Stylus.SetIsPressAndHoldEnabled(VideoAreaRoot, false);
        Stylus.SetIsFlicksEnabled(VideoAreaRoot, false);
        VideoAreaRoot.PreviewTouchDown += OnVideoTouchDown;
        VideoAreaRoot.PreviewTouchMove += OnVideoTouchMove;
        VideoAreaRoot.PreviewTouchUp += OnVideoTouchUp;
        VideoAreaRoot.LostTouchCapture += OnVideoTouchLostCapture;
        VideoAreaRoot.Unloaded += (_, _) => CancelVideoTouch();
        Deactivated += OnTouchWindowDeactivated;
        // Horizontal swipe over the right-side info panel switches 简介 / 评论 (mobile-style).
        InfoPanel.PreviewTouchDown += OnInfoTouchDown;
        InfoPanel.PreviewTouchMove += OnInfoTouchMove;
        InfoPanel.PreviewTouchUp += OnInfoTouchUp;
        InfoPanel.PreviewStylusDown += OnInfoStylusDown;
        InfoPanel.PreviewStylusMove += OnInfoStylusMove;
        InfoPanel.PreviewStylusUp += OnInfoStylusUp;
        InfoPanel.PreviewMouseLeftButtonDown += OnInfoMouseDown;
        InfoPanel.PreviewMouseMove += OnInfoMouseMove;
        InfoPanel.PreviewMouseLeftButtonUp += OnInfoMouseUp;
        // The gesture help is only advertised when the machine has a touch digitizer; the first
        // finger that lands on the video reveals it anyway, so it is discoverable on any device.
        TouchHelpButton.Visibility = TouchSupport.HasTouchDevice ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTouchWindowDeactivated(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        // The LibVLC foreground overlay is a separate owned WPF window.
        if (!IsActive && _overlayKeyboardWindow?.IsActive != true) CancelVideoTouch();
    }));

    private bool IsTouchControl(DependencyObject? source)
    {
        while (source != null && source != VideoAreaRoot)
        {
            if (source == ControlBar || source == SkipNoticeBorder || source == EndOverlay ||
                source == LoadingOverlay || source == TouchHelp || source is ButtonBase or Slider or TextBoxBase or Selector)
                return true;
            source = source is ContentElement content ? ContentOperations.GetParent(content)
                ?? (content as FrameworkContentElement)?.Parent : VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnVideoTouchDown(object? sender, TouchEventArgs e)
    {
        if (_closing) return;
        _lastPlayerInputWasTouch = true;
        if (_videoTouches.Count > 0)
        {
            // Multi-touch cancels the pending seek/hold; it never becomes a tap on release.
            e.Handled = true;
            if (e.TouchDevice.Capture(VideoAreaRoot)) _videoTouches.Add(e.TouchDevice);
            CancelTouchAction();
            return;
        }
        // Use the routed source, never IsMouseOver: a finger does not need a mouse cursor.
        if (IsTouchControl(e.OriginalSource as DependencyObject) || AnyPlayerPopupOpen || _mp == null) return;
        TouchHelpButton.Visibility = Visibility.Visible; // A real finger is proof of a touch digitizer.
        if (TouchHelp.Visibility == Visibility.Visible)
        {
            // Tapping the picture dismisses the gesture sheet instead of toggling the control bar.
            e.Handled = true;
            TouchHelp.Visibility = Visibility.Collapsed;
            ShowControls();
            StartControlsHideCountdown();
            return;
        }
        e.Handled = true; // Prevent WPF from promoting this gesture to mouse clicks/right-clicks.
        if (!e.TouchDevice.Capture(VideoAreaRoot)) return;
        _videoTouches.Add(e.TouchDevice);
        _touchStartPoint = e.GetTouchPoint(VideoAreaRoot).Position;
        _touchSession = new PlayerTouchSession(_touchStartPoint.X, _touchStartPoint.Y,
            VideoAreaRoot.ActualWidth, VideoAreaRoot.ActualHeight);
        _touchStartMs = Math.Max(0, _mp.Time);
        _touchTargetMs = _touchStartMs;
        _touchStartVolume = _volumeCache;
        _touchStartBrightness = _touchBrightness;
        _touchDoubleTap = _touchLastTapAt > 0 && Environment.TickCount64 - _touchLastTapAt <= 300 &&
            (_touchStartPoint - _touchLastTapPoint).Length <= 32;
        _touchTapTimer?.Stop();
        _touchLastTapAt = 0;
        _controlsHideTimer?.Stop();
        _touchHoldTimer?.Stop();
        _touchUiNextAt = 0; // the first move of a new gesture paints immediately
        _touchHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressThresholdMs) };
        _touchHoldTimer.Tick += (_, _) =>
        {
            _touchHoldTimer.Stop();
            if (_closing || _isLive || _ended || _mp is not { IsPlaying: true } || _touchSession?.TryHold() != true) return;
            StartFastForward();
            ShowSpeedHint($"{FastForwardRate:0.#}x 快进中 · 松手恢复", hold: true);
        };
        _touchHoldTimer.Start();
    }

    private void OnVideoTouchMove(object? sender, TouchEventArgs e)
    {
        if (!_videoTouches.Contains(e.TouchDevice)) return;
        e.Handled = true;
        if (_touchSession == null || _videoTouches.Count != 1) return;
        var point = e.GetTouchPoint(VideoAreaRoot).Position;
        _touchSession.Move(point.X, point.Y);
        if (_touchSession.Action == PlayerTouchAction.Pending) return;
        _touchHoldTimer?.Stop();
        switch (_touchSession.Action)
        {
            case PlayerTouchAction.Seek:
                if (_isLive || _mp.Length <= 0)
                {
                    ShowSpeedHint("当前视频不支持拖动进度", hold: true);
                    return;
                }
                _touchOwnsSeek = true;
                _isSeeking = true;
                // The target is recomputed on every move (release needs the exact value), but the
                // visible overlay is repainted at most ~20x/s: the video page's control layer is a
                // layered (per-pixel-alpha) WPF window, which WPF rasterizes in software — touching
                // it on every move is what made dragging feel like a GPU-less slideshow.
                _touchTargetMs = _touchSession.SeekTarget(_touchStartMs, _mp.Length);
                if (!TouchUiDue()) return;
                _updatingSlider = true;
                ProgressSlider.Value = _touchTargetMs * 1000.0 / _mp.Length;
                _updatingSlider = false;
                var deltaSeconds = (_touchTargetMs - _touchStartMs) / 1000.0;
                ShowSpeedHint($"{TouchTime(_touchTargetMs)} / {TouchTime(_mp.Length)}  ({deltaSeconds:+0;-0;0}秒) · 松手跳转", true);
                break;
            case PlayerTouchAction.Volume:
                var volume = _touchSession.VolumeTarget(_touchStartVolume);
                if (!TouchUiDue()) return;
                SetVolume(volume);
                ShowSpeedHint($"音量 {volume}%", true);
                break;
            case PlayerTouchAction.Brightness:
                _touchBrightness = _touchSession.BrightnessTarget(_touchStartBrightness);
                if (!TouchUiDue()) return;
                TouchDimmer.Opacity = 1 - _touchBrightness;
                ShowSpeedHint($"画面亮度 {_touchBrightness * 100:0}%", true);
                break;
        }
    }

    /// <summary>
    /// Rate limit for the per-move UI refresh of a touch gesture (slider/OSD/dimmer). The values
    /// themselves are still computed on every move so the release commits the exact final state.
    /// </summary>
    private bool TouchUiDue()
    {
        var now = Environment.TickCount64;
        if (now < _touchUiNextAt) return false;
        _touchUiNextAt = now + TouchUiIntervalMs;
        return true;
    }

    private static string TouchTime(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(
        ms >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");

    private void OnVideoTouchUp(object? sender, TouchEventArgs e)
    {
        if (!_videoTouches.Remove(e.TouchDevice)) return;
        e.Handled = true;
        _touchHoldTimer?.Stop();
        var action = _touchSession?.Action ?? PlayerTouchAction.Canceled;
        // Evaluate the final position too: some digitizers coalesce the last Move into Up.
        if (_touchSession != null && action != PlayerTouchAction.Canceled && _videoTouches.Count == 0)
        {
            _videoTouches.Add(e.TouchDevice);
            OnVideoTouchMove(sender, e);
            _videoTouches.Remove(e.TouchDevice);
            action = _touchSession.Action;
        }
        // The throttled preview may be a few moves behind: commit the exact final value here.
        if (_touchSession != null)
        {
            if (action == PlayerTouchAction.Volume)
                SetVolume(_touchSession.VolumeTarget(_touchStartVolume), showHint: false);
            else if (action == PlayerTouchAction.Brightness)
            {
                _touchBrightness = _touchSession.BrightnessTarget(_touchStartBrightness);
                TouchDimmer.Opacity = 1 - _touchBrightness;
            }
        }
        if (_touchOwnsSeek)
        {
            _touchOwnsSeek = false;
            _isSeeking = false;
            if (action == PlayerTouchAction.Seek && !_closing && !_isLive)
            {
                SeekTo(_touchTargetMs); // Exactly one seek, after the finger is released.
                SyncProgressNow();
            }
        }
        if (action == PlayerTouchAction.Hold)
        {
            StopFastForward();
            ShowSpeedHint("已结束快进", false);
        }
        else if (action == PlayerTouchAction.Pending && !_closing)
        {
            if (_touchDoubleTap)
            {
                // 双击 = 播放/暂停(手机端同款)。★ 不再切全屏 —— 用户明确要求。
                _touchLastTapAt = 0;
                _touchTapTimer?.Stop();
                ShowControls();
                OnPlayPauseClick(this, new RoutedEventArgs());
            }
            else
            {
                _touchLastTapAt = Environment.TickCount64;
                _touchLastTapPoint = _touchStartPoint;
                _touchTapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _touchTapTimer.Tick += (_, _) =>
                {
                    _touchTapTimer.Stop();
                    _touchLastTapAt = 0;
                    if (_closing) return;
                    if (_controlsHidden) { ShowControls(); StartControlsHideCountdown(); }
                    else HideControls();
                };
                _touchTapTimer.Start();
            }
        }
        else if (action != PlayerTouchAction.Canceled)
            ShowSpeedHint(action == PlayerTouchAction.Seek ? "进度手势结束" :
                action == PlayerTouchAction.Volume ? $"音量 {_volumeCache}%" : $"画面亮度 {_touchBrightness * 100:0}%", false);
        if (_videoTouches.Count == 0) _touchSession = null;
        if (e.TouchDevice.Captured == VideoAreaRoot) e.TouchDevice.Capture(null);
        if (action != PlayerTouchAction.Pending) StartControlsHideCountdown();
    }

    private void CancelTouchAction()
    {
        _touchHoldTimer?.Stop();
        _touchTapTimer?.Stop();
        _touchLastTapAt = 0;
        var held = _touchSession?.Action == PlayerTouchAction.Hold;
        _touchSession?.Cancel();
        if (_touchOwnsSeek) { _isSeeking = false; _touchOwnsSeek = false; UpdateProgressUi(); }
        if (held) StopFastForward();
        if (!_closing) ShowSpeedHint("触控手势已取消", false);
    }

    private void OnVideoTouchLostCapture(object? sender, TouchEventArgs e)
    {
        if (!_videoTouches.Remove(e.TouchDevice)) return;
        CancelTouchAction();
        if (_videoTouches.Count == 0) _touchSession = null;
    }

    private void CancelVideoTouch()
    {
        _touchHoldTimer?.Stop();
        _touchTapTimer?.Stop();
        if (_touchSession != null) CancelTouchAction();
        var devices = _videoTouches.ToArray();
        _videoTouches.Clear();
        _touchSession = null;
        foreach (var device in devices)
            if (device.Captured == VideoAreaRoot) device.Capture(null);
    }

    private void OnTouchHelpClick(object sender, RoutedEventArgs e)
    {
        CancelVideoTouch();
        TouchHelp.Visibility = TouchHelp.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ShowControls();
        _controlsHideTimer?.Stop();
    }

    private void OnTouchHelpClose(object sender, RoutedEventArgs e)
    {
        TouchHelp.Visibility = Visibility.Collapsed;
        StartControlsHideCountdown();
    }
}
