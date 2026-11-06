using LibVLCSharp.Shared;
using System.IO;
namespace BiliTouch.Services;
public sealed record EnhancementProfile(bool Enabled,string Scaler,string Hdr,string Adapter)
{
 public string Key=>$"{Enabled}:{Scaler}:{Hdr}:{Adapter}";
 public static EnhancementProfile Current{
  get{var s=SettingsStore.Instance;var on=s.ExperimentalVideoEnhancement && s.VideoEnhancementActive;
   var scaler=!on?"linear":s.VideoEnhancementMode=="视频处理器缩放"?"processor":GraphicsAdapters.Selected is{HasVendorSuperResolution:false}?"processor":"super";
   return new(on,scaler,on && s.VideoEnhancementHdr && GraphicsAdapters.Selected?.VendorId==0x10de?"generate":"auto",s.EnhancementAdapterId);
  }
 }
 public string[] Options=>new[]{"--verbose=2","--vout=direct3d11","--d3d11-upscale-mode="+Scaler,"--d3d11-hdr-mode="+Hdr};
}
public static class VideoEnhancement
{
 private sealed class Runtime {public string State="等待视频画面";public string Adapter="";}
 private static readonly object Gate=new();private static readonly Dictionary<LibVLC,Runtime> States=new();
 public static event Action? Changed;
 public static string Status(LibVLC core){lock(Gate){if(!States.TryGetValue(core,out var state))return "等待视频画面";return state.State+(state.Adapter.Length>0?"\n"+state.Adapter:"");}}
 internal static void Observe(LibVLC core,EnhancementProfile profile)
 {
  lock(Gate)States[core]=new Runtime{State=profile.Enabled?"已请求增强，等待驱动确认":"画质增强已关闭"};
  core.Log+=(_,e)=>{
   var message=e.Message;string? state=null;string? adapter=null;
   if(message.Contains("Using D3D11VA",StringComparison.OrdinalIgnoreCase))adapter="处理设备："+message;
   if(message.Contains("turning VSR ON"))state="厂商超分已启用（驱动已确认）";
   else if(message.Contains("turning VSR OFF"))state="超分待机：当前画面无需放大";
   else if(message.Contains("Super Resolution filter not supported") || (message.Contains("Failed to set",StringComparison.OrdinalIgnoreCase) && (message.Contains("VPE") || message.Contains("super resolution",StringComparison.OrdinalIgnoreCase))))state="驱动未支持超分，已回退普通缩放";
   else if(message.Contains("Using VideoProcessor scaler",StringComparison.OrdinalIgnoreCase) || message.Contains("Using Video Processor scaler",StringComparison.OrdinalIgnoreCase))state="视频处理器缩放已启用";
   else if(message.Contains("Using Super Resolution scaler"))state="厂商超分处理器已创建，等待画面放大";
   var relevant=state!=null || adapter!=null || message.Contains("scaler",StringComparison.OrdinalIgnoreCase) || message.Contains("VSR",StringComparison.OrdinalIgnoreCase);
   if(!relevant)return;
   lock(Gate){var value=States[core];if(state!=null)value.State=state;if(adapter!=null)value.Adapter=adapter;}
   try{File.AppendAllText(Path.Combine(AppPaths.DataDir,"video-enhancement.log"),$"{DateTimeOffset.Now:O} {profile.Key} {message}\n");}catch{}
   Changed?.Invoke();
  };
 }
}
