using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliDesk;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using BiliDesk.Views.Controls;
using BiliDesk.Views.Pages;

internal static class DynamicTests
{
    static DynamicItem Parse(string json) { using var doc = JsonDocument.Parse(json); return DynamicParser.Parse(doc.RootElement)!; }
    const string Draw = """
    {"id_str":"9001","type":"DYNAMIC_TYPE_DRAW","basic":{"comment_id_str":"7001","comment_type":11},"modules":{"module_author":{"mid":"42","name":"示例 UP","pub_time":"刚刚"},"module_dynamic":{"major":{"opus":{"title":"今天的记录","summary":{"text":"正文不会因为图片字段缺失而消失。"},"pics":[{"url":"","width":800,"height":600}]}}},"module_stat":{"comment":{"count":2},"like":{"count":3,"status":true}}}}
    """;
    public static void Run(Action<bool,string> check, string output)
    {
        var draw = Parse(Draw);
        check(draw.Kind == DynamicKind.ImageText, "string dynamic kind");
        check(draw.CommentOid == "7001" && draw.CommentType == 11, "server comment identity");
        check(draw.AuthorMid == 42 && draw.ContentTitle == "今天的记录" && draw.HasText, "opus title and summary");
        check(draw.IsLiked && draw.LikeCount == 3, "server like state");
        foreach (var (type, kind) in new[] {("DYNAMIC_TYPE_AV",DynamicKind.Video),("DYNAMIC_TYPE_WORD",DynamicKind.Text),("DYNAMIC_TYPE_FORWARD",DynamicKind.Forward),("DYNAMIC_TYPE_ARTICLE",DynamicKind.Article),("DYNAMIC_TYPE_LIVE_RCMD",DynamicKind.Live)})
            check(Parse($$"""{"id_str":"1","type":"{{type}}","text":"content","rid":"2"}""").Kind == kind, type);
        check(Parse("""{"id":123,"type":4,"text":"legacy"}""").Id == "123", "numeric legacy id");
        var opus = Parse(Draw.Replace("\"url\":\"\"", "\"url\":\"https://example.invalid/p.png\""));
        check(opus.HasAttachedCard && opus.HasPictures, "pictures are visible");
        var forward = Parse("""{"id_str":"2","type":"DYNAMIC_TYPE_FORWARD","orig":{"id_str":"3","type":"DYNAMIC_TYPE_WORD","text":"original"}}""");
        check(forward.HasAttachedCard && forward.Original?.Text == "original", "forward visible");
        var deleted = Parse("""{"id_str":"2","type":"DYNAMIC_TYPE_FORWARD","orig":{"type":"DYNAMIC_TYPE_NONE"}}""");
        check(deleted.OriginalDeleted, "deleted original");
        var live = Parse("""{"id_str":"2","type":"DYNAMIC_TYPE_LIVE_RCMD","modules":{"module_dynamic":{"major":{"live_rcmd":{"content":"{\"live_play_info\":{\"room_id\":123,\"title\":\"直播标题\"}}"}}}}}""");
        check(live.LiveRoomId == 123 && live.Text == "直播标题", "live recommendation nested JSON");
        var video = Parse("""{"id_str":"2","type":"DYNAMIC_TYPE_AV","modules":{"module_author":{"mid":42,"name":"UP"},"module_dynamic":{"major":{"archive":{"aid":"100","bvid":"BV1234567890","title":"video","stat":{"play":"1.2万","danmaku":"1.3万"}}}}}}""");
        check(video.Video?.Aid == 100 && video.Video.ViewCount == 12000 && video.Video.DanmakuCount == 13000, "video numeric strings");
        check(video.Video?.Author == "UP" && video.Video.OwnerMid == 42, "video author propagated");
        var events = new List<string>(); draw.PropertyChanged += (_,e) => events.Add(e.PropertyName!);
        draw.TopComments = new() { new() { UserName="观众", Text="热评测试" } }; draw.HasCommentsChanged();
        check(events.Contains("TopComments") && events.Contains("HasComments"), "comment bindings notified");
        draw.SetLiked(false); check(draw.LikeCount == 2 && !draw.IsLiked, "unlike updates count");
        draw.SetLiked(false); check(draw.LikeCount == 2, "same state idempotent");

        var api = Svc.Api;
        var handler = new FakeHandler();
        typeof(ApiClient).GetField("_http", BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api, new HttpClient(handler));
        foreach (var name in new[]{"_buvid3","_buvid4"}) typeof(ApiClient).GetField(name, BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api,"test");
        Svc.Session.Current.SessData = "test-only";
        Wait(api.GetDynamicFeedFullAsync());
        check(handler.Paths.Last().Contains("/feed/all?"), "full feed endpoint");
        var comments = api.GetDynamicCommentsAsync("7001",11); Wait(comments);
        check(handler.Paths.Last().Contains("oid=7001") && handler.Paths.Last().Contains("type=11"), "comment query object");
        check(comments.Result?[0].LikeCount == 5, "comment numeric likes");
        var vm = new FollowViewModel();
        var first = vm.SelectUpAsync(new FollowUser { Mid=10, Name="slow" });
        var second = vm.SelectUpAsync(new FollowUser { Mid=20, Name="fast" });
        Wait(second); Wait(first);
        check(vm.Dynamics.Count == 1 && vm.Dynamics[0].Id == "20", "rapid selection latest result wins");
        check(!vm.Loading && vm.SelectionTitle == "fast 的动态", "selection title and loading");
        check(!vm.HasMore, "server terminal cursor respected");
        Wait(vm.CheckNewDynamicsAsync());
        check(handler.Paths.Last().Contains("host_mid=20"), "new posts poll selected author");

        var page = new FollowPage();
        var window = new Window { Content=page, Width=1100, Height=780, ShowInTaskbar=false };
        window.Show(); Pump();
        for (int i=0;i<300;i++) vm.Dynamics.Add(new DynamicItem { Id=$"fixture{i}", Kind=DynamicKind.Text, AuthorName="示例 UP", Text="混合动态时间线 · 正文示例", PubTimeText="刚刚" });
        page.DataContext = vm; Pump();
        var list = (ListBox)page.FindName("DynamicList"); list.UpdateLayout();
        int realized = Enumerable.Range(0,vm.Dynamics.Count).Count(i=>list.ItemContainerGenerator.ContainerFromIndex(i)!=null);
        check(realized > 0 && realized < 40, $"virtualized containers: {realized}/{vm.Dynamics.Count}");
        var card = new DynamicFeedCard { DataContext=draw, Width=640 };
        window.Content=card; Pump();
        var texts = Descendants<TextBlock>(card).Select(t=>t.Text).ToList();
        check(texts.Contains("今天的记录") && texts.Contains("热评测试"), "rendered title and hot comment");
        check(!texts.Any(t=>t=="BiliDesk.Models.DynamicItem"), "no fallback type name");
        card.Measure(new Size(640,double.PositiveInfinity));
        card.Arrange(new Rect(new Point(),card.DesiredSize)); card.UpdateLayout();
        var bitmap = new RenderTargetBitmap(640,(int)Math.Ceiling(card.DesiredSize.Height),96,96,PixelFormats.Pbgra32); bitmap.Render(card);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(Path.Combine(output,"dynamic-card.png"))) encoder.Save(stream);
        foreach (var count in new[]{1,2,4,9})
        {
            var imageItem = new DynamicItem { Id="images", Kind=DynamicKind.ImageText, AuthorName="image fixture" };
            for (int i=0;i<count;i++) imageItem.Pictures.Add(new DynamicPicture());
            window.Content = new DynamicFeedCard { DataContext=imageItem }; Pump();
            check(Descendants<DynamicPicturePanel>(window).Any(), $"render picture panel {count}");
        }
        // Exercise the native page with a full detail response and real WPF templates.
        var detail=new DynamicDetailPage(draw);
        window.Content=detail;Pump();Wait(detail.ViewModel.InitializeAsync());
        while(detail.ViewModel.Loading)Pump();
        check(detail.ViewModel.Item.CommentOid=="7001", "native detail keeps server comment target");
        check(detail.ViewModel.Comments.Count==1, "native comments rendered");
        check(!Descendants<Microsoft.Web.WebView2.Wpf.WebView2>(detail).Any(), "dynamic detail has no WebView");
        var comment=detail.ViewModel.Comments[0];
        Wait(detail.ViewModel.LoadRepliesAsync(comment));
        check(comment.Replies.Count==2 && !comment.HasMoreReplies, "native replies expand and finish");
        Svc.Session.Current.BiliJct="test-csrf";
        typeof(ApiClient).GetField("_writeReady",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api,true);
        detail.ViewModel.ComposeReply(comment);detail.ViewModel.Draft="native reply";
        handler.FailPost=true;Wait(detail.ViewModel.SendAsync());
        check(detail.ViewModel.Draft=="native reply" && detail.ViewModel.Error.Length>0,"failed send preserves draft");
        handler.FailPost=false;Wait(detail.ViewModel.SendAsync());
        check(detail.ViewModel.Draft.Length==0 && handler.Forms.Any(f=>f.Contains("type=11") && f.Contains("root=8001")),"native reply targets correct comment type and root");
        detail.ViewModel.ComposeRepost();detail.ViewModel.Draft="native repost";Wait(detail.ViewModel.SendAsync());
        check(handler.Forms.Any(f=>f.Contains("dynamic_id=9001") && f.Contains("content=native+repost")),"native repost form");
        Wait(detail.ViewModel.LikeCommentAsync(comment));
        check(comment.IsLiked && handler.Forms.Any(f=>f.Contains("rpid=8001") && f.Contains("type=11")),"native comment like type");
        foreach(var mode in new[]{AppTheme.Light,AppTheme.Dark})
        {
            Application.Current.Resources.MergedDictionaries[0]=new ResourceDictionary {Source=new Uri($"/BiliDesk;component/Themes/Colors.{mode}.xaml",UriKind.Relative)};Pump();
            var shot=new RenderTargetBitmap((int)detail.ActualWidth,(int)detail.ActualHeight,96,96,PixelFormats.Pbgra32);shot.Render(detail);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(shot));
            using(var stream=File.Create(Path.Combine(output,$"dynamic-detail-{mode}.png")))png.Save(stream);
        }
        var home=new HomeViewModel {IsPopularTab=true,IsRecommendTab=false};
        typeof(HomeViewModel).GetField("_popularPage",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(home,1);
        handler.FailPopular=true;Wait(home.LoadMoreAsync());
        check((int)typeof(HomeViewModel).GetField("_popularPage",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(home)! == 1,"home failed page retains cursor");
        handler.FailPopular=false;Wait(home.LoadMoreAsync());
        check(handler.Paths.Last().Contains("pn=2") && home.PopularItems.Count==1,"home retries same page successfully");
        var gallery=new BiliDesk.Views.Windows.ImagePreviewWindow();gallery.Show();
        Wait(gallery.LoadGalleryAsync(new[]{"",""}));
        check(((TextBlock)gallery.FindName("GalleryIndex")).Text=="1 / 2","gallery starts at selected image");
        ((Button)gallery.FindName("NextImage")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        check(((TextBlock)gallery.FindName("GalleryIndex")).Text=="2 / 2" && !((Button)gallery.FindName("NextImage")).IsEnabled,"gallery next and end boundary");
        gallery.Close();
        var switchingHome=new HomeViewModel();switchingHome.RecommendItems.Add(new VideoItem {Bvid="cached"});
        handler.FailPopular=true;handler.DelayPopular=true;
        var slowPopular=switchingHome.SwitchToPopularAsync();Wait(switchingHome.SwitchToRecommendAsync());Wait(slowPopular);
        check(switchingHome.IsRecommendTab && !switchingHome.Loading && switchingHome.Error.Length==0,"inactive home request cannot overwrite active state");
        handler.FailPopular=false;handler.DelayPopular=false;
        var wallItems=new System.Collections.ObjectModel.ObservableCollection<VideoItem>(Enumerable.Range(0,600).Select(i=>new VideoItem {Bvid=$"BV{i:D10}",Title=$"首页虚拟化验证 {i} · 测试较长的标题文字与换行布局",Author="示例 UP"}));
        var wallRoot=new FrameworkElementFactory(typeof(BiliDesk.Helpers.VirtualizingCardWallPanel));
        wallRoot.SetValue(BiliDesk.Helpers.CardWall.EnableProperty,true);
        var wallList=new ItemsControl {ItemsSource=wallItems,ItemsPanel=new ItemsPanelTemplate(wallRoot),ItemTemplate=new DataTemplate {VisualTree=new FrameworkElementFactory(typeof(VideoCard))}};
        var content=new StackPanel();content.Children.Add(new TextBlock {Text="首页测试标题",Height=50});content.Children.Add(wallList);content.Children.Add(new Border {Height=250});
        var wallScroll=new ScrollViewer {Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        window.Content=wallScroll;Pump();Pump();
        var wall=Descendants<BiliDesk.Helpers.VirtualizingCardWallPanel>(window).First();
        check(wall.RealizedCount>0 && wall.RealizedCount<60,$"home viewport containers {wall.RealizedCount}/600");
        wallScroll.ScrollToVerticalOffset(wallScroll.ScrollableHeight/2);Pump();Pump();
        var visibleItems=Descendants<VideoCard>(wall).Select(c=>(VideoItem)c.DataContext).ToList();
        check(visibleItems.Count>0 && visibleItems.All(c=>int.Parse(c.Bvid[2..])>200),"home middle scroll realizes correct rows");
        check(wall.RealizedCount<60,"home long scroll remains bounded");
        var anchor=visibleItems[visibleItems.Count/2];window.Width=850;Pump();Pump();
        check(Descendants<VideoCard>(wall).Any(c=>ReferenceEquals(c.DataContext,anchor)),"home resize retains viewport anchor");
        wallScroll.ScrollToBottom();Pump();Pump();
        Console.WriteLine($"HOME bottom offset={wallScroll.VerticalOffset:0} extent={wallScroll.ExtentHeight:0} realized={string.Join(',',Descendants<VideoCard>(wall).Select(c=>((VideoItem)c.DataContext).Bvid))}");
        check(Descendants<VideoCard>(wall).Any(c=>ReferenceEquals(c.DataContext,wallItems.Last())),"home last item reachable with footer");
        var generatedCount=Enumerable.Range(0,wallItems.Count).Count(i=>wallList.ItemContainerGenerator.ContainerFromIndex(i)!=null);
        check(generatedCount<60,$"home generator retains {generatedCount}/600 containers after long scroll");
        wallItems.Clear();for(int i=0;i<12;i++)wallItems.Add(new VideoItem {Bvid=$"BV{i:D10}",Title="筛选后的内容"});Pump();Pump();
        check(wall.RealizedCount<=12 && wall.RealizedCount>0,"home collection reset clamps viewport");
        BiliDesk.Helpers.CardWall.SetFixedColumns(2);Pump();Pump();
        check(BiliDesk.Helpers.CardWall.GetColumns(wall)==2,"home honors fixed column setting");
        BiliDesk.Helpers.CardWall.SetFixedColumns(0);Pump();Pump();
        wallItems.RemoveAt(0);Pump();Pump();
        check(Descendants<VideoCard>(wall).All(c=>wallItems.Contains((VideoItem)c.DataContext)),"home removal does not retain stale cards");
        var blankCover=new Border {Background=Brushes.Red};BiliDesk.Helpers.Cover.SetSource(blankCover," ");
        check(blankCover.Background==null,"recycled empty cover clears previous image");
        var mine=new MinePage {SectionFactory=key=>new System.Windows.Controls.TextBox {Text=key.ToString()}};
        window.Content=mine;Pump();
        check(mine.CurrentTabIndex==0,"mine starts on overview");
        mine.SelectSection(PageKey.History);Pump();
        var historySection=((Grid)mine.FindName("SectionHost")).Children.OfType<FrameworkElement>().First(x=>x.IsVisible);
        mine.SwipeDown(new Point(500,300),mine);mine.SwipeMove(new Point(150,305));mine.SwipeUp();for(int i=0;i<5;i++)Pump();
        check(mine.CurrentTabIndex==2,"mine horizontal gesture advances one section");
        mine.SwipeDown(new Point(400,300),mine);mine.SwipeMove(new Point(402,550));mine.SwipeUp();Pump();
        check(mine.CurrentTabIndex==2,"mine vertical gesture does not change section");
        mine.HandleHorizontalWheel(-1);for(int i=0;i<5;i++)Pump();
        check(mine.CurrentTabIndex==1 && ReferenceEquals(historySection,((Grid)mine.FindName("SectionHost")).Children.OfType<FrameworkElement>().First(x=>x.IsVisible)),"mine restores cached section");
        mine.SelectSection(PageKey.Mine);mine.SwipeDown(new Point(100,300),mine);mine.SwipeMove(new Point(600,300));mine.SwipeUp();for(int i=0;i<5;i++)Pump();
        check(mine.CurrentTabIndex==0,"mine edge swipe stays in range");
        // Mixed, variable-height timeline: the original plain-text-only fixture missed the freeze.
        var mixedPage=new FollowPage();window.Content=mixedPage;Pump();
        var mixedVm=new FollowViewModel();mixedVm.Users.Add(new FollowUser {Mid=42,Name="UP"});
        for(int i=0;i<100;i++)
        {
            var d=new DynamicItem {Id=$"mixed-{i}",Kind=i%2==0?DynamicKind.ImageText:DynamicKind.Text,Text=new string('字',i%5*100+10),AuthorName="混合流"};
            if(d.Kind==DynamicKind.ImageText)for(int k=0;k<i%9+1;k++)d.Pictures.Add(new DynamicPicture {Width=1600,Height=k%2==0?900:2400});
            mixedVm.Dynamics.Add(d);
        }
        var mount=System.Diagnostics.Stopwatch.StartNew();mixedPage.DataContext=mixedVm;Pump();Pump();
        check(mount.ElapsedMilliseconds<3000,"mixed timeline mounting completes without layout loop");
        var mixedList=(ListBox)mixedPage.FindName("DynamicList");
        for(int i=0;i<100;i+=7){mixedList.ScrollIntoView(mixedVm.Dynamics[i]);Pump();}
        check(Enumerable.Range(0,100).Count(i=>mixedList.ItemContainerGenerator.ContainerFromIndex(i)!=null)<30,"mixed timeline scroll remains virtualized");
        var nav=new NavigationDispatcher(window);Svc.Navigate=nav;
        DynamicActions.OpenDetail(draw);Pump();
        check(Application.Current.Windows.OfType<Window>().Any(w=>w.Content is DynamicDetailPage),"card routes to native dynamic page");
        foreach(var w in Application.Current.Windows.OfType<Window>().Where(w=>w!=window).ToList())w.Close();
        window.Close(); Svc.Session.Current.SessData="";
    }
    static IEnumerable<T> Descendants<T>(DependencyObject obj) where T:DependencyObject
    { for(int i=0;i<VisualTreeHelper.GetChildrenCount(obj);i++) { var child=VisualTreeHelper.GetChild(obj,i); if(child is T match) yield return match; foreach(var nested in Descendants<T>(child)) yield return nested; } }
    static void Wait(Task task) { var limit=DateTime.UtcNow.AddSeconds(10); while(!task.IsCompleted && DateTime.UtcNow<limit) Pump(); task.GetAwaiter().GetResult(); }
    static void Pump() { var frame=new DispatcherFrame(); var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)}; timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;}; timer.Start();Dispatcher.PushFrame(frame); }
    sealed class FakeHandler:HttpMessageHandler
    {
        public List<string> Paths {get;}=new();
        public List<string> Forms {get;}=new();
        public bool FailPost {get;set;}
        public bool FailPopular {get;set;}
        public bool DelayPopular {get;set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var url=request.RequestUri!.ToString(); Paths.Add(url);
            if(url.Contains("/popular?") && DelayPopular)await Task.Delay(200,token);
            if(url.Contains("host_mid=10")) await Task.Delay(200,token);
            var id=url.Contains("host_mid=20")?"20":"10";
            string json;
            if(request.Method==HttpMethod.Post)
            {
                Forms.Add(await request.Content!.ReadAsStringAsync(token));
                json=FailPost?"{\"code\":-400,\"message\":\"test failure\"}":"{\"code\":0}";
            }
            else if(url.Contains("/popular?")) json=FailPopular?"{\"code\":-400,\"message\":\"failed\"}":"""{"code":0,"data":{"list":[{"bvid":"BV1234567890","title":"home test","owner":{"mid":1,"name":"UP"}}]}}""";
            else if(url.Contains("/detail?"))json="{\"code\":0,\"data\":{\"item\":"+Draw+"}}";
            else if(url.Contains("/reply/reply?"))json="""{"code":0,"data":{"replies":[{"rpid":8101,"content":{"message":"reply one"},"member":{"uname":"first"}},{"rpid":8102,"content":{"message":"reply two"},"member":{"uname":"second"}}]}}""";
            else if(url.Contains("/reply?"))json="""{"code":0,"data":{"page":{"count":1},"replies":[{"rpid":8001,"rcount":2,"content":{"message":"这是原生评论区，可选择文字、回复和点赞。"},"member":{"uname":"viewer"},"like":5}]}}""";
            else json="""{"code":0,"data":{"has_more":false,"offset":"terminal","items":[{"id_str":"PLACEHOLDER","type":"DYNAMIC_TYPE_WORD","text":"fixture"}]}}""".Replace("PLACEHOLDER",id);
            return new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(json)};
        }
    }
}
