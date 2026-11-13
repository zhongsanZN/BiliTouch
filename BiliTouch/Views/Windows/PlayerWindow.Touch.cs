using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Helpers;

namespace BiliTouch.Views;

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
    /// <summary>手势说明只在首次用手指点时自动弹一次(控制栏的「触控」按钮已删除)</summary>
    private bool _touchHelpShown;

    // ---- 双指捏合 = 全屏 / 退出全屏 ----
    // 手机/平板上这是最自然的"放大画面"手势: 两指张开 → 全屏, 捏合 → 退出。
    // 判定用**两指距离的相对变化**而不是绝对距离: 屏幕大小、DPI、手指粗细都不影响手感。
    private bool _pinchActive;
    private double _pinchStartDistance;
    private bool _pinchTriggered;
    /// <summary>张开到 1.6 倍触发全屏; 捏合到 0.6 倍触发退出。留足余量, 轻微抖动不会误触发。</summary>
    private const double PinchInScale = 1.6, PinchOutScale = 0.6;
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
        // ★ 2026-11-04: 按下走 OnInfoTouchDownCapture(它内部会先 CaptureTouch 再转给 OnInfoTouchDown)。
        //   捕获是"手指滑出面板/滑到视频区仍能收尾"的关键, 见那个方法的说明。
        InfoPanel.PreviewTouchDown += OnInfoTouchDownCapture;
        InfoPanel.PreviewTouchMove += OnInfoTouchMove;
        InfoPanel.PreviewTouchUp += OnInfoTouchUp;
        InfoPanel.LostTouchCapture += OnInfoTouchLostCapture;
        InfoPanel.PreviewStylusDown += OnInfoStylusDown;
        InfoPanel.PreviewStylusMove += OnInfoStylusMove;
        InfoPanel.PreviewStylusUp += OnInfoStylusUp;
        InfoPanel.PreviewMouseLeftButtonDown += OnInfoMouseDown;
        InfoPanel.PreviewMouseMove += OnInfoMouseMove;
        InfoPanel.PreviewMouseLeftButtonUp += OnInfoMouseUp;
        // ★ 2026-11-04: 控制栏的「触控」按钮已删除(用户: "触控不需要开关, 该是底层支持")。
        //   手势本来就全默认生效; 说明改成**首次用手指点视频时自动弹一次**, 之后不再打扰。
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
            // 第二根手指落下 = 捏合手势的起点。记下两指初始间距(相对值判定, 见字段说明)
            if (_videoTouches.Count == 2)
            {
                _pinchStartDistance = CurrentPinchDistance();
                _pinchActive = _pinchStartDistance > 24;   // 两指几乎重合时不做捏合
                _pinchTriggered = false;
            }
            return;
        }
        // Use the routed source, never IsMouseOver: a finger does not need a mouse cursor.
        if (DanmakuAreaPopup?.IsOpen == true && !IsDescendantOf(e.OriginalSource as DependencyObject, BtnDanmakuArea) &&
            !IsDescendantOf(e.OriginalSource as DependencyObject, ControlBar))
        {
            CloseDanmakuAreaPopup();
            e.Handled = true;
            return;
        }
        if (IsTouchControl(e.OriginalSource as DependencyObject) || AnyPlayerPopupOpen || _mp == null) return;
        // ★ 首次用手指点视频: 自动弹一次手势说明(控制栏那个「触控」按钮已经删了)。
        //   只弹一次; 这一次触摸**只用于展示**, 不继续走单击/长按 —— 否则同一帧里
        //   下面那段"点说明面板就收起"会立刻把它关掉(自己吃自己)。
        if (!_touchHelpShown)
        {
            _touchHelpShown = true;
            TouchHelp.Visibility = Visibility.Visible;
            ShowControls();
            StartControlsHideCountdown();
            e.Handled = true;
            return;
        }
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

    /// <summary>当前两指间距(取前两根按下的手指)。拿不到就返回 0, 调用方会当成"没在捏"。</summary>
    private double CurrentPinchDistance()
    {
        TouchDevice? first = null, second = null;
        foreach (var d in _videoTouches)
        {
            if (first == null) first = d;
            else if (second == null) { second = d; break; }
        }
        if (first == null || second == null) return 0;
        var a = first.GetTouchPoint(VideoAreaRoot).Position;
        var b = second.GetTouchPoint(VideoAreaRoot).Position;
        return (a - b).Length;
    }

    /// <summary>
    /// 双指捏合 → 全屏 / 退出全屏。
    ///
    /// ★ 为什么用**相对比例**而不是绝对距离: 手指间距在不同 DPI、不同屏幕、不同人手里差别很大,
    ///   1.6 倍张开 / 0.6 倍捏合是比例判据, 谁做都触发得出来。
    /// ★ 一次手势只触发一次(_pinchTriggered): 用户张开后常常还会继续挪一下, 不能连切两次。
    /// ★ 全屏走 ToggleFullscreen 之外的分支: 已经是全屏就不再重复进, 捏合退出同理。
    /// </summary>
    private void CheckPinch()
    {
        if (!_pinchActive || _pinchTriggered || _pinchStartDistance <= 0) return;
        var scale = CurrentPinchDistance() / _pinchStartDistance;
        if (scale >= PinchInScale)
        {
            _pinchTriggered = true;
            if (!_isFullscreen)
            {
                ToggleFullscreen();
                ShowSpeedHint("双指张开 · 全屏", hold: false);
            }
        }
        else if (scale <= PinchOutScale)
        {
            _pinchTriggered = true;
            if (_isFullscreen)
            {
                ToggleFullscreen();
                ShowSpeedHint("双指捏合 · 退出全屏", hold: false);
            }
        }
    }

    private void OnVideoTouchMove(object? sender, TouchEventArgs e)
    {
        // ★★★ 2026-11-04 兜底: 右栏翻页手势滑到**视频区**时, 事件会落到这里。
        //   视频区是独立的 HWND(空气层浮窗), InfoPanel 收不到它的 Move。
        //   如果此刻正有一个右栏翻页手势在进行, 就把它接过来继续算 ——
        //   否则面板会停在简介/评论中间(用户报的那个现象)。
        //
        // ★★ 判据必须同时满足两条, 缺一不可:
        //     ① `_infoDraggingPage` —— 确实有右栏手势在进行;
        //     ② **这个 TouchDevice 不在 _videoTouches 里** —— 它属于右栏那次手势,
        //        不是视频区自己按下的手指。
        //   只判 ① 会把**视频区的双指手势**误吞(那两根手指正在捏合/全屏,
        //   _videoTouches 里有它们) —— 那样双击、双指全屏、长按快进全都会失灵。
        if (_infoDraggingPage && !_videoTouches.Contains(e.TouchDevice))
        {
            if (InfoMove(e.GetTouchPoint(InfoPanel).Position)) e.Handled = true;
            return;
        }
        if (!_videoTouches.Contains(e.TouchDevice)) return;
        e.Handled = true;
        // 双指: 只做捏合, 不做进度/音量/亮度(那些是单指手势)
        if (_videoTouches.Count >= 2)
        {
            CheckPinch();
            return;
        }
        if (_touchSession == null) return;
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
        // 与 OnVideoTouchMove 同款兜底:**收尾必须执行**, 否则面板停在半路。
        // 判据同 Move(两条都要满足), 否则会吞掉视频区自己的触摸抬起。
        if (_infoDraggingPage && !_videoTouches.Contains(e.TouchDevice))
        {
            if (InfoUp()) e.Handled = true;
            _infoHorizontalDecided = false;
            return;
        }
        if (!_videoTouches.Remove(e.TouchDevice)) return;
        e.Handled = true;
        _touchHoldTimer?.Stop();
        // 双指手势收场: 少于两指就不再是捏合(剩下一根也不该接着做单指手势)
        if (_videoTouches.Count < 2) _pinchActive = false;
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
