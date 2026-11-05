using System.IO;
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
using BiliDesk.Views;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

internal static class WebPlayerLayoutTests
{
 static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
 static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Hidden)!.GetValue(target)!;
 static void Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Hidden)!.Invoke(target,args);
 static T Element<T>(PlayerWindow p,string name)=>(T)p.FindName(name);
 static Rect Bounds(FrameworkElement e,Visual ancestor)=>e.TransformToAncestor(ancestor).TransformBounds(new Rect(e.RenderSize));
 public static void Run(Action<bool,string> check,string artifacts)
 {
  var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
  var old=Svc.Settings.UseWebPlayerLayout;var oldScale=Svc.Settings.UiScalePercent;PlayerWindow? player=null;
  try
  {
   Svc.Settings.SetWebPlayerLayout(false);Svc.Settings.SetUiScalePercent(100);
   player=new PlayerWindow {Width=1320,Height=860};player.Show();Pump(300);
   var stage=Element<Grid>(player,"PlayerStage");var surface=Element<Grid>(player,"VideoSurface");
   var comments=Element<StackPanel>(player,"CommentsPanel");var host=Element<ContentControl>(player,"WebCommentsHost");
   var scroll=Element<ScrollViewer>(player,"WebLeftScroll");var placeholder=Element<Border>(player,"WebVideoPlaceholder");
   var dock=Element<Border>(player,"WebDockAnchor");var intro=Element<StackPanel>(player,"IntroPanel");
   var video=Element<LibVLCSharp.WPF.VideoView>(player,"VideoView");var mp=Field<MediaPlayer>(player,"_mp");mp.Mute=true;
   check(!Field<bool>(player,"_webLayoutMounted") && double.IsNaN(surface.Height),"default player preserves the original side-by-side layout");
   var settings=new SettingsViewModel();settings.IsWebPlayerLayout=true;Pump(500);
   check(Field<bool>(player,"_webLayoutMounted") && ReferenceEquals(host.Content,comments),"General settings move the existing comment tree below the video");
   check(ReferenceEquals(video.Parent,surface) && ReferenceEquals(surface.Parent,stage),"video view keeps the same visual parent during layout changes");
   var rows=Enumerable.Range(1,35).Select(i=>new CommentItem {Rpid=i,UserName="评论用户 "+i,Content="评论位于视频下方。连续浏览时，画面进入右侧简介上方的页面内区域，播放不中断。",LikeCount=i}).ToArray();
   Element<ItemsControl>(player,"CommentList").ItemsSource=rows;
   Element<TextBlock>(player,"UpNameText").Text="示例 UP";Element<TextBlock>(player,"VideoTitleText").Text="网页布局与页面内视频停靠";
   Element<TextBlock>(player,"DescText").Text="简介与选集保留在右侧。向上浏览评论时，同一个播放器平滑进入分享、下载下方。";
   Pump(250);
   var initial=Bounds(surface,stage);var commentTop=comments.TransformToAncestor(stage).Transform(new Point()).Y;
   check(Math.Abs(initial.Height-initial.Width*9/16)<2 && commentTop>=initial.Bottom,"inline video uses its aspect ratio and comments start below it");
   check(Element<FrameworkElement>(player,"WebLeftPane").IsVisible && Element<FrameworkElement>(player,"InfoPanel").IsVisible,"both browser-style columns remain native page regions");
   var sameWindowCount=Application.Current.Windows.Count;
   Save(player,Path.Combine(artifacts,"web-layout-top.png"));
   var before=initial;var movement=new List<double>();EventHandler moving=(_,_)=>movement.Add(Field<double>(player,"_webDockProgress"));
   CompositionTarget.Rendering+=moving;
   scroll.ScrollToVerticalOffset(placeholder.ActualHeight*0.4);Pump(500);CompositionTarget.Rendering-=moving;
   check(movement.Any(p=>p>0.01 && p<0.99),"scrolling renders intermediate video positions instead of a hard relocation");
   scroll.ScrollToVerticalOffset(placeholder.ActualHeight+60);Pump(600);
   var parked=Bounds(surface,stage);var anchor=Bounds(dock,stage);var share=Bounds(Element<Button>(player,"BtnShare"),stage);
   check(parked.Left>initial.Left+initial.Width*0.8 && Math.Abs(parked.Width-anchor.Width)<2,"video arrives inside the right rail with the correct dock width");
   check(parked.Top>=share.Bottom && Bounds(intro,stage).Top>=parked.Bottom-1,"docked video is below sharing/downloading and above the introduction");
   check(Application.Current.Windows.Count==sameWindowCount,"moving the video does not create an independent floating window");
   check(ReferenceEquals(video.MediaPlayer,mp) && ReferenceEquals(host.Content,comments) && ReferenceEquals(Element<ItemsControl>(player,"CommentList").ItemsSource,rows),"player, comments and their data remain the same instances");
   check(Element<Border>(player,"WebMiniControls").IsVisible && Element<Border>(player,"ControlBar").Visibility==Visibility.Collapsed,"right-rail video uses compact controls that fit the available width");
   Save(player,Path.Combine(artifacts,"web-layout-docked.png"));
   var dockedOffset=scroll.VerticalOffset;
   Call(player,"OnWebRestoreVideo",player,new RoutedEventArgs());
   scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-30){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent,Source=scroll});Pump(400);
   Console.WriteLine($"RESTORE TAKEOVER before={dockedOffset:0.0} after={scroll.VerticalOffset:0.0}");
   check(scroll.VerticalOffset>=dockedOffset-1,"precision input immediately takes over from the return-to-top animation");
   scroll.ScrollToVerticalOffset(placeholder.ActualHeight+60);Pump(300);
   Pump(100);var restoreOffsets=new List<double>();
   ScrollChangedEventHandler restored=(_,e)=>{if(ReferenceEquals(e.OriginalSource,scroll))restoreOffsets.Add(scroll.VerticalOffset);};
   scroll.ScrollChanged+=restored;
   Call(player,"OnWebRestoreVideo",player,new RoutedEventArgs());Pump(80);
   Pump(850);scroll.ScrollChanged-=restored;
   check(restoreOffsets.Any(offset=>offset>1 && offset<placeholder.ActualHeight+59),"return-to-video control animates through intermediate scroll positions");
   check(scroll.VerticalOffset<1 && Math.Abs(Bounds(surface,stage).Left-initial.Left)<2,"returning to the top restores the inline video smoothly");
   var sample=Path.GetFullPath("artifacts/touch-sample-silent.mp4");
   if(File.Exists(sample))
   {
    player.PlayLocalFile(sample);mp.Mute=true;Pump(1200);
    Element<ItemsControl>(player,"CommentList").ItemsSource=rows;Pump(100);
    var time=mp.Time;scroll.ScrollToVerticalOffset(placeholder.ActualHeight+80);Pump(650);
    check(mp.IsPlaying && mp.Time>time && ReferenceEquals(video.MediaPlayer,mp),"native muted playback continues while the video moves into the right rail");
   }
   var savedOffset=scroll.VerticalOffset;
   Call(player,"ToggleFullscreen");Pump(500);
   check(Field<bool>(player,"_isFullscreen") && !Field<bool>(player,"_webLayoutMounted"),"fullscreen temporarily restores a full-stage video surface");
   Call(player,"ToggleFullscreen");Pump(700);
   check(!Field<bool>(player,"_isFullscreen") && Field<bool>(player,"_webLayoutMounted") && scroll.VerticalOffset>0,"fullscreen exit restores the browser layout and comment reading position");
   Svc.Settings.SetUiScalePercent(150);Pump(700);
   parked=Bounds(surface,stage);anchor=Bounds(dock,stage);
   check(Math.Abs(parked.Left-anchor.Left)<3 && Math.Abs(parked.Width-anchor.Width)<3,"docking follows actual geometry when interface scaling changes");
   check(parked.Bottom<=stage.ActualHeight+2,"docked player stays inside the viewport at enlarged UI scale");
   Save(player,Path.Combine(artifacts,"web-layout-docked-scaled.png"));
   player.Width=1920;player.Height=600;Pump(650);
   check(Bounds(surface,stage).Bottom<=stage.ActualHeight+2,"wide short windows reserve enough vertical space for the docked video");
   scroll.ScrollToTop();Pump(600);
   check(Bounds(surface,stage).Bottom<=stage.ActualHeight+2,"inline video and its controls fit vertically in a wide short window");
   scroll.ScrollToVerticalOffset(placeholder.ActualHeight+60);Pump(600);
   player.Width=840;Pump(650);
   check(Bounds(surface,stage).Bottom<=stage.ActualHeight+2 && Bounds(surface,stage).Left>=0,"minimum-size windows keep the dock inside the viewport at 150 percent scale");
   Svc.Settings.SaveNow();using(var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.DataDir,"settings.json"))))
    check(document.RootElement.GetProperty("UseWebPlayerLayout").GetBoolean(),"alternate player layout is included in the persisted settings snapshot");
   settings.IsSplitPlayerLayout=true;Pump(500);
   check(!Field<bool>(player,"_webLayoutMounted") && ReferenceEquals(comments.Parent,Element<Grid>(player,"PageHost")),"switching back returns comments to the original right-side host");
   check(double.IsNaN(surface.Width) && double.IsNaN(surface.Height) && Grid.GetColumnSpan(surface)==1,"switching back restores normal video sizing without leftover dock geometry");
   check(ReferenceEquals(Element<Button>(player,"CommentFab").Parent,Element<Grid>(player,"InfoPanel")),"comment controls return to the correct rail when changing layout");
   check(!Field<bool>(player,"_webRendering"),"layout animations detach their rendering handler once finished");
  }
  finally
  {
   player?.Close();Pump(250);Svc.Settings.SetWebPlayerLayout(old);Svc.Settings.SetUiScalePercent(oldScale);
   SynchronizationContext.SetSynchronizationContext(context);
  }
 }
 static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
 static void Save(FrameworkElement root,string path){root.UpdateLayout();var bmp=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(path);encoder.Save(file);}
}
