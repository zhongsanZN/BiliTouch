using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Controls;
using BiliTouch.Views.Pages;

internal static class UiPolishTests
{
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public static void Run(Action<bool,string> check,string artifacts,bool dynamicOnly=false)
    {
        var httpField=typeof(ApiClient).GetField("_http",Private)!;var oldHttp=httpField.GetValue(Svc.Api);
        var handler=new CommentsHttp();using var http=new HttpClient(handler);httpField.SetValue(Svc.Api,http);
        try
        {
            var main=new MainWindow();main.Show();Pump(250);
            App.MainVm.Navigate(PageKey.Home);Pump(250);
            var pageOf=typeof(MainWindow).GetMethod("PageOf",Private)!;
            var home=(HomePage)pageOf.Invoke(main,new object[]{PageKey.Home})!;
            var homeRefresh=(Button)home.FindName("HomeRefreshButton");
            var homePoint=homeRefresh.TranslatePoint(new Point(),main);
            var header=(Grid)home.FindName("PageHeader");var tabs=(Grid)home.FindName("TabsRow");
            check(header.ActualHeight<50,"home refresh overlay no longer expands the title row");
            var tabTop=tabs.TranslatePoint(new Point(),home).Y;
            check(tabTop<85,"home category labels stay close to the title instead of sinking below an empty band");
            check(main.Icon==AppBranding.WindowIcon,"main taskbar icon explicitly uses the current embedded brand image");
            using(var trayIcon=AppBranding.CreateTrayIcon())check(trayIcon.Width>0 && trayIcon.Height>0,"tray icon loads from the embedded ICO independently of the executable path");
            Save(main,System.IO.Path.Combine(artifacts,"touch-branding-home.png"));
            check(home.FindName("BtnFilter")==null && home.FindName("FilterPopup")==null,"home has no block-content floating entry");
            check(homeRefresh.IsVisible && !Descendants<Button>(home).Any(b=>b.ToolTip?.ToString()?.Contains("私信")==true),"home refresh is in the header and private messages are removed from its toolbar");
            App.MainVm.Navigate(PageKey.Follow);Pump(350);
            var follow=(FollowPage)pageOf.Invoke(main,new object[]{PageKey.Follow})!;
            var followRefresh=Descendants<Button>(follow).Single(b=>b.ToolTip?.ToString()=="刷新");
            var followPoint=followRefresh.TranslatePoint(new Point(),main);
            check(Math.Abs(homePoint.X-followPoint.X)<2 && Math.Abs(homePoint.Y-followPoint.Y)<2,"home and dynamic refresh buttons share the same screen position");
            var sidebar=(StackPanel)main.FindName("SidebarActions");
            var theme=(Button)main.FindName("SidebarTheme");var messages=(Button)main.FindName("SidebarMessages");
            check(Grid.GetRow(sidebar)==2 && theme.IsVisible && messages.IsVisible && theme.ActualHeight>=44,"theme and private messages remain reachable in the bottom sidebar");
            var themeTop=theme.TranslatePoint(new Point(),main).Y;
            check(themeTop>main.ActualHeight*0.7 && themeTop+theme.ActualHeight<=main.ActualHeight,"bottom sidebar controls stay inside the window");
            Save(main,System.IO.Path.Combine(artifacts,"ui-sidebar-follow.png"));
            var sessionBefore=Svc.Session.Current;
            try
            {
                typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,new Session{SessData="fixture",DedeUserID="42"});
                var followVm=(FollowViewModel)follow.DataContext;
                typeof(FollowViewModel).GetProperty(nameof(FollowViewModel.NewDynamicsText))!.SetValue(followVm,"20 条新动态");
                typeof(FollowViewModel).GetProperty(nameof(FollowViewModel.HasNewDynamics))!.SetValue(followVm,true);
                follow.DataContext=null;follow.DataContext=followVm;Pump(150);
                var fresh=(Button)follow.FindName("NewDynamicsButton");var publish=(Button)follow.FindName("PublishDynamicButton");
                var freshPoint=fresh.TranslatePoint(new Point(),follow);var publishPoint=publish.TranslatePoint(new Point(),follow);
                check(fresh.IsVisible && freshPoint.X+fresh.ActualWidth<=publishPoint.X && Math.Abs(freshPoint.Y-publishPoint.Y)<2,"new dynamics sit directly left of publish on the same header row");
                check(fresh.ActualHeight>=44 && publish.ActualHeight>=44,"compact dynamic header keeps touch targets at least 44 DIP high");
                var board=(Border)follow.FindName("FollowLiveBoard");var contacts=(FrameworkElement)follow.FindName("ContactsScroll");
                check(Math.Abs(board.TranslatePoint(new Point(),follow).Y-contacts.TranslatePoint(new Point(),follow).Y)<2,"live board moves up to align with the contact list without a duplicate title row");
                var feed=(ListBox)follow.FindName("DynamicList");var feedTop=feed.TranslatePoint(new Point(),follow).Y;
                Save(main,System.IO.Path.Combine(artifacts,"follow-header-compact.png"));
                typeof(FollowViewModel).GetProperty(nameof(FollowViewModel.HasNewDynamics))!.SetValue(followVm,false);Pump(80);
                check(Math.Abs(feedTop-feed.TranslatePoint(new Point(),follow).Y)<1,"new-dynamics visibility no longer shifts the feed vertically");
            }
            finally{typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,sessionBefore);}

