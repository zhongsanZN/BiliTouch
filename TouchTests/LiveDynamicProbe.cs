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
            // 截图: 给"动态卡视觉"留证据(封面比例/类型标/统计/图标互动行)
            list.ScrollIntoView(items.FirstOrDefault(x=>x.Video!=null) ?? items[0]);for(int i=0;i<12;i++)Pump();
            var feedShot=new RenderTargetBitmap((int)feedPage.ActualWidth,(int)feedPage.ActualHeight,96,96,PixelFormats.Pbgra32);feedShot.Render(feedPage);
            var feedEnc=new PngBitmapEncoder();feedEnc.Frames.Add(BitmapFrame.Create(feedShot));
            using(var s=File.Create(Path.Combine(output,"live-feedcard.png")))feedEnc.Save(s);
            // 单卡近景: 视频卡(看封面比例/类型标/统计/图标)
            var videoItem=items.FirstOrDefault(x=>x.Video!=null);
            if(videoItem!=null){
                // 放进一个限宽的容器里再截图 —— 直接挂 Window.Content 会让卡片按窗口宽铺开,
                // 而真实列表里它的宽度由 ListBox 决定(约 760-800)。不模拟这层会截出错误的比例。
                var thicket=new Border {Width=760,Padding=new Thickness(12),Background=(System.Windows.Media.Brush)Application.Current.Resources["AppBackgroundBrush"]};
                var card=new BiliDesk.Views.Controls.DynamicFeedCard {DataContext=videoItem,HorizontalAlignment=HorizontalAlignment.Stretch};
                thicket.Child=card;feedWindow.Content=thicket;
                for(int i=0;i<12;i++)Pump();
                var cshot=new RenderTargetBitmap((int)Math.Ceiling(thicket.ActualWidth),(int)Math.Ceiling(Math.Max(thicket.ActualHeight,200)),96,96,PixelFormats.Pbgra32);cshot.Render(thicket);
                var cenc=new PngBitmapEncoder();cenc.Frames.Add(BitmapFrame.Create(cshot));
                using(var s=File.Create(Path.Combine(output,"live-videocard.png")))cenc.Save(s);
                Console.WriteLine($"CARD shot: aspect={videoItem.VideoAspect:0.000} action={videoItem.PubAction} dur=[{videoItem.DurationText}] view=[{videoItem.ViewCountText}] dm=[{videoItem.DanmakuCountText}] videoDur=[{videoItem.Video?.Duration}] videoDm={videoItem.Video?.DanmakuCount} videoView={videoItem.Video?.ViewCount}");
                // 检查角标真实状态: 是不是被布局/可见性吃掉了
                // ★ 用**几何**验证角标, 不靠看截图: 把每个角标相对卡片的位置与尺寸量出来,
                //   再判断它是否落在封面区域内、且没有被裁到负坐标。
                var panel=Descendants<BiliDesk.Views.Controls.AspectPanel>(card).FirstOrDefault();
                Console.WriteLine($"  AspectPanel: found={panel!=null} size={(panel==null?"-":panel.ActualWidth+"x"+panel.ActualHeight)}");
                void Geo(string tag, System.Windows.FrameworkElement? el)
                {
                    if(el==null){Console.WriteLine($"  [{tag}] 元素不存在");return;}
                    try
                    {
                        var box=el.TransformToAncestor(card).TransformBounds(
                            new System.Windows.Rect(0,0,el.ActualWidth,el.ActualHeight));
                        Console.WriteLine($"  [{tag}] vis={el.Visibility} size={el.ActualWidth:0}x{el.ActualHeight:0} " +
                                          $"at=({box.X:0},{box.Y:0}) insideCard={box.Right<=card.ActualWidth+1 && box.Bottom<=card.ActualHeight+1}");
                    }
                    catch(Exception ex){Console.WriteLine($"  [{tag}] 位置取不到: {ex.Message}");}
                }
                Geo("duration", Descendants<System.Windows.Controls.TextBlock>(card).FirstOrDefault(t=>t.Text==videoItem.DurationText));
                Geo("view", Descendants<System.Windows.Controls.TextBlock>(card).FirstOrDefault(t=>t.Text==videoItem.ViewCountText && t.Text.Length>0));
                Geo("action", Descendants<System.Windows.Controls.TextBlock>(card).FirstOrDefault(t=>t.Text==videoItem.PubAction && t.Text.Length>0));
            }
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
