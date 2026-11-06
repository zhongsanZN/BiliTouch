using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
using BiliTouch.Views.Pages;
internal static class Release020Tests
{
 const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
 static T Field<T>(object o,string n)=>(T)o.GetType().GetField(n,Hidden)!.GetValue(o)!;
 static object? Call(object o,string n,params object[] args)=>o.GetType().GetMethod(n,Hidden)!.Invoke(o,args);
 static void Pump(int ms){var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f);}
 static void Until(Func<bool> ready,int timeout=15000){var start=Environment.TickCount64;while(!ready()){if(Environment.TickCount64-start>timeout)throw new TimeoutException("release020 condition timeout");Pump(25);}}
 static IEnumerable<T> Desc<T>(DependencyObject d)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(d);i++){var c=VisualTreeHelper.GetChild(d,i);if(c is T t)yield return t;foreach(var x in Desc<T>(c))yield return x;}}
 static void Save(FrameworkElement e,string path){var image=new RenderTargetBitmap(Math.Max(1,(int)e.ActualWidth),Math.Max(1,(int)e.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(e);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);png.Save(file);}
 public static void Run(Action<bool,string> check,string output)
 {
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
  var settings=Svc.Settings;var snapshot=typeof(SettingsStore).GetMethod("Snapshot",Hidden)!;var old=(AppSettings)snapshot.Invoke(settings,null)!;
  var window=new Window{Width=1180,Height=860};PlayerWindow? player=null;LiveRoomWindow? live=null;
  try{
   SkinService.Instance.Configure(SkinService.StarParty,true,true);Svc.Theme.SetMode(AppTheme.Dark);
   var vm=new SettingsViewModel();var page=new SettingsPage{DataContext=vm};window.Content=page;window.Show();Pump(200);
   var fonts=Desc<ComboBox>(page).Single(c=>c.DisplayMemberPath=="Name" && ReferenceEquals(c.ItemsSource,AppTypography.Choices));
   foreach(var font in AppTypography.Choices){fonts.SelectedValue=font.Id;Pump(70);check(Desc<TextBlock>(fonts).Any(t=>t.Text==font.Name),"font picker displays the font name after selecting "+font.Id);}
   var tabs=Desc<RadioButton>(page).Where(b=>b.GroupName=="SettingsTab").ToArray();check(tabs.Length==4 && tabs.All(t=>t.ActualHeight>=30 && t.ActualHeight<=36),"four settings pills fit inside the header without vertical clipping");
   Save(page,Path.Combine(output,"release020-settings.png"));
   vm.IsPlaybackTab=true;Pump(100);vm.ExperimentalVideoEnhancement=true;vm.VideoEnhancementActive=false;
   Pump(150);Desc<ScrollViewer>(page).First(v=>v.Content is StackPanel).ScrollToEnd();Pump(200);
   check(Desc<CheckBox>(page).Any(c=>System.Windows.Data.BindingOperations.GetBinding(c,ToggleButton.IsCheckedProperty)?.Path.Path=="ExperimentalVideoEnhancement"),"experimental enhancement controls are in the playback settings tree even without filter rules");Save(page,Path.Combine(output,"release020-experimental.png"));
   SearchLayout(check,window,output);
   var icons=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(40)};foreach(var kind in new[]{"Like","Coin","Favorite","Puzzle"})icons.Children.Add(new SkinActionIcon{Kind=kind,Count=2,Width=64,Height=64,Margin=new Thickness(12)});window.Content=icons;Pump(80);Save(icons,Path.Combine(output,"release020-action-icons.png"));
   check(new[]{"action_like","action_coin","action_coins","action_favorite","action_puzzle"}.All(n=>SkinService.Instance.Image(n).PixelWidth>0),"all five requested original theme assets are packaged and readable");
   check(GraphicsAdapters.All.Count>0 && GraphicsAdapters.All.All(a=>a.Id.Length>0 && a.Name.Length>0),"DXGI enumerates named physical display adapters with stable IDs");
   Console.WriteLine("GPU inventory: "+string.Join(" | ",GraphicsAdapters.All.Select(a=>a.Name+" / "+a.Vendor)));
   settings.UpdateVideoEnhancement(experimental:false,active:true,adapter:"auto",mode:"自动厂商增强",hdr:false);
   player=new PlayerWindow{Width=1240,Height=820};player.Show();Pump(120);
   check(((Button)player.FindName("BtnEnhancement")).Visibility==Visibility.Collapsed,"enhancement controls stay hidden until the experimental setting is enabled");
   var scale=(double)typeof(PlayerWindow).GetMethod("DanmakuViewportScale",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{300d})!;
   check(scale>=.57 && scale<.65,"docked video reduces danmaku to about sixty percent without changing the saved font preference");
   var sample=Path.GetFullPath("artifacts/touch-sample-silent.mp4");player.PlayLocalFile(sample);var normal=Field<LibVLCSharp.Shared.MediaPlayer>(player,"_mp");normal.Volume=0;Until(()=>normal.IsPlaying && normal.Time>0);
   settings.UpdateVideoEnhancement(experimental:true,active:true);
   Until(()=>!Field<bool>(player,"_enhancementSwitching") && Field<LibVLCSharp.Shared.MediaPlayer>(player,"_mp").IsPlaying,25000);
   var enhanced=Field<LibVLCSharp.Shared.MediaPlayer>(player,"_mp");enhanced.Volume=0;
   check(!ReferenceEquals(enhanced,normal) && ((Button)player.FindName("BtnEnhancement")).IsVisible,"turning enhancement on replaces the native player and reveals its real control");
   check(EnhancementProfile.Current.Options.Contains("--d3d11-upscale-mode=super") || EnhancementProfile.Current.Scaler=="processor","enhancement is configured with actual instance-level scaler options");
   Until(()=>enhanced.IsPlaying && enhanced.Time>0,25000);
   var core=Field<LibVLCSharp.Shared.LibVLC>(player,"_libVLC");Until(()=>!VideoEnhancement.Status(core).StartsWith("已请求"),15000);Console.WriteLine("Enhancement runtime: "+VideoEnhancement.Status(core));
   check(VideoEnhancement.Status(core).Contains("超分") || VideoEnhancement.Status(core).Contains("处理器"),"runtime scaler status is distinct from the decoding-adapter message");
   check(enhanced.IsPlaying && enhanced.Time>0,"enhanced native pipeline actually decodes video");
   settings.UpdateVideoEnhancement(active:false);Until(()=>!Field<bool>(player,"_enhancementSwitching") && Field<LibVLCSharp.Shared.MediaPlayer>(player,"_mp").IsPlaying,25000);
   check(ReferenceEquals(Field<LibVLCSharp.Shared.LibVLC>(player,"_libVLC"),VlcCore.Get()),"turning enhancement off returns to the retained normal core without disposing active core instances");
   player.Close();Until(()=>!player.IsLoaded);player=null;
   live=new LiveRoomWindow{Width=1140,Height=760};live.Show();Pump(150);settings.SetPlayerVolume(40);
   Call(live,"HandleLiveVerticalWheel",-30,true);Call(live,"HandleLiveVerticalWheel",-30,true);Call(live,"HandleLiveVerticalWheel",-30,true);
   check(((Border)live.FindName("LiveDim")).Opacity==0,"small precision packets accumulate without a brightness jump per packet");
   Call(live,"HandleLiveVerticalWheel",-30,true);
   check(Math.Abs(((Border)live.FindName("LiveDim")).Opacity-.05)<.001,"left-side precision gesture adjusts brightness after one accumulated notch");
   Call(live,"HandleLiveVerticalWheel",120,false);
   check((int)((Slider)live.FindName("LiveVolume")).Value>0,"precision wheel shares the live volume slider and saved volume state");
   Call(live,"HandleLivePinch",120);check(Field<bool>(live,"_full"),"trackpad pinch enters live fullscreen");Call(live,"HandleLivePinch",-120);check(Field<bool>(live,"_full"),"one pinch stream cannot flip fullscreen back and forth");
   live.Close();Until(()=>!live.IsLoaded);live=null;
   Notifications(check,window,output);
  }finally{player?.Close();live?.Close();window.Close();Pump(800);typeof(SettingsStore).GetMethod("ApplySanitized",Hidden)!.Invoke(settings,new object[]{old});settings.SaveNow();}
 }
 static void SearchLayout(Action<bool,string> check,Window window,string output)
 {
  var vm=new SearchViewModel();var page=new SearchPage{DataContext=vm};window.Content=page;Pump(160);
  foreach(var width in new[]{1180d,680d}){
   window.Width=width;Pump(100);
   var body=(Grid)page.FindName("SearchResultsBody");var tabs=(Border)page.FindName("SearchKindTabs");var card=(Border)page.FindName("SearchCard");
   check(body.TranslatePoint(new Point(),page).Y==112,"all search result kinds start below the same 112 DIP header at "+width);
   var cb=card.TransformToAncestor(page).TransformBounds(new Rect(card.RenderSize));var tb=tabs.TransformToAncestor(page).TransformBounds(new Rect(tabs.RenderSize));
   check(!cb.IntersectsWith(tb),"collapsed search input does not overlap the four category tabs at "+width);
   Call(page,"OpenCard");Pump(420);check(!tabs.IsHitTestVisible && tabs.Opacity==0,"expanded search history cannot overlap active category buttons");
   Save(page,Path.Combine(output,"release020-search-open-"+width+".png"));
   Call(page,"CloseCard");Pump(500);check(((Border)page.FindName("SearchContentBox")).Visibility==Visibility.Collapsed && tabs.Opacity==1,"collapse removes the history border and restores category buttons");
   Save(page,Path.Combine(output,"release020-search-closed-"+width+".png"));
  }
  window.Width=1180;
 }
 static void Notifications(Action<bool,string> check,Window window,string output)
 {
  var httpField=typeof(ApiClient).GetField("_http",Hidden)!;var old=httpField.GetValue(Svc.Api);var session=Svc.Session.Current;
  var fixture=new NoticeFixture();using var http=new HttpClient(fixture);httpField.SetValue(Svc.Api,http);
  typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,new Session{SessData="fixture",BiliJct="fixture",DedeUserID="42"});
  try{
   var vm=new MessagesViewModel();vm.Channel="回复与@";Until(()=>!vm.NotificationLoading);
   check(vm.Notifications.Count==2 && vm.Notifications.Any(n=>n.Kind=="reply") && vm.Notifications.Any(n=>n.Kind=="at"),"reply and mention endpoints merge into one chronological module");
   vm.Channel="收到的喜欢";Until(()=>!vm.NotificationLoading);
   check(vm.Notifications.Count==2 && vm.Notifications.All(n=>n.Kind=="like"),"likes combine the latest and accumulated notification shapes without duplicates");
   vm.Channel="新增粉丝";Until(()=>!vm.NotificationLoading);
   check(vm.Notifications.Count==1 && vm.Notifications[0].UserName=="新粉丝" && vm.Notifications[0].UserId==77,"recent-fan module uses profile names, identities and follow times");
   var page=new MessagesPage{DataContext=vm};window.Content=page;Pump(140);Save(page,Path.Combine(output,"release020-notifications.png"));
   check(Desc<RadioButton>(page).Count(c=>c.GroupName=="MessageChannel")==4,"message modules are exposed by the actual page navigation");
  }finally{httpField.SetValue(Svc.Api,old);typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,session);}
 }
 internal static void LiveNotifications(string sessionPath)
 {
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
  typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,JsonSerializer.Deserialize<Session>(File.ReadAllText(sessionPath)));
  foreach(var kind in new[]{"reply","at","like","fans"}){
   var task=Svc.Api.GetMessageNotificationsAsync(kind);Until(()=>task.IsCompleted);var result=task.GetAwaiter().GetResult();Console.WriteLine(JsonSerializer.Serialize(new{kind,result.ok,result.error,count=result.items.Count,result.more}));
   if(result.ok && result.items.Count>0 && result.more){var last=result.items.Last();long.TryParse(last.Id.Split(':').Last(),out var id);var next=Svc.Api.GetMessageNotificationsAsync(kind,2,id,last.Timestamp);Until(()=>next.IsCompleted);var page=next.GetAwaiter().GetResult();Console.WriteLine(JsonSerializer.Serialize(new{kind,page=2,page.ok,page.error,count=page.items.Count,newIds=page.items.Count(n=>result.items.All(x=>x.Id!=n.Id))}));}
  }
 }
 sealed class NoticeFixture:HttpMessageHandler
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)
  {
   var path=r.RequestUri!.AbsolutePath;
   var data=path.EndsWith("followers")?"""{"list":[{"mid":77,"uname":"新粉丝","face":"","sign":"关注关系说明","mtime":1700000002}]}""":path.EndsWith("like")?"""{"latest":{"items":[{"id":11,"like_time":1700000004,"users":[{"mid":66,"nickname":"喜欢这条内容的人"}],"item":{"title":"视频标题","uri":"https://www.bilibili.com/video/BV1xx411c7mD"}}]},"total":{"items":[{"id":12,"like_time":1700000003,"users":[{"mid":67,"nickname":"另一位观众"}],"item":{"title":"动态标题"}}]}}""":path.EndsWith("at")?"""{"items":[{"id":21,"at_time":1700000001,"user":{"mid":55,"nickname":"提到你的人"},"item":{"source_content":"在评论中提到了你"}}]}""":"""{"items":[{"id":31,"reply_time":1700000000,"user":{"mid":44,"nickname":"回复你的观众"},"item":{"source_content":"这是一条回复","title":"对应视频"}}]}""";
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"code\":0,\"data\":"+data+"}",Encoding.UTF8,"application/json")});
  }
 }
}
