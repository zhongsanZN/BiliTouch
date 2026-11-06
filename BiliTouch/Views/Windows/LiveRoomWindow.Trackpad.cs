using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
namespace BiliTouch.Views;
public partial class LiveRoomWindow
{
 private int _liveWheelRemainder,_livePinchRemainder,_liveHorizontalRemainder;
 private bool _livePinchConsumed,_liveHorizontalConsumed;
 private DispatcherTimer? _liveWheelIdle,_liveGestureHide;
 private HwndSource? _liveOverlaySource;
 private void HookLiveTrackpad(Window overlay)
 {
  overlay.PreviewMouseWheel+=LivePrecisionWheel;
  _liveOverlaySource=PresentationSource.FromVisual(overlay) as HwndSource;
  _liveOverlaySource?.AddHook(LiveOverlayWheelHook);
 }
 private void UnhookLiveTrackpad()
 {
  if(_liveOverlayWindow!=null)_liveOverlayWindow.PreviewMouseWheel-=LivePrecisionWheel;
  _liveOverlaySource?.RemoveHook(LiveOverlayWheelHook);_liveOverlaySource=null;
  _liveWheelIdle?.Stop();_liveGestureHide?.Stop();
 }
 private IntPtr LiveOverlayWheelHook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
 {
  if(message==0x020E){OnHorizontalWheel(unchecked((short)((wParam.ToInt64()>>16)&0xffff)));handled=true;}return IntPtr.Zero;
 }
 private void RestartLiveWheelGesture()
 {
  if(_liveWheelIdle==null){_liveWheelIdle=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(180)};_liveWheelIdle.Tick+=(_,_)=>{_liveWheelIdle.Stop();_liveWheelRemainder=_livePinchRemainder=_liveHorizontalRemainder=0;_livePinchConsumed=_liveHorizontalConsumed=false;};}
  _liveWheelIdle.Stop();_liveWheelIdle.Start();
 }
 private void LivePrecisionWheel(object sender,MouseWheelEventArgs e)
 {
  if(e.Handled || e.Delta==0 || IsClosing)return;
  RestartLiveWheelGesture();
  if((Keyboard.Modifiers&ModifierKeys.Control)!=0){HandleLivePinch(e.Delta);e.Handled=true;return;}
  var point=e.GetPosition(VideoOverlay);
  var brightness=Math.Abs(e.Delta)<120 && point.X<VideoOverlay.ActualWidth/2;
  HandleLiveVerticalWheel(e.Delta,brightness);e.Handled=true;
 }
 private void HandleLivePinch(int delta)
 {
  if(_livePinchConsumed)return;
  if(Math.Sign(delta)!=Math.Sign(_livePinchRemainder))_livePinchRemainder=0;
  _livePinchRemainder+=delta;
  if(Math.Abs(_livePinchRemainder)<120)return;
  _livePinchConsumed=true;
  if(_full!=(_livePinchRemainder>0))ToggleFullscreen();
 }
 private void HandleLiveVerticalWheel(int delta,bool brightness)
 {
  if(Math.Sign(delta)!=Math.Sign(_liveWheelRemainder))_liveWheelRemainder=0;
  _liveWheelRemainder+=delta;var steps=_liveWheelRemainder/120;
  if(steps==0)return;_liveWheelRemainder-=steps*120;
  if(brightness){_liveBrightness=Math.Clamp(_liveBrightness+steps*.05,.2,1);LiveDim.Opacity=1-_liveBrightness;ShowLiveGesture("亮度 "+(int)(_liveBrightness*100)+"%");}
  else{VolumeSliderSync((int)LiveVolume.Value+steps*5,true);ShowLiveGesture("音量 "+(int)LiveVolume.Value+"%");}
 }
 protected override void OnHorizontalWheel(int delta)
 {
  if(IsClosing || _full || delta==0 || !VideoOverlay.IsMouseOver)return;
  RestartLiveWheelGesture();if(_liveHorizontalConsumed)return;
  if(Math.Sign(delta)!=Math.Sign(_liveHorizontalRemainder))_liveHorizontalRemainder=0;
  _liveHorizontalRemainder+=delta;if(Math.Abs(_liveHorizontalRemainder)<120)return;
  _liveHorizontalConsumed=true;
  _chatVisible=_liveHorizontalRemainder<0;
  if(_fillWindow && _chatVisible)ToggleFillWindow();else ApplyChatLayout();
  ShowLiveGesture(_chatVisible?"已展开弹幕侧栏":"已收起弹幕侧栏");
 }
 private void ShowLiveGesture(string text)
 {
  LiveGestureText.Text=text;LiveGestureText.Visibility=Visibility.Visible;ShowControls();StartControlsCountdown();
  if(_liveGestureHide==null){_liveGestureHide=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(1100)};_liveGestureHide.Tick+=(_,_)=>{_liveGestureHide.Stop();LiveGestureText.Visibility=Visibility.Collapsed;};}
  _liveGestureHide.Stop();_liveGestureHide.Start();
 }
}
