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
            var requestedDecoder = args.FirstOrDefault(a => a.StartsWith("--decoder="))?[10..];
            if (requestedDecoder != null)
            {
                var selectedDecoder = VideoDecode.Choices.First(c => c.Id == requestedDecoder || c.Name.Contains(requestedDecoder, StringComparison.OrdinalIgnoreCase));
                Svc.Settings.SetDecoderAdapter(selectedDecoder.Id);
                Svc.Settings.SaveNow();
            }
            results["decoderChoices"] = VideoDecode.Choices.Select(c => new { c.Id, c.Name }).ToArray();
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
            var verifyVmem = !args.Contains("--native-renderer");
            Svc.Settings.SetVmemRenderer(verifyVmem);
            Svc.Settings.SetWebPlayerLayout(args.Contains("--web-layout"));
            var uiAssets=new[]{"0395","0397","1229","1273","1275","1316","1325","1327","1333","1334","1335","1336","1420","1490","2030","2031","2032","2114","2184","2193","2206","2208","2211","2212","2213","2214","2684","gramophone"};
            foreach(var asset in uiAssets)
                if(BiliTouch.Helpers.SkinService.Instance.Image("ui_"+asset).PixelWidth<=0)throw new InvalidOperationException("Published UI artwork missing: "+asset);
            results["themeUiResourcesLoaded"]=uiAssets.Length;
            var cursor=BiliTouch.Helpers.AppCursor.Normal;
            if(cursor==System.Windows.Input.Cursors.Arrow || cursor!=BiliTouch.Helpers.AppCursor.Pressed)throw new InvalidOperationException("Published native game cursor failed to load");
            results["nativeGameCursorLoaded"]=true;
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
            if (verifyVmem)
            {
                var vmem = (BiliTouch.Helpers.VmemVideoView)window.FindName("VmemView");
                if (vmem.PresentedFrames < 5 || vmem.Source == null)
                    throw new InvalidOperationException("Published composited video did not upload live frames");
                results["vmemPresentedFrames"] = vmem.PresentedFrames;
                results["presentationMode"] = vmem.PresentationMode;
                results["sourceWidth"] = vmem.VideoSize.Width;
                results["sourceHeight"] = vmem.VideoSize.Height;
            }
            results["selectedDecoderAdapter"] = Svc.Settings.DecoderAdapterId;
            results["decodeStatus"] = VideoDecodeStatus.Instance.Status;
            results["hardwareDecodeRequested"] = player.EnableHardwareDecoding;
            results["webLayoutMounted"] = typeof(PlayerWindow).GetField("_webLayoutMounted", BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window);
            if (args.Contains("--web-layout") && !Equals(results["webLayoutMounted"], true))
                throw new InvalidOperationException("Published web layout did not mount");
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
