using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BiliTouch.Helpers;
namespace BiliTouch.Views;
public partial class LiveRoomWindow
{
    private PlayerTouchSession? _liveTouch;
    private int _liveTouchId = -1, _liveStartVolume;
    private double _liveBrightness = 1, _liveStartBrightness, _livePinchDistance;
    private readonly System.Collections.Generic.Dictionary<int, Point> _liveFingers = new();
    private long _liveLastTap;
    private void InitializeLiveTouch()
    {
        Stylus.SetIsPressAndHoldEnabled(VideoOverlay, false);
        Stylus.SetIsFlicksEnabled(VideoOverlay, false);
        VideoOverlay.PreviewTouchDown += (_, e) => {
            if (e.OriginalSource is DependencyObject source && IsInsideControls(source)) return;
            var point = e.GetTouchPoint(VideoOverlay).Position;
            _liveFingers[e.TouchDevice.Id] = point;
            VideoOverlay.CaptureTouch(e.TouchDevice);
            if (_liveFingers.Count > 1) {
                _liveTouch?.Cancel();
                var points = _liveFingers.Values.Take(2).ToArray();
                _livePinchDistance = (points[0] - points[1]).Length;
            } else {
                _liveTouchId = e.TouchDevice.Id;
                _liveTouch = new PlayerTouchSession(point.X, point.Y, VideoOverlay.ActualWidth, VideoOverlay.ActualHeight);
                _liveStartVolume = (int)LiveVolume.Value; _liveStartBrightness = _liveBrightness;
            }
            e.Handled = true;
        };
        VideoOverlay.PreviewTouchMove += (_, e) => {
            if (!_liveFingers.ContainsKey(e.TouchDevice.Id)) return;
            var point = e.GetTouchPoint(VideoOverlay).Position; _liveFingers[e.TouchDevice.Id] = point;
            if (_liveFingers.Count == 2 && _livePinchDistance > 24) {
                var points = _liveFingers.Values.ToArray(); var scale = (points[0] - points[1]).Length / _livePinchDistance;
                if ((!_full && scale > 1.6) || (_full && scale < .6)) { ToggleFullscreen(); _livePinchDistance = 0; }
            } else if (_liveTouchId == e.TouchDevice.Id && _liveTouch != null) {
                _liveTouch.Move(point.X, point.Y);
                if (_liveTouch.Action == PlayerTouchAction.Volume) { VolumeSliderSync(_liveTouch.VolumeTarget(_liveStartVolume), true); LiveGestureText.Text = "音量 " + (int)LiveVolume.Value + "%"; }
                if (_liveTouch.Action == PlayerTouchAction.Brightness) { _liveBrightness = _liveTouch.BrightnessTarget(_liveStartBrightness); LiveDim.Opacity = 1 - _liveBrightness; LiveGestureText.Text = "亮度 " + (int)(_liveBrightness * 100) + "%"; }
                LiveGestureText.Visibility = _liveTouch.Action is PlayerTouchAction.Volume or PlayerTouchAction.Brightness ? Visibility.Visible : Visibility.Collapsed;
            }
            e.Handled = true;
        };
        VideoOverlay.PreviewTouchUp += (_, e) => {
            if (!_liveFingers.Remove(e.TouchDevice.Id)) return;
            var tap = _liveFingers.Count == 0 && _liveTouch?.Action == PlayerTouchAction.Pending;
            _liveTouch = null; VideoOverlay.ReleaseTouchCapture(e.TouchDevice);
            LiveGestureText.Visibility = Visibility.Collapsed;
            if (tap) {
                var now = Environment.TickCount64;
                if (now - _liveLastTap < 300) { ToggleFullscreen(); _liveLastTap = 0; }
                else { ShowControls(); StartControlsCountdown(); _liveLastTap = now; }
            }
            e.Handled = true;
        };
        VideoOverlay.LostTouchCapture += (_, e) => { _liveFingers.Remove(e.TouchDevice.Id); _liveTouch?.Cancel(); LiveGestureText.Visibility = Visibility.Collapsed; };
        Deactivated += (_, _) => { _liveTouch?.Cancel(); _liveFingers.Clear(); LiveGestureText.Visibility = Visibility.Collapsed; };
    }
}
