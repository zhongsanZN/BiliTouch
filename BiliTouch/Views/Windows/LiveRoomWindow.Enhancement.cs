using System.Windows;
using System.Windows.Media;
using BiliTouch.Services;
using LibVLCSharp.Shared;
namespace BiliTouch.Views;
public partial class LiveRoomWindow
{
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
  var on=Svc.Settings.ExperimentalVideoEnhancement && Svc.Settings.VideoEnhancementActive;
  LiveEnhancementButton.Visibility=Svc.Settings.ExperimentalVideoEnhancement?Visibility.Visible:Visibility.Collapsed;
  LiveEnhancementButton.Foreground=new SolidColorBrush(on?Color.FromRgb(255,212,85):Colors.White);
  LiveEnhancementButton.ToolTip=(on?"画质增强开启":"画质增强关闭")+"\n"+VideoEnhancement.Status(_liveCore);
 }
 private async void LiveEnhancementChanged()
 {
  if(IsClosing)return;UpdateLiveEnhancementButton();if(_liveEnhancementSwitching){_liveEnhancementQueued=true;return;}
  _liveEnhancementSwitching=true;
  try{
   do{
    _liveEnhancementQueued=false;var core=VlcCore.Get();if(ReferenceEquals(core,_liveCore))continue;
    ++_streamRequest;_changingStream=true;
    await _mediaGate.WaitAsync();
    try{
     var old=_player;old.Playing-=OnPlaying;await Task.Run(old.Stop);
     LiveVideo.MediaPlayer=null;await Task.Run(old.Dispose);
     _liveCore=core;_player=new LibVLCSharp.Shared.MediaPlayer(core){EnableHardwareDecoding=VideoDecode.HardwareEnabled,EnableKeyInput=false,EnableMouseInput=false};
     _player.Playing+=OnPlaying;LiveVideo.MediaPlayer=_player;_player.Volume=_muted?0:Svc.Settings.PlayerVolume;
    }finally{_mediaGate.Release();_changingStream=false;}
    if(!_paused && Model.RoomId>0)await LoadStreamAsync();UpdateLiveEnhancementButton();
   }while(_liveEnhancementQueued && !IsClosing);
  }catch(Exception ex){if(!IsClosing)Svc.Toast.Show("画质增强切换失败："+ex.Message);}
  finally{_liveEnhancementSwitching=false;if(_closeAfterLiveEnhancement)Close();}
 }
}
