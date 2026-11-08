using System.Net;
using System.Net.Http;
using System.Reflection;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using LibVLCSharp.Shared;

internal static class WebControlsUnreadTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
    static void Set(object o, string name, object value) => o.GetType().GetField(name, Hidden)!.SetValue(o, value);
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    static void Until(Func<bool> ready)
    {
        var start = Environment.TickCount64;
        while (!ready()) { if (Environment.TickCount64 - start > 10000) throw new TimeoutException(); Pump(15); }
    }
    static T Wait<T>(Task<T> task) { Until(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    static void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    public static void Run(Action<bool, string> check)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var session = Svc.Session.Current;
        var httpField = typeof(ApiClient).GetField("_http", Hidden)!; var oldHttp = httpField.GetValue(Svc.Api);
        var oldWeb = Svc.Settings.UseWebPlayerLayout; var oldSort = Svc.Settings.FollowSortMode;
        var fixture = new UnreadFixture(); using var http = new HttpClient(fixture);
        typeof(SessionManager).GetProperty("Current")!.SetValue(Svc.Session, new Session { SessData = "fixture", DedeUserID = "42" });
        httpField.SetValue(Svc.Api, http);
        PlayerWindow? player = null;
        try
        {
            var state = Wait(Svc.Api.GetDynamicUnreadStateAsync());
            check(state.ok && state.authors.Count == 4 && state.authors[101] && !state.authors[202] && state.authors[303] && state.authors[404], "official portal includes explicit read states and overflow UP-list pages");
            check(fixture.UpListRequests > 0, "overflow red dots are loaded through the official uplist cursor");
            fixture.LegacyPortal = true;
            var legacy = Wait(Svc.Api.GetDynamicUnreadStateAsync());
            check(legacy.ok && legacy.authors.Count == 2 && legacy.authors[101] && !legacy.authors[202], "legacy array-shaped portal responses retain both true and false flags");
            fixture.LegacyPortal = false;
            var vm = new FollowViewModel();
            foreach (var mid in new long[] { 202, 101, 303, 404 }) vm.Users.Add(new FollowUser { Mid = mid, Name = "fixture " + mid });
            vm.EnsureFollowOrder(vm.Users); vm.RememberTimestamps(new[] { new DynamicItem { Id = "1", AuthorMid = 202, PubTs = 9999 } });
            vm.FollowSortMode = FollowSortMode.UnreadFirst;
            check(Wait(vm.RefreshServerUnreadAsync()), "view model accepts a complete successful server snapshot");
            check(vm.Users.Take(3).All(u => u.HasNew) && !vm.Users.Last().HasNew, "all red-dot rows form one leading group even when the read author has a newer timestamp");
            check(vm.Users.All(u => u.NewCount is 0 or 1), "boolean server red dots do not fabricate an unread-post count");
            fixture.Unread.Remove(101); fixture.Unread.Remove(303);
            Wait(vm.RefreshServerUnreadAsync());
            check(vm.Users.Where(u => u.Mid is 101 or 303).All(u => !u.HasNew), "reading on another device clears cached red dots on the next server refresh");
            fixture.FailPortal = true;
            check(!Wait(vm.RefreshServerUnreadAsync()) && vm.Users.Single(u => u.Mid == 404).HasNew, "a failed portal request preserves the last known red dot instead of pretending everything was read");
            fixture.FailPortal = false; fixture.FailOverflow = true;
            check(!Wait(vm.RefreshServerUnreadAsync()) && vm.Users.Single(u => u.Mid == 404).HasNew, "a partial pagination failure cannot replace the previous complete snapshot");
            fixture.FailOverflow = false;
            var visibleOrder = vm.Users.Select(u => u.Mid).ToArray();
            var selected = vm.Users.Single(u => u.Mid == 404);
            Wait(vm.SelectUpAsync(selected));
            check(fixture.ReadRequests.Contains(404) && fixture.SpaceRequests == 0, "selecting a followed author uses the official dynamic-home feed/all + host_mid path");
            check(!vm.Users.Single(u => u.Mid == 404).HasNew, "selected-author red dot clears after the server reports it read");
            check(vm.Users.Select(u => u.Mid).SequenceEqual(visibleOrder) && ReferenceEquals(vm.SelectedUp,selected) && selected.IsSelected,
                "clearing a selected author's red dot preserves its row position and selection");
            fixture.Unread.UnionWith(new long[] { 101, 303 }); Wait(vm.RefreshServerUnreadAsync());
            check(vm.Users.Select(u => u.Mid).SequenceEqual(visibleOrder),"a later poll cannot move contacts while browsing an author");
            vm.MarkAllReadCommand.Execute(null); Until(() => vm.MarkAllReadCommand.CanExecute(null));
            check(fixture.ReadRequests.Contains(101) && fixture.ReadRequests.Contains(303) && vm.Users.All(u => !u.HasNew), "mark-all reads each unread author remotely and rechecks the server state");
            check(vm.MarkAllReadText == "已标记", "mark-all reports success after server confirmation");
            check(vm.Users.Select(u => u.Mid).SequenceEqual(visibleOrder),"mark-all also leaves the displayed contact order intact");
            fixture.Unread.Add(101); fixture.FailRead = 101; Wait(vm.RefreshServerUnreadAsync());
            vm.MarkAllReadCommand.Execute(null); Until(() => vm.MarkAllReadCommand.CanExecute(null));
            check(vm.Users.Single(u => u.Mid == 101).HasNew && vm.Error.Contains("尚未同步"), "a failed read request does not falsely clear its red dot or claim synchronization");
            fixture.FailRead = 0;

            var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.HoldNextPortal = held; var stale = vm.RefreshServerUnreadAsync();
            // ★★ 2026-11-08：这里必须先确认“旧的那次请求真的已经发出且被挂住”，再发新的那次。
            //   原来靠“调用顺序”隐式假设先发的先到处理器，而出站前现在会过一道限速闸（要 await），
            //   两个并发调用到达处理器的先后不再固定 —— HoldNextPortal 会抓到错的那一个，
            //   于是 Wait(新那次) 永远等不到（实测 60 秒超时）。
            //   这不是改了断言：它要验的仍然是“新的能先完成，并且旧的晚到响应不能重新点亮红点”，
            //   只是把“旧的确实在路上”这个前提显式等出来。
            Until(() => fixture.HoldNextPortal == null);
            fixture.Unread.Clear(); check(Wait(vm.RefreshServerUnreadAsync()), "a newer refresh can finish while an older request is still in flight");
            held.SetResult(UnreadFixture.Json(new { code = 0, data = new { up_list = new[] { new { mid = 101, has_update = true } } } }));
            check(!Wait(stale) && vm.Users.All(u => !u.HasNew), "a late pre-read response cannot relight already-cleared red dots");

            fixture.Unread.Add(101);
            Wait(vm.RefreshAsync());
            check(vm.Users.First().Mid == 101 && vm.Users.Count == 4,
                "an explicit page refresh regroups unread authors without removing any contact");
            check(vm.SelectedUp?.Mid == 404 && ReferenceEquals(vm.SelectedUp,vm.Users.Single(u=>u.Mid==404)) && vm.SelectedUp.IsSelected,
                "refresh retains the viewed author and remaps selection to its current row");

            var store = new SettingsStore(); var apply = typeof(SettingsStore).GetMethod("ApplySanitized", Hidden)!;
            apply.Invoke(store, new object[] { new AppSettings { FollowSortMode = 0, FollowSortSelectionVersion = 0 } });
            check(store.FollowSortMode == FollowSortMode.UnreadFirst, "legacy recent-update defaults migrate to unread-first");
            apply.Invoke(store, new object[] { new AppSettings { FollowSortMode = 0, FollowSortSelectionVersion = 1 } });
            check(store.FollowSortMode == FollowSortMode.RecentUpdate, "an explicit current-version sorting choice remains unchanged");
            apply.Invoke(store, new object[] { new AppSettings { FollowSortMode = 2 } });
            check(store.FollowSortMode == FollowSortMode.FollowOrder, "migration preserves an explicitly selected follow order");

            Svc.Settings.SetWebPlayerLayout(true);
            player = new PlayerWindow { Width = 1280, Height = 800 }; player.Show(); Pump(350);
            var large = (Border)player.FindName("ControlBar"); var mini = (Border)player.FindName("WebMiniControls");
            var surface = (Grid)player.FindName("VideoSurface");
            var danmaku = (Canvas)player.FindName("DanmakuCanvas");
            Set(player, "_controlsHidden", false); Set(player, "_webDockProgress", 0d);
            Call(player, "StartLegacyWebDockTarget", 1d);
            check(large.Visibility == Visibility.Collapsed && mini.Visibility == Visibility.Collapsed, "legacy dock transition hides both control bars synchronously before its first frame");
            check(danmaku.Visibility == Visibility.Hidden, "legacy movement hides danmaku synchronously before its first frame");
            Call(player, "StopWebVideoAnimation");
            Set(player, "_webRendering", true); Call(player, "UpdateAlternateVideoControls");
            check(large.Visibility == Visibility.Collapsed && mini.Visibility == Visibility.Collapsed, "snapshot capture pending state already hides both bars");
            check(danmaku.Visibility == Visibility.Hidden, "snapshot capture hides danmaku before the moving frame appears");
            Set(player, "_controlsHidden", true); Call(player, "ShowControls");
            check(large.Visibility == Visibility.Collapsed && mini.Visibility == Visibility.Collapsed, "mouse/touch control reveal cannot override transition suppression");
            Set(player, "_webRendering", true);
            Set(player, "_webDockProgress", 1d); surface.Width = 400;
            Call(player, "UpdateAlternateVideoControls");
            check(large.Visibility == Visibility.Collapsed && mini.Visibility == Visibility.Collapsed, "compact controls remain hidden while the captured frame moves or settles");
            check(danmaku.Visibility == Visibility.Hidden, "danmaku remains hidden while the captured frame moves and settles");
            Set(player, "_webRendering", false); Call(player, "UpdateAlternateVideoControls");
            check(large.Visibility == Visibility.Collapsed && mini.Visibility == Visibility.Visible, "settled small video restores only its compact control bar");
            check(danmaku.Visibility == Visibility.Visible, "settled small video restores its danmaku layer");
            Set(player, "_webRendering", true); Call(player, "UpdateAlternateVideoControls");
            check(large.Visibility == Visibility.Collapsed && mini.Visibility == Visibility.Collapsed, "small-to-large transition also hides the old compact bar immediately");
            check(danmaku.Visibility == Visibility.Hidden, "small-to-large capture also hides danmaku immediately");
            Call(player, "StopWebVideoAnimation"); Set(player, "_webDockProgress", 0d); Call(player, "UpdateAlternateVideoControls");
            check(large.Visibility == Visibility.Visible && mini.Visibility == Visibility.Collapsed, "settled large video restores only its full control bar");
            check(danmaku.Visibility == Visibility.Visible, "cancelled transition restores danmaku with the large video");
            Set(player, "_audioHealCheckAt", 123L);
            Call(player, "ScheduleAudioHealRecheck");
            Set(player, "_loadGen", 77);
            Pump(1850);
            check((long)player.GetType().GetField("_audioHealCheckAt", Hidden)!.GetValue(player)! == 123L,
                "a delayed audio recovery from the previous media cannot act on a newly loaded video");
            var mp = (MediaPlayer)player.GetType().GetField("_mp", Hidden)!.GetValue(player)!;
            long pcm = 0;
            mp.SetAudioCallbacks((_, _, count, _) => Interlocked.Add(ref pcm, count), null, null, null, null);
            mp.SetAudioFormat("S16N", 48000, 2);
            var media = (Media)player.GetType().GetMethod("BuildMedia", Hidden)!.Invoke(player, new object[] {
                Path.GetFullPath("artifacts/touch-sample-silent.mp4"), new Uri(Path.GetFullPath("artifacts/hires-silent.m4a")).AbsoluteUri })!;
            Call(player, "PlayMedia", media);
            Until(() => mp.IsPlaying && mp.AudioTrack >= 0 && Interlocked.Read(ref pcm) > 0);
            var originalTrack = mp.AudioTrack;
            check(mp.SetAudioTrack(-1), "native audio fixture can deliberately disable its selected track");
            Pump(150);
            Set(player, "_audioHealCheckAt", -1L); Set(player, "_pendingSeekMs", -1L);
            var beforeRecovery = Interlocked.Read(ref pcm);
            Call(player, "HealAudioIfDead", "regression-native");
            Until(() => mp.AudioTrack >= 0 && Interlocked.Read(ref pcm) > beforeRecovery);
            check(mp.AudioTrack == originalTrack, "the production audio recovery method reselects the real track and PCM callbacks resume");
            Set(player, "_audioHealCheckAt", -1L); Call(player, "HealAudioIfDead", "regression-healthy");
            check(mp.AudioTrack == originalTrack, "the production recovery method leaves an already healthy audio track selected");
        }
        finally
        {
            player?.Close(); Pump(150); httpField.SetValue(Svc.Api, oldHttp);
            typeof(SessionManager).GetProperty("Current")!.SetValue(Svc.Session, session);
            Svc.Settings.SetWebPlayerLayout(oldWeb); Svc.Settings.SetFollowSortMode(oldSort);
        }
    }

    sealed class UnreadFixture : HttpMessageHandler
    {
        public HashSet<long> Unread = new() { 101, 303, 404 };
        public HashSet<long> ReadRequests = new();
        public bool FailPortal, FailOverflow, LegacyPortal; public long FailRead;
        public int UpListRequests, SpaceRequests;
        public TaskCompletionSource<HttpResponseMessage>? HoldNextPortal;
        public static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if(uri.AbsolutePath.EndsWith("/nav")) return Task.FromResult(Json(new {code=0,data=new{isLogin=true,mid=42,uname="fixture"}}));
            if(uri.AbsolutePath.EndsWith("/followings")) return Task.FromResult(Json(new {code=0,data=new{list=new long[]{202,101,303,404}.Select(mid=>new{mid,uname="fixture "+mid}).ToArray()}}));
            object User(long mid) => new { mid, has_update = Unread.Contains(mid) };
            if (uri.AbsolutePath.EndsWith("/portal"))
            {
                if (HoldNextPortal is { } held) { HoldNextPortal = null; return held.Task; }
                if (LegacyPortal) return Task.FromResult(Json(new { code = 0, data = new { up_list = new[] { User(101), User(202) } } }));
                return Task.FromResult(FailPortal ? Json(new { code = -500, message = "fixture failure" }) :
                    Json(new { code = 0, data = new { up_list = new { items = new[] { User(101), User(202), User(303) }, has_more = true, offset = "next" } } }));
            }
            if (uri.AbsolutePath.EndsWith("/uplist"))
            {
                UpListRequests++;
                return Task.FromResult(FailOverflow ? Json(new { code = -500 }) : Json(new { code = 0, data = new { items = new[] { User(404) }, has_more = false, offset = "" } }));
            }
            if (uri.AbsolutePath.EndsWith("/feed/space")) SpaceRequests++;
            if (uri.AbsolutePath.EndsWith("/feed/all"))
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                if (long.TryParse(query["host_mid"], out var mid))
                {
                    ReadRequests.Add(mid);
                    if (mid == FailRead) return Task.FromResult(Json(new { code = -500, message = "read failed" }));
                    Unread.Remove(mid);
                }
                return Task.FromResult(Json(new { code = 0, data = new { items = Array.Empty<object>(), has_more = false, offset = "" } }));
            }
            return Task.FromResult(Json(new { code = 0, data = new { b_3 = "fixture", b_4 = "fixture" } }));
        }
    }
}
