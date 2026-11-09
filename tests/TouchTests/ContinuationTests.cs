using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
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
using BiliTouch.Views;
using BiliTouch.Views.Controls;

internal static class ContinuationTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object o,string n)=>(T)o.GetType().GetField(n,Hidden)!.GetValue(o)!;
    static void Set(object o,string n,object v)=>o.GetType().GetField(n,Hidden)!.SetValue(o,v);
    static object? Call(object o,string n,params object[] args)=>o.GetType().GetMethod(n,Hidden)!.Invoke(o,args);
    static void Pump(int ms){var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f);}
    static void Until(Func<bool> ready){var start=Environment.TickCount64;while(!ready()){if(Environment.TickCount64-start>10000)throw new TimeoutException("continuation UI timeout");Pump(20);}}
    static void Wait(Task task){Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    static void Save(FrameworkElement root,string path){var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);png.Save(file);}
    static void Wheel(UIElement source,int delta){source.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,delta){RoutedEvent=Mouse.PreviewMouseWheelEvent,Source=source});}
    public static void Run(Action<bool,string> check,string artifacts)
    {
        var oldContext=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var httpField=typeof(ApiClient).GetField("_http",Hidden)!;var oldHttp=httpField.GetValue(Svc.Api);var oldSession=Svc.Session.Current;
        var oldWeb=Svc.Settings.UseWebPlayerLayout;var oldSkin=Svc.Settings.SkinId;var oldMode=Svc.Theme.Mode;
        var handler=new Fixture();using var http=new HttpClient(handler);httpField.SetValue(Svc.Api,http);
        typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,new Session{SessData="fixture",BiliJct="fixture",DedeUserID="42"});
        PlayerWindow? player=null;Window? host=null;
        try
        {
            Svc.Theme.SetMode(AppTheme.Light);SkinService.Instance.Configure(SkinService.StarParty,true,true);Svc.Settings.SetWebPlayerLayout(false);
            var surface=new SkinSurface{Width=1800,Height=124};surface.Measure(new Size(1800,124));surface.Arrange(new Rect(0,0,1800,124));surface.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1800,124,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);var pixels=new byte[1800*124*4];bitmap.CopyPixels(pixels,1800*4,0);
            // The shared-header redesign already uses a fading foundation/rim rather
            // than a solid yellow top row. Verify the artwork-to-extension coverage.
            byte AlphaAt(int x) => pixels[(60 * 1800 + x) * 4 + 3];
            check(AlphaAt(100)>200 && AlphaAt(900)>0 && AlphaAt(1799)<AlphaAt(900),
                "theme artwork extends into the header and fades without a hard background cutoff");
            Save(surface,Path.Combine(artifacts,"continuation-header-seam.png"));

            player=new PlayerWindow{Width=1320,Height=860};player.Show();Pump(180);
            Set(player,"_currentBvid","BVfixture");
            handler.CommentsPending=new TaskCompletionSource<HttpResponseMessage>();var pending=handler.CommentsPending;
            var load=(Task)Call(player,"LoadVideoAsync",0L)!;
            Until(()=>handler.Comments>0 || load.IsCompleted);
            check(handler.Comments>0,$"video details initiate comments before playback completes (requests={string.Join(',',handler.Paths)})");
            check(((RadioButton)player.FindName("TabIntro")).IsChecked==true,"original layout begins fetching comments while introduction remains selected");
            var duplicate=(Task)Call(player,"LoadCommentsAsync",false)!;
            check(handler.Comments==1 && !duplicate.IsCompleted,"opening comments during preload shares the in-flight request");
            pending.SetResult(Fixture.Response("{\"code\":0,\"data\":{\"page\":{\"count\":0},\"replies\":[]}}"));Wait(duplicate);
            check(Field<bool>(player,"_commentsLoaded"),"successful hidden-tab preload is retained");
            Wait((Task)Call(player,"LoadCommentsAsync",false)!);check(handler.Comments==1,"returning to a preloaded comment tab does not refetch");
            Wait(load);
            var setting=(Button)player.FindName("BtnDanmakuArea");
            check(System.Windows.Automation.AutomationProperties.GetName(setting)=="弹幕设置" && (string)setting.ToolTip=="弹幕设置",
                "the themed danmaku settings entry retains its accessible four-character label");
            check(player.FindName("DanmakuAreaText")==null && player.FindName("DanmakuBtnText")==null,"old percentage entry and text-based danmaku toggle are removed");
            Call(player,"ToggleDanmaku");check(((System.Windows.Shapes.Path)player.FindName("DanmakuOffSlash")).Visibility==Visibility.Visible,"disabled danmaku is represented by a slashed icon");Call(player,"ToggleDanmaku");
            var input=(TextBox)player.FindName("DanmakuInput");input.Text="发送测试（仅本地夹具）";
            Wait((Task)Call(player,"SendCurrentDanmakuAsync")!);
            check(handler.Posts==1 && input.Text=="","successful danmaku submission clears the composer");
            check(handler.LastPost.Contains("oid=55") && handler.LastPost.Contains("csrf=fixture") && handler.LastPost.Contains("mode=1"),"danmaku submission includes cid, csrf and normal scrolling mode");
            check(handler.LastUri!.Query.Contains("w_rid=") && handler.LastUri.Query.Contains("wts="),"danmaku submission uses the current WBI signing flow");
            handler.Fail=true;input.Text="失败后保留";Wait((Task)Call(player,"SendCurrentDanmakuAsync")!);check(input.Text=="失败后保留","failed send retains the user's draft");handler.Fail=false;
            handler.SendPending=new TaskCompletionSource<HttpResponseMessage>();var sendPending=handler.SendPending;
            var send=(Task)Call(player,"SendCurrentDanmakuAsync")!;
            check(!((Button)player.FindName("BtnSendDanmaku")).IsEnabled,"send button blocks overlapping danmaku posts");
            var posts=handler.Posts;Wait((Task)Call(player,"SendCurrentDanmakuAsync")!);check(handler.Posts==posts,"repeated send while a post is pending does not submit twice");
            Set(player,"_currentCid",56L);sendPending.SetResult(Fixture.Response("{\"code\":0}"));Wait(send);check(input.Text=="失败后保留","old-part send response does not erase a new-part draft");Set(player,"_currentCid",55L);
            Call(player,"ShowControls");
            var controlsRoot=(FrameworkElement)player.FindName("VideoAreaRoot");
            check(Math.Abs(input.TranslatePoint(new Point(),(FrameworkElement)player.FindName("ControlBar")).Y-((FrameworkElement)player.FindName("BtnPlay")).TranslatePoint(new Point(),(FrameworkElement)player.FindName("ControlBar")).Y)<6,"danmaku composer shares the single control row");
            check(input.ActualHeight>=32 && input.IsVisible,"danmaku composer is laid out in the real native-video overlay");
            ((FrameworkElement)player.FindName("FailOverlay")).Visibility=Visibility.Collapsed;
            ((FrameworkElement)player.FindName("LoadingOverlay")).Visibility=Visibility.Collapsed;
            Save(controlsRoot,Path.Combine(artifacts,"continuation-danmaku-composer.png"));

            player.WindowState=WindowState.Maximized;Pump(150);var states=new List<WindowState>();player.StateChanged+=(_,_)=>states.Add(player.WindowState);
            Call(player,"ToggleFullscreen");Pump(180);check(player.WindowState==WindowState.Maximized && !states.Contains(WindowState.Normal),"entering fullscreen from maximized has no intermediate Normal window state");
            states.Clear();Call(player,"ToggleFullscreen");Pump(180);check(player.WindowState==WindowState.Maximized && !states.Contains(WindowState.Normal),"exiting fullscreen back to maximized has no intermediate Normal window state");
            player.WindowState=WindowState.Normal;player.Width=1320;player.Height=860;Pump(150);
            ((ItemsControl)player.FindName("CommentList")).ItemsSource=Enumerable.Range(1,12).Select(i=>new CommentItem{Rpid=i,UserName="评论用户",Content="小窗移动验证：滚动到评论区后进入侧边播放区域。"}).ToArray();
            Svc.Settings.SetWebPlayerLayout(true);Pump(350);
            var webScroll=(ScrollViewer)player.FindName("WebLeftScroll");webScroll.ScrollToTop();Pump(600);
            bool sawBrand=false;
            EventHandler seeBrand=(_,_)=>{if(((Border)player.FindName("DockTransitionCover")).Visibility==Visibility.Visible)sawBrand=true;};
            CompositionTarget.Rendering+=seeBrand;
            webScroll.ScrollToVerticalOffset(Math.Min(webScroll.ScrollableHeight,((FrameworkElement)player.FindName("WebVideoPlaceholder")).ActualHeight*.5+60));Pump(130);
            CompositionTarget.Rendering-=seeBrand;
            check(sawBrand,$"native video resize is covered by the BiliTouch identity during docking (offset={webScroll.VerticalOffset:F0}, extent={webScroll.ScrollableHeight:F0}, mounted={Field<bool>(player,"_webLayoutMounted")})");
            Save(player,Path.Combine(artifacts,"continuation-dock-brand-transition.png"));Pump(600);
            check(((Border)player.FindName("DockTransitionCover")).Visibility==Visibility.Collapsed && !Field<bool>(player,"_webRendering"),"settled video removes the branded cover and detaches its rendering callback");
            player.Close();player=null;

            // Exercise actual card thumbnails, with local cached artwork and no external network.
            var url="https://example.invalid/viewport-cover.png";Directory.CreateDirectory(AppPaths.CacheDir);
            File.Copy("BiliTouch/Assets/app.png",Path.Combine(AppPaths.CacheDir,Hashing.Md5Hex(url)+".jpg"),true);
            var queueType=typeof(CoverLoader).Assembly.GetType("BiliTouch.Helpers.ViewportImageQueue")!;
            int Count()=>(int)queueType.GetProperty("Started",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            var cards=new StackPanel();for(int i=0;i<180;i++)cards.Children.Add(new VideoCard{DataContext=new VideoItem{Bvid="BV"+i,Title="信息流卡片 "+i,Cover=url}});
            var viewer=new ScrollViewer{Content=cards,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            host=new Window{Width=700,Height=650,Content=viewer};var before=Count();host.Show();Pump(700);
            var initiallyStarted=Count()-before;check(initiallyStarted>0 && initiallyStarted<30,"180-card feed starts image work only near its viewport");
            viewer.ScrollToVerticalOffset(12000);Pump(650);check(Count()-before>initiallyStarted && Count()-before<55,"scrolling prefetches the next nearby cards without decoding the entire feed");
            TrackpadScroll.Cancel(viewer);viewer.ScrollToVerticalOffset(0);Pump(70);
            for(int i=0;i<5;i++)Wheel(cards,-120);Pump(1000);
                        // ★ 2026-11-05: 整格滚轮现在也吃"信息流灵敏度"(与触控板共用同一把尺子)。
            //   旧断言写死 450 = 5 格 × 90px, 那是"整格不被接管"时的语义。
            //   现在按默认灵敏度 75% 算: 5 × 90 × 0.75 = 337.5px。断言改成按设置算, 不再写死数字。
            var notchStep=TrackpadScroll.NotchPixels;
            var expectedDistance=5*notchStep;
            check(Math.Abs(viewer.VerticalOffset-expectedDistance)<3,
                $"a burst of five mouse notches retains all accumulated scroll distance (offset={viewer.VerticalOffset:F1}, expected={expectedDistance:F1})");
            Console.WriteLine($"FEED thumbnails initial={initiallyStarted}/180 after-scroll={Count()-before}/180; five-notch distance={viewer.VerticalOffset:F1}");
            Save(host,Path.Combine(artifacts,"continuation-feed-viewport.png"));host.Close();host=null;
        }
        finally
        {
            player?.Close();host?.Close();httpField.SetValue(Svc.Api,oldHttp);typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,oldSession);
            Svc.Settings.SetWebPlayerLayout(oldWeb);SkinService.Instance.Configure(oldSkin,true,true);Svc.Theme.SetMode(oldMode);SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
    sealed class Fixture:HttpMessageHandler
    {
        public int Comments,Posts;public bool Fail;public string LastPost="";public Uri? LastUri;
        public readonly List<string> Paths=new();
        public TaskCompletionSource<HttpResponseMessage>? CommentsPending,SendPending;
        public static HttpResponseMessage Response(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json)};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var path=request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if(path=="/x/v2/reply/main"){Comments++;if(CommentsPending!=null){var p=CommentsPending;CommentsPending=null;return await p.Task;}return Response("{\"code\":0,\"data\":{\"cursor\":{\"all_count\":0},\"replies\":[]}}");}
            if(path=="/x/v2/dm/post")
            {
                Posts++;LastPost=await request.Content!.ReadAsStringAsync();LastUri=request.RequestUri;
                if(SendPending!=null){var p=SendPending;SendPending=null;return await p.Task;}
                return Response(Fail?"{\"code\":36703,\"message\":\"发送频率过快\"}":"{\"code\":0,\"data\":{\"dmid_str\":\"42\"}}");
            }
            return Response(path switch
            {
                "/x/web-interface/nav"=>"{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":42,\"uname\":\"测试\",\"wbi_img\":{\"img_url\":\"https://example.invalid/01234567890123456789012345678901.png\",\"sub_url\":\"https://example.invalid/01234567890123456789012345678901.png\"}}}",
                "/x/web-interface/view"=>"{\"code\":0,\"data\":{\"aid\":7,\"bvid\":\"BVfixture\",\"cid\":55,\"title\":\"播放器功能验证\",\"duration\":360,\"desc\":\"本地夹具，不向真实账号发送弹幕\",\"owner\":{\"mid\":42,\"name\":\"示例 UP\"},\"stat\":{\"view\":100,\"danmaku\":12},\"pages\":[{\"cid\":55,\"page\":1,\"part\":\"测试\"}]}}",
                _=>"{\"code\":-400,\"message\":\"fixture: no data\"}"
            });
        }
    }
}
