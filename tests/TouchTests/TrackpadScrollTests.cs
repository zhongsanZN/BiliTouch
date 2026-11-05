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
using BiliDesk;
using BiliDesk.Helpers;
using BiliDesk.Services;
using BiliDesk.ViewModels;

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
   var dictionary=new ResourceDictionary{Source=new Uri("/BiliDesk;component/Views/Pages/Settings/SettingsTabs.xaml",UriKind.Relative)};
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
   var mouseDistances=new List<double>();
   foreach(var sensitivity in new[]{25,175})
   {
    SmoothScroll.Stop(inner);inner.ScrollToVerticalOffset(0);Pump(100);settings.TouchpadFeedSensitivity=sensitivity;
    Wheel(innerContent,-120);Pump(450);mouseDistances.Add(inner.VerticalOffset);
   }
   check(Math.Abs(mouseDistances[0]-90)<2 && Math.Abs(mouseDistances[1]-90)<2,"ordinary mouse notches keep the same distance at both sensitivity limits");
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