            App.IsExiting=true;main.Close();App.IsExiting=false;

            var item=new DynamicItem {Id="1",Kind=DynamicKind.Text,Text="动态正文与评论独立滚动",AuthorName="测试 UP",CommentOid="123",CommentType=11};
            var detail=new DynamicDetailPage(item);
            typeof(DynamicDetailViewModel).GetField("_initialized",Private)!.SetValue(detail.ViewModel,true);
            Wait(detail.ViewModel.LoadCommentsAsync(true));
            check(detail.ViewModel.Comments.Count==60 && handler.Pages.Take(3).SequenceEqual(new[]{1,2,3}),"dynamic detail preloads three comment pages");
            var host=new Window {Width=1140,Height=760,Content=detail};host.Show();Pump(200);
            var list=(ListBox)detail.FindName("CommentList");var composer=(Border)detail.FindName("CommentComposerHost");var pane=(Grid)detail.FindName("CommentsPane");
            var composerRect=composer.TransformToAncestor(detail).TransformBounds(new Rect(composer.RenderSize));
            var paneRect=pane.TransformToAncestor(detail).TransformBounds(new Rect(pane.RenderSize));
            check(composerRect.Left>=paneRect.Left && composerRect.Right<=paneRect.Right+1,"comment composer is contained in the right comment pane");
            check(composer.ActualHeight<90 && list.ActualHeight>400,"compact composer leaves a tall comment viewport without the blank footer");
            check(!Descendants<Button>(detail).Any(b=>b.Content?.ToString()=="加载更多评论"),"dynamic detail has no manual load-more button");
            check(!Descendants<TextBlock>(detail).Any(t=>t.IsVisible && t.Text=="发表评论"),"normal composer has no redundant post-comment heading");
            var scroll=Descendants<ScrollViewer>(list).First();scroll.ScrollToEnd();
            Until(()=>detail.ViewModel.Comments.Count>=100 && !detail.ViewModel.Loading);
            check(detail.ViewModel.Comments.Count>=100,"approaching the comment bottom automatically fetches more comments");
            Save(host,System.IO.Path.Combine(artifacts,"ui-dynamic-detail-light.png"));
            Application.Current.Resources.MergedDictionaries[0]=new ResourceDictionary {Source=new Uri("/BiliTouch;component/Themes/Colors.Dark.xaml",UriKind.Relative)};Pump(80);
            Save(host,System.IO.Path.Combine(artifacts,"ui-dynamic-detail-dark.png"));
            host.Width=760;host.Height=560;Pump(100);
            check(composer.ActualHeight<90 && list.ActualHeight>250,"compact detail layout preserves room for reading comments");
            Save(host,System.IO.Path.Combine(artifacts,"ui-dynamic-detail-compact.png"));
            var card=Descendants<DynamicFeedCard>(detail).Single();
            check(!Descendants<Button>(card).Any(b=>b.ToolTip?.ToString()=="复制动态链接"),"dynamic action row removes the copy-link icon");
            var share=(Geometry)Application.Current.FindResource("ShareIconGeometry");var like=(Geometry)Application.Current.FindResource("LikeIconGeometry");
            check(Descendants<System.Windows.Shapes.Path>(card).Any(p=>p.Data?.ToString()==share.ToString()),"dynamic repost uses the shared player share geometry");
            check(Descendants<BiliTouch.Helpers.SkinActionIcon>(card).Any(p=>p.Kind=="Like" && p.Data?.ToString()==like.ToString() && p.Width==20),"dynamic like uses the player geometry at the same size");
            host.Close();
            if(dynamicOnly)return;

