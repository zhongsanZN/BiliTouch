using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using BiliTouch.Views;
namespace BiliTouch.Services;
internal static class PublishVerification
{
    internal static async Task RunAsync(Application app,string[] args)
    {
        var output = args.First(a=>a.StartsWith("--result="))[9..];
        var media = args.First(a=>a.StartsWith("--media="))[8..];
        var root = Path.Combine(Path.GetDirectoryName(output)!,"isolated-profile");
        Directory.CreateDirectory(root);
        typeof(AppPaths).GetProperty(nameof(AppPaths.DataDir))!.SetValue(null,root);
        typeof(AppPaths).GetProperty(nameof(AppPaths.CacheRoot))!.SetValue(null,root);
        var results = new Dictionary<string,object?> { ["version"]=App.AppVersion, ["baseDirectory"]=AppContext.BaseDirectory };
        PlayerWindow? window=null;
        try {
            // Reproduce the actual installed-account failure in an isolated profile.
            File.WriteAllText(Path.Combine(root,"settings.json"), """
                {"DanmakuEnabled":true,"DanmakuFilterScroll":false,"DanmakuFilterFixed":false,"DanmakuFilterColorful":false,"DanmakuFilterAdvanced":false}
                """);
            Svc.Settings.Load(); Svc.Settings.SaveNow();
            if (!Svc.Settings.HasVisibleDanmakuTypes) throw new InvalidOperationException("Legacy danmaku settings did not recover");
            results["legacyDanmakuSettingsRecovered"]=true;
            var settings=new BiliTouch.ViewModels.SettingsViewModel();
            if(settings.SkinOptions.Length<3 || !settings.SkinOptions.Contains("影之刃零 · 影境")) throw new InvalidOperationException("Published theme selector is incomplete");
            results["skinOptions"]=settings.SkinOptions;
            BiliTouch.Helpers.SkinService.Instance.Configure(BiliTouch.Helpers.SkinService.PhantomBlade,true,true);
            results["themeEffectsDescription"]=settings.SkinEffectsDescription;
            using(var wave=Application.GetResourceStream(new Uri("pack://application:,,,/BiliTouch;component/Assets/Audio/PhantomBladeZero/like.wav"))!.Stream)
            using(var sound=new System.Media.SoundPlayer(wave)) { sound.Load(); if(!sound.IsLoadCompleted) throw new InvalidOperationException("Published theme audio failed to load"); }
            results["themeAudioResourceLoaded"]=true;
            using(var icon=AppBranding.CreateTrayIcon()) {
                if(icon.Width<=0) throw new InvalidOperationException("Published tray icon did not load");
                results["trayIconWidth"]=icon.Width;
            }
            Svc.Theme.Initialize(); Svc.Settings.SetPlayerVolume(0);
            if(args.Contains("--enhancement"))Svc.Settings.UpdateVideoEnhancement(experimental:true,active:true,adapter:"auto");
            foreach(var file in new[]{"libvlc.dll","libvlccore.dll","plugins/demux/libmp4_plugin.dll","plugins/codec/libavcodec_plugin.dll"})
                if(!File.Exists(Path.Combine(VlcCore.LibVlcDirectory,file))) throw new FileNotFoundException("Required external VLC library missing",file);
            window=new PlayerWindow { Width=1100,Height=760 }; window.Show(); window.PlayLocalFile(media);
            var player=(LibVLCSharp.Shared.MediaPlayer)typeof(PlayerWindow).GetField("_mp",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            player.Volume=0;
            var deadline=DateTime.UtcNow.AddSeconds(20); long first=-1;
            while(DateTime.UtcNow<deadline) {
                await Task.Delay(100);
                if(player.IsPlaying && player.Time>0) { first=player.Time; break; }
            }
            if(first<0) throw new InvalidOperationException("Published player did not start decoding: "+player.State);
            await Task.Delay(650); var second=player.Time;
            if(second<=first+100) throw new InvalidOperationException("Published player did not advance");
            results["firstMs"]=first;results["secondMs"]=second;results["videoWidth"]=player.Media?.Tracks.FirstOrDefault(t=>t.TrackType==LibVLCSharp.Shared.TrackType.Video).Data.Video.Width;
            var core=(LibVLCSharp.Shared.LibVLC)typeof(PlayerWindow).GetField("_libVLC",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            results["enhancementRequested"]=EnhancementProfile.Current.Enabled;results["enhancementStatus"]=VideoEnhancement.Status(core);
            results["passed"]=true;
        } catch(Exception ex) { results["passed"]=false;results["error"]=ex.ToString(); }
        finally {
            if(window!=null) { window.Close(); await Task.Delay(700); }
            File.WriteAllText(output,JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown(results.TryGetValue("passed",out var ok) && Equals(ok,true) ? 0 : 1);
        }
    }
}
