using System.IO;
using BiliTouch;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views.Pages;
internal static class HeaderRecoveryTests
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Pump(int ms) { var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f); }
    static void Until(Func<bool> ready) { var start=Environment.TickCount64;while(!ready()){if(Environment.TickCount64-start>15000)throw new TimeoutException("recovery test timed out");Pump(20);} }
    static void Save(FrameworkElement root,string path) { var b=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(root);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(b));using var file=File.Create(path);enc.Save(file); }
    public static void Run(Action<bool,string> check,string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Svc.Theme.SetMode(AppTheme.Dark);SkinService.Instance.Configure(SkinService.StarParty,true,true);
        var handler=new FollowingFixture();var field=typeof(ApiClient).GetField("_http",Hidden)!;var old=field.GetValue(Svc.Api);var session=Svc.Session.Current;
        using var http=new HttpClient(handler);field.SetValue(Svc.Api,http);
        typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,new Session{SessData="fixture",BiliJct="fixture",DedeUserID="42"});
        var window=new Window{Width=1440,Height=900};
        try {
            var vm=new FollowViewModel();var refresh=vm.RefreshAsync();Until(()=>vm.Users.Count==50 && !vm.Loading);
            check(!handler.Release.Task.IsCompleted && vm.LoadingUsers,"first page becomes usable while remaining followings are still loading");
            handler.Release.TrySetResult();Until(()=>vm.Users.Count==1536 && !vm.LoadingUsers);
            check(vm.Users.Select(u=>u.Mid).Distinct().Count()==1536,"all 1536 following authors load automatically without a scroll gesture");
            var follow=new FollowPage{DataContext=vm};
            var pages=new UserControl[]{new HomePage(),follow,new MinePage(),new SettingsPage{DataContext=new SettingsViewModel()}};
            foreach(var page in pages) {
                var shell=new Grid();shell.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(96)});shell.ColumnDefinitions.Add(new ColumnDefinition());
                shell.Children.Add(new SkinSurface{Kind="Rail"});Grid.SetColumn(page,1);shell.Children.Add(page);window.Content=shell;
                if(!window.IsVisible)window.Show();Pump(150);
                var title=(FrameworkElement)page.FindName("ShellPageTitle");var point=title.TranslatePoint(new Point(),page);
                check(Math.Abs(point.X-24)<.1 && Math.Abs(point.Y-20)<.1,page.GetType().Name+" shares the same title origin");
                Save(shell,Path.Combine(output,"recovery-"+page.GetType().Name+".png"));
                if(page==follow){var list=(ListBox)follow.FindName("ContactsList");var realized=Enumerable.Range(0,list.Items.Count).Count(i=>list.ItemContainerGenerator.ContainerFromIndex(i)!=null);check(realized>0 && realized<40,"1536 authors use fewer than 40 realized contact rows: "+realized);var body=(FrameworkElement)follow.FindName("FollowFeedBody");check(body.TranslatePoint(new Point(),follow).Y>=112,"followed-live board starts below the theme header");}
                window.Width=840;Pump(100);
                point=title.TranslatePoint(new Point(),page);
                check(Math.Abs(point.X-24)<.1 && Math.Abs(point.Y-20)<.1,page.GetType().Name+" keeps its title origin in a narrow window");
                Save(shell,Path.Combine(output,"recovery-narrow-"+page.GetType().Name+".png"));
                window.Width=1440; window.Content=null;shell.Children.Remove(page);
            }
            var profile=new MineOverviewPage{DataContext=new{IsLoggedIn=true,Name="中三",Mid=42,Face="",Sign="双人图构图验收",FollowingText="1536",FollowersText="1630",VideoCountText="64",Error="",ShowEmpty=false,Loading=false,Videos=Array.Empty<VideoItem>()}};
            window.Content=profile;Pump(100);Save(profile,Path.Combine(output,"recovery-profile-wide.png"));
            window.Width=700;Pump(100);Save(profile,Path.Combine(output,"recovery-profile-compact.png"));
            window.Width=1280;window.Content=follow;Pump(100);
            var toggle=(Button)follow.FindName("LiveBoardToggle");
            var hit=toggle.InputHitTest(new Point(toggle.ActualWidth/2,toggle.ActualHeight/2)) as DependencyObject;
            while(hit!=null && hit!=toggle)hit=VisualTreeHelper.GetParent(hit);
            check(hit==toggle,"empty middle of the followed-live title is a clickable button surface");
            var wasOpen=vm.LiveBoardOpen;toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check(vm.LiveBoardOpen!=wasOpen,"followed-live button toggles the actual view model");
        } finally { window.Close();field.SetValue(Svc.Api,old);typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,session); }
    }
    sealed class FollowingFixture:HttpMessageHandler
    {
        public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var uri=request.RequestUri!;string json="{\"code\":0,\"data\":{}}";
            if(uri.AbsolutePath.EndsWith("/nav"))json="{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":42,\"uname\":\"fixture\"}}";
            if(uri.AbsolutePath.EndsWith("/followings")){
                var query=System.Web.HttpUtility.ParseQueryString(uri.Query);var page=int.Parse(query["pn"]!);if(page>1)await Release.Task.WaitAsync(token);
                var users=Enumerable.Range((page-1)*50,50).Where(i=>i<1536).Select(i=>new{mid=i+1000,uname="UP "+i,face="",sign=""});json=System.Text.Json.JsonSerializer.Serialize(new{code=0,data=new{list=users}});
            }
            if(uri.AbsolutePath.EndsWith("/feed/all"))json="{\"code\":0,\"data\":{\"items\":[],\"has_more\":false}}";
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
        }
    }
}
