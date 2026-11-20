using System.Windows;
using System.Windows.Media;
using BiliTouch.Services;
using LibVLCSharp.Shared;
namespace BiliTouch.Views;
public partial class LiveRoomWindow
{
 private static readonly SolidColorBrush EnhancementOnBrush = CreateFrozenBrush(Color.FromRgb(255, 212, 85));
 private static readonly SolidColorBrush EnhancementOffBrush = CreateFrozenBrush(Colors.White);
 private static SolidColorBrush CreateFrozenBrush(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }
 private bool _liveEnhancementSwitching,_liveEnhancementQueued,_closeAfterLiveEnhancement;
 private void InitializeLiveEnhancement()
 {
  Svc.Settings.VideoEnhancementChanged+=LiveEnhancementChanged;VideoEnhancement.Changed+=LiveEnhancementStatus;
  Closed+=(_,_)=>{Svc.Settings.VideoEnhancementChanged-=LiveEnhancementChanged;VideoEnhancement.Changed-=LiveEnhancementStatus;};UpdateLiveEnhancementButton();
 }
 private void LiveEnhancementStatus(){if(!IsClosing)Dispatcher.BeginInvoke(new Action(UpdateLiveEnhancementButton));}
 private void LiveEnhancementClick(object sender,RoutedEventArgs e)=>Svc.Settings.UpdateVideoEnhancement(active:!Svc.Settings.VideoEnhancementActive);
 private void UpdateLiveEnhancementButton()
 {
  var available=Svc.Settings.ExperimentalVideoEnhancement;
  var supported=!_vmemMode;
  var on=available && supported && Svc.Settings.VideoEnhancementActive;
  LiveEnhancementButton.Visibility=available?Visibility.Visible:Visibility.Collapsed;
  LiveEnhancementButton.IsEnabled=supported;
  LiveEnhancementButton.Foreground=on?EnhancementOnBrush:EnhancementOffBrush;
  LiveEnhancementButton.ToolTip=supported?(on?"画质增强开启":"画质增强关闭")+"\n"+VideoEnhancement.Status(_liveCore):"当前合成播放暂不支持厂商超分和 HDR 增强；显卡解码和合成仍正常启用。";
 }
 private async void LiveEnhancementChanged()
 {
  if(IsClosing)return;UpdateLiveEnhancementButton();if(_liveEnhancementSwitching){_liveEnhancementQueued=true;return;}
  _liveEnhancementSwitching=true;
  try{
   do{
    _liveEnhancementQueued=false;var core=_vmemMode?VlcCore.GetVmem():VlcCore.Get();if(ReferenceEquals(core,_liveCore))continue;
    ++_streamRequest;_changingStream=true;
    await _mediaGate.WaitAsync();
    try{
     var next=new LibVLCSharp.Shared.MediaPlayer(core){EnableKeyInput=false,EnableMouseInput=false};
     var old=_player;
     old.Playing-=OnPlaying;
     await Task.Run(old.Stop);
     if(!_vmemMode) LiveVideo.MediaPlayer=null;
     _liveCore=core;_player=next;
     _player.Playing+=OnPlaying;
     if(_vmemMode) LiveVmem.Attach(_player); else LiveVideo.MediaPlayer=_player;
     _player.EnableHardwareDecoding=VideoDecode.HardwareEnabled;
     _player.Volume=_muted?0:Svc.Settings.PlayerVolume;
     await Task.Run(old.Dispose);
    }finally{_mediaGate.Release();_changingStream=false;}
    if(!_paused && !IsClosing && !_closeAfterLiveEnhancement && Model.RoomId>0)await LoadStreamAsync();
    UpdateLiveEnhancementButton();
   }while(_liveEnhancementQueued && !IsClosing && !_closeAfterLiveEnhancement);
  }catch(Exception ex){if(!IsClosing)Svc.Toast.Show("画质增强切换失败："+ex.Message);}
  finally{_liveEnhancementSwitching=false;if(_closeAfterLiveEnhancement)Close();}
 }
}
