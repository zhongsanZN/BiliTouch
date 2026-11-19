using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Pages;

internal static class GestureContinuationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Call(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args)!;
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    public static void Run(Action<bool, string> check)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var sourceItems = new System.Collections.ObjectModel.ObservableCollection<int>(Enumerable.Range(0, 12));
        bool suspended = true;
        var list = new ItemsControl();
        var fill = new DeferredFill(list, sourceItems, 4, () => suspended);
        fill.Start(); Pump(80);
        check(fill.View.Count == 0, "pending feed cards wait while a horizontal gesture is active");
        suspended = false; Pump(120);
        check(fill.View.Cast<int>().SequenceEqual(sourceItems), "feed resumes in order after the gesture");
        suspended = true; sourceItems.Add(12); sourceItems.Insert(0, -1); Pump(80);
        check(fill.View.Count == 12, "new feed batches do not rebuild a page during a gesture");
        suspended = false; Pump(120);
        check(fill.View.Cast<int>().SequenceEqual(sourceItems), "append and insertion during a paused fill retain ordering without duplicates");
        fill.Dispose();
        var main = new MainWindow(); main.Show();
        App.MainVm.Navigate(PageKey.Home); Pump(250);
        var home = (HomePage)Call(main, "PageOf", PageKey.Home);
        var popular = (RadioButton)home.FindName("PopularTab");
        popular.IsChecked = true; Pump(100);
        var host = (FrameworkElement)home.FindName("TabHost");
        double width = host.ActualWidth;
        home.SwipeDown(new Point(width * .9, 100), host);
        check(!home.SwipeMove(new Point(width * .9 + 2, 125)), "home vertical start remains a scroll");
        check(home.SwipeMove(new Point(width * .2, 128)), "home can acquire horizontal after a vertical start");
        home.SwipeUp(); Pump(230);
        check(((RadioButton)home.FindName("RankingTab")).IsChecked == true, "home over-half touch advances one page");
        for (int i = 0; i < 4; i++)
        {
            home.SwipeDown(new Point(300, 100), host);
            home.SwipeMove(new Point(210, 100)); home.SwipeUp(); Pump(200);
            check(((RadioButton)home.FindName("RankingTab")).IsChecked == true, "fast short touch does not skip the half-page threshold");
        }
        for (int i = 0; i < 6; i++)
        {
            home.SwipeDown(new Point(width * .9, 100), host);
            home.SwipeMove(new Point(width * .2, 100)); home.SwipeUp();
            // Begin the reverse drag while the previous snap animation is still active.
            home.SwipeDown(new Point(width * .1, 100), host);
            home.SwipeMove(new Point(width * .8, 100)); home.SwipeUp(); Pump(200);
            check(((RadioButton)home.FindName("RankingTab")).IsChecked == true, "repeated touch reversals during settle do not skip or twitch between pages");
        }
        Call(main, "OnHorizontalWheel", 120); Pump(390);
        check(((RadioButton)home.FindName("RankingTab")).IsChecked == true, "a single 120 packet is not misidentified as a mouse notch");
        Call(main, "OnHorizontalWheel", 160); Call(main, "OnHorizontalWheel", -140); Pump(390);
        check(((RadioButton)home.FindName("RankingTab")).IsChecked == true, "wheel reversal uses net displacement instead of resetting its origin");
        Call(main, "OnHorizontalWheel", -240); Pump(390);
        check(popular.IsChecked == true, "wheel after a touch release returns exactly one page");
        Call(main, "OnHorizontalWheel", 500); Call(main, "OnHorizontalWheel", -500); Pump(390);
        check(popular.IsChecked == true, "full-distance out-and-back in one stream cancels the page change");

        foreach (var key in new[] { PageKey.Settings, PageKey.Messages, PageKey.Search })
        {
            App.MainVm.Navigate(key); Pump(180);
            var page = (UserControl)Call(main, "PageOf", key);
            var g = page.GetType().GetProperty("ModuleGestures", Hidden)!.GetValue(page)!;
            var content = (FrameworkElement)page.FindName(key == PageKey.Search ? "ResultArea" : "ModuleContent");
            int Before() => (int)g.GetType().GetProperty("SelectedIndex", Hidden)!.GetValue(g)!;
            var before = Before(); var w = content.ActualWidth;
            Call(g, "Begin", new Point(w * .9, 80), content);
            check(!(bool)Call(g, "Move", new Point(w * .9, 105)), key + " permits vertical start");
            check((bool)Call(g, "Move", new Point(w * .2, 108)), key + " reconsiders the gesture axis");
            Pump(20); Call(g, "End"); Pump(350);
            check(Before() == before + 1, key + " commits one over-half page");
            Call(main, "OnHorizontalWheel", -120); Pump(390);
            check(Before() == before + 1, key + " rejects short wheel flick");
            Call(main, "OnHorizontalWheel", -240); Pump(390);
            check(Before() == before, key + " wheel works immediately after touch");
        }

        foreach (var key in new[] { PageKey.Mine, PageKey.Follow })
        {
            App.MainVm.Navigate(key); Pump(200);
            var page = (UserControl)Call(main, "PageOf", key);
            var hostName = key == PageKey.Mine ? "SectionHost" : "FollowFeedHost";
            var body = (FrameworkElement)page.FindName(hostName);
            var prefix = key == PageKey.Mine ? "Swipe" : "FeedSwipe";
            int Index() => page is MinePage mine ? mine.CurrentTabIndex : ((FollowViewModel)page.DataContext).VideoOnly ? 1 : 0;
            var w = body.ActualWidth;
            Call(page, prefix + "Down", new Point(w * .9, 100), body);
            check(!(bool)Call(page, prefix + "Move", new Point(w * .9, 125)), key + " allows vertical start");
            check((bool)Call(page, prefix + "Move", new Point(w * .2, 128)), key + " acquires horizontal later");
            Call(page, prefix + "Up"); Pump(220);
            check(Index() == 1, key + " touch crosses one page");
            Call(main, "OnHorizontalWheel", -240); Pump(390);
            check(Index() == 0, key + " wheel works after touch release");
        }

        App.MainVm.Navigate(PageKey.Settings); Pump(120);
        Call(main, "OnHorizontalWheel", 250);
        App.MainVm.Navigate(PageKey.Messages); Pump(390);
        var messages = (MessagesPage)Call(main, "PageOf", PageKey.Messages);
        var gestures = messages.GetType().GetProperty("ModuleGestures", Hidden)!.GetValue(messages)!;
        check((int)gestures.GetType().GetProperty("SelectedIndex", Hidden)!.GetValue(gestures)! == 0,
            "pending wheel completion cannot switch a different page after navigation");
        App.IsExiting = true; main.Close(); App.IsExiting = false;

        var player = new PlayerWindow(); player.Show(); Pump(150);
        var info = (FrameworkElement)player.FindName("InfoPanel");
        var pages = (FrameworkElement)player.FindName("PageHost");
        player.InfoDown(new Point(pages.ActualWidth * .9, 100), info);
        check(!player.InfoMove(new Point(pages.ActualWidth * .9, 125)), "player lets the vertical scroller run first");
        check(player.InfoMove(new Point(pages.ActualWidth * .1, 128)), "player accepts a later horizontal intention");
        player.InfoUp(); Pump(230);
        check(!player.InfoUp(), "a duplicate promoted release cannot commit again");
        player.HandleHorizontalWheelAt(-120, info.PointToScreen(new Point(20, info.ActualHeight - 30))); Pump(390);
        check(((RadioButton)player.FindName("TabIntro")).IsChecked != true, "player rejects short wheel flicks");
        player.HandleHorizontalWheelAt(-240, info.PointToScreen(new Point(20, info.ActualHeight - 30))); Pump(390);
        check(((RadioButton)player.FindName("TabIntro")).IsChecked == true, "player wheel returns one page after touch");

        var live = new WriteableBitmap(16, 9, 96, 96, PixelFormats.Bgra32, null);
        player.ApplySharedCoverArtwork(live);
        check(!live.IsFrozen, "shared cover never freezes a caller-owned writable bitmap");
        live.WritePixels(new Int32Rect(0, 0, 16, 9), new byte[16 * 9 * 4], 64, 0);
        check(!live.IsFrozen, "decoder can write another frame after the cover snapshot");
        player.Close(); Pump(200);

        var version = UpdateChecker.ParseVersion("v0.2.17-fix2")!;
        var update = new UpdateInfo { Version = version };
        check(update.SetupFileName == "BiliTouch-0.2.17-fix2-setup.exe", "fix version filename survives the update path");
        using var release = JsonDocument.Parse("""
            {"assets":[
             {"name":"BiliTouch-0.2.17-setup.exe","browser_download_url":"https://example.invalid/old","size":10},
             {"name":"BiliTouch-0.2.17-fix2-setup.exe","browser_download_url":"https://example.invalid/fix","size":20,"id":42}]}
            """);
        var asset = typeof(UpdateChecker).GetMethod("FindInstallerAsset", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { release.RootElement, version });
        check(asset != null && (string?)asset.GetType().GetProperty("Url")!.GetValue(asset) == "https://example.invalid/fix", "fix tag selects its matching installer instead of the base-version asset");
    }
}
