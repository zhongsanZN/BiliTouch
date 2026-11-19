using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliTouch.Services;
namespace BiliTouch.Views;
public partial class PlayerWindow
{
 private static readonly SolidColorBrush EnhancementOnBrush = CreateFrozenBrush(Color.FromRgb(255, 212, 85));
 private static readonly SolidColorBrush EnhancementOffBrush = CreateFrozenBrush(Colors.White);
 private static SolidColorBrush CreateFrozenBrush(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }
 private bool _closeAfterEnhancementSwitch;
 private bool _enhancementSwitching,_enhancementQueued,_pauseAfterEnhancementSwitch;
 private void InitializeEnhancementUi()
 {
  Svc.Settings.VideoEnhancementChanged+=OnEnhancementSettingsChanged;VideoEnhancement.Changed+=OnEnhancementNativeStatus;
  Closed+=(_,_)=>{Svc.Settings.VideoEnhancementChanged-=OnEnhancementSettingsChanged;VideoEnhancement.Changed-=OnEnhancementNativeStatus;};
  UpdateEnhancementButton();
 }
 private void OnEnhancementNativeStatus(){if(!_closing)Dispatcher.BeginInvoke(new Action(UpdateEnhancementButton));}
 private void OnEnhancementClick(object sender,RoutedEventArgs e)=>Svc.Settings.UpdateVideoEnhancement(active:!Svc.Settings.VideoEnhancementActive);
 private void UpdateEnhancementButton()
 {
  if(BtnEnhancement==null)return;var available=Svc.Settings.ExperimentalVideoEnhancement;
  var supported = !_vmemMode;
  var on=available && supported && Svc.Settings.VideoEnhancementActive;
  foreach(var button in new[]{BtnEnhancement,BtnMiniEnhancement}){
   button.Visibility=available?Visibility.Visible:Visibility.Collapsed;
   button.IsEnabled=supported;
   button.Foreground=on?EnhancementOnBrush:EnhancementOffBrush;
   button.ToolTip=supported?(on?"画质增强开启":"画质增强关闭")+"\n"+VideoEnhancement.Status(_libVLC):"当前点播播放暂不支持厂商超分和 HDR 增强；显卡解码和合成仍正常启用。";
   System.Windows.Automation.AutomationProperties.SetName(button,!supported?"画质增强暂不可用":on?"关闭画质增强":"开启画质增强");
  }
 }
 private async void OnEnhancementSettingsChanged()
 {
  if(_closing)return;UpdateEnhancementButton();
  if(_enhancementSwitching){_enhancementQueued=true;return;}
  _enhancementSwitching=true;
  try{
   do{
    _enhancementQueued=false;var core=_vmemMode?VlcCore.GetVmem():VlcCore.Get();if(ReferenceEquals(core,_libVLC))continue;
    var old=_mp;var position=Math.Max(0,old.Time);var paused=old.State==LibVLCSharp.Shared.VLCState.Paused;var rate=old.Rate;
    old.TimeChanged-=OnTimeChanged;old.LengthChanged-=OnLengthChanged;old.EndReached-=OnEndReached;
    await ReplaceNativeInputAsync(null);if(_closing)break;
    VideoView.MediaPlayer=null;
    // 先将 _mp 切换到新实例再释放旧实例，避免 _closing 中途打断时 _mp 悬空指向已释放对象
    _libVLC=core;CreateNativeVideoPlayer();_mp.EnableHardwareDecoding=VideoDecode.HardwareEnabled;_mp.Volume=_volumeCache;
    await Task.Run(old.Dispose);if(_closing || _closeAfterEnhancementSwitch)break;
    _resumeAfterSwitchMs=position;_resumeByStartTime=false;_pauseAfterEnhancementSwitch=paused;
    if(!string.IsNullOrEmpty(_lastPlayUrl)){ShowLoading("正在切换画质增强…");PlayMedia(BuildMedia(_isListenMode?_listenAudioUrl??_lastPlayUrl:_lastPlayUrl,_isListenMode?null:_lastAudioUrl));_mp.SetRate(rate);}
    UpdateEnhancementButton();
   }while(_enhancementQueued && !_closing && !_closeAfterEnhancementSwitch);
  }catch(Exception ex){if(!_closing){Svc.Toast.Show("画质增强切换失败："+ex.Message);UpdateEnhancementButton();}}
  finally{_enhancementSwitching=false;if(_closeAfterEnhancementSwitch)Close();}
 }
}
