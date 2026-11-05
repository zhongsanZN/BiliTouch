using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views.Controls;
using BiliTouch.Views.Pages;

internal static class MineExperienceTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public static void Run(Action<bool,string> check,string artifacts)
    {
        var originalDir=AppPaths.DataDir;
        var testDir=Path.Combine(artifacts,"mine-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(testDir);
        typeof(AppPaths).GetProperty(nameof(AppPaths.DataDir))!.SetValue(null,testDir);
        check(Path.GetFileName(AppPaths.DefaultDownloadDir)=="bilitouch","default downloads use the bilitouch subfolder");
        Directory.CreateDirectory(Path.Combine(testDir,"downloads"));
        AppPaths.SetDownloadDir(Path.Combine(testDir,"downloads"));
        try
        {
            var handler=new CacheHttp();using var http=new HttpClient(handler);
            Task<(bool,string?,string?,string?,int)> Source(string b,long cid,int q)=>Task.FromResult<(bool,string?,string?,string?,int)>((true,null,"https://fixture.invalid/video",null,32));
            var service=new OfflineDownloadService(Source,http);service.Load();
            var video=new VideoDetail {Bvid="BVTEST",Title="测试合集",Owner="测试 UP",Pages=new(){new(){Cid=1,Page=1,Part="第一集"},new(){Cid=2,Page=2,Part="第二集"}}};
            service.Enqueue(video,video.Pages,64);service.Enqueue(video,video.Pages,64);
            check(service.Items.Count==2,"duplicate queue requests do not duplicate episodes");
            Until(()=>service.Items.All(e=>e.Done>24000));
            var first=service.Items[0];service.Pause(first);Until(()=>!service.IsRunning(first));
            check(first.State==OfflineState.Paused && File.Exists(first.PartPath) && !File.Exists(first.FilePath),"pause keeps resumable partial data out of completed inventory");
            Until(()=>!service.IsRunning(service.Items[1]));
            check(service.Items[1].IsComplete && service.Items[1].Quality==32,"completed cache records the server's actual downgraded quality");
            var restarted=new OfflineDownloadService(Source,http);restarted.Load();
            var resume=restarted.Items.Single(e=>e.Id==first.Id);
            check(resume.State==OfflineState.Paused && resume.Cid==first.Cid && resume.Title==video.Title,"task identity and paused state survive restart");
            restarted.Resume(resume);Until(()=>!restarted.IsRunning(resume) && resume.State!=OfflineState.Queued);
            check(resume.IsComplete && handler.Ranges.Any(r=>r>0) && File.ReadAllBytes(resume.FilePath).SequenceEqual(handler.Bytes),"range resume produces the exact complete video");
            File.Delete(resume.FilePath);Directory.CreateDirectory(Path.GetDirectoryName(resume.PartPath)!);File.WriteAllBytes(resume.PartPath,handler.Bytes[..10000]);
            resume.State=OfflineState.Paused;handler.IgnoreRange=true;restarted.Resume(resume);Until(()=>!restarted.IsRunning(resume) && resume.State!=OfflineState.Queued);
            check(resume.IsComplete && File.ReadAllBytes(resume.FilePath).SequenceEqual(handler.Bytes),"server ignoring Range restarts cleanly instead of corrupting the file");
            File.Delete(resume.FilePath);resume.State=OfflineState.Failed;handler.Fail=true;restarted.Resume(resume);Until(()=>!restarted.IsRunning(resume) && resume.State!=OfflineState.Queued);
            check(resume.State==OfflineState.Failed && resume.Error.Length>0,"HTTP failure remains a visible retryable task");
            handler.Fail=false;restarted.Resume(resume);Until(()=>!restarted.IsRunning(resume) && resume.State!=OfflineState.Queued);
            check(resume.IsComplete,"retry completes the same task");

            var cacheVm=new CacheViewModel(restarted);cacheVm.LoadAsync().GetAwaiter().GetResult();
            check(cacheVm.VisibleItems.Cast<object>().Count()==2,"completed cache tab contains completed episodes");
            cacheVm.IsDownloadingTab=true;check(!cacheVm.VisibleItems.Cast<object>().Any(),"active tab excludes completed episodes");
            cacheVm.IsCompletedTab=true;
            var cachePage=new CachePage {DataContext=cacheVm};
            var host=new Window {Width=1100,Height=760,Content=cachePage};host.Show();Pump(120);
            Save(host,Path.Combine(artifacts,"mine-cache-completed.png"));
            host.Width=680;Pump(100);Save(host,Path.Combine(artifacts,"mine-cache-compact.png"));host.Width=1100;Pump(100);
            Application.Current.Resources.MergedDictionaries[0]=new ResourceDictionary {Source=new Uri("/BiliTouch;component/Themes/Colors.Dark.xaml",UriKind.Relative)};Pump(80);
            Save(host,Path.Combine(artifacts,"mine-cache-dark.png"));
            Application.Current.Resources.MergedDictionaries[0]=new ResourceDictionary {Source=new Uri("/BiliTouch;component/Themes/Colors.Light.xaml",UriKind.Relative)};Pump(80);
            check(Descendants<TextBlock>(cachePage).Any(t=>t.Text=="第一集" || t.Text.Contains("第一集")),"cache renders episode identity instead of raw filenames");
            cacheVm.BeginEdit(resume);check(cacheVm.IsEditMode && resume.IsSelected,"holding an offline item enters selection");
            cacheVm.ExitEditCommand.Execute(null);
            var unaffected=Path.Combine(testDir,"downloads","unrelated.mp4");File.WriteAllText(unaffected,"unrelated");
            var deleting=restarted.DeleteAsync(resume);Until(()=>deleting.IsCompleted);deleting.GetAwaiter().GetResult();
            check(!File.Exists(resume.FilePath) && File.Exists(unaffected) && restarted.Items.Count==1,"deleting a task removes only its owned files");
            host.Close();
            var original=Path.Combine(testDir,"legacy.mp4");File.WriteAllBytes(original,handler.Bytes);
            restarted.Import(new[]{original});var imported=restarted.Items.First(e=>e.ImportSource==original);Until(()=>!restarted.IsRunning(imported) && imported.State!=OfflineState.Queued);
            check(imported.IsComplete && File.ReadAllBytes(imported.FilePath).SequenceEqual(handler.Bytes),"legacy video import creates a managed cache copy");
            var removeImport=restarted.DeleteAsync(imported);Until(()=>removeImport.IsCompleted);removeImport.GetAwaiter().GetResult();
            check(File.Exists(original),"removing an imported cache preserves the original video");
            var index=Path.Combine(testDir,"offline-downloads.json");File.WriteAllText(index,"broken-record");
            try{new OfflineDownloadService(Source,http).Load();check(false,"corrupt manifest must fail");}catch(IOException){check(File.ReadAllText(index)=="broken-record","corrupt cache inventory is preserved rather than overwritten");}

            bool preferred=false,cancelVisible=false;
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>
            {
                var dialog=Application.Current.Windows.OfType<BiliTouch.Views.FluentWindow>().Single(w=>w.Title=="缓存视频");
                preferred=Descendants<CheckBox>(dialog).Any(c=>c.Tag is VideoPage {Cid:2} && c.IsChecked==true);
                var cancel=Descendants<Button>(dialog).Single(b=>b.Content?.ToString()=="取消");cancelVisible=cancel.IsCancel && cancel.MinHeight>=44;
                Save(dialog,Path.Combine(artifacts,"mine-cache-picker.png"));cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            var choice=VideoDownloader.Select(video,video.Pages,null,2);
            check(preferred,"cache picker defaults to the currently playing episode");
            check(choice==null && cancelVisible,"cache picker has an accessible cancel action without enqueuing tasks");
            TestGestures(check,artifacts);
            TestMineWheel(check);
            TestProfile(check,artifacts);
        }
        finally{typeof(AppPaths).GetProperty(nameof(AppPaths.DataDir))!.SetValue(null,originalDir);AppPaths.SetDownloadDir(null);}
    }
    internal static void TestGestures(Action<bool,string> check,string artifacts)
    {
        var item=new VideoItem {Bvid="BVHOLD",Title="长按测试"};
        var card=new VideoCard {DataContext=item};
        var stack=new StackPanel();stack.Children.Add(card);stack.Children.Add(new Border {Height=1200});
        var scroll=new ScrollViewer {Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        var grid=new Grid();grid.Children.Add(scroll);var page=new UserControl {Content=grid};int refresh=0,edit=0;
        ModuleGestures.Install(page,()=>{refresh++;return Task.CompletedTask;},o=>{edit++;((VideoItem)o).IsSelected=true;card.Selectable=true;});
        var window=new Window {Width=620,Height=500,Content=page};window.Show();Pump(120);
        var finger=new Finger(card);finger.Down(80,60);Pump(620);finger.Up(80,60);Pump(50);
        check(edit==1 && item.IsSelected,"finger hold selects a card once without release toggling it off");
        finger.Down(80,60);finger.Move(80,95);Pump(600);finger.Up(80,95);
        check(edit==1 && refresh==0,$"drag cancels hold and a short pull never refreshes (edit={edit}, refresh={refresh})");
        scroll.ScrollToTop();Pump(80);finger.Down(80,60);finger.Move(80,155);finger.Up(80,155);Pump(60);
        check(refresh==1,"pulling from the top refreshes once on release");
        scroll.ScrollToVerticalOffset(300);Pump(100);finger.Down(80,60);finger.Move(80,165);finger.Up(80,165);Pump(60);
        check(refresh==1,"mid-list vertical drag does not refresh");
        scroll.ScrollToTop();Pump(100);
        card.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120){RoutedEvent=UIElement.PreviewMouseWheelEvent,Source=card});
        card.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120){RoutedEvent=UIElement.PreviewMouseWheelEvent,Source=card});Pump(260);
        check(refresh==2,"trackpad and mouse wheel overscroll refresh once after the stream ends");
        card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent,Source=card});Pump(600);
        card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseUpEvent,Source=card});
        check(edit==2,"mouse left-button hold also enters card editing");
        window.Close();
        foreach(var type in new[]{typeof(HistoryPage),typeof(FavoritesPage),typeof(WatchLaterPage)})
        {
            var module=(UserControl)Activator.CreateInstance(type)!;
            var buttons=Descendants<Button>(module).ToList();
            check(!buttons.Any(b=>b.ToolTip?.ToString() is "刷新" or "返回「我的」" or "批量移出收藏" or "批量移出"),type.Name+" has no permanent refresh/edit/back toolbar");
        }
    }
    private static void TestMineWheel(Action<bool,string> check)
    {
        var main=new MainWindow();main.Show();App.MainVm.Navigate(PageKey.Mine);Pump(400);
        var mine=(MinePage)typeof(MainWindow).GetMethod("PageOf",Private)!.Invoke(main,new object[]{PageKey.Mine})!;
        mine.SectionFactory=key=>new TextBox {Text=key.ToString()};
        var wheel=typeof(MainWindow).GetMethod("OnHorizontalWheel",Private)!;
        wheel.Invoke(main,new object[]{120});Pump(230);
        check(mine.CurrentTabIndex==1,"native horizontal wheel reaches Mine instead of returning at the home-only guard");
        wheel.Invoke(main,new object[]{-120});Pump(230);check(mine.CurrentTabIndex==0,"horizontal wheel can return to personal home");
        mine.HandleHorizontalWheel(1);mine.HandleHorizontalWheel(1);Pump(450);
        check(mine.CurrentTabIndex==2,"a second horizontal wheel input survives the tab animation");
        App.IsExiting=true;main.Close();App.IsExiting=false;
    }
    private static void TestProfile(Action<bool,string> check,string artifacts)
    {
        var page=new MineOverviewPage();var host=new Window {Width=1100,Height=760,Content=page};host.Show();Pump(120);
        var vm=page.ViewModel;
        foreach(var pair in new (string,object)[]{("IsLoggedIn",true),("Name","中三"),("Sign","记录创作与生活"),("Mid",123456L)})typeof(PersonalHomeViewModel).GetProperty(pair.Item1)!.SetValue(vm,pair.Item2);
        vm.Videos.Add(new VideoItem {Title="我自己的作品",Bvid="BVOWN",Author="中三"});Pump(100);
        var labels=Descendants<TextBlock>(page).Where(t=>t.IsVisible).Select(t=>t.Text).ToList();
        check(!labels.Any(t=>t.Contains("还没有投稿")),"profile empty hint disappears when creations exist");
        check(labels.Contains("我的投稿") && labels.Contains("我自己的作品") && !labels.Any(t=>t=="观看记录" || t=="我的收藏夹"),"personal home shows profile and owned creations without duplicate modules");
        Save(host,Path.Combine(artifacts,"mine-personal-home.png"));
        host.Width=680;Pump(100);Save(host,Path.Combine(artifacts,"mine-personal-home-compact.png"));host.Close();
    }
    private static void Until(Func<bool> test){var start=Environment.TickCount64;while(!test()){if(Environment.TickCount64-start>12000)throw new TimeoutException("test state did not settle");Pump(20);}}
    private static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var c=VisualTreeHelper.GetChild(root,i);if(c is T t)yield return t;foreach(var d in Descendants<T>(c))yield return d;}}
    private static void Save(FrameworkElement root,string path){root.UpdateLayout();var bmp=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var f=File.Create(path);png.Save(f);}
    private sealed class CacheHttp:HttpMessageHandler
    {
        public readonly byte[] Bytes=Enumerable.Range(0,262144).Select(i=>(byte)(i%251)).ToArray();
        public readonly List<long> Ranges=new();public bool IgnoreRange,Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(Fail)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var offset=request.Headers.Range?.Ranges.First().From??0;Ranges.Add(offset);if(IgnoreRange)offset=0;
            var response=new HttpResponseMessage(offset>0?HttpStatusCode.PartialContent:HttpStatusCode.OK){Content=new StreamContent(new SlowStream(Bytes[(int)offset..]))};
            response.Content.Headers.ContentLength=Bytes.Length-offset;if(offset>0)response.Content.Headers.ContentRange=new ContentRangeHeaderValue(offset,Bytes.Length-1,Bytes.Length);
            return Task.FromResult(response);
        }
    }
    private sealed class SlowStream(byte[] bytes):MemoryStream(bytes)
    {public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){await Task.Delay(12,token);return await base.ReadAsync(buffer[..Math.Min(8192,buffer.Length)],token);}}
    private sealed class Finger:TouchDevice
    {
        private static int _id=5000;private readonly FrameworkElement _target;private readonly Visual _root;private Point _point;private TouchAction _action;
        public Finger(FrameworkElement target):base(++_id){_target=target;var source=PresentationSource.FromVisual(target)!;_root=source.RootVisual;SetActiveSource(source);}
        public override TouchPoint GetTouchPoint(IInputElement? relativeTo){var p=relativeTo is Visual v?_root.TransformToDescendant(v).Transform(_point):_point;return new(this,p,new Rect(p,new Size(4,4)),_action);}
        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo)=>new(){GetTouchPoint(relativeTo)};
        private void At(double x,double y,TouchAction action){_point=_target.TranslatePoint(new Point(x,y),(UIElement)_root);_action=action;}
        public void Down(double x,double y){At(x,y,TouchAction.Down);Activate();ReportDown();}
        public void Move(double x,double y){At(x,y,TouchAction.Move);ReportMove();Pump(25);}
        public void Up(double x,double y){At(x,y,TouchAction.Up);ReportUp();Deactivate();Pump(25);}
    }
}
