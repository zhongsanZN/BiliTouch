using BiliTouch;
using BiliTouch.Models;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views.Controls;
using BiliTouch.Views.Pages;
internal static class Release022Tests {
 const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
 static T Field<T>(object o,string n)=>(T)o.GetType().GetField(n,Hidden)!.GetValue(o)!;
 static void Call(object o,string n)=>o.GetType().GetMethod(n,Hidden)!.Invoke(o,null);
 static void Pump(int ms){var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f);}
 static IEnumerable<T> Desc<T>(DependencyObject d)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(d);i++){var c=VisualTreeHelper.GetChild(d,i);if(c is T t)yield return t;foreach(var x in Desc<T>(c))yield return x;}}
 static byte[] Pixels(FrameworkElement e,string? file=null){var b=new RenderTargetBitmap((int)e.ActualWidth,(int)e.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(e);if(file!=null){var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(b));using var f=File.Create(file);enc.Save(f);}var pixels=new byte[b.PixelWidth*b.PixelHeight*4];b.CopyPixels(pixels,b.PixelWidth*4,0);return pixels;}
 public static void Run(Action<bool,string> check,string output){
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
  Svc.Theme.SetMode(AppTheme.Dark);SkinService.Instance.Configure(SkinService.StarParty,true,true);
  var sidebar=new MainWindow();var entry=(Button)sidebar.FindName("StarPartyWebsite");
  check(entry.Visibility==Visibility.Visible && entry.Width>=44 && ((Image)entry.Content).Source!=null,"Star Party website entrance loads the specified bundled icon below the application logo");
  SkinService.Instance.Configure("",true,true);check(entry.Visibility==Visibility.Collapsed,"website entrance hides immediately outside the Star Party decoration");
  SkinService.Instance.Configure(SkinService.StarParty,true,true);check(entry.Visibility==Visibility.Visible,"website entrance returns when the Star Party decoration is enabled");sidebar.Close();
  var window=new Window{Width=1000,Height=720};
  foreach(var page in new UserControl[]{new MinePage(),new SettingsPage{DataContext=new SettingsViewModel()},new MessagesPage{DataContext=new MessagesViewModel()}}){window.Content=page;if(!window.IsVisible)window.Show();Pump(200);var tabs=Desc<NavigationTabs>(page).Single();check(Desc<RadioButton>(tabs).All(b=>ReferenceEquals(b.Style,Application.Current.FindResource("UnderlineTabStyle"))),page.GetType().Name+" uses the exact home radio style");Pixels(page,Path.Combine(output,"release022-"+page.GetType().Name+".png"));}
  var home=new HomePage{DataContext=new HomeViewModel()};window.Content=home;Pump(200);var card=(Border)home.FindName("SearchCard");var width=card.Width;
  for(int i=0;i<3;i++){Call(home,"OpenSearchOverlay");Pump(400);Call(home,"CloseSearchOverlay");Pump(600);}
  check(((Border)home.FindName("SearchContentBox")).Visibility==Visibility.Collapsed && Math.Abs(card.Width-width)<.1 && card.ClipToBounds,"repeated home search transitions clear the content border and restore their clipped bounds");
  check(((TextBox)home.FindName("SearchKeywordBox")).Template!=null && TextOptions.GetTextRenderingMode(card)==TextRenderingMode.Grayscale,"home search uses an explicit chrome-free template and alpha-safe text rendering");
  Pixels(home,Path.Combine(output,"release022-search-closed.png"));
  var gallery=new InlineImageGallery();window.Content=gallery;Pump(100);
  var url="https://example.invalid/gallery022.png";var data=new byte[200*120*4];for(int i=0;i<data.Length;i+=4){data[i]=130;data[i+1]=(byte)(70+i/800);data[i+2]=80;data[i+3]=255;}
  var image=BitmapSource.Create(200,120,96,96,PixelFormats.Bgra32,null,data,800);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));Directory.CreateDirectory(AppPaths.CacheDir);using(var file=File.Create(Path.Combine(AppPaths.CacheDir,Hashing.Md5Hex(url)+".jpg")))png.Save(file);
  var task=gallery.OpenAsync(new[]{url,url+"?next"},0);var deadline=Environment.TickCount64+5000;while(!task.IsCompleted && Environment.TickCount64<deadline)Pump(20);task.GetAwaiter().GetResult();Pump(100);
  var picture=Field<Image>(gallery,"_image");check(picture.Width>gallery.NaturalWidth*2,"fit mode enlarges a smaller original to use the available gallery area");
  var previous=Field<Button>(gallery,"_previous");var next=Field<Button>(gallery,"_next");check(previous.HorizontalAlignment==HorizontalAlignment.Left && next.HorizontalAlignment==HorizontalAlignment.Right && previous.VerticalAlignment==VerticalAlignment.Center,"gallery navigation belongs to the left and right picture edges");
  check(!Desc<Button>(gallery).Any(b=>b.Content is TextBlock t && t.Text is "原尺寸" or "适应窗口"),"gallery fits automatically without a lower-left size toggle");
  check(Field<Button>(gallery,"_save").VerticalAlignment==VerticalAlignment.Bottom && Field<Button>(gallery,"_collapse").VerticalAlignment==VerticalAlignment.Top,"save and close controls sit at picture corners");
  Pixels(gallery,Path.Combine(output,"release022-gallery.png"));
  gallery.SetInline(true);var viewport=Field<Grid>(gallery,"_viewport");bool bubbled=false;
  gallery.AddHandler(System.Windows.Input.Mouse.MouseWheelEvent,new System.Windows.Input.MouseWheelEventHandler((_,e)=>bubbled=true));
  var wheel=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent,Source=viewport};
  viewport.RaiseEvent(wheel);check(!wheel.Handled && gallery.Zoom==1,"inline portrait wheel remains unhandled so comments can scroll without image zoom");
  wheel.RoutedEvent=System.Windows.Input.Mouse.MouseWheelEvent;viewport.RaiseEvent(wheel);check(bubbled,"inline image wheel bubbles to the parent scrolling region");
  gallery.Cancel();
  var fixture=Path.Combine(AppContext.BaseDirectory,"../../../../../tests/TouchTests/Fixtures/hsr-pv-reference.json");
  using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixture));var post=DynamicParser.Parse(json.RootElement)!;
  var reference=post.RichNodes.Single(n=>n.IsLink);check(reference.ReferencedBvid=="BV1XJ4g6WEyL","OPUS BV reference resolves its native video identifier even when video metadata is null");
  check(post.Video==null && post.CommentOid=="414357881" && post.LinkedVideos.Single().Bvid==reference.ReferencedBvid,"PV reference preview preserves the original post and comment target");
  var fromWeb=new DynamicRichNode{Type="RICH_TEXT_NODE_TYPE_WEB",JumpUrl="//www.bilibili.com/video/BV1XJ4g6WEyL/?t=1"};
  check(fromWeb.ReferencedBvid==reference.ReferencedBvid,"web-form video links use the same native reference route");
  check(new DynamicRichNode{Type="RICH_TEXT_NODE_TYPE_WEB",JumpUrl="https://example.com/BV1XJ4g6WEyLsuffix"}.ReferencedBvid=="","invalid partial video identifiers are rejected");
  reference.VideoPreview=new VideoItem{Bvid=reference.ReferencedBvid,Title="即兴巡演 PV",Cover=url,Author="崩坏星穹铁道",Duration="3:12"};
  var cache=(System.Collections.Concurrent.ConcurrentDictionary<string,VideoItem>)typeof(DynamicFeedCard).GetField("LinkedVideoCache",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;cache[reference.ReferencedBvid]=reference.VideoPreview;
  var feed=new DynamicFeedCard{DataContext=post,IsDetail=true};window.Content=feed;Pump(150);
  check(((ItemsControl)feed.FindName("LinkedVideoCards")).Items.Count==1,"reference preview appears alongside the original attached card");
  var text=(DynamicRichText)feed.FindName("BodyText");DynamicRichNode? activated=null;text.NodeActivated+=(_,n)=>activated=n;
  // Observe activation without executing a real player navigation in this isolated regression.
  var route=typeof(DynamicRichText).GetField("NodeActivated",BindingFlags.Instance|BindingFlags.NonPublic)!;
  var handlers=(EventHandler<DynamicRichNode>)route.GetValue(text)!;
  route.SetValue(text,handlers.GetInvocationList().Last());
  var link=text.Inlines.OfType<System.Windows.Documents.Hyperlink>().Single();var click=new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent);link.RaiseEvent(click);
  check(ReferenceEquals(activated,reference) && click.Handled,"pink hyperlink activates the BV node and stops parent card navigation");
  Pixels(feed,Path.Combine(output,"release022-linked-video.png"));window.Close();Pump(50);
 }
 public static void Live(Action<bool,string> check,string output)
 {
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
  var lookup=Svc.Api.GetVideoAsync("BV1XJ4g6WEyL");var deadline=Environment.TickCount64+20000;
  while(!lookup.IsCompleted && Environment.TickCount64<deadline)Pump(50);
  check(lookup.IsCompletedSuccessfully && lookup.Result is {ok:true,data:not null},"official HSR PV metadata resolves through the application API");
  var detail=lookup.Result.data!;var cover=CoverLoader.LoadAsync(detail.Pic,1200);deadline=Environment.TickCount64+20000;
  while(!cover.IsCompleted && Environment.TickCount64<deadline)Pump(50);
  check(cover.IsCompletedSuccessfully && cover.Result!=null,"referenced PV cover downloads and decodes through the real image loader");
  var post=new DynamicItem{RichNodes=new(){new DynamicRichNode{Type="RICH_TEXT_NODE_TYPE_BV",Rid=detail.Bvid,Text=detail.Title}},AuthorName="崩坏星穹铁道"};
  var feed=new DynamicFeedCard{DataContext=post,IsDetail=true};var window=new Window{Content=feed,Width=900,Height=720};window.Show();Pump(2500);
  check(post.LinkedVideos.Single().Cover==detail.Pic && post.LinkedVideos.Single().Title==detail.Title,"visible dynamic reference hydrates its cover and title without replacing the post");
  Pixels(feed,Path.Combine(output,"release022-linked-video-live.png"));window.Close();
 }

}