            var player=new PlayerWindow();player.Show();Pump(250);
            check(player.Icon==AppBranding.WindowIcon,"player taskbar icon uses the same embedded brand image");
            check(player.FindName("BtnWatchLater")==null && player.FindName("BtnWatchLaterTop") is Button,"watch later has one visible entry in the player title bar");
            var name=(TextBlock)player.FindName("UpNameText");name.Text="很长的 UP 主名字也能在资料行中自然显示";
            ((TextBlock)player.FindName("UpDescText")).Text="查看个人主页";Pump(80);
            var up=(Grid)player.FindName("UpHeader");var avatar=(Border)player.FindName("UpAvatarBox");var followButton=(Button)player.FindName("BtnFollow");
            check(Math.Abs(avatar.TranslatePoint(new Point(),up).X)<2 && avatar.ActualWidth==40,"UP profile starts at the left of the full-width author row");
            var nameBox=name.TransformToAncestor(up).TransformBounds(new Rect(name.RenderSize));var buttonBox=followButton.TransformToAncestor(up).TransformBounds(new Rect(followButton.RenderSize));
            check(nameBox.Right<=buttonBox.Left+1 && up.ActualWidth>250,"long author names do not collide with the follow button");
            check(((System.Windows.Shapes.Path)player.FindName("ShareGlyph")).Data.ToString()==share.ToString(),"player and dynamic share use the same global geometry");
            Save(player,System.IO.Path.Combine(artifacts,"ui-player-author.png"));
            var originalBounds = player.RestoreBounds;
            Invoke(player,"HandleTrackpadPinch",40);Invoke(player,"HandleTrackpadPinch",40);
            check(!Field<bool>(player,"_isFullscreen"),"small trackpad deltas accumulate without accidental fullscreen");
            Invoke(player,"HandleTrackpadPinch",40);
            check(Field<bool>(player,"_isFullscreen"),"trackpad spread enters fullscreen");
            Invoke(player,"HandleTrackpadPinch",-240);
            check(Field<bool>(player,"_isFullscreen"),"one trackpad pinch stream cannot flip fullscreen twice");
            Pump(220);Invoke(player,"HandleTrackpadPinch",-120);Pump(200);
            check(!Field<bool>(player,"_isFullscreen") && player.WindowState==WindowState.Normal,"trackpad pinch exits to the original normal state");
            check(Near(player.RestoreBounds,originalBounds),"normal window position and dimensions survive fullscreen");
            check(Field<LibVLCSharp.Shared.MediaPlayer>(player,"_mp").Scale==0,"trackpad gesture never changes the picture scale");
            Pump(220);player.WindowState=WindowState.Maximized;Pump(200);
            Invoke(player,"HandleTrackpadPinch",120);Pump(250);
            check(Field<bool>(player,"_isFullscreen"),"a maximized player can enter fullscreen");
            Invoke(player,"HandleTrackpadPinch",-120);Pump(250);
            check(!Field<bool>(player,"_isFullscreen") && player.WindowState==WindowState.Maximized,"fullscreen exit restores the maximized state");
            player.WindowState=WindowState.Normal;Pump(180);
            check(Near(player.RestoreBounds,originalBounds),"restoring after maximized fullscreen keeps the original normal bounds");
            typeof(PlayerWindow).GetField("_aid",Private)!.SetValue(player,123L);
            ((RadioButton)player.FindName("TabComments")).IsChecked=true;Pump(120);
            var comment = new CommentItem {Rpid=10,UserName="测试评论"};
            Invoke(player,"ShowCommentDetail",comment,comment);Pump(200);
            var back=(Button)player.FindName("BtnBackToComments");var fab=(Button)player.FindName("CommentFab");
            var backRect=back.TransformToAncestor(player).TransformBounds(new Rect(back.RenderSize));
            var fabRect=fab.TransformToAncestor(player).TransformBounds(new Rect(fab.RenderSize));
            check(back.IsVisible && back.Width==fab.Width && back.Height==fab.Height,"comment detail return matches the post-comment button size");
            check(Math.Abs(backRect.Bottom-fabRect.Bottom)<1 && backRect.Right<fabRect.Left && fabRect.Left-backRect.Right<12,"comment return is immediately to the left of the bottom-right composer button");
            check(!Descendants<Button>(player).Any(b=>b.Content?.ToString()=="返回评论列表"),"comment header has no redundant text return button");
            Invoke(player,"OpenComposer");Pump(220);
            var composerPanel=(Border)player.FindName("CommentComposerPanel");
            var inputRect=composerPanel.TransformToAncestor(player).TransformBounds(new Rect(composerPanel.RenderSize));
            check(inputRect.Bottom<=backRect.Top,"expanded composer does not obscure the return button");
            Save(player,System.IO.Path.Combine(artifacts,"release-comment-return.png"));
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(100);
            check(back.Visibility==Visibility.Collapsed && ((FrameworkElement)player.FindName("CommentList")).Visibility==Visibility.Visible,"bottom return closes the thread and restores the comment list");
            player.Close();
            CheckSettings(check,artifacts);
        }
        finally{httpField.SetValue(Svc.Api,oldHttp);}
    }
    private static void CheckSettings(Action<bool,string> check,string artifacts)
    {
        var dict=new ResourceDictionary { Source=new Uri("/BiliTouch;component/Views/Pages/Settings/SettingsTabs.xaml",UriKind.Relative) };
        var vm=new SettingsViewModel();
        var general=(FrameworkElement)((DataTemplate)dict["GeneralTabTemplate"]).LoadContent();general.DataContext=vm;
        var host=new Window {Width=850,Height=800,Content=new ScrollViewer {Content=general}};host.Show();Pump(120);
        var filter=BiliTouch.Helpers.FilterService.Instance;var oldTitle=filter.TitleKeyword;var oldUp=filter.UpKeyword;
        try
        {
            check(Descendants<TextBlock>(general).Any(t=>t.Text=="屏蔽内容"),"global content blocking lives in General settings");
            var titleBox=Descendants<TextBox>(general).Single(t=>System.Windows.Data.BindingOperations.GetBinding(t,TextBox.TextProperty)?.Path.Path=="TitleKeyword");
            titleBox.Text="屏蔽测试词";Pump(80);
            check(filter.TitleKeyword=="屏蔽测试词","General block keyword editor remains connected to the existing global filter");
            filter.BlockTitle("设置撤销测试标题");Pump(80);
            var rule=filter.Rules.First(r=>r.Label=="设置撤销测试标题");
            var remove=Descendants<Button>(general).Single(b=>ReferenceEquals(b.CommandParameter,rule));
            check(remove.Command==vm.RemoveFilterRuleCommand,"individual block chips resolve the Settings command");
            remove.Command.Execute(rule);Pump(80);
            check(!filter.Rules.Contains(rule),"General settings can remove an individual existing block rule");
            var clear=Descendants<Button>(general).Single(b=>b.Content?.ToString()=="清除");clear.Command.Execute(null);Pump(80);
            check(!filter.HasFilter,"General clear action removes keyword and precise rules together");
            titleBox.BringIntoView();Pump(100);Save(host,System.IO.Path.Combine(artifacts,"release-settings-general.png"));
        }
        finally {filter.TitleKeyword=oldTitle;filter.UpKeyword=oldUp;host.Close();}
        var about=(FrameworkElement)((DataTemplate)dict["AboutTabTemplate"]).LoadContent();about.DataContext=vm;
        var aboutHost=new Window {Width=1000,Height=800,Content=new ScrollViewer {Content=about}};aboutHost.Show();Pump(100);
        var author=Descendants<Button>(about).Single(b=>b.ToolTip?.ToString()=="在应用内打开中三的 B 站主页");
        check(author.Command==vm.OpenBiliTouchAuthorCommand && SettingsViewModel.BiliTouchAuthorMid==349921038,"About links the BiliTouch author to the verified personal UID");
        check(Descendants<TextBlock>(about).Any(t=>t.Text.StartsWith("围绕触摸屏、触控板手势")),"About describes touchscreen and touchpad gestures explicitly");
        check(Descendants<System.Windows.Controls.Image>(about).Any(i=>i.Source?.ToString().Contains("upstream-original.png")==true),"the original app icon is retained in acknowledgements");
        Save(aboutHost,System.IO.Path.Combine(artifacts,"release-about.png"));aboutHost.Close();
    }
    private static void Invoke(object target,string method,params object[] args)=>target.GetType().GetMethod(method,Private)!.Invoke(target,args);
    private static T Field<T>(object target,string field)=>(T)target.GetType().GetField(field,Private)!.GetValue(target)!;
    private static bool Near(Rect a,Rect b)=>Math.Abs(a.Left-b.Left)<2 && Math.Abs(a.Top-b.Top)<2 && Math.Abs(a.Width-b.Width)<2 && Math.Abs(a.Height-b.Height)<2;
    private static void Wait(Task task){Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    private static void Until(Func<bool> done){var start=Environment.TickCount64;while(!done()){if(Environment.TickCount64-start>15000)throw new TimeoutException("UI check did not settle");Pump(30);}}
    private static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T t)yield return t;foreach(var nested in Descendants<T>(child))yield return nested;}}
    private static void Save(FrameworkElement root,string path){root.UpdateLayout();var bmp=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(path);png.Save(file);}
    private sealed class CommentsHttp:HttpMessageHandler
    {
        public readonly List<int> Pages=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            string json="{\"code\":-400,\"message\":\"fixture: no data\"}";
            if(request.RequestUri!.AbsolutePath=="/x/v2/reply")
            {
                int page=int.Parse(Regex.Match(request.RequestUri.Query,@"(?:\?|&)pn=(\d+)").Groups[1].Value);Pages.Add(page);
                var replies=Enumerable.Range(1,20).Select(i=>new {rpid=page*1000+i,ctime=1793800000,like=i,member=new {uname="评论用户 "+i,mid=i},content=new {message="第 "+page+" 页评论 "+i+"：评论区与输入框各自占据清晰的位置。"}});
                json=JsonSerializer.Serialize(new {code=0,data=new {page=new {count=160},replies}});
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});
        }
    }
}
