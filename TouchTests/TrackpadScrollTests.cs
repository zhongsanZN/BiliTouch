using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.ViewModels;

internal static class TrackpadScrollTests
{
 static long Counter(string name)=>(long)typeof(TrackpadScroll).GetProperty(name,BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
 static MouseWheelEventArgs Wheel(UIElement element,int delta)
 {var e=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,delta){RoutedEvent=Mouse.PreviewMouseWheelEvent,Source=element};element.RaiseEvent(e);return e;}
 public static void Run(Action<bool,string> check,string artifacts)
 {
  var old=Svc.Settings.TouchpadFeedSensitivity;Window? window=null;
  try
  {
   var settings=new SettingsViewModel();settings.TouchpadFeedSensitivity=0;
   check(settings.TouchpadFeedSensitivity==25,"sensitivity rejects values below 25 percent");
   settings.TouchpadFeedSensitivity=999;
   check(settings.TouchpadFeedSensitivity==175,"sensitivity rejects values above 175 percent");
   settings.TouchpadFeedSensitivity=75;
   Svc.Settings.SaveNow();
   using(var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.DataDir,"settings.json"))))
    check(json.RootElement.GetProperty("TouchpadFeedSensitivity").GetInt32()==75,"General sensitivity is included in the persisted settings snapshot");
   bool lastValueSaved=true;
   for(int i=0;i<30;i++)
   {
    settings.TouchpadFeedSensitivity=25;settings.TouchpadFeedSensitivity=175;settings.TouchpadFeedSensitivity=75;Svc.Settings.SaveNow();
    using var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.DataDir,"settings.json")));
    lastValueSaved &= saved.RootElement.GetProperty("TouchpadFeedSensitivity").GetInt32()==75;
   }
   check(lastValueSaved,"rapid slider changes cannot let an older queued save overwrite the final value");
   var dictionary=new ResourceDictionary{Source=new Uri("/BiliTouch;component/Views/Pages/Settings/SettingsTabs.xaml",UriKind.Relative)};
   var general=(FrameworkElement)((DataTemplate)dictionary["GeneralTabTemplate"]).LoadContent();general.DataContext=settings;
   window=new Window{Width=1000,Height=760,Content=new ScrollViewer{Content=general}};window.Show();Pump(150);
   var slider=Descendants<Slider>(general).Single(s=>BindingOperations.GetBinding(s,Slider.ValueProperty)?.Path.Path=="TouchpadFeedSensitivity");
   slider.Value=105;Pump(60);check(settings.TouchpadFeedSensitivity==105,"the visible General slider changes the stored feed sensitivity");
   settings.TouchpadFeedSensitivity=75;Pump(60);check(Math.Abs(slider.Value-75)<0.1,"General slider and percentage description update from the setting");
   slider.BringIntoView();Pump(100);
   var settingsScroll=(ScrollViewer)window.Content;settingsScroll.ScrollToVerticalOffset(settingsScroll.VerticalOffset+260);Pump(100);
   Save(window,Path.Combine(artifacts,"general-feed-settings.png"));
   var innerContent=new Border{Height=12000,Background=System.Windows.Media.Brushes.Transparent};
   var inner=new ScrollViewer{Height=260,Content=innerContent,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
   var panel=new StackPanel();panel.Children.Add(inner);panel.Children.Add(new Border{Height=3000});
   var outer=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
   TrackpadScroll.SetIsFeed(outer,true);SmoothScroll.SetEnabled(outer,true);SmoothScroll.SetEnabled(inner,true);
   window.Width=600;window.Height=550;window.Content=outer;Pump(200);
   check(TrackpadScroll.GetIsFeed(inner),"feed sensitivity flows through nested native scrolling regions");
   var distances=new List<double>();
   foreach(var sensitivity in new[]{25,75,175})
   {
    TrackpadScroll.Cancel(inner);inner.ScrollToVerticalOffset(0);Pump(50);settings.TouchpadFeedSensitivity=sensitivity;
    var before=Counter("OffsetWrites");var input=Counter("InputPackets");
    for(var i=0;i<60;i++)Wheel(innerContent,-5);
    check(inner.VerticalOffset<0.5 && Counter("OffsetWrites")==before,"input packets wait for a render frame at "+sensitivity+" percent");
    Pump(500);distances.Add(inner.VerticalOffset);
    check(Math.Abs(inner.VerticalOffset-300*0.6*sensitivity/100.0)<2,"all high-frequency packet distance is retained at "+sensitivity+" percent");
    check(Counter("InputPackets")-input==60 && Counter("OffsetWrites")-before<60,"sixty packets are coalesced into fewer viewport writes at "+sensitivity+" percent");
   }
   check(Math.Abs(distances[2]/distances[0]-7)<0.1,"low and high sensitivity change distance proportionally");
   check(outer.VerticalOffset==0,"nested feed input scrolls the inner viewport without stealing its gesture");
   TrackpadScroll.Cancel(inner);inner.ScrollToVerticalOffset(0);Pump(60);
   TrackpadScroll.SetIsFeed(outer,false);Wheel(innerContent,-30);Pump(350);
   check(Math.Abs(inner.VerticalOffset-18)<2,"non-feed precision scrolling keeps its existing scale");
   TrackpadScroll.SetIsFeed(outer,true);
   // ---- 整格滚轮(±120)必须也走同一套累积平滑, 且距离吃灵敏度 ----
   // ★ 这条断言在 2026-11-05 改过语义: 旧版是"普通鼠标整格距离在灵敏度两端都恒为 90px"
   //   (那时信息流的整格根本没被接管, 直接落到 WPF 瞬移, 所以量到的其实是不变的 90)。
   //   用户明确要求"鼠标滚轮最差、也要平滑", 于是整格也进了 TrackpadScroll, 并和精密输入
   //   共用同一把灵敏度尺子(90 × 灵敏度%) —— 25% 时 22.5px/格, 175% 时 157.5px/格。
   //   若这条还按旧语义断言"两端都 90", 它会把这次修复判成回归。
   var notchDistances=new List<double>();
   foreach(var sensitivity in new[]{25,75,175})
   {
    SmoothScroll.Stop(inner);inner.ScrollToVerticalOffset(0);Pump(100);settings.TouchpadFeedSensitivity=sensitivity;
    Wheel(innerContent,-120);Pump(1000);
    notchDistances.Add(inner.VerticalOffset);
   }
   check(Math.Abs(notchDistances[1]-90*0.75)<3 && Math.Abs(notchDistances[0]-90*0.25)<3
         && Math.Abs(notchDistances[2]-90*1.75)<3,
     "whole-notch wheel distance follows feed sensitivity (90px per notch at 100 percent)");
   check(notchDistances[2]>notchDistances[1]*2,"low and high sensitivity scale whole notches proportionally");
   // 一格必须"滑过去"而不是"跳过去":
   //   ① 滚轮事件当场**不动** offset(不是瞬移, 只是记了个目标);
   //   ② 600ms 之内由**多帧**补齐到整格距离(写入次数 > 2 说明真的分了帧)。
   // ★ 为什么这样断言而不是"第一帧只走了几像素": 第一帧的 dt 取决于机器负载(掉帧时 dt 可以
   //   到 0.1s 的钳位上限), 按它做阈值必然在慢机器上假失败; 而"事件当场不动 + 多帧补完"是
   //   平滑的必要条件, 且与时序无关。
   SmoothScroll.Stop(inner);inner.ScrollToVerticalOffset(0);Pump(80);settings.TouchpadFeedSensitivity=75;
   var writesBeforeNotch=Counter("OffsetWrites");
   Wheel(innerContent,-120);
   check(inner.VerticalOffset<0.5,"a whole notch does not teleport the viewport inside the event handler");
   Pump(900);
   var notchWrites=Counter("OffsetWrites")-writesBeforeNotch;
   check(Math.Abs(inner.VerticalOffset-67.5)<3,"the whole notch lands exactly one notch step after easing");
   // ★ 上界给到 150 而不是几十: tau=0.12 的指数逼近要 ~40 帧才把剩余量压到 0.5px 以下,
   //   而 Pump 的帧率依机器而定; 这条断言要证的是"分了帧"(>2), 不是"恰好几帧"。
   check(notchWrites>2 && notchWrites<150,"the whole notch is committed across several frames, not one jump");
   // 连滚累积: 三格 = 三倍单格距离, 且中途不跳回(每帧位置单调不减)。
   SmoothScroll.Stop(inner);inner.ScrollToVerticalOffset(0);Pump(80);
   var monotonic=true;var lastOffset=0.0;
   for(var i=0;i<3;i++){Wheel(innerContent,-120);Pump(40);if(inner.VerticalOffset<lastOffset-0.01)monotonic=false;lastOffset=inner.VerticalOffset;}
   Pump(1000);
   check(monotonic,"rapid whole notches continue from the live offset and never jump back");
   check(Math.Abs(inner.VerticalOffset-3*67.5)<5,"three whole notches accumulate to three notch steps at 75 percent");
   // 按下立刻取消: 整格滚动中途按下鼠标, 不能再自己往前飘。
   SmoothScroll.Stop(inner);inner.ScrollToVerticalOffset(0);Pump(80);
   Wheel(innerContent,-120);Pump(16);
   inner.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
     {RoutedEvent=UIElement.PreviewMouseDownEvent,Source=inner});
   var atCancel=inner.VerticalOffset;Pump(400);
   check(Math.Abs(inner.VerticalOffset-atCancel)<1.5,"mouse down cancels a running whole-notch scroll without drift");
   // 非信息流容器: 类处理器不会接管整格(不设 Handled), WPF 保留原语义 —— 绝不因为这次改动变慢变飘。
   var plain=new ScrollViewer{Height=200,Content=new Border{Height=9000},VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
   var plainPanel=new StackPanel();plainPanel.Children.Add(plain);window.Content=new ScrollViewer{Content=plainPanel};Pump(200);
   TrackpadScroll.SetIsFeed(plain,false);
   var notchEvent=Wheel((UIElement)plain.Content,-120);
   check(!notchEvent.Handled,"whole notches outside the feed are left to WPF instead of being smoothed globally");
   window.Content=outer;Pump(200);
   TrackpadScroll.SetIsFeed(outer,true);
   SmoothScroll.Stop(inner);inner.ScrollToVerticalOffset(0);Pump(80);
   Wheel(innerContent,-60);SmoothScroll.Stop(inner);Pump(250);
   check(inner.VerticalOffset<1,"touch takeover cancels pending precision scroll without a delayed jump");
   var list=new ListBox{Height=260,ItemsSource=Enumerable.Range(0,4000).Select(i=>"信息流条目 "+i)};
   VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);VirtualizingPanel.SetScrollUnit(list,ScrollUnit.Pixel);
   TrackpadScroll.SetIsFeed(list,true);window.Content=list;Pump(180);
   var scroller=Find<ScrollViewer>(list)!;settings.TouchpadFeedSensitivity=175;
   var watch=Stopwatch.StartNew();var writes=Counter("OffsetWrites");var packets=Counter("InputPackets");
   for(var frame=0;frame<20;frame++){for(var packet=0;packet<12;packet++)Wheel(scroller,-5);Pump(16);}
   Pump(300);
   var realized=Enumerable.Range(0,list.Items.Count).Count(i=>list.ItemContainerGenerator.ContainerFromIndex(i)!=null);
   check(scroller.VerticalOffset>1000 && realized<100,"high-sensitivity long feed keeps item recycling while advancing through content");
   Console.WriteLine($"TRACKPAD sample packets={Counter("InputPackets")-packets} writes={Counter("OffsetWrites")-writes} realized={realized} elapsed={watch.ElapsedMilliseconds}ms");
   check(Counter("OffsetWrites")-writes<Counter("InputPackets")-packets,"streamed high-sensitivity input avoids a layout write for every wheel packet");
   window.Close();Pump(50);check(!(bool)typeof(TrackpadScroll).GetField("_rendering",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!,"unloaded and completed feeds release their frame callback");
  }
  finally{window?.Close();Svc.Settings.SetTouchpadFeedSensitivity(old);}
 }
 static T? Find<T>(DependencyObject root) where T:DependencyObject
 {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)return match;if(Find<T>(child) is{} found)return found;}return null;}
 static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
 static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
 {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var item in Descendants<T>(child))yield return item;}}
 static void Save(FrameworkElement root,string path){root.UpdateLayout();var bmp=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(path);png.Save(file);}
}
