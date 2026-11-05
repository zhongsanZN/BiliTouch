using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Controls;
using LibVLCSharp.Shared;
using MediaPlayer=LibVLCSharp.Shared.MediaPlayer;

internal static class AudioImagesFontsTests
{
 const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
 static T Field<T>(object o,string name)=>(T)o.GetType().GetField(name,Hidden)!.GetValue(o)!;
 static void Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Hidden)!.Invoke(o,args);
 static T Part<T>(PlayerWindow p,string name)=>(T)p.FindName(name);
 public static void Run(Action<bool,string> check,string artifacts)
 {
  var oldContext=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
  var oldFont=Svc.Settings.FontId;var oldLayout=Svc.Settings.UseWebPlayerLayout;var oldScale=Svc.Settings.UiScalePercent;
  var oldAudio=Svc.Settings.PreferredAudioQuality;
  var oldSession=Svc.Session.Current;var httpField=typeof(ApiClient).GetField("_http",Hidden)!;var oldHttp=httpField.GetValue(Svc.Api);
  var handler=new AudioFixture();using var http=new HttpClient(handler);httpField.SetValue(Svc.Api,http);
  typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,new Session{SessData="fixture",DedeUserID="42"});
  PlayerWindow? player=null;
  try
  {
   var result=Wait(Svc.Api.GetPlayUrlAsync("BVfixture",42,80));
   check(result.ok && result.actualAudio==AudioQuality.HiRes && result.audioUrl!.EndsWith("hires.m4a"),"single-object FLAC response selects the actual Hi-Res URL");
   check(result.remux?.SelectedAudioQuality==AudioQuality.HiRes && result.remux.AudioCodecs=="fLaC","short-video remux retains the selected FLAC codec and audio quality");
   handler.FlacArray=true;result=Wait(Svc.Api.GetPlayUrlAsync("BVfixture",42,64));
   check(result.actualAudio==AudioQuality.HiRes && handler.MuxedRequests==0,"720P automatic playback keeps premium audio instead of taking the muxed AAC shortcut");
   handler.FlacArray=false;handler.Flac=false;handler.DolbyObject=true;result=Wait(Svc.Api.GetPlayUrlAsync("BVfixture",42,80,AudioQuality.HiRes));
   check(result.actualAudio==AudioQuality.Dolby && result.audioUrl!.EndsWith("dolby.m4a"),"missing FLAC falls back to Dolby object audio before AAC");
   handler.Dolby=false;result=Wait(Svc.Api.GetPlayUrlAsync("BVfixture",42,80));
   check(result.actualAudio==AudioQuality.High && result.audioUrl!.EndsWith("high.m4a"),"ordinary fallback selects the highest bitrate AAC even when response order differs");
   handler.Flac=handler.Dolby=true;handler.SnakeCase=true;result=Wait(Svc.Api.GetPlayUrlAsync("BVfixture",42,80));
   check(result.actualAudio==AudioQuality.HiRes && result.audioUrl!.EndsWith("hires.m4a"),"premium audio recognizes snake-case URL fields");
   handler.SnakeCase=false;
   var keyMethod=typeof(ShortVideoCache).GetMethod("KeyOf",BindingFlags.Static|BindingFlags.NonPublic)!;
   var dir=ShortVideoCache.Dir;Directory.CreateDirectory(dir);
   var aac=Path.Combine(dir,(string)keyMethod.Invoke(null,new object[]{"BVquality",80,AudioQuality.High})!+".mp4");
   var flac=Path.Combine(dir,(string)keyMethod.Invoke(null,new object[]{"BVquality",80,AudioQuality.HiRes})!+".mp4");
   File.WriteAllBytes(aac,new byte[]{1});
   check(ShortVideoCache.TryGet("BVquality",80,AudioQuality.HiRes)==null,"an existing AAC cache cannot masquerade as Hi-Res");
   File.WriteAllBytes(flac,new byte[]{2});
   check(ShortVideoCache.TryGet("BVquality",80,AudioQuality.HiRes)==flac && ShortVideoCache.TryGet("BVquality",80,AudioQuality.High)==aac,"short-video cache isolates distinct audio tracks at the same video quality");
   File.Delete(aac);File.Delete(flac);

   var settings=new SettingsViewModel();settings.FontId="HarmonyOS";
   check(new Typeface(AppTypography.Family("HarmonyOS"),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal).TryGetGlyphTypeface(out var harmony) && harmony.FontUri.ToString().Contains("HarmonyOS_Sans_SC_Regular",StringComparison.OrdinalIgnoreCase),"HarmonyOS font resolves from the embedded official font file");
   settings.FontId="JingNan";
   check(new Typeface(AppTypography.Family("JingNan"),FontStyles.Normal,FontWeights.Bold,FontStretches.Normal).TryGetGlyphTypeface(out var jingnan) && jingnan.CharacterToGlyphMap.ContainsKey('触'),"JingNan font resolves from the supplied file and contains Chinese glyphs");
   Svc.Settings.SaveNow();using(var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.DataDir,"settings.json"))))check(json.RootElement.GetProperty("FontId").GetString()=="JingNan","font choice is included in the settings snapshot");
   Svc.Settings.SetWebPlayerLayout(false);Svc.Settings.SetUiScalePercent(100);
   player=new PlayerWindow{Width=1320,Height=860};player.Show();Pump(250);
   check(player.FontFamily.Source.Contains("荆南波波黑"),"new windows inherit the selected application font");
   Call(player,"AddDanmaku","中三 BiliTouch 弹幕字体",1,1.0,0xFFFFFFu);Pump(100);
   var dm=Part<Canvas>(player,"DanmakuCanvas");
   check(Descendants<TextBlock>(dm).Any(t=>t.FontFamily.Source.Contains("荆南波波黑")),"native VLC overlay danmaku inherits the selected font");
   settings.FontId="HarmonyOS";Pump(120);Call(player,"AddDanmaku","切换后弹幕",1,1.0,0xFFFFFFu);Pump(100);
   check(player.FontFamily.Source.Contains("HarmonyOS") && Descendants<TextBlock>(dm).All(t=>t.FontFamily.Source.Contains("HarmonyOS")),"font changes update existing windows and rebuild measured danmaku lanes");
   check(!ReferenceEquals(AppCursor.Normal,AppCursor.Pressed),"both supplied cursor states load as native application cursors");
   var arrow=new QueryCursorEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.QueryCursorEvent,Cursor=Cursors.Arrow};
   Part<Grid>(player,"VideoAreaRoot").RaiseEvent(arrow);check(arrow.Cursor==AppCursor.Normal,"ordinary app pointers use the supplied pink cursor");
   var cursorTarget=Part<Grid>(player,"VideoAreaRoot");var oldCursor=cursorTarget.Cursor;cursorTarget.Cursor=Cursors.IBeam;
   var textCursor=new QueryCursorEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.QueryCursorEvent,Cursor=Cursors.IBeam};
   Part<Grid>(player,"VideoAreaRoot").RaiseEvent(textCursor);check(textCursor.Cursor==Cursors.IBeam,"text editing retains its meaningful I-beam cursor");
   cursorTarget.Cursor=Cursors.None;
   var hiddenCursor=new QueryCursorEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.QueryCursorEvent,Cursor=Cursors.None};
   Part<Grid>(player,"VideoAreaRoot").RaiseEvent(hiddenCursor);check(hiddenCursor.Cursor==Cursors.None,"video auto-hide keeps the cursor hidden");cursorTarget.Cursor=oldCursor;

   Directory.CreateDirectory(AppPaths.CacheDir);
   var url="https://example.invalid/comment-native.png";File.Copy("BiliTouch/Assets/app.png",Path.Combine(AppPaths.CacheDir,Hashing.Md5Hex(url)+".jpg"),true);
   var picture=new CommentPicture{Url=url,Width=500,Height=500};var comment=new CommentItem{Rpid=1,UserName="图片评论用户",Content="图片在页面内查看"};comment.Pictures.Add(picture);
   Part<ItemsControl>(player,"CommentList").ItemsSource=new[]{comment};Part<System.Windows.Controls.Primitives.ToggleButton>(player,"TabComments").IsChecked=true;Pump(250);
   var thumbnail=Descendants<Border>(Part<StackPanel>(player,"CommentsPanel")).First(b=>ReferenceEquals(b.DataContext,picture));
   var surface=Part<Grid>(player,"VideoSurface");var video=Part<LibVLCSharp.WPF.VideoView>(player,"VideoView");var sameParent=video.Parent;var windows=Application.Current.Windows.Count;
   Call(player,"OpenCommentPicture",thumbnail,picture);Pump(350);
   var gallery=Field<InlineImageGallery>(player,"_commentGallery");
   check(Field<bool>(player,"_pictureSplitActive") && ReferenceEquals(gallery.Parent,Part<ContentControl>(player,"OriginalImageHost")),"original layout mounts the native gallery underneath the video");
   check(surface.VerticalAlignment==VerticalAlignment.Top && surface.ActualHeight<Part<Grid>(player,"PlayerStage").ActualHeight,"opening an image removes the tall video letterbox region and aligns video to the top");
   check(Application.Current.Windows.Count==windows && ReferenceEquals(video.Parent,sameParent),"comment image viewing creates no popup window and does not reparent native video");
   check(Descendants<Image>(gallery).Any(i=>i.Source!=null),"in-page gallery decodes the actual cached full-size image");
   Call(gallery,"SetZoom",2.0);check(gallery.Zoom==2,"in-page gallery supports native image zoom");gallery.ResetView();check(gallery.Zoom==1,"gallery reset restores the fitted image");
   Save(player,Path.Combine(artifacts,"native-comment-image-split.png"));
   settings.IsWebPlayerLayout=true;Pump(600);
   check(!Field<bool>(player,"_pictureSplitActive") && gallery.Parent is ContentControl slot && slot.Parent is Grid{Tag:"CommentPictureCell"},"web layout expands the same gallery directly inside its comment");
   check(thumbnail.Visibility==Visibility.Collapsed && gallery.IsInline,"web expansion replaces the clicked thumbnail in its own cell without a second preview panel");
   check(Part<Grid>(player,"OriginalPicturePane").Visibility==Visibility.Collapsed,"web layout does not reserve a second image area under the video");
   Save(player,Path.Combine(artifacts,"native-comment-image-web.png"));
   Call(player,"ToggleFullscreen");Pump(300);Call(player,"ToggleFullscreen");Pump(450);
   check(Field<bool>(player,"_webLayoutMounted") && gallery.IsVisible,"fullscreen restores the inline comment image after exit");
   Call(player,"CloseCommentPicture");Pump(100);check(Field<InlineImageGallery?>(player,"_commentGallery")==null,"closing a gallery releases the image and its comment slot");
   settings.IsSplitPlayerLayout=true;Pump(350);Call(player,"OpenCommentPicture",thumbnail,picture);Pump(200);Call(player,"CloseCommentPicture");
   check(double.IsNaN(surface.Height) && surface.VerticalAlignment==VerticalAlignment.Stretch,"closing the original-layout image restores normal video sizing");
   using var media=new Media(VlcCore.Get(),Path.GetFullPath("artifacts/hires-silent.m4a"),FromType.FromPath);
   var parse=Wait(media.Parse(MediaParseOptions.ParseLocal));
   var audioInfo=string.Join(',',media.Tracks.Select(t=>t.TrackType+":"+t.Codec+":"+(t.TrackType==TrackType.Audio?t.Data.Audio.Rate:0)));
   Console.WriteLine($"LOSSLESS parsed={parse} tracks={audioInfo}");
   check(media.Tracks.Any(t=>t.TrackType==TrackType.Audio && t.Codec==0x63616c66),"packaged VLC identifies the real FLAC codec in the lossless sample");
   using var decoder=new MediaPlayer(VlcCore.Get());uint decodedRate=0;long samples=0;
   decoder.SetAudioCallbacks((_,_,count,_)=>Interlocked.Add(ref samples,count),null,null,null,null);
   decoder.SetAudioFormatCallback((ref IntPtr opaque,ref IntPtr format,ref uint rate,ref uint channels)=>{decodedRate=rate;return 0;},null);
   decoder.Play(media);Pump(850);
   Console.WriteLine($"LOSSLESS decoded_rate={decodedRate} samples={samples}");
   check(decoder.IsPlaying && samples>0 && decodedRate==96000,"packaged VLC decodes actual 96 kHz FLAC PCM into a silent memory output");decoder.Stop();
   player.PlayLocalFile(Path.GetFullPath("artifacts/touch-sample-silent.mp4"));var mp=Field<MediaPlayer>(player,"_mp");mp.Mute=true;mp.Volume=0;Pump(600);
   player.GetType().GetField("_currentCid",Hidden)!.SetValue(player,42L);player.GetType().GetField("_currentBvid",Hidden)!.SetValue(player,"BVfixture");
   player.GetType().GetField("_volumeCache",Hidden)!.SetValue(player,0);player.GetType().GetField("_isLocalPlayback",Hidden)!.SetValue(player,false);
   handler.VideoOverride=Path.GetFullPath("artifacts/touch-sample-silent.mp4");handler.AudioOverride=new Uri(Path.GetFullPath("artifacts/hires-silent.m4a")).AbsoluteUri;
   var switching=(Task)player.GetType().GetMethod("SwitchAudioQualityAsync",Hidden)!.Invoke(player,new object[]{AudioQuality.HiRes})!;Wait(switching);Pump(900);
   check(Field<string>(player,"_lastAudioUrl")==handler.AudioOverride && Field<string>(player,"_listenAudioUrl")==handler.AudioOverride,"switching audio updates replay and listen-mode sources to the actual FLAC track");
   check(Field<AudioQuality>(player,"_actualAudio")==AudioQuality.HiRes && mp.IsPlaying && mp.AudioTrackCount>0,"the real video player runs its selected FLAC slave track while muted");
  }
  finally
  {
   player?.Close();Pump(150);Svc.Settings.PreferredAudioQuality=oldAudio;Svc.Settings.SetFont(oldFont);Svc.Settings.SetWebPlayerLayout(oldLayout);Svc.Settings.SetUiScalePercent(oldScale);
   httpField.SetValue(Svc.Api,oldHttp);typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,oldSession);SynchronizationContext.SetSynchronizationContext(oldContext);
  }
 }
 public static void Live()
 {
  var field=typeof(ApiClient).GetField("_http",Hidden)!;var oldHttp=field.GetValue(Svc.Api);var old=Svc.Session.Current;
  using var http=new HttpClient(new HttpClientHandler{Proxy=new WebProxy("http://127.0.0.1:7897"),UseProxy=true});http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent","Mozilla/5.0");field.SetValue(Svc.Api,http);
  var stored=JsonSerializer.Deserialize<Session>(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData/Local/BiliTouch/session.json")))!;
  typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,stored);
  try
  {
   foreach(var bvid in new[]{"BV1DB4y1x7A9","BV1E8xNzbEtd","BV1Me411L7N5"})
   {
    var detail=Wait(Svc.Api.GetVideoAsync(bvid));if(!detail.ok || detail.data==null){Console.WriteLine($"LIVE {bvid} detail unavailable");continue;}
    foreach(var qn in new[]{80,64}){var result=Wait(Svc.Api.GetPlayUrlAsync(bvid,detail.data.Cid,qn));Console.WriteLine($"LIVE {bvid} qn={qn} ok={result.ok} selected={result.actualAudio} tracks={string.Join(',',result.audioTracks.Select(t=>t.Quality+":"+t.Codecs))} audio={result.audioUrl!=null}");
     if(qn==80 && result.audioTracks.Any(t=>t.Quality==AudioQuality.Dolby)){var dolby=Wait(Svc.Api.GetPlayUrlAsync(bvid,detail.data.Cid,80,AudioQuality.Dolby));Console.WriteLine($"LIVE {bvid} explicit_dolby={dolby.actualAudio}");}}
   }
  }
  finally{field.SetValue(Svc.Api,oldHttp);typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,old);}
 }
 public static void WebCursor(Action<bool,string> check)
 {
  foreach(var cursor in new[]{AppCursor.Normal,AppCursor.Pressed})
  {
   var handle=(System.Runtime.InteropServices.SafeHandle)typeof(Cursor).GetProperty("Handle",Hidden)!.GetValue(cursor)!;
   if(!GetIconInfo(handle.DangerousGetHandle(),out var info))throw new InvalidOperationException("Cannot inspect native cursor");
   try
   {
    var bitmap=System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(info.Color,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
    check(bitmap.PixelWidth==30 && bitmap.PixelHeight==30,"native cursor is half the original width and height (30 × 30)");
   }
   finally{if(info.Color!=IntPtr.Zero)DeleteObject(info.Color);if(info.Mask!=IntPtr.Zero)DeleteObject(info.Mask);}
  }
  var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
  var view=new Microsoft.Web.WebView2.Wpf.WebView2();var window=new FluentWindow{Width=700,Height=450,Content=view};window.Show();
  try
  {
   Wait(Svc.WebView2.InitAsync(view));var loaded=new TaskCompletionSource<bool>();view.CoreWebView2.NavigationCompleted+=(_,e)=>loaded.TrySetResult(e.IsSuccess);
   view.NavigateToString("<html><body><button id='pointer' style='cursor:pointer'>按钮</button><input id='editor' style='cursor:text'><div id='resize' style='cursor:ew-resize'>拖动</div></body></html>");
   check(Wait(loaded.Task),"isolated embedded browser loads the local cursor test page");
   var result=Wait(view.CoreWebView2.ExecuteScriptAsync("(()=>{const p=document.getElementById('pointer');p.dispatchEvent(new PointerEvent('pointerover',{bubbles:true}));const normal=getComputedStyle(p).cursor;p.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true}));const pressed=getComputedStyle(p).cursor;p.dispatchEvent(new PointerEvent('pointerup',{bubbles:true}));return {normal,pressed,reset:getComputedStyle(p).cursor,text:getComputedStyle(document.getElementById('editor')).cursor,resize:getComputedStyle(document.getElementById('resize')).cursor};})()"));
   using var json=JsonDocument.Parse(result);var root=json.RootElement;
   check(root.GetProperty("normal").GetString()!.StartsWith("url("),"embedded webpage pointers use the supplied cursor image");
   var cursorUrl=root.GetProperty("normal").GetString()!.Split('"')[1];
   var svg=Uri.UnescapeDataString(cursorUrl[(cursorUrl.IndexOf(',')+1)..]);
   check(svg.Contains("width='30' height='30'") && svg.Contains("data:image/png;base64,"),"embedded webpage cursor displays at 30 × 30 using the original image");
   check(root.GetProperty("normal").GetString()!=root.GetProperty("pressed").GetString() && root.GetProperty("normal").GetString()==root.GetProperty("reset").GetString(),"embedded webpages switch cursor color on press and restore it on release");
   check(root.GetProperty("text").GetString()=="text" && root.GetProperty("resize").GetString()=="ew-resize","embedded webpage text and resize cursors retain their original purpose");
  }
  finally{view.Dispose();window.Close();SynchronizationContext.SetSynchronizationContext(context);}
 }
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
 struct CursorInfo{public int Icon;public uint X,Y;public IntPtr Mask,Color;}
 [System.Runtime.InteropServices.DllImport("user32.dll")]
 [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
 static extern bool GetIconInfo(IntPtr cursor,out CursorInfo info);
 [System.Runtime.InteropServices.DllImport("gdi32.dll")]
 [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
 static extern bool DeleteObject(IntPtr bitmap);
 static T Wait<T>(Task<T> task){var start=Environment.TickCount64;while(!task.IsCompleted){if(Environment.TickCount64-start>45000)throw new TimeoutException();Pump(20);}return task.GetAwaiter().GetResult();}
 static void Wait(Task task){var start=Environment.TickCount64;while(!task.IsCompleted){if(Environment.TickCount64-start>45000)throw new TimeoutException();Pump(20);}task.GetAwaiter().GetResult();}
 static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
 static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
 {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var item in Descendants<T>(child))yield return item;}}
 static void Save(FrameworkElement root,string path){root.UpdateLayout();var bmp=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(path);png.Save(file);}
 sealed class AudioFixture:HttpMessageHandler
 {
  public bool Flac=true,Dolby=true,FlacArray,DolbyObject,SnakeCase;public int MuxedRequests;public string? VideoOverride,AudioOverride;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri!.AbsolutePath;object data=new{};
   object Track(int id,string codec,string file,int bandwidth)=>SnakeCase?new{id,codecs=codec,base_url="https://example.invalid/"+file,bandwidth}:(object)new{id,codecs=codec,baseUrl=(id<100?VideoOverride:AudioOverride)??"https://example.invalid/"+file,bandwidth,width=640,height=360};
   if(path.EndsWith("/spi"))data=new{b_3="fixture",b_4="fixture"};
   else if(path.EndsWith("/nav"))data=new{wbi_img=new{img_url="https://example.invalid/01234567890123456789012345678901.png",sub_url="https://example.invalid/01234567890123456789012345678901.png"}};
   else if(path.Contains("playurl"))
   {
    if(request.RequestUri.Query.Contains("fnval=1&") || request.RequestUri.Query.EndsWith("fnval=1"))MuxedRequests++;
    data=new{timelength=30000,quality=80,accept_quality=new[]{80,64},accept_description=new[]{"1080P","720P"},dash=new{video=new[]{Track(64,"avc1","video.mp4",100000),Track(80,"avc1","video.mp4",200000)},audio=new[]{Track(30232,"mp4a.40.2","low.m4a",64000),Track(30280,"mp4a.40.2","high.m4a",320000)},flac=new{audio=Flac?(FlacArray?new[]{Track(30251,"fLaC","hires.m4a",1400000)}:Track(30251,"fLaC","hires.m4a",1400000)):null},dolby=new{audio=Dolby?(DolbyObject?Track(30250,"ec-3","dolby.m4a",448000):new[]{Track(30250,"ec-3","dolby.m4a",448000)}):null}}};
   }
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{code=0,data}))});
  }
 }
}
