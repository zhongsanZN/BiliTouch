using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Pages;

internal static class Release023Tests
{
 const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
 static object Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Hidden|BindingFlags.DeclaredOnly)!.Invoke(o,args)!;
 static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
 static object Gestures(object page)=>page.GetType().GetProperty("ModuleGestures",Hidden)!.GetValue(page)!;
 static int Selected(object gesture)=>(int)gesture.GetType().GetProperty("SelectedIndex",Hidden)!.GetValue(gesture)!;
 static void Set(object o,string name,object value)=>o.GetType().GetField(name,Hidden)!.SetValue(o,value);
 public static void Run(Action<bool,string> check,string output)
 {
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));Svc.Theme.SetMode(AppTheme.Dark);
  var window=new Window{Width=1100,Height=720};
  foreach(var page in new UserControl[]{new SettingsPage{DataContext=new SettingsViewModel()},new MessagesPage{DataContext=new MessagesViewModel()}})
  {
   window.Content=page;if(!window.IsVisible)window.Show();Pump(100);var g=Gestures(page);var tabs=(FrameworkElement)page.FindName("ModuleTabs");
   check(Selected(g)==0,page.GetType().Name+" starts in its first module");
   Call(g,"Begin",new Point(600,250),page.FindName("ModuleContent"));check((bool)Call(g,"Move",new Point(300,254)),"horizontal touch drag claims the module gesture");Call(g,"End");Pump(220);
   check(Selected(g)==1,"horizontal touch release changes exactly one module");
   Call(g,"Begin",new Point(500,200),page.FindName("ModuleContent"));check(!(bool)Call(g,"Move",new Point(503,410)) && !(bool)Call(g,"End") && Selected(g)==1,"vertical touch remains available to the content scroller");
   Call(g,"Begin",new Point(500,200),new Slider());check(!(bool)Call(g,"Move",new Point(100,200)),"slider interaction cannot trigger a module swipe");
   Call(g,"Step",-1);Pump(120);for(var i=0;i<10;i++)tabs.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-30){RoutedEvent=Mouse.PreviewMouseWheelEvent});Pump(220);
   check(Selected(g)==1,"precision wheel packets on the tabs switch once per gesture stream");
   tabs.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.PreviewMouseWheelEvent});Pump(220);check(Selected(g)==2,"a new wheel gesture can select the next module");
  }
  window.Close();
  var main=new MainWindow();main.Show();App.MainVm.Navigate(PageKey.Settings);Pump(500);
  Call(main,"OnHorizontalWheel",60);Call(main,"OnHorizontalWheel",60);Call(main,"OnHorizontalWheel",120);Pump(230);
  var settings=Call(main,"PageOf",PageKey.Settings);check(Selected(Gestures(settings))==1,"native horizontal touchpad packets reach settings and commit once");
  Call(main,"OnPreviewMouseDown",main,new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.XButton2){RoutedEvent=Mouse.PreviewMouseDownEvent});Pump(220);check(Selected(Gestures(settings))==2,"forward mouse side button selects the next settings module");
  Call(main,"OnPreviewMouseDown",main,new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.XButton1){RoutedEvent=Mouse.PreviewMouseDownEvent});Pump(220);check(Selected(Gestures(settings))==1,"back mouse side button selects the previous settings module");
  App.MainVm.Navigate(PageKey.Messages);Pump(500);Call(main,"OnHorizontalWheel",120);Pump(220);var messages=Call(main,"PageOf",PageKey.Messages);check(Selected(Gestures(messages))==1,"native horizontal touchpad packets also reach message modules");
  App.MainVm.Navigate(PageKey.Home);Pump(500);var home=(HomePage)Call(main,"PageOf",PageKey.Home);
  SkinService.Instance.Configure("",true,true);Pump(80);var partition=(Button)home.FindName("HomePartitionButton");var icon=(FrameworkElement)partition.Content;
  var center=icon.TranslatePoint(new Point(icon.ActualWidth/2,icon.ActualHeight/2),partition);
  check(icon.Width==20 && Math.Abs(center.X-partition.ActualWidth/2)<1 && Math.Abs(center.Y-partition.ActualHeight/2)<1,"default partition glyph is correctly sized and centered like refresh");
  SkinService.Instance.Configure(SkinService.StarParty,true,true);Pump(80);check(icon.Width==42,"Star Party retains its larger original partition art");main.Close();
  var player=new PlayerWindow();player.Show();Set(player,"_currentCid",11L);
  Call(player,"ApplyPages",new List<VideoPage>{new(){Cid=11,Page=1,Part="第一段"},new(){Cid=12,Page=2,Part="第二段"}});Pump(80);
  var section=(Grid)player.FindName("PagesSection");var body=(Grid)player.FindName("PagesPanel");var button=(Button)player.FindName("BtnPages");var season=(Grid)player.FindName("SeasonPanel");
  check(section.Visibility==Visibility.Visible && body.Visibility==Visibility.Visible,"multipart list is expanded in its inline section");
  check(ReferenceEquals(section.Parent,season.Parent) && ((Panel)section.Parent).Children.IndexOf(section)<((Panel)season.Parent).Children.IndexOf(season),"multipart section shares the collection layout and precedes it");
  check(button.IsDescendantOf(section),"multipart toggle belongs to its section instead of the intro/comment tab row");
  Call(player,"OnPagesToggleClick",button,new RoutedEventArgs());check(body.Visibility==Visibility.Collapsed && section.Visibility==Visibility.Visible,"collapsing multipart keeps its header available to reopen");
  Call(player,"OnPagesToggleClick",button,new RoutedEventArgs());check(body.Visibility==Visibility.Visible,"multipart header reopens the list");
  Call(player,"ApplyPages",new List<VideoPage>{new(){Cid=11,Page=1}});check(section.Visibility==Visibility.Collapsed,"a single-part video clears the entire multipart section");
  Set(player,"_currentBvid","BV1season1");
  Call(player,"ApplySeason",new SeasonInfo{SeasonId=99,Title="测试合集",Episodes=new List<SeasonEpisode>{new(){Bvid="BV1season1",Title="第一集",Index=1},new(){Bvid="BV1season2",Title="第二集",Index=2}}});Pump(80);
  var seasonList=(ListBox)player.FindName("SeasonList");var btnSeasonToggle=(Button)player.FindName("BtnSeasonToggle");
  check(season.Visibility==Visibility.Visible && seasonList.Visibility==Visibility.Visible && Equals(btnSeasonToggle.Content,"收起合集"),"season collection starts expanded with 收起合集 button");
  Call(player,"OnSeasonCloseClick",btnSeasonToggle,new RoutedEventArgs());
  check(season.Visibility==Visibility.Visible && seasonList.Visibility==Visibility.Collapsed && Equals(btnSeasonToggle.Content,"展开合集"),"clicking 收起合集 collapses the episode list while keeping the header available");
  Call(player,"OnSeasonCloseClick",btnSeasonToggle,new RoutedEventArgs());
  check(seasonList.Visibility==Visibility.Visible && Equals(btnSeasonToggle.Content,"收起合集"),"clicking 展开合集 re-expands the episode list");
  player.Close();Pump(100);
  var build=typeof(ApiClient).GetMethod("BuildDynamicLikeRequest",BindingFlags.NonPublic|BindingFlags.Static)!;
  foreach(var liked in new[]{true,false})
  {
   using var request=(HttpRequestMessage)build.Invoke(null,new object[]{"1266699324362850339",liked,"test csrf","test-only"})!;
   using var json=JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
   check(request.Content.Headers.ContentType!.MediaType=="application/json" && request.RequestUri!.Query.Contains("csrf=test%20csrf"),"dynamic reaction uses JSON with the CSRF query parameter");
   check(json.RootElement.GetProperty("up").GetInt32()==(liked?1:2) && json.RootElement.GetProperty("dyn_id_str").GetString()=="1266699324362850339",liked?"like uses explicit action 1 and the original dynamic ID":"unlike uses explicit action 2 instead of toggle action 0");
  }
  var api=new ApiClient();var handler=new Segments();typeof(ApiClient).GetField("_http",Hidden)!.SetValue(api,new HttpClient(handler));
  var data=api.GetDanmakuAsync(1,720,6000).GetAwaiter().GetResult();
  check(data.Count==6000 && data[0].Time==0 && data[^1].Time>719,"dense danmaku sampling retains both the beginning and end of the video");
  check(handler.SecondAttempts==2,"a failed later segment is retried rather than disappearing permanently");
  var all=api.GetDanmakuAsync(1,720).GetAwaiter().GetResult();check(all.Count==7000 && all[^1].Time>719,"normal dense videos retain more than the former 6000-comment cutoff");
 }
 public static void Live(Action<bool,string> check,string output)
 {
  var api=new ApiClient();var detail=api.GetVideoAsync("BV1JYsd6sE4k").GetAwaiter().GetResult();
  check(detail is {ok:true,data:not null},"reported multipart PV metadata resolves");
  var page=detail.data!.Pages.First(p=>p.Cid==43140187661);
  var data=api.GetDanmakuAsync(page.Cid,page.DurationSec).GetAwaiter().GetResult();
  Console.WriteLine($"LIVE DANMAKU count={data.Count} duration={page.DurationSec} last={(data.Count>0?data.Max(d=>d.Time):-1)}");
  File.WriteAllText(Path.Combine(output,"release023-danmaku-live.json"),JsonSerializer.Serialize(new{Bvid=detail.data.Bvid,page.Cid,page.DurationSec,Count=data.Count,LastSecond=data.Count>0?data.Max(d=>d.Time):-1,FormerCutoffSecond=data.Count>=6000?data[5999].Time:-1}));
  check(data.Count>0,"reported PV returns a nonempty real danmaku timeline");
  check(data.Max(d=>d.Time)>page.DurationSec*.9,"reported PV retains danmaku through the final tenth of playback");
 }
 sealed class Segments:HttpMessageHandler
 {
  internal int SecondAttempts;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var second=request.RequestUri!.Query.Contains("segment_index=2");if(second && Interlocked.Increment(ref SecondAttempts)==1)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
   using var stream=new MemoryStream();for(var i=0;i<3500;i++){using var item=new MemoryStream();V(item,16);V(item,(uint)((second?360000:0)+i*359999/3499));V(item,24);V(item,1);var text=Encoding.UTF8.GetBytes("弹幕"+i);V(item,58);V(item,(uint)text.Length);item.Write(text);V(stream,10);V(stream,(uint)item.Length);item.Position=0;item.CopyTo(stream);}
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(stream.ToArray())});
  }
  static void V(Stream s,uint n){while(n>=128){s.WriteByte((byte)(n|128));n>>=7;}s.WriteByte((byte)n);}
 }
}
