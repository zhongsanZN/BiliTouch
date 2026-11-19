using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Models;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Pages;
using BiliTouch.Views.Controls;
internal static class Release021Tests {
 const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
 static object? Call(object o,string n,params object[] args)=>o.GetType().GetMethod(n,Hidden)!.Invoke(o,args);
 static T Field<T>(object o,string n)=>(T)o.GetType().GetField(n,Hidden)!.GetValue(o)!;
 static void Pump(int ms){var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f);}
 static IEnumerable<T> Desc<T>(DependencyObject d)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(d);i++){var c=VisualTreeHelper.GetChild(d,i);if(c is T t)yield return t;foreach(var x in Desc<T>(c))yield return x;}}
 static void Save(FrameworkElement e,string path){var b=new RenderTargetBitmap((int)e.ActualWidth,(int)e.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(e);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(b));using var f=File.Create(path);png.Save(f);}
 public static void Run(Action<bool,string> check,string output){
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
  Svc.Theme.SetMode(AppTheme.Dark);SkinService.Instance.Configure(SkinService.StarParty,true,true);Svc.Settings.SetCloseToTray(false);
  check(new AppSettings().TouchpadFeedSensitivity==100,"new profiles default to 100 percent touchpad sensitivity");
  var vm=new SettingsViewModel();check(!vm.TouchpadSensitivityHint.Contains("旧版")&&!vm.TouchpadSensitivityHint.Contains("滚轮"),"setting describes touchpad input without version comparisons");
  var main=new MainWindow{Width=1440,Height=900};main.Show();Pump(600);
  var header=(SkinSurface)main.FindName("SharedHeaderSurface");var initial=header.TranslatePoint(new Point(),main);var loaded=0;header.Loaded+=(_,_)=>loaded++;
  foreach(var key in new[]{PageKey.Follow,PageKey.Mine,PageKey.Settings,PageKey.Home}){
   Call(main,"ShowPage",key,true);Pump(400);
   check(header.Opacity==1 && header.IsVisible && header.TranslatePoint(new Point(),main)==initial,"shared header stays fixed while navigating to "+key);
  }
  check(loaded==0,"navigation never unloads or replaces the shared decoration");
  var home=(HomePage)((ContentControl)main.FindName("PageHost")).Content;var button=(Button)home.FindName("HomePartitionButton");main.OpenPartition(button);Pump(260);
  var panel=(Border)main.FindName("PartitionPanel");var anchor=button.TranslatePoint(new Point(button.ActualWidth,button.ActualHeight),main);var menu=panel.TranslatePoint(new Point(),main);
  check(menu.X>main.ActualWidth/2 && Math.Abs(menu.Y-anchor.Y-8)<2,"partition menu opens beneath its home button on the right");
  Save(main,Path.Combine(output,"release021-partition.png"));Call(main,"HidePartition");
  var homeVm=(HomeViewModel)home.DataContext;var deadline=Environment.TickCount64+18000;while(homeVm.Loading && Environment.TickCount64<deadline)Pump(80);var feedPath=Path.Combine(output,"release021-feed.json");
  if(File.Exists(feedPath)){
   var items=System.Text.Json.JsonSerializer.Deserialize<List<VideoItem>>(File.ReadAllText(feedPath))!;homeVm.RecommendItems.Clear();foreach(var item in items)homeVm.RecommendItems.Add(item);
  }else File.WriteAllText(feedPath,System.Text.Json.JsonSerializer.Serialize(homeVm.RecommendItems));
  Pump(1600);
  Save(main,Path.Combine(output,"release021-home.png"));Console.WriteLine("Home screenshot loaded cards: "+homeVm.RecommendItems.Count);
  main.Close();Pump(100);
  var player=new PlayerWindow{Width=1280,Height=860};player.Show();Pump(200);
  check(!((Button)player.FindName("BtnLike")).ToolTip.ToString()!.Contains("(L)"),"like tooltip contains no shortcut suffix");
  check(((Grid)player.FindName("LikeIconBox")).Width>=40 && ((SkinActionIcon)player.FindName("ThemedCoinIcon")).Width>=36,"reaction icons have enough room for the enlarged original artwork");
  var picture=new CommentPicture{Url="https://example.invalid/portrait021.png",Width=500,Height=1000};var comment=new CommentItem{UserName="竖图评论",Content="评论图片在原位展开"};comment.Pictures.Add(picture);
  ((ItemsControl)player.FindName("CommentList")).ItemsSource=new[]{comment};((System.Windows.Controls.Primitives.ToggleButton)player.FindName("TabComments")).IsChecked=true;Pump(180);
  var thumb=Desc<Border>((StackPanel)player.FindName("CommentsPanel")).First(b=>ReferenceEquals(b.DataContext,picture));Call(player,"OpenCommentPicture",thumb,picture);Pump(80);
  check(!Field<bool>(player,"_pictureSplitActive") && Field<InlineImageGallery>(player,"_commentGallery").IsInline,"portrait metadata opens in the comment column without resizing video");
  Save(player,Path.Combine(output,"release021-portrait-comment.png"));Call(player,"CloseCommentPicture");player.Close();Pump(250);
 }
}
