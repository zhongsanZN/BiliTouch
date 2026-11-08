using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
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
   // ★ 2026-11-05 锁死一个静默失败: ApplyWebVideoBounds 里"变了才写"的守卫曾用 Math.Abs(a-b)>0.01,
   //   而元素宽度初值是 NaN ⇒ NaN 与任何数比较都是 false ⇒ 守卫永远认为"没变", 视频区一次都没被设置,
   //   停靠动画走完画面仍是 0x0(用户看到的就是"小窗里什么都没有")。这里直接断言尺寸是有限正数。
   check(!double.IsNaN(surface.Width) && surface.Width>1 && !double.IsNaN(surface.Height) && surface.Height>1,"mounted web layout gives the native video surface a real finite size");
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
   Console.WriteLine($"RESTORE FINAL offset={scroll.VerticalOffset:0.0} surfaceLeft={Bounds(surface,stage).Left:0.0} initialLeft={initial.Left:0.0} progress={Field<double>(player,"_webDockProgress"):0.###} animTo={Field<double>(player,"_webAnimationTo"):0.###} rendering={Field<bool>(player,"_webRendering")} placeholder={placeholder.ActualHeight:0.0}");
   check(scroll.VerticalOffset<1 && Math.Abs(Bounds(surface,stage).Left-initial.Left)<2,"returning to the top restores the inline video smoothly");
   var sample=Path.GetFullPath("artifacts/touch-sample-silent.mp4");
   if(File.Exists(sample))
   {
    player.PlayLocalFile(sample);mp.Mute=true;Pump(1200);
    Element<ItemsControl>(player,"CommentList").ItemsSource=rows;Pump(100);
    // Start inline so this exercises the dock transition, not an already parked video.
    scroll.ScrollToTop();Pump(1000);
    var danmaku=Element<Canvas>(player,"DanmakuCanvas");
    var nativeView=Element<LibVLCSharp.WPF.VideoView>(player,"VideoView");
    // ★ 2026-11-09：截图替身过渡已删除，现在统一走纯 WPF 遮罩路径。
    //   断言改为：过渡期间弹幕隐藏、原生 VideoView 全程可见（不再 Hidden），结束恢复弹幕。
    var time=mp.Time;bool sawMotion=false,hiddenDuringMotion=true,nativeStayedVisible=true;
    EventHandler observe=(_,_)=>
    {
     if(Field<bool>(player,"_webRendering"))
     {
      sawMotion=true;
      hiddenDuringMotion &= danmaku.Visibility==Visibility.Hidden;
      nativeStayedVisible &= nativeView.Visibility==Visibility.Visible;
     }
    };
    CompositionTarget.Rendering+=observe;
    // ★★★ 2026-11-08 修一条"测的是机器快慢、不是行为"的断言：
    //   这里原本只 Pump(1200) 就断言"弹幕已恢复"。而过渡要跑多久取决于机器负载 ——
    //   实测同一份代码在 CPU 54% 时要 1.4 秒、空闲时 0.9 秒。于是这条断言实际在问
    //   "你这台机器够快吗"，而不是"过渡有没有把弹幕藏起来再还回来"。
    //   ⇒ 改成等它真的结束（8 秒上限兜住死循环）：卡死会因为超时失败，慢不会。
    var settleWatch=System.Diagnostics.Stopwatch.StartNew();
    try {scroll.ScrollToVerticalOffset(placeholder.ActualHeight+80);Pump(1200);
     while(Field<bool>(player,"_webRendering") && settleWatch.ElapsedMilliseconds<8000) Pump(120);}
    finally {CompositionTarget.Rendering-=observe;}
    check(mp.IsPlaying && mp.Time>time && ReferenceEquals(video.MediaPlayer,mp),"native muted playback continues while the video moves into the right rail");
    check(sawMotion && hiddenDuringMotion && danmaku.Visibility==Visibility.Visible,"playing dock transition hides danmaku through settling and then restores it");
    check(nativeStayedVisible,"pure-WPF dock transition keeps the native VideoView visible throughout (no snapshot swap)");
    var landed=Bounds(surface,stage);Pump(250);var settled=Bounds(surface,stage);
    check(Math.Abs(settled.Top-landed.Top)<1 && Math.Abs(settled.Left-landed.Left)<1,
     "native surface lands once and does not jump after the transition");
    scroll.ScrollToTop();Settle(player);
    check(danmaku.Visibility==Visibility.Visible && !Field<bool>(player,"_webRendering") && Math.Abs(Bounds(surface,stage).Top-initial.Top)<1,
     "playing small-to-large transition restores the inline position and danmaku");
    scroll.ScrollToVerticalOffset(placeholder.ActualHeight+80);Settle(player);
   }
   var savedOffset=scroll.VerticalOffset;
   // ★★★ 2026-11-08 锁死「退出全屏后视频变黑」（用户报的第二个问题）：
   //   挂/卸网页布局会改 VideoSurface 的 HorizontalAlignment/VerticalAlignment。
   //   而 Width/Height 在原生布局下是 NaN（Stretch）。一旦变成 Left/Top + NaN，
   //   元素改按内容量尺寸，内容是 HwndHost(VideoView)、没有期望尺寸 ⇒ **量成 0×0**。
   //   0×0 会立刻给原生窗口发一次 SetWindowPos，libvlc 侧就是 `resize 0x0`
   //   ⇒ d3d11 销毁 pool 与 vout（日志 `destroying useless vout`）⇒ **永久黑屏**，
   //   之后尺寸再怎么变都不会重建。所以这一条必须在这里钉住：进出全屏的任何时刻，
   //   视频区都不能出现 0 宽或 0 高。
   var zeroSizes=new List<string>();
   SizeChangedEventHandler watchSize=(_,e)=>{ if(e.NewSize.Width<0.5 || e.NewSize.Height<0.5) zeroSizes.Add($"{e.PreviousSize.Width:0.#}x{e.PreviousSize.Height:0.#}→{e.NewSize.Width:0.#}x{e.NewSize.Height:0.#}"); };
   surface.SizeChanged+=watchSize; video.SizeChanged+=watchSize;
   try
   {
    Call(player,"ToggleFullscreen");Pump(500);
    check(Field<bool>(player,"_isFullscreen") && !Field<bool>(player,"_webLayoutMounted"),"fullscreen temporarily restores a full-stage video surface");
    Call(player,"ToggleFullscreen");Pump(700);
   }
   finally { surface.SizeChanged-=watchSize; video.SizeChanged-=watchSize; }
   check(zeroSizes.Count==0,"the native video surface never collapses to zero size while toggling fullscreen (a 0x0 resize makes libvlc destroy the vout and the picture stays black): "+string.Join(", ",zeroSizes));
   check(!Field<bool>(player,"_isFullscreen") && Field<bool>(player,"_webLayoutMounted") && scroll.VerticalOffset>0,"fullscreen exit restores the browser layout and comment reading position");
   // ★★ 2026-11-08 同一类问题（“测的是机器快慢”）：
   //   改界面缩放走 LayoutChanged → 排队 → 重新测量 → 重新摆原生窗口，是一条**异步链**。
   //   原来固定 Pump(700) 就断言几何对齐，负载高时链还没跑完（实测 CPU 54% 时视频区仍停在内联几何）。
   //   ⇒ 改成“等它对齐，最多 8 秒”（实测同一台机器上 0.5~5、不等，取决于当时负载）；
   //     永远对不齐的真 bug 仍然会被抓住。
   Svc.Settings.SetUiScalePercent(150);
   // ★ 尺寸变了之后滚动位置会被重新夹一遍，可能从“已停靠”退回内联。
   //   本条断言要验的是“**停靠状态下**视频区跟着锚点走”，所以得先把前提重新摆好，
   //   而不是靠“刚好还没退回去”的瞬态过关。
   scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
   var dockWatch=System.Diagnostics.Stopwatch.StartNew();
   do {
    Pump(120);
    parked=Bounds(surface,stage);anchor=Bounds(dock,stage);
    if(Math.Abs(parked.Left-anchor.Left)<3 && Math.Abs(parked.Width-anchor.Width)<3) break;
   } while(dockWatch.ElapsedMilliseconds<8000);
   check(Math.Abs(parked.Left-anchor.Left)<3 && Math.Abs(parked.Width-anchor.Width)<3,$"docking follows actual geometry when interface scaling changes (parked {parked.Left:0.#}x{parked.Width:0.#} vs anchor {anchor.Left:0.#}x{anchor.Width:0.#}, prog={Field<double>(player,"_webDockProgress"):0.##} target={Field<double>(player,"_webAnimationTo"):0.##} off={scroll.VerticalOffset:0.#}/{scroll.ScrollableHeight:0.#} vp={scroll.ViewportHeight:0.#} waited {dockWatch.ElapsedMilliseconds}ms rend={Field<bool>(player,"_webRendering")} sw={surface.Width} sh={surface.Height})");
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
 static void Settle(PlayerWindow player)
 {
  var deadline=Environment.TickCount64+8000;
  do {
   Pump(50);
   if(!Field<bool>(player,"_webRendering"))return;
  } while(Environment.TickCount64<deadline);
  throw new TimeoutException("Video transition did not settle");
 }
 static void Save(FrameworkElement root,string path){root.UpdateLayout();var bmp=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(path);encoder.Save(file);}
}
