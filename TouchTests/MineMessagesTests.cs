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
using BiliDesk;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using BiliDesk.Views.Controls;
using BiliDesk.Views.Pages;

internal static class MineMessagesTests
{
 static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
 static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Hidden)!.GetValue(target)!;
 public static void Run(Action<bool,string> check,string artifacts)
 {
  var oldContext=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
  var field=typeof(ApiClient).GetField("_http",Hidden)!;var previous=field.GetValue(Svc.Api);var oldSession=Svc.Session.Current;
  var handler=new Fixture();using var http=new HttpClient(handler);field.SetValue(Svc.Api,http);
  typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,new Session {SessData="fixture",BiliJct="fixture",DedeUserID="42"});
  try
  {
   Directory.CreateDirectory(AppPaths.CacheDir);
   foreach(var url in new[]{"https://example.invalid/cover.png","https://example.invalid/official.png"})File.Copy("BiliDesk/Assets/app.png",Path.Combine(AppPaths.CacheDir,Hashing.Md5Hex(url)+".jpg"),true);
   var result=Wait(Svc.Api.GetSpaceInfoAsync(42));
   check(result.ok && result.data!.Following==37 && result.data.Follower==1629,"space profile combines WBI info with relation statistics");
   check(result.data!.VideoCount==64 && result.data.VideoCountKnown,"space profile keeps the archive count from the public card");
   handler.RelationFailure=true;result=Wait(Svc.Api.GetSpaceInfoAsync(42));
   check(result.ok && result.data!.Following==37 && result.data.RelationStatsKnown,"card fallback reads following from card.friend, not an absent top-level field");
   handler.CardFailure=true;result=Wait(Svc.Api.GetSpaceInfoAsync(42));
   check(result.ok && !result.data!.RelationStatsKnown,"unavailable statistics remain unknown instead of pretending to be zero");
   handler.CardFailure=handler.RelationFailure=false;
   var mine=new MineOverviewPage();var vm=(PersonalHomeViewModel)mine.DataContext;Wait(vm.RefreshAsync());
   check(vm.FollowingText=="37" && vm.FollowersText=="1629","personal home exposes the real following and follower counts");
   check(mine.FindName("FollowingButton") is Button && mine.FindName("FollowersButton") is Button,"personal statistics have separate interactive list entries");
   var host=Host(mine);Save(host,Path.Combine(artifacts,"messages-personal-home.png"));host.Close();
   var relations=new UserRelationsViewModel(42,false);Wait(relations.LoadAsync(true));
   check(relations.Users.Count==1 && relations.Users[0].Mid==101,"following list loads concrete user records");
   var relationPage=new UserRelationsPage(42,true);host=Host(relationPage);Pump(120);
   check(Children<ContactListRow>(relationPage).Any(r=>r.Title=="粉丝"),"relation entry mounts a native user list with concrete user cards");host.Close();
   var fans=new UserRelationsViewModel(42,true);Wait(fans.LoadAsync(true));
   check(fans.Users.Count==1 && fans.Users[0].Mid==202,"fans use the followers endpoint instead of the following endpoint");
   var sessions=Wait(Svc.Api.GetMsgSessionsAsync());
   check(sessions.ok && sessions.sessions!.Single(s=>s.TalkerId==844424930131965).Name=="服务通知","official service account name is read from session account_info");
   check(sessions.sessions!.Single(s=>s.IsOfficial).Face.Length>0,"official notification avatar comes from account_info.pic_url");
   var history=Wait(Svc.Api.GetMsgHistoryAsync(101));
   check(history.ok && history.hasMore,"numeric has_more is accepted for private message pagination");
   var video=history.items!.Single(m=>m.Kind==MsgKind.Video);
   check(video.Bvid=="BV1fixture000" && video.CoverUrl.Length>0 && video.Title=="分享视频" && video.Description=="视频简介","video share retains cover, title, description and its playable identity");
   var notification=history.items!.Single(m=>m.Kind==MsgKind.Notification);
   check(notification.Description=="通知正文" && notification.Actions.Single().Url=="https://www.bilibili.com/account/","notification keeps its body and chooses the web action URI");
   check(history.items!.Single(m=>m.Seqno==50).Text=="富文本内容","rich text segments are retained as readable text");
   var messageVm=new MessagesViewModel();Wait(messageVm.LoadAsync());
   check(messageVm.Sessions.First().Timestamp>messageVm.Sessions.Last().Timestamp,"sessions sort by numeric activity time across month boundaries");
   var normal=messageVm.Sessions.Single(s=>s.TalkerId==101);Wait(messageVm.LoadThreadAsync(normal));
   var gate=new TaskCompletionSource();handler.GateNextHistory=gate;
   var pending=messageVm.LoadThreadAsync(normal);
   Wait(messageVm.LoadThreadAsync(messageVm.Sessions.Single(s=>s.IsOfficial)));Wait(messageVm.LoadThreadAsync(normal));
   gate.SetResult();Wait(pending);
   check(messageVm.Current==normal && messageVm.Messages.Count==3,"switching A to B and back to A during loading keeps the requested conversation populated");
   Wait(messageVm.LoadOlderAsync());
   check(messageVm.Messages.Select(m=>m.Seqno).SequenceEqual(new long[]{49,50,51,52}),"older messages prepend in order without duplicates");
   var page=new MessagesPage {DataContext=messageVm};host=Host(page);Pump(120);
   check(Children<TextBlock>(page).Count(t=>t.Text=="私信")==1 && !Children<TextBlock>(page).Any(t=>t.Text=="B 站消息"),"message page has one clean private-message title");
   check(Children<ContactListRow>(page).Count()==messageVm.Sessions.Count,"private sessions use the shared timeline contact row");
   Until(()=>Children<Image>(page).Any(i=>i.IsVisible && i.Source!=null && Cover.GetImageSource(i)=="https://example.invalid/cover.png"));
   check(Children<Image>(page).Any(i=>i.IsVisible && i.Source!=null && Cover.GetImageSource(i)=="https://example.invalid/cover.png"),"private video shares render a decoded cover instead of a title-only bubble");
   check(Children<Image>(page).Where(i=>i.Width==36 && i.Source!=null).All(i=>((BitmapSource)i.Source).PixelWidth<=128),"shared contact avatars decode at thumbnail size instead of full chat-image size");
   Save(host,Path.Combine(artifacts,"messages-private-cards.png"));host.Close();
   var robot=messageVm.Sessions.Single(s=>s.IsOfficial);Wait(messageVm.LoadThreadAsync(robot));messageVm.Draft="test";
   check(!messageVm.CanSend && !messageVm.CanCompose,"official notification channels do not offer an ineffective reply action");
   CheckRefresh(check,artifacts);
   CheckTouch(check,artifacts,1);CheckTouch(check,artifacts,2);
   check(handler.Sends==0,"read and render verification never sends a private message");
  }
  finally {field.SetValue(Svc.Api,previous);typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,oldSession);SynchronizationContext.SetSynchronizationContext(oldContext);}
 }
 public static void Live()
 {
  var original=Svc.Session.Current;var httpField=typeof(ApiClient).GetField("_http",Hidden)!;var oldHttp=httpField.GetValue(Svc.Api);
  using var http=new HttpClient(new HttpClientHandler {Proxy=new WebProxy("http://127.0.0.1:7897"),UseProxy=true});http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent","Mozilla/5.0");
  var stored=JsonSerializer.Deserialize<Session>(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData/Local/BiliDesk/session.json")))!;
  try
  {
   typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,stored);httpField.SetValue(Svc.Api,http);
   var mid=long.Parse(stored.DedeUserID!);var info=Wait(Svc.Api.GetSpaceInfoAsync(mid));
   Console.WriteLine($"LIVE profile: ok={info.ok}, following={info.data?.Following}, followers={info.data?.Follower}, known={info.data?.RelationStatsKnown}, videos={info.data?.VideoCount}");
   var follow=Wait(Svc.Api.GetFollowingsAsync(mid,1,20));var fans=Wait(Svc.Api.GetFollowersAsync(mid,1,20));
   Console.WriteLine($"LIVE relation lists: following ok={follow.ok}, count={follow.list?.Count}; fans ok={fans.ok}, count={fans.list?.Count}");
   var result=Wait(Svc.Api.GetMsgSessionsAsync());if(!result.ok || result.sessions==null)throw new Exception(result.err);
   Console.WriteLine($"LIVE private sessions: {result.sessions.Count}; named={result.sessions.Count(s=>s.Name.Length>0)}; official={result.sessions.Count(s=>s.IsOfficial)}");
   foreach(var session in result.sessions.Where(s=>s.IsOfficial).Take(5))Console.WriteLine($"LIVE official: {session.DisplayName}; avatar={session.Face.Length>0}");
   var target=result.sessions.FirstOrDefault(s=>!s.IsOfficial);
   if(target!=null){var history=Wait(Svc.Api.GetMsgHistoryAsync(target.TalkerId));Console.WriteLine($"LIVE history: ok={history.ok}; cards={history.items?.Count(m=>m.IsCard)}; covers={history.items?.Count(m=>m.HasCover)}; videos={history.items?.Count(m=>m.Bvid.Length>0)}");}
   Console.WriteLine("LIVE read-only probe complete; no messages sent and no read markers submitted.");
  }
  finally{httpField.SetValue(Svc.Api,oldHttp);typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,original);}
 }
 static void CheckRefresh(Action<bool,string> check,string artifacts)
 {
  var page=new UserControl {Content=new Grid()};var gate=new TaskCompletionSource();var gesture=ModuleGestures.Install(page,()=>gate.Task);
  var host=Host(page);var task=gesture.RefreshAsync();Pump(50);
  check(Field<LoadingRing>(gesture,"_refreshRing").IsVisible && Field<TextBlock>(gesture,"_hintText").Text=="","module refresh displays a spinning circle without the large text banner");
  var ring=Field<LoadingRing>(gesture,"_refreshRing");var first=Pixels(ring);Pump(180);var second=Pixels(ring);
  check(!first.SequenceEqual(second),"refresh arc visibly rotates between rendered frames instead of looking like a static full circle");
  Save(host,Path.Combine(artifacts,"messages-pull-spinner.png"));gate.SetResult();Wait(task);Pump(240);
  check(!Field<Border>(gesture,"_hint").IsVisible,"refresh circle disappears once the request completes");host.Close();
 }
 static void CheckTouch(Action<bool,string> check,string artifacts,double scale)
 {
  var items=Enumerable.Range(1,35).Select(i=>new MsgSession {TalkerId=i,Name="联系人 "+i}).ToArray();
  var stack=new StackPanel();stack.Children.Add(new Border {Tag="AllDynamics",Height=64,Background=Brushes.Transparent,DataContext=new MsgSession {TalkerId=9999},Child=new TextBlock {Text="全部动态"}});foreach(var item in items)stack.Children.Add(new ContactListRow {Title=item.Name,DataContext=item,Height=64});
  var scroll=new ScrollViewer {Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
  var root=new Grid {LayoutTransform=new ScaleTransform(scale,scale)};root.Children.Add(scroll);
  var selected=0;ContactSelection.Install(scroll,o=>selected=(int)((MsgSession)o).TalkerId);
  var host=new Window {Width=520,Height=720,Content=root};host.Show();Pump(180);scroll.ScrollToVerticalOffset(300);Pump(80);
  var finger=new Finger(scroll);finger.Down(100,100);
  check(selected==0,$"{scale}x scale: touch-down does not prematurely select a conversation");
  finger.Move(100,50);finger.Up(100,50);Pump(180);
  check(selected==0,$"{scale}x scale: vertical contact drag never activates a row");
  check(ContactSelection.GetSuppressHover(stack.Children[5]),$"{scale}x scale: stale mouse hover is suppressed during finger scrolling");
  scroll.ScrollToVerticalOffset(300);Pump(80);
  var hit=scroll.InputHitTest(new Point(100,100)) as DependencyObject;
  while(hit!=null && hit is not ContactListRow)hit=VisualTreeHelper.GetParent(hit);
  var expected=(int)((MsgSession)((FrameworkElement)hit!).DataContext).TalkerId;
  finger=new Finger(scroll);finger.Down(100,100);finger.Up(100,100);Pump(120);
  check(selected==expected,$"{scale}x scale: a stationary touch selects the actual row underneath the finger after scrolling");
  scroll.ScrollToTop();Pump(100);finger=new Finger(scroll);finger.Down(100,20);finger.Up(100,20);Pump(80);
  check(selected==9999,$"{scale}x scale: the all-dynamics header remains touch-selectable");
  Save(host,Path.Combine(artifacts,$"messages-contact-touch-{scale}.png"));host.Close();
 }
 static Window Host(object content){var w=new Window {Width=1120,Height=800,Content=content,Background=(Brush)Application.Current.FindResource("AppBackgroundBrush")};w.Show();Pump(150);return w;}
 static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject
 {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var d=VisualTreeHelper.GetChild(root,i);if(d is T t)yield return t;foreach(var child in Children<T>(d))yield return child;}}
 static T Wait<T>(Task<T> task){Until(()=>task.IsCompleted);return task.GetAwaiter().GetResult();}
 static void Wait(Task task){Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
 static void Until(Func<bool> done){var at=Environment.TickCount64;while(!done()){if(Environment.TickCount64-at>20000)throw new TimeoutException();Pump(25);}}
 static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
 static byte[] Pixels(FrameworkElement root){var w=(int)root.ActualWidth;var h=(int)root.ActualHeight;var bmp=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bmp.Render(root);var bytes=new byte[w*h*4];bmp.CopyPixels(bytes,w*4,0);return bytes;}
 static void Save(FrameworkElement root,string path){root.UpdateLayout();var image=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var f=File.Create(path);png.Save(f);}
 sealed class Finger:TouchDevice
 {
  static int next=7000;readonly FrameworkElement target;readonly Visual root;Point point;TouchAction action;
  public Finger(FrameworkElement t):base(++next){target=t;var src=PresentationSource.FromVisual(t)!;root=src.RootVisual;SetActiveSource(src);}
  public override TouchPoint GetTouchPoint(IInputElement? relativeTo){var p=relativeTo is Visual v?root.TransformToDescendant(v).Transform(point):point;return new TouchPoint(this,p,new Rect(p,new Size(4,4)),action);}
  public override TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo)=>new(){GetTouchPoint(relativeTo)};
  void At(double x,double y,TouchAction a){point=target.TranslatePoint(new Point(x,y),(UIElement)root);action=a;}
  public void Down(double x,double y){At(x,y,TouchAction.Down);Activate();ReportDown();Pump(25);}
  public void Move(double x,double y){At(x,y,TouchAction.Move);ReportMove();Pump(25);}
  public void Up(double x,double y){At(x,y,TouchAction.Up);ReportUp();Deactivate();Pump(25);}
 }
 sealed class Fixture:HttpMessageHandler
 {
  public bool CardFailure,RelationFailure;public int Sends;public TaskCompletionSource? GateNextHistory;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri!.AbsolutePath;var query=request.RequestUri.Query;
   object data=new {};int code=0;
   if(path.EndsWith("/spi"))data=new {b_3="fixture",b_4="fixture"};
   else if(path.EndsWith("/nav"))data=new {isLogin=true,mid=42,uname="测试用户",face="",wbi_img=new {img_url="https://example.invalid/01234567890123456789012345678901.png",sub_url="https://example.invalid/01234567890123456789012345678901.png"}};
   else if(path=="/x/space/wbi/acc/info")data=new {mid=42,name="测试用户",sign="个人资料",face="",level=6};
   else if(path=="/x/web-interface/card"){code=CardFailure?-403:0;data=new {card=new {name="测试用户",sign="个人资料",face="",friend=37,fans=1629},follower=1629,archive_count=64};}
   else if(path=="/x/relation/stat"){code=RelationFailure?-403:0;data=new {following=37,follower=1629};}
   else if(path=="/x/relation/followings")data=new {list=new[]{new {mid=101,uname="关注对象",face="",sign="签名"}}};
   else if(path=="/x/relation/followers")data=new {list=new[]{new {mid=202,uname="粉丝",face="",sign="签名"}}};
   else if(path.Contains("arc/search"))data=new {page=new {count=64},list=new {vlist=new object[0]}};
   else if(path.EndsWith("get_sessions"))data=new {session_list=new object[]{new {talker_id=101,session_ts=1793800000000000L,last_msg=Message(51,11,new {title="分享视频",bvid="BV1fixture000",cover="",desc="简介"})},new {talker_id=844424930131965L,session_ts=1793700000000000L,account_info=new {name="服务通知",pic_url="https://example.invalid/official.png"},last_msg=Message(1,10,new {title="通知",text="通知正文"})}}};
   else if(path.EndsWith("/cards"))data=new[]{new {mid=101,name="测试UP",face=""}};
   else if(path.EndsWith("fetch_session_msgs"))data=query.Contains("end_seqno=")?new {has_more=0,messages=new[]{Message(49,1,new {content="较早文字"})}}:(object)new {has_more=1,messages=new[]{Message(50,18,new {content=new[]{new {text="富文本"},new {text="内容"}}}),Message(51,11,new {title="分享视频",bvid="BV1fixture000",cover="https://example.invalid/cover.png",desc="视频简介",rid=123,type_=1}),Message(52,10,new {title="通知",text="通知正文",jump_text="查看",jump_uri="bilibili://account",jump_uri_config=new {web_uri="https://www.bilibili.com/account/"}})}};
   else if(path.EndsWith("send_msg"))Sends++;
   var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new {code,data}))};
   if(path.EndsWith("fetch_session_msgs") && GateNextHistory is {} gate){GateNextHistory=null;return DelayResponse(gate.Task,response);}
   return Task.FromResult(response);
  }
  static async Task<HttpResponseMessage> DelayResponse(Task gate,HttpResponseMessage response){await gate;return response;}
  static object Message(long seq,int type,object content)=>new {sender_uid=101,msg_type=type,msg_seqno=seq,timestamp=1793800000,content=JsonSerializer.Serialize(content)};
 }
}
