using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views.Pages;

internal static class DetailThemeTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public static void Run(Action<bool,string> check,string artifacts)
    {
        var field=typeof(ApiClient).GetField("_http",Private)!;var old=field.GetValue(Svc.Api);
        using var http=new HttpClient(new Fixture());field.SetValue(Svc.Api,http);
        MainWindow? main=null;
        try
        {
            main=new MainWindow();main.Show();Pump(300);
            var embeddedDetail = new DynamicDetailPage(new DynamicItem { Id="theme-fixture", Kind=DynamicKind.Text, CommentOid="123", CommentType=11,
                AuthorName="主题布局示例", Text="动态内容与评论分栏展示，顶部与其他页面共享主题装扮。" });
            typeof(DynamicDetailViewModel).GetField("_initialized",Private)!.SetValue(embeddedDetail.ViewModel,true);
            main.ShowEmbeddedPage(embeddedDetail,"动态详情");Pump(300);
            check(embeddedDetail.ViewModel.Comments.Count>0,"detail keeps a populated comment viewport");
            var embeddedHeader=(Grid)main.FindName("EmbeddedHeader");
            var backButton=(Button)main.FindName("EmbeddedBackButton");
            check(embeddedHeader.ActualHeight==112 && backButton.ActualHeight>=44,
                "embedded detail uses the standard header height and reachable back button");
            check(((TextBlock)main.FindName("EmbeddedTitle")).Text=="动态详情",
                "embedded detail has one shared page title");
            var originalSkin=Svc.Settings.SkinId;var originalEffects=Svc.Settings.SkinEffects;
            var originalUseAccent=Svc.Settings.SkinUseAccent;var originalMode=Svc.Theme.Mode;
            try
            {
                foreach(var skin in new[]{SkinService.StarParty,SkinService.PhantomBlade})
                {
                    SkinService.Instance.Configure(skin,false,true);
                    Svc.Theme.SetMode(AppTheme.Light);Pump(120);
                    Save(main,System.IO.Path.Combine(artifacts,"detail-"+skin+"-light.png"));
                    ThemeTransition.SwitchWithRipple(main,new Point(40,main.ActualHeight-60),AppTheme.Dark);
                    Pump(700);
                    check(Svc.Theme.IsDark && typeof(ThemeTransition).GetField("_active",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)==null,
                        skin+" theme transition completes and clears the snapshot");
                    check(((SkinSurface)main.FindName("EmbeddedHeaderSurface")).IsVisible,
                        skin+" detail header remains visible in dark mode");
                    Save(main,System.IO.Path.Combine(artifacts,"detail-"+skin+"-dark.png"));
                }
            }
            finally
            {
                SkinService.Instance.Configure(originalSkin,originalEffects,originalUseAccent);
                Svc.Theme.SetMode(originalMode);
            }
            main.HideEmbedded();Pump(80);
            check(((System.Windows.Controls.ContentControl)main.FindName("PageHost")).IsVisible,
                "back from detail restores the main page");
            ThemeSnapshotTests.Run(check);

            main.HideEmbedded();
            App.IsExiting=true;main.Close();App.IsExiting=false;main=null;
        }
        finally
        {
            if(main!=null){App.IsExiting=true;main.Close();App.IsExiting=false;}
            field.SetValue(Svc.Api,old);
        }
    }
    static void Pump(int ms)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(ms)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
    static void Save(FrameworkElement root,string path)
    {
        root.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(root);
        var bmp=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth*dpi.DpiScaleX),
            (int)Math.Ceiling(root.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
        bmp.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));
        using var file=File.Create(path);png.Save(file);
    }
    sealed class Fixture:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var json="{\"code\":-404,\"message\":\"fixture\"}";
            if(request.RequestUri!.AbsolutePath=="/x/v2/reply")
            {
                var replies=Enumerable.Range(1,8).Select(i=>new {rpid=i,ctime=1793800000,like=i,
                    member=new {uname="评论用户 "+i,mid=i},content=new {message="这是一条示例评论，正文、评论列表和输入框可以各自清楚地显示。"}});
                json=System.Text.Json.JsonSerializer.Serialize(new {code=0,data=new {page=new {count=8},replies}});
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});
        }
    }
}
