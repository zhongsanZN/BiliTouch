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
 public string[] Options=>new[]{"--vout=direct3d11","--d3d11-upscale-mode="+Scaler,"--d3d11-hdr-mode="+Hdr};
}
public static class VideoEnhancement
{
 private sealed class Runtime {public string State="等待视频画面";public string Adapter="";}
 private static readonly object Gate=new();private static readonly Dictionary<LibVLC,Runtime> States=new();
 // ★ 真机日志：40 行里 33 次 "turning VSR ON/OFF"，1~2 秒一次。
 //   这类消息只是驱动按"当前帧是否需要放大"的瞬态判定，同类 2 秒内只落一条日志即可。
 private static long _lastVsrLogMs;
 public static event Action? Changed;
 public static string Status(LibVLC core){lock(Gate){if(!States.TryGetValue(core,out var state))return "等待视频画面";return state.State+(state.Adapter.Length>0?"\n"+state.Adapter:"");}}

 /// <summary>
 /// 把一条 LibVLC 日志解析成**用户可见的稳定状态**。
 /// ★ "turning VSR OFF" 故意返回 null：它是驱动每秒级的瞬态（当前帧无需放大），
 ///   不是能力状态。旧代码把它设成"超分待机"，于是 ON/OFF 每次都改 state、
 ///   每次都 Changed?.Invoke() ⇒ 播放器按钮 / ToolTip 每 1~2 秒抖一次。
 ///   现在状态一旦确认"已启用"就保持稳定，OFF 不覆盖，UI 不再抖。
 /// </summary>
 internal static string? ParseState(string message)
 {
  if(message.Contains("turning VSR ON"))return "厂商超分已启用（驱动已确认）";
  // turning VSR OFF：瞬态，不改变状态（见上方说明）。
  if(message.Contains("Super Resolution filter not supported") || (message.Contains("Failed to set",StringComparison.OrdinalIgnoreCase) && (message.Contains("VPE") || message.Contains("super resolution",StringComparison.OrdinalIgnoreCase))))return "驱动未支持超分，已回退普通缩放";
  if(message.Contains("Using VideoProcessor scaler",StringComparison.OrdinalIgnoreCase) || message.Contains("Using Video Processor scaler",StringComparison.OrdinalIgnoreCase))return "视频处理器缩放已启用";
  if(message.Contains("Using Super Resolution scaler"))return "厂商超分处理器已创建，等待画面放大";
  return null;
 }

 /// <summary>同类 VSR 瞬态消息 2 秒内只落一条日志（真机 1~2 秒一次切换）。</summary>
 internal static bool ShouldLogVsrToggle()
 {
  var now=Environment.TickCount64;
  var prev=Interlocked.Read(ref _lastVsrLogMs);
  if(now-prev<2000)return false;
  Interlocked.Exchange(ref _lastVsrLogMs,now);
  return true;
 }

 internal static void Observe(LibVLC core,EnhancementProfile profile)
 {
  lock(Gate)States[core]=new Runtime{State=profile.Enabled?"已请求增强，等待驱动确认":"画质增强已关闭"};
  core.Log+=(_,e)=>{
   var message=e.Message;
   var state=ParseState(message);
   string? adapter=message.Contains("Using D3D11VA",StringComparison.OrdinalIgnoreCase)?"处理设备："+message:null;
   var relevant=state!=null || adapter!=null || message.Contains("scaler",StringComparison.OrdinalIgnoreCase) || message.Contains("VSR",StringComparison.OrdinalIgnoreCase);
   if(!relevant)return;
   var vsrToggle=message.Contains("turning VSR ON") || message.Contains("turning VSR OFF");
   var logThis=!vsrToggle || ShouldLogVsrToggle();
   if(logThis)
   {
    var logPath=Path.Combine(AppPaths.DataDir,"video-enhancement.log");
    var line=$"{DateTimeOffset.Now:O} {profile.Key} {message}\n";
    ThreadPool.QueueUserWorkItem(_=>{try{File.AppendAllText(logPath,line);}catch{}});
   }
   var changed=false;
   lock(Gate){var value=States[core];if(state!=null && value.State!=state){value.State=state;changed=true;}if(adapter!=null && value.Adapter!=adapter){value.Adapter=adapter;changed=true;}}
   if(changed)Changed?.Invoke();
  };
 }
}
