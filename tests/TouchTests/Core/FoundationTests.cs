using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;

internal static class FoundationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond = _ => Task.FromResult(Json(new { code = -1 }));
    private static TaskCompletionSource<HttpResponseMessage> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static Task<HttpResponseMessage> Reply(object value) => Task.FromResult(Json(value));
    private static Dictionary<string, string> Query(HttpRequestMessage request) => Decode(request.RequestUri!.Query.TrimStart('?'));
    private static Dictionary<string, string> Decode(string text) => text.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]),
            p => Uri.UnescapeDataString((p.Length > 1 ? p[1] : "").Replace('+', ' ')));
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static Task Call(object target, string method) => (Task)target.GetType().GetMethod(method, Hidden)!.Invoke(target, null)!;
    private static void Login(string id) => Svc.Session.UpdateFromCookies(new Dictionary<string, string>
    { ["SESSDATA"] = "fixture-" + id, ["DedeUserID"] = id, ["bili_jct"] = "fixture-csrf" });
    private static object Media(long id) => new { id, aid = id, bvid = "BV" + id.ToString("D10"), title = "fixture " + id };
    private static object Folder(params long[] ids) => new { code = 0, data = new { has_more = false, medias = ids.Select(Media).ToArray() } };
    private static async Task Until(Func<bool> condition)
    {
        var end = Environment.TickCount64 + 8000;
        while (!condition() && Environment.TickCount64 < end) await Task.Delay(5);
        if (!condition()) throw new TimeoutException("Foundation fixture did not reach the expected request");
    }

    internal static void Run(Action<bool, string> check)
    {
        var originalDir = AppPaths.DataDir;
        var originalRoot = AppPaths.CacheRoot;
        var originalSession = Svc.Session.Current;
        var httpField = typeof(ApiClient).GetField("_http", Hidden)!;
        var originalHttp = httpField.GetValue(Svc.Api);
        var context = SynchronizationContext.Current;
        var profile = Path.Combine(originalDir, "foundation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        typeof(AppPaths).GetProperty(nameof(AppPaths.DataDir))!.SetValue(null, profile);
        typeof(AppPaths).GetProperty(nameof(AppPaths.CacheRoot))!.SetValue(null, profile);
        using var http = new HttpClient(new Handler()) { Timeout = TimeSpan.FromSeconds(8) };
        httpField.SetValue(Svc.Api, http);
        Set(Svc.Api, "_buvid3", "fixture-b3"); Set(Svc.Api, "_buvid4", "fixture-b4");
        Set(Svc.Api, "_writeReady", true);
        Set(Svc.Api, "_wbiKeys", (new string('a', 32), new string('b', 32)));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            Svc.Session.Load(); Login("100");
            var work = RunAsync(check);
            var deadline = Environment.TickCount64 + 45000;
            while (!work.IsCompleted && Environment.TickCount64 < deadline)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
            }
            if (!work.IsCompleted) throw new TimeoutException("Foundation suite timed out");
            work.GetAwaiter().GetResult();
        }
        finally
        {
            httpField.SetValue(Svc.Api, originalHttp);
            typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session, originalSession);
            typeof(AppPaths).GetProperty(nameof(AppPaths.DataDir))!.SetValue(null, originalDir);
            typeof(AppPaths).GetProperty(nameof(AppPaths.CacheRoot))!.SetValue(null, originalRoot);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static async Task RunAsync(Action<bool, string> check)
    {
        var lifetime = Svc.Session.CaptureAccount();
        Svc.Session.Clear(); Login("100");
        check(!lifetime.IsCurrent, "logout and re-login as the same user invalidate earlier work");
        lifetime = Svc.Session.CaptureAccount();
        Svc.Session.BackfillRiskCookies(new Dictionary<string, string> { ["b_nut"] = "fixture" });
        check(lifetime.IsCurrent, "device-cookie backfill does not change the account lifetime");
        var notified = false; var onUiThread = false;
        EventHandler<EventArgs> accountChanged = (_, _) =>
        { notified = true; onUiThread = System.Windows.Application.Current.Dispatcher.CheckAccess(); };
        Svc.Session.AccountChanged += accountChanged;
        await Task.Run(Svc.Session.Clear); await Until(() => notified);
        Svc.Session.AccountChanged -= accountChanged;
        check(onUiThread, "background account changes notify page state on the UI thread");
        Login("100");

        foreach (var quality in Enum.GetValues<AudioQuality>())
        {
            var store = new SettingsStore { PreferredAudioQuality = quality };
            store.SaveNow(); var loaded = new SettingsStore(); loaded.Load();
            check(loaded.PreferredAudioQuality == quality, "audio preference survives save and reload: " + quality);
        }
        new SettingsStore { PreferredAudioQuality = (AudioQuality)999 }.SaveNow();
        var sanitized = new SettingsStore(); sanitized.Load();
        check(sanitized.PreferredAudioQuality == AudioQuality.Auto, "unknown audio preferences fall back to Auto");

        var signedRequests = 0; var validSignatures = true;
        _respond = async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/nav"))
                return Json(new { code = 0, data = new { wbi_img = new
                { img_url = "https://fixture/" + new string('c', 32) + ".png", sub_url = "https://fixture/" + new string('d', 32) + ".png" } } });
            var query = Query(request);
            var mixin = await (Task<string?>)typeof(ApiClient).GetMethod("GetMixinKeyAsync", Hidden)!.Invoke(Svc.Api, null)!;
            var encode = typeof(ApiClient).GetMethod("Enc", BindingFlags.Static | BindingFlags.NonPublic)!;
            var raw = string.Join("&", query.Where(x => x.Key != "w_rid").OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Key + "=" + (string)encode.Invoke(null, new object[] { x.Value })!));
            validSignatures &= query["w_rid"] == Hashing.Md5Hex(raw + mixin);
            return ++signedRequests == 1 ? Json(new { code = -403 }) : Json(Folder(1));
        };
        var signed = await Svc.Api.GetFavResourcesAsync(1);
        check(signed.ok && signedRequests == 2 && validSignatures, "WBI key refresh retries with a valid fresh signature");

        var pendingFolders = new Dictionary<string, TaskCompletionSource<HttpResponseMessage>>();
        _respond = request => { var gate = Gate(); pendingFolders[Query(request)["media_id"]] = gate; return gate.Task; };
        var favorites = new FavoritesViewModel(_ => true);
        var a = new FavFolder { Id = 1, Title = "A" }; var b = new FavFolder { Id = 2, Title = "B" };
        favorites.Folders.Add(a); favorites.Folders.Add(b);
        var loadA = favorites.SelectFolderAsync(a); var loadB = favorites.SelectFolderAsync(b);
        await Until(() => pendingFolders.Count == 2);
        pendingFolders["2"].SetResult(Json(Folder(2))); await loadB;
        pendingFolders["1"].SetResult(Json(Folder(1))); await loadA;
        check(favorites.CurrentFolderTitle == "B" && favorites.Items.Single().Aid == 2,
            "late responses from A cannot contaminate folder B");
        check(!favorites.LoadingMore, "stale folder responses do not retain a loading state");
        pendingFolders.Clear();
        loadA = favorites.SelectFolderAsync(a); loadB = favorites.SelectFolderAsync(b);
        await Until(() => pendingFolders.Count == 2);
        pendingFolders["1"].SetResult(Json(Folder(1))); await loadA;
        check(favorites.LoadingMore, "an old response cannot clear the new folder loading indicator");
        pendingFolders["2"].SetResult(Json(Folder(2))); await loadB;
        check(favorites.Items.Single().Aid == 2, "the current folder completes after an old request is discarded");

        var secondAttempt = 0; var folderPages = new List<int>();
        _respond = request =>
        {
            var page = int.Parse(Query(request)["pn"]); folderPages.Add(page);
            if (page == 2 && secondAttempt++ == 0) return Reply(new { code = -1, message = "fixture failure" });
            return Reply(new { code = 0, data = new { has_more = page == 1, medias = new[] { Media(page) } } });
        };
        await favorites.SelectFolderAsync(a); await favorites.LoadMoreAsync(); await favorites.LoadMoreAsync();
        check(folderPages.SequenceEqual(new[] { 1, 2, 2 }), "favorite pagination retries the failed page");

        var clearReads = 0; var deletes = new List<long>(); var clearGate = Gate();
        _respond = async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var form = Decode(await request.Content!.ReadAsStringAsync());
                deletes.Add(long.Parse(form["media_id"])); return await clearGate.Task;
            }
            clearReads++;
            return Json(clearReads == 1 ? Folder(1) : Folder());
        };
        var clear = Call(favorites, "ClearFolderAsync");
        await Until(() => deletes.Count == 1 || clear.IsCompleted);
        if (clear.IsCompleted) await clear;
        check(deletes.Count == 1, "confirmed clear submits its first delete");
        check(favorites.Busy && !favorites.SelectFolderCommand.CanExecute(b), "folder selection is disabled during a confirmed clear");
        await favorites.SelectFolderAsync(b);
        check(favorites.CurrentFolderTitle == "A", "direct folder switches are blocked while clearing");
        clearGate.SetResult(Json(new { code = 0 })); await clear;
        check(deletes.SequenceEqual(new long[] { 1 }) && favorites.Items.Count == 0 && !favorites.Busy,
            "clear keeps its original target and completes after the folder becomes empty");

        _respond = _ => Reply(Folder(1)); await favorites.SelectFolderAsync(a);
        var readGate = Gate(); var reads = 0; var changedAccountDeletes = 0;
        _respond = request => { if (request.Method == HttpMethod.Post) changedAccountDeletes++; else reads++; return readGate.Task; };
        clear = Call(favorites, "ClearFolderAsync"); await Until(() => reads > 0);
        Svc.Session.Clear(); Login("200"); readGate.SetResult(Json(Folder(1))); await clear;
        check(changedAccountDeletes == 0 && favorites.Folders.Count == 0 && favorites.Items.Count == 0,
            "logout cancels a clear before its pending read can submit a delete");

        _respond = _ => Reply(Folder(Enumerable.Range(1, 21).Select(x => (long)x).ToArray()));
        favorites.Folders.Add(a); await favorites.SelectFolderAsync(a);
        favorites.ToggleEditCommand.Execute(null);
        foreach (var item in favorites.Items) item.IsSelected = true;
        var batchGate = Gate(); var batches = 0;
        _respond = _ => { batches++; return batchGate.Task; };
        var batchDelete = Call(favorites, "RemoveSelectedAsync"); await Until(() => batches == 1);
        Svc.Session.Clear(); Login("100"); batchGate.SetResult(Json(new { code = 0 })); await batchDelete;
        check(batches == 1 && !favorites.Busy, "account changes stop subsequent batches of favorite removals");

        var historyPages = new List<int>(); var failPageTwo = true;
        _respond = request =>
        {
            var page = int.Parse(Query(request)["pn"]); historyPages.Add(page);
            if (page == 2 && failPageTwo) { failPageTwo = false; return Reply(new { code = -1 }); }
            return Reply(new { code = 0, data = Enumerable.Range(0, 30).Select(x => Media(page * 100 + x)).ToArray() });
        };
        var history = new HistoryViewModel();
        await history.LoadAsync(true); await history.LoadAsync(false); await history.LoadAsync(false);
        check(historyPages.SequenceEqual(new[] { 1, 2, 2 }) && history.Entries.Count == 60,
            "history pagination retries the failed page instead of skipping records");
        var oldHistory = Gate(); var historyStarted = false;
        _respond = _ => { historyStarted = true; return oldHistory.Task; };
        var pendingHistory = history.LoadAsync(true); await Until(() => historyStarted);
        Svc.Session.Clear(); Login("100"); oldHistory.SetResult(Json(new { code = 0, data = new[] { Media(999) } }));
        await pendingHistory;
        check(history.Entries.Count == 0 && !history.Loading, "old history cannot return after logout and re-login as the same user");

        var messageVm = new MessagesViewModel();
        var contactA = new MsgSession { TalkerId = 101, Name = "A" };
        var contactB = new MsgSession { TalkerId = 102, Name = "B" };
        messageVm.Sessions.Add(contactA); messageVm.Sessions.Add(contactB);
        messageVm.Current = contactA; messageVm.Draft = "old draft";
        messageVm.Messages.Add(new MsgItem { Text = "old private text" });
        Svc.Session.Clear();
        check(messageVm.Sessions.Count == 0 && messageVm.Messages.Count == 0 && messageVm.Current == null
            && messageVm.Draft.Length == 0 && !messageVm.CanSend && !messageVm.CanCompose,
            "logout immediately clears private messages, recipients and drafts");
        Login("200"); var sent = 0;
        _respond = request => { if (request.Method == HttpMethod.Post) sent++; return Reply(new { code = -1 }); };
        await messageVm.LoadAsync(); await messageVm.SendAsync();
        check(sent == 0, "a failed load for the new account cannot send the old account draft");

        messageVm.Sessions.Add(contactA); messageVm.Sessions.Add(contactB);
        messageVm.Current = contactA; messageVm.Draft = "message for A";
        var sendGate = Gate(); sent = 0;
        _respond = _ => { sent++; return sendGate.Task; };
        var send = messageVm.SendAsync(); await Until(() => sent == 1);
        messageVm.Current = contactB;
        sendGate.SetResult(Json(new { code = 0 })); await send;
        check(messageVm.Current == contactB && messageVm.Messages.Count == 0,
            "a completed send to A is never appended to the visible conversation B");

        var noticeGate = Gate(); var noticeRequested = false;
        _respond = _ => { noticeRequested = true; return noticeGate.Task; };
        messageVm.Channel = "收到的喜欢"; await Until(() => noticeRequested);
        Svc.Session.Clear(); Login("100");
        noticeGate.SetResult(Json(new { code = 0, data = new { items = new[] { new { id = 1,
            user = new { mid = 101, nickname = "old account contact" }, item = new { source_content = "old notification" } } } } }));
        await Task.Delay(50);
        check(messageVm.Notifications.Count == 0 && !messageVm.NotificationLoading,
            "notifications from an old account cannot repopulate the cleared page");

        var limiter = (ApiRateLimiter)typeof(ApiClient).GetField("RateLimiter", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        limiter.Penalize(TimeSpan.FromMilliseconds(80));
        sent = 0; _respond = _ => { sent++; return Reply(new { code = 0 }); };
        var waitingSend = Svc.Api.SendMsgAsync(101, "queued fixture");
        Svc.Session.Clear(); Login("200");
        var stoppedSend = await waitingSend;
        check(!stoppedSend.ok && sent == 0, "an account change while waiting for rate limiting stops the queued write");

        var watchLater = new WatchLaterViewModel();
        var laterGate = Gate(); var laterRequested = false;
        _respond = _ => { laterRequested = true; return laterGate.Task; };
        var later = watchLater.LoadAsync(); await Until(() => laterRequested);
        Svc.Session.Clear(); Login("100");
        laterGate.SetResult(Json(new { code = 0, data = new { count = 1, list = new[] { Media(1) } } }));
        await later;
        check(watchLater.Items.Count == 0 && !watchLater.Loading, "watch-later ignores old account responses");

        var profileGate = Gate(); var oldProfileStarted = false;
        _respond = request =>
        {
            var cookies = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : "";
            if (cookies.Contains("fixture-100")) { oldProfileStarted = true; return profileGate.Task; }
            return Reply(new { code = 0, data = new { isLogin = true, uname = "New account", face = "new-face", mid = 200 } });
        };
        var main = new MainViewModel(); await Until(() => oldProfileStarted);
        Svc.Session.Clear(); Login("200");
        await Until(() => main.UserName == "New account");
        profileGate.SetResult(Json(new { code = 0, data = new { isLogin = true, uname = "Old account", face = "old-face", mid = 100 } }));
        await Task.Delay(50);
        check(main.IsLoggedIn && main.UserName == "New account" && main.UserFace == "new-face",
            "late profile responses cannot replace the newly logged-in account");
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => request.RequestUri!.AbsolutePath.EndsWith("/finger/spi")
                ? Reply(new { code = 0, data = new { b_3 = "fixture-b3", b_4 = "fixture-b4" } })
                : _respond(request);
    }
}
