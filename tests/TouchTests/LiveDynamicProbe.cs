using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliDesk;
using BiliDesk.Models;
using BiliDesk.Helpers;
using BiliDesk.Services;
using BiliDesk.Views.Pages;
using BiliDesk.ViewModels;

internal static class LiveDynamicProbe
{
    public static void Run(string output,bool fullFeed=false,bool mineMode=false)
    {
        // Keep credentials in memory only. AppPaths points to the isolated test directory.
        var sessionFile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","Local","BiliDesk","session.json");
        if(!File.Exists(sessionFile)) {Console.WriteLine("LIVE unavailable: no session at the application data path");return;}
        var session=JsonSerializer.Deserialize<Session>(File.ReadAllText(sessionFile),new JsonSerializerOptions {PropertyNameCaseInsensitive=true});
        if(session==null || string.IsNullOrEmpty(session.SessData) || string.IsNullOrEmpty(session.Buvid3) || string.IsNullOrEmpty(session.Buvid4))
        {Console.WriteLine("LIVE unavailable: session/fingerprint incomplete");return;}
        typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,session);
        foreach(var pair in new[]{("_buvid3",session.Buvid3),("_buvid4",session.Buvid4)})
            typeof(ApiClient).GetField(pair.Item1,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(Svc.Api,pair.Item2);
        if(mineMode)
        {
            var mine=new MinePage {DataContext=new BiliDesk.ViewModels.MineViewModel()};
            var host=new Window {Width=1200,Height=850,Content=mine,ShowInTaskbar=false};host.Show();
            foreach(var section in new[]{PageKey.Mine,PageKey.History,PageKey.Favorites,PageKey.WatchLater,PageKey.Cache})
            {
                mine.SelectSection(section);for(int i=0;i<35;i++)Pump();
                Console.WriteLine($"MINE section {section} responsive");Console.Out.Flush();
                var shot=new RenderTargetBitmap((int)mine.ActualWidth,(int)mine.ActualHeight,96,96,PixelFormats.Pbgra32);shot.Render(mine);
                var mineEncoder=new PngBitmapEncoder();mineEncoder.Frames.Add(BitmapFrame.Create(shot));
                using(var stream=File.Create(Path.Combine(output,$"mine-{section}.png")))mineEncoder.Save(stream);
            }
            host.Close();return;
        }
        var feed=Svc.Api.GetDynamicFeedFullAsync();Wait(feed);
        if(!feed.Result.ok || feed.Result.items is not {Count:>0} items)
        {Console.WriteLine("LIVE feed failed: "+feed.Result.err);return;}
        Console.WriteLine($"LIVE feed: {items.Count} items; "+string.Join(", ",items.GroupBy(x=>x.Kind).Select(g=>$"{g.Key}={g.Count()}")));
        if(fullFeed)
        {
            var feedWindow=new Window {Width=1200,Height=850,ShowInTaskbar=false};
            var feedPage=new FollowPage();feedWindow.Content=feedPage;feedWindow.Show();Pump();
            var vm=new BiliDesk.ViewModels.FollowViewModel();
            vm.Users.Add(new FollowUser {Mid=1,Name="验证"});
            foreach(var item in items)vm.Dynamics.Add(item);
            var delays=new List<double>();var clock=System.Diagnostics.Stopwatch.StartNew();double last=0;
            var heartbeat=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(16)};
            heartbeat.Tick+=(_,_)=>{double now=clock.Elapsed.TotalMilliseconds;delays.Add(now-last);last=now;};heartbeat.Start();
            Console.WriteLine("FEED mounting");Console.Out.Flush();
            feedPage.DataContext=vm;
            for(int i=0;i<50;i++)Pump();
            Console.WriteLine("FEED responsive after mounting");Console.Out.Flush();
            var list=(System.Windows.Controls.ListBox)feedPage.FindName("DynamicList");
            foreach(var item in items) {list.ScrollIntoView(item);Pump();}
            heartbeat.Stop();
            delays.Sort();
            Console.WriteLine($"FEED scroll completed; UI heartbeat p95={delays[(int)(delays.Count*0.95)]:0.0}ms max={delays.Last():0.0}ms (16ms timer; not GPU frame time)");
            feedWindow.Close();return;
        }
        var source=items.FirstOrDefault(x=>x.HasPictures) ?? items[0];
        var page=new DynamicDetailPage(source);
        var window=new Window {Content=page,Width=1200,Height=850,ShowInTaskbar=false};window.Show();
        var limit=DateTime.UtcNow.AddSeconds(35);
        while((page.ViewModel.DetailLoading || page.ViewModel.Loading) && DateTime.UtcNow<limit)Pump();
        Console.WriteLine($"LIVE detail: {source.Kind}, pictures={source.Pictures.Count}, comments={page.ViewModel.Comments.Count}, detailError={page.ViewModel.DetailError}, commentError={page.ViewModel.Error}");
        var imageTasks=source.Pictures.Take(9).Select(p=>CoverLoader.LoadAsync(p.Url,720)).ToArray();
        var assets=Task.WhenAll(imageTasks);Wait(assets);Pump();
        Console.WriteLine($"LIVE images decoded: {assets.Result.Count(x=>x!=null)}/{imageTasks.Length}");
        var bitmap=new RenderTargetBitmap((int)page.ActualWidth,(int)page.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(page);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(Path.Combine(output,"live-native-detail.png")))encoder.Save(stream);
        var recommendations=Svc.Api.GetRecommendAsync(true);Wait(recommendations);
        if(recommendations.Result.ok && recommendations.Result.items is {Count:>0} videos)
        {
            var homeVm=new BiliDesk.ViewModels.HomeViewModel();
            foreach(var video in videos)homeVm.RecommendItems.Add(video);
            var home=new HomePage {DataContext=homeVm};window.Content=home;
            for(int i=0;i<16;i++)Pump();
            var covers=Task.WhenAll(videos.Take(8).Select(v=>CoverLoader.LoadAsync(v.Cover,480)));Wait(covers);Pump();
            var rendered=Descendants<BiliDesk.Views.Controls.VideoCard>(home).Count();
            Console.WriteLine($"LIVE home: items={videos.Count}, realized cards={rendered}, decoded covers={covers.Result.Count(x=>x!=null)}/{covers.Result.Length}");
            var homeBitmap=new RenderTargetBitmap((int)home.ActualWidth,(int)home.ActualHeight,96,96,PixelFormats.Pbgra32);homeBitmap.Render(home);
            var homeEncoder=new PngBitmapEncoder();homeEncoder.Frames.Add(BitmapFrame.Create(homeBitmap));
            using(var stream=File.Create(Path.Combine(output,"live-home.png")))homeEncoder.Save(stream);
        }
        else Console.WriteLine("LIVE home failed: "+recommendations.Result.err);
        window.Close();
        typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session,new Session());
        Console.WriteLine("LIVE read-only probe completed; no posts, likes or reposts sent");
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T t)yield return t;foreach(var nested in Descendants<T>(child))yield return nested;}}
    static void Wait(Task task) {var limit=DateTime.UtcNow.AddSeconds(25);while(!task.IsCompleted && DateTime.UtcNow<limit)Pump();if(!task.IsCompleted)throw new TimeoutException("Live read timed out");task.GetAwaiter().GetResult();}
    static void Pump(){var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
}
