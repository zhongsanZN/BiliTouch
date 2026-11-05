using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
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
using BiliTouch.Views;

internal static class LiveRoomTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, Hidden)!.GetValue(o)!;
    static object? Call(object o, string n, params object[] args) => o.GetType().GetMethod(n, Hidden)!.Invoke(o, args);
    static void Pump(int ms) { var f = new DispatcherFrame(); var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; t.Tick += (_, _) => { t.Stop(); f.Continue = false; }; t.Start(); Dispatcher.PushFrame(f); }
    static void Until(Func<bool> ready) { var start = Environment.TickCount64; while (!ready()) { if (Environment.TickCount64 - start > 15000) throw new TimeoutException("live-room UI timeout"); Pump(20); } }
    static T Wait<T>(Task<T> task) { Until(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    static void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    static void Save(FrameworkElement root, string path) { var b = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); b.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(b)); using var f = File.Create(path); png.Save(f); }
    static byte[] Packet(byte[] body, int version = 0, int op = 5) { var b = new byte[16 + body.Length]; BinaryPrimitives.WriteInt32BigEndian(b, b.Length); BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(4), 16); BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6), (short)version); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), op); body.CopyTo(b, 16); return b; }
    static byte[] Packet(string json, int op = 5) => Packet(Encoding.UTF8.GetBytes(json), 0, op);
    static byte[] Compressed(byte[] body, int version) { using var output = new MemoryStream(); using (Stream z = version == 3 ? new BrotliStream(output, CompressionLevel.Fastest, true) : new ZLibStream(output, CompressionLevel.Fastest, true)) z.Write(body); return Packet(output.ToArray(), version); }
    const string Chat = """{"cmd":"DANMU_MSG:4:0:2","info":[[0,1,25,"16711680"],"实时弹幕测试",["123","测试观众"],[12,"星星"]]}""";
    static string Sc(string id = "7", int seconds = 60) => """{"cmd":"SUPER_CHAT_MESSAGE","data":{"id":__ID__,"uid":99,"price":30,"message":"这是一条醒目留言，测试价格、用户名与倒计时。","end_time":__END__,"user_info":{"uname":"SC 观众"}}} """.Replace("__ID__",id).Replace("__END__",(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+seconds).ToString());
    public static void Run(Action<bool, string> check, string artifacts)
    {
        var context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { Protocol(check); State(check); WindowAndApi(check, artifacts); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }
    static void Protocol(Action<bool, string> check)
    {
        using var service = new LiveDanmakuService(Svc.Api, Dispatcher.CurrentDispatcher);
        var entries = new List<LiveChatEntry>(); var connections = new List<bool>(); var deleted = new List<string>(); string command = "";
        service.ChatReceived += entries.Add; service.ConnectionChanged += connections.Add; service.SuperChatDeleted += ids => deleted.AddRange(ids); service.RoomEvent += (cmd, _) => command = cmd;
        check(!service.IsConnected, "live socket is not reported connected before server authentication");
        service.ParsePackets(Packet("{\"code\":0}", 8)); Pump(80);
        check(connections.SequenceEqual(new[] { true }), "authentication succeeds only after op=8 code=0");
        service.ParsePackets(Packet("{\"code\":-101}", 8)); Pump(50); check(connections.Last() == false, "rejected authentication is represented as disconnected");
        var nested = Packet(Chat).Concat(Packet(Sc())).ToArray();
        service.ParsePackets(Compressed(Compressed(nested, 2), 3)); Pump(120);
        check(entries.Count == 2, "Brotli containing zlib and multiple complete packets is decoded");
        check(entries[0].Text == "实时弹幕测试" && entries[0].UserId == 123 && entries[0].Color == 0xFF0000 && entries[0].Medal == "星星 12", "live chat preserves string-valued UID, color and fan medal");
        check(entries[1].User == "SC 观众" && entries[1].Price == 30 && entries[1].Kind == LiveChatKind.SuperChat && !entries[1].IsExpired, "SC uses nested user_info, paid amount and server expiration");
        service.ParsePackets(Packet("{\"cmd\":\"SUPER_CHAT_MESSAGE_DELETE\",\"data\":{\"ids\":[7,\"8\"]}}"));
        service.ParsePackets(Packet("{\"cmd\":\"PREPARING\",\"data\":{}}")); Pump(80);
        check(deleted.SequenceEqual(new[] { "7", "8" }) && command == "PREPARING", "moderation deletes and room state reach the live room");
        int before = entries.Count;
        service.ParsePackets(Packet("{\"cmd\":\"SEND_GIFT\",\"data\":{\"uname\":\"礼物\",\"num\":1,\"giftName\":\"礼物\"}}"));
        var invalid = Packet(Chat); BinaryPrimitives.WriteInt16BigEndian(invalid.AsSpan(4), 2); service.ParsePackets(invalid);
        var bomb = Compressed(new byte[8 * 1024 * 1024 + 1], 3); service.ParsePackets(bomb); Pump(80);
        check(entries.Count == before, "gifts, malformed headers and oversized decompression do not produce chat");
        service.ParsePackets(Packet(Chat)); service.Stop(); Pump(80);
        check(entries.Count == before, "stopping invalidates already queued messages from the old room");
    }
    static void State(Action<bool, string> check)
    {
        using var vm = new LiveRoomViewModel(); vm.Reset(123); var batches = new List<int>(); vm.BatchApplied += b => batches.Add(b.Count);
        for (int i = 0; i < 3000; i++) vm.Enqueue(new LiveChatEntry { User = "观众", Text = "弹幕 " + i });
        vm.Flush(); check(vm.Messages.Count == 40 && batches[0] == 40, "busy live chat applies only 40 messages per UI batch");
        for (int i = 0; i < 60; i++) vm.Flush();
        check(vm.Messages.Count == 500 && vm.Messages.Last().Text == "弹幕 2999", "bounded backlog keeps the latest 500 messages");
        using var scDoc = JsonDocument.Parse(Sc()); var sc = LiveChatEntry.ParseSuperChat(scDoc.RootElement.GetProperty("data"));
        vm.Enqueue(sc); vm.Enqueue(sc); vm.Flush(); check(vm.ActiveSuperChats.Count == 1, "initial and websocket copies of the same SC are deduplicated");
        vm.DeleteSuperChats(new[] { "7", "8" });
        vm.Enqueue(new LiveChatEntry { Id = "8", Kind = LiveChatKind.SuperChat, Text = "迟到消息", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) }); vm.Flush();
        check(vm.ActiveSuperChats.Count == 0 && vm.Messages.All(m => m.Kind != LiveChatKind.SuperChat), "SC deletion removes history and prevents late delivery from restoring it");
        vm.Enqueue(new LiveChatEntry { Id = "9", Text = "到期", Kind = LiveChatKind.SuperChat, ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(150) }); vm.Flush(); Pump(180); vm.Tick();
        check(vm.ActiveSuperChats.Count == 0, "expired SC is removed from the pinned panel");
        var longSc = new LiveChatEntry { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(90) }; check(longSc.RemainingText.StartsWith("1:"), "SC countdown represents durations over one hour");
        vm.Reset(456); check(vm.Messages.Count == 0 && !vm.HasSuperChats && vm.RoomId == 456, "switching rooms clears old chat and SC");
    }
    static void WindowAndApi(Action<bool, string> check, string artifacts)
    {
        var field = typeof(ApiClient).GetField("_http", Hidden)!; var old = field.GetValue(Svc.Api); var session = Svc.Session.Current; var mode = Svc.Theme.Mode; int volume = Svc.Settings.PlayerVolume;
        var settings = Svc.Settings;
        var oldDm = (settings.DanmakuEnabled, settings.DanmakuAreaPercent, settings.DanmakuSmartFilter, settings.DanmakuBlockKeywords, settings.DanmakuColorful, settings.DanmakuOpacityPercent, settings.DanmakuFontScalePercent, settings.DanmakuSpeedLevel);
        var fixture = new Fixture(Path.Combine(artifacts, "../touch-sample-silent.mp4")); using var http = new HttpClient(fixture); field.SetValue(Svc.Api, http);
        typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session, new Session { SessData = "fixture", BiliJct = "fixture-csrf", DedeUserID = "42", Buvid3 = "fixture3", Buvid4 = "fixture4" });
        LiveRoomWindow? window = null;
        try
        {
            Svc.Theme.SetAccent("");
            var result = Wait(Svc.Api.GetLiveRoomDetailsAsync(6));
            check(result.room is { RoomId: 123, UserId: 77, Anchor: "测试主播", Title: "直播间独立布局验证", Followed: true }, "room metadata resolves short ID and anchor identity");
            check(result.room!.SuperChats.Count == 1 && result.room.Announcement == "本地验证，不向真实房间发言。", "room details include announcement and active SC");
            fixture.Fallback = true; var fallback = Wait(Svc.Api.GetLiveRoomDetailsAsync(6)); fixture.Fallback = false;
            check(fallback.room is { RoomId: 123, Anchor: "后备主播", Announcement: "后备公告" }, $"room metadata falls back to legacy public endpoints ({fallback.ok}, {fallback.room?.RoomId}, {fallback.room?.Anchor}, [{fallback.room?.Announcement}], {fallback.error})");
            fixture.DanmakuFallback = true; var config = Wait(Svc.Api.GetLiveDanmakuInfoAsync(123)); fixture.DanmakuFallback = false;
            check(config.url == "wss://localhost:443/sub" && config.token == "fixture-token", "rejected web danmaku configuration falls back to public player token and hosts");
            var play = Wait(Svc.Api.GetLivePlayUrlExAsync(123));
            check(play.actualQn == 400 && play.options.Count == 2 && play.options.All(q => q.qn != 10000), "quality options come from the stream rather than the platform-wide labels");
            fixture.OmitAccept = true; play = Wait(Svc.Api.GetLivePlayUrlExAsync(123)); fixture.OmitAccept = false;
            check(play.options.Count == 1 && play.options[0].qn == 400, "missing quality availability exposes only the actual current stream");
            var sent = Wait(Svc.Api.SendLiveMessageAsync(123, "测试弹幕"));
            check(sent.ok && fixture.LastPost.Contains("roomid=123") && fixture.LastPost.Contains("csrf=fixture-csrf") && fixture.LastPost.Contains("csrf_token=fixture-csrf"), "live send posts the canonical room and both csrf fields");
            check(fixture.Origin == "https://live.bilibili.com" && fixture.Referer == "https://live.bilibili.com/123", "live send uses live origin and room referer");
            window = new LiveRoomWindow { Width = 1360, Height = 840 }; window.Show();
            var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_player"); player.Volume = 0;
            window.OpenRoom(6); Until(() => window.Model.RoomId == 123 && fixture.PlayRequests >= 3); Pump(1000);
            check(window.FindName("ProgressSlider") == null && window.FindName("BtnCoin") == null && window.FindName("CommentList") == null, "live room has no VOD timeline, coin buttons or replay comments");
            // ★ 2026-11-05 直播间对齐播放页后, 清晰度从 ComboBox 换成"按钮 + 弹层"(与播放器同一形态),
            //   所以这里查 QualityList。断言的含义不变: 真实取到的档位数量必须来自流本身。
            var qualityList = (ListBox)window.FindName("QualityList");
            check(qualityList.Items.Count == 2, "independent live controls expose real stream quality choices");
            // ★ 新控件: 音量改成"悬停/点击向上展开"的弹层, 播放按钮与播放器同款矢量图标。
            //   注意直播间里这个弹层的 x:Name 是 VolumePopup(与播放器同名)。
            //   ★ 不断言"初始一定关着": 鼠标真实光标可能正好落在喇叭上, MouseEnter 会把弹层打开 ——
            //     那是正确行为, 拿它当回归点会变成看运气的假失败。这里只验"滑杆确实住在弹层里"。
            var volumePopup = (System.Windows.Controls.Primitives.Popup)window.FindName("VolumePopup");
            var volumeSlider = (Slider)window.FindName("LiveVolume");
            var pauseGlyph = (System.Windows.Shapes.Path)window.FindName("PauseGlyph");
            check(volumePopup != null && volumePopup.Child != null && volumeSlider != null &&
                  volumePopup.Child.IsAncestorOf(volumeSlider),
                "live volume slider lives in an upward popup instead of occupying the control bar");
            Call(window, "MuteClick", (Button)window.FindName("MuteButton"), new RoutedEventArgs()); Pump(60);
            check(volumePopup!.IsOpen, "tapping the speaker expands the volume slider upward");
            volumePopup.IsOpen = false;
            check(pauseGlyph != null && pauseGlyph.Data != null, "live play/pause uses the same vector glyph as the player");
            var nativeReady = player.IsPlaying; check(nativeReady, "independent live player actually decodes the local silent video fixture");
            var model = window.Model;
            settings.UpdateDanmakuSettings(true, 50, true, "屏蔽词", true, opacityPercent: 60, fontScalePercent: 150, speedLevel: 3);
            check(((Slider)window.FindName("FontSetting")).Value == 150 && ((Slider)window.FindName("OpacitySetting")).Value == 60, "live settings follow global danmaku preferences");
            int beforeBlocked = model.Messages.Count;
            Call(window, "ReceiveChat", new LiveChatEntry { User = "测试", Text = "包含屏蔽词的弹幕" }); model.Flush();
            check(model.Messages.Count == beforeBlocked || model.Messages.Last().Text != "包含屏蔽词的弹幕", "live reception respects the user's keyword filter");
            ((Slider)window.FindName("SpeedSetting")).Value = 1;
            check(settings.DanmakuSpeedLevel == 1, "live settings persist through the existing global settings service");
            Call(window, "OverlaySettingsClick", window, new RoutedEventArgs()); Pump(80);
            var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("LiveSettingsPopup");
            check(popup.IsOpen && PresentationSource.FromVisual(popup.Child) != null, "live danmaku settings open in the real native-video overlay"); popup.IsOpen = false;
            for (int i = 0; i < 120; i++) model.Enqueue(new LiveChatEntry { User = "观众 " + i, Text = "这是一条实时弹幕。侧栏独立滚动，画面弹幕即时出现。", Medal = "星星 12" });
            model.Flush(); Pump(300);
            var list = (ListBox)window.FindName("ChatList");
            check(VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling, "live sidebar uses a recycling virtualized list");
            Call(window, "ChatWheel", list, new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120));
            model.Enqueue(new LiveChatEntry { User = "新观众", Text = "向上阅读时不抢回底部" }); model.Flush(); Pump(120);
            check(((Button)window.FindName("LatestButton")).Visibility == Visibility.Visible, "reading older messages pauses automatic following and shows unread messages");
            Call(window, "LatestClick", list, new RoutedEventArgs()); Pump(150);
            check(((Button)window.FindName("LatestButton")).Visibility == Visibility.Collapsed, "return to latest resumes chat following");
            Svc.Theme.SetMode(AppTheme.Light); Pump(100); Save(window, Path.Combine(artifacts, "live-room-light.png")); Save((FrameworkElement)window.FindName("VideoOverlay"), Path.Combine(artifacts, "live-room-video-overlay.png"));
            Svc.Theme.SetMode(AppTheme.Dark); Pump(100); Save(window, Path.Combine(artifacts, "live-room-dark.png"));
            window.Width = 850; window.Height = 650; Pump(150); Save(window, Path.Combine(artifacts, "live-room-compact.png"));
            var controls = (Border)window.FindName("LiveControls");
            // ★ 2026-11-05: LiveControls 的直接子元素从 Grid 变成 StackPanel(上面多了一行"直播中/人气"),
            //   所以这里按名字取真正的最右一颗按钮(全屏), 再断言它没被裁出去。
            var fullButton = (FrameworkElement)window.FindName("FullscreenButton");
            var rightEdge = fullButton.TranslatePoint(new Point(fullButton.ActualWidth, 0), controls).X;
            check(rightEdge <= controls.ActualWidth + 0.5,
                $"all live controls fit on one row in the compact layout (right={rightEdge:F0}, bar={controls.ActualWidth:F0})");
            var input = (TextBox)window.FindName("ChatInput"); fixture.FailSend = true; input.Text = "失败保留草稿"; Wait((Task)Call(window, "SendMessageAsync")!);
            check(input.Text == "失败保留草稿" && window.Model.Error.Contains("频率"), "live send error retains the draft and displays the server reason");
            fixture.FailSend = false; fixture.SendPending = new TaskCompletionSource<HttpResponseMessage>();
            var send = (Task)Call(window, "SendMessageAsync")!; int posts = fixture.Posts; Wait((Task)Call(window, "SendMessageAsync")!);
            check(fixture.Posts == posts, "live composer blocks overlapping submissions");
            window.Model.Reset(456); typeof(LiveRoomWindow).GetField("_request", Hidden)!.SetValue(window, Field<int>(window, "_request") + 1); input.Text = "新房间草稿";
            fixture.SendPending.SetResult(Fixture.Response("{\"code\":0}")); fixture.SendPending = null; Wait(send);
            check(input.Text == "新房间草稿", "old-room send response cannot erase a new-room draft");
            window.WindowState = WindowState.Maximized; Pump(120); var states = new List<WindowState>(); window.StateChanged += (_, _) => states.Add(window.WindowState);
            Call(window, "ToggleFullscreen"); Pump(120); Call(window, "ToggleFullscreen"); Pump(120);
            check(!states.Contains(WindowState.Normal) && ((FrameworkElement)window.FindName("ChatPanel")).IsVisible, "live fullscreen keeps a maximized HWND and restores the sidebar");
            var socket = Field<LiveDanmakuService>(window, "_chat");
            socket.ParsePackets(Packet("{\"cmd\":\"PREPARING\",\"data\":{}}")); Pump(150);
            Until(() => player.State == LibVLCSharp.Shared.VLCState.Stopped);
            check(model.LiveState == 0 && ((FrameworkElement)window.FindName("PlaybackState")).Visibility == Visibility.Visible, "downstream room end stops playback and shows the ended state");
            socket.ParsePackets(Packet("{\"cmd\":\"LIVE\",\"data\":{}}")); Until(() => player.IsPlaying);
            check(model.LiveState == 1, "room start reconnects the actual video stream");
            window.Close(); Until(() => !window.IsLoaded); window = null;
            check(true, "closing a live room stops playback, websocket, rendering and native resources");
        }
        finally
        {
            if (window != null) { window.Close(); Pump(1500); }
            field.SetValue(Svc.Api, old); typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session, session); Svc.Theme.SetMode(mode); Svc.Settings.SetPlayerVolume(volume);
            settings.UpdateDanmakuSettings(oldDm.DanmakuEnabled, oldDm.DanmakuAreaPercent, oldDm.DanmakuSmartFilter, oldDm.DanmakuBlockKeywords, oldDm.DanmakuColorful, opacityPercent: oldDm.DanmakuOpacityPercent, fontScalePercent: oldDm.DanmakuFontScalePercent, speedLevel: oldDm.DanmakuSpeedLevel);
        }
    }
    public static void Network(string artifacts, string? sessionPath = null)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        // Read-only. An explicit session path enables a separate authenticated receive probe.
        // SessionManager is never loaded for persistence; no login data is saved or printed.
        if (sessionPath != null)
            typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session, JsonSerializer.Deserialize<Session>(File.ReadAllText(sessionPath)));
        Svc.Theme.SetAccent(""); Svc.Theme.SetMode(AppTheme.Dark);
        var window = new LiveRoomWindow { Width = 1360, Height = 840 };
        ((Slider)window.FindName("LiveVolume")).Value = 0;
        bool closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); window.OpenRoom(7734200);
            var start = Environment.TickCount64;
            var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_player");
            var chat = Field<LiveDanmakuService>(window, "_chat");
            while (Environment.TickCount64 - start < 45000 && !(player.IsPlaying && chat.IsConnected && window.Model.Messages.Any(m => m.Kind == LiveChatKind.Danmaku))) Pump(100);
            bool snapshot = player.IsPlaying && player.TakeSnapshot(0, Path.Combine(artifacts, "live-room-network-frame.png"), 1280, 0);
            var report = new { signedIn = Svc.Session.HasLogin, room = window.Model.RoomId, state = window.Model.LiveState, metadata = window.Model.Anchor.Length > 0, videoPlaying = player.IsPlaying, snapshot, chatAuthenticated = chat.IsConnected, messages = window.Model.Messages.Count, realtimeMessages = window.Model.Messages.Count(m => m.Kind == LiveChatKind.Danmaku), sc = window.Model.ActiveSuperChats.Count, error = window.Model.Error, chatError = chat.LastError, connection = window.Model.ConnectionText };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json); File.WriteAllText(Path.Combine(artifacts, sessionPath == null ? "live-room-network.json" : "live-room-network-auth.json"), json);
            Save(window, Path.Combine(artifacts, "live-room-network.png"));
            Save((FrameworkElement)window.FindName("VideoOverlay"), Path.Combine(artifacts, "live-room-network-overlay.png"));
        }
        finally { window.Close(); var end = Environment.TickCount64; while (!closed && Environment.TickCount64 - end < 10000) Pump(50); }
    }
    sealed class Fixture(string sample) : HttpMessageHandler
    {
        public bool Fallback, OmitAccept, FailSend, DanmakuFallback; public int PlayRequests, Posts; public string LastPost = "", Origin = "", Referer = "";
        public TaskCompletionSource<HttpResponseMessage>? SendPending;
        public static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Danmu/getConf") && DanmakuFallback) return Response("{\"code\":0,\"data\":{\"token\":\"fixture-token\",\"host_server_list\":[{\"host\":\"localhost\",\"wss_port\":443}]}}");
            if (path == "/msg/send") { Posts++; LastPost = await request.Content!.ReadAsStringAsync(); Origin = request.Headers.GetValues("Origin").Single(); Referer = request.Headers.Referrer!.AbsoluteUri; if (SendPending != null) return await SendPending.Task; return Response(FailSend ? "{\"code\":10030,\"message\":\"发送频率过快\"}" : "{\"code\":0}"); }
            if (path.EndsWith("getInfoByRoom")) return Response(Fallback ? "{\"code\":-352}" : """{"code":0,"data":{"room_info":{"room_id":123,"uid":77,"title":"直播间独立布局验证","area_name":"游戏 · 测试","live_status":1},"anchor_info":{"base_info":{"uname":"测试主播"}},"room_news":{"content":"本地验证，不向真实房间发言。"},"relation_info":{"attention":1},"watched_show":{"text_large":"1.2 万人看过"},"super_chat_info":{"message_list":[__SC__]}}} """.Replace("__SC__",JsonDocument.Parse(Sc()).RootElement.GetProperty("data").GetRawText()));
            if (path == "/room/v1/Room/get_info") return Response("{\"code\":0,\"data\":{\"room_id\":123,\"uid\":77,\"title\":\"后备直播间\",\"description\":\"<p>后备公告</p>\",\"live_status\":1}}");
            if (path == "/live_user/v1/Master/info") return Response("{\"code\":0,\"data\":{\"info\":{\"uname\":\"后备主播\"}}}");
            if (path.EndsWith("gethistory")) return Response("{\"code\":0,\"data\":{\"room\":[{\"nickname\":\"历史观众\",\"text\":\"最近发言\",\"uid\":12}]}}");
            if (path.EndsWith("getRoomPlayInfo"))
            {
                PlayRequests++; string availability = OmitAccept ? "" : "\"accept_qn\":[400,80],";
                var local = new Uri(Path.GetFullPath(sample)).AbsoluteUri;
                return Response("""{"code":0,"data":{"live_status":1,"playurl_info":{"playurl":{"g_qn_desc":[{"qn":10000,"desc":"不存在的原画"},{"qn":400,"desc":"蓝光"},{"qn":80,"desc":"流畅"}],"stream":[{"protocol_name":"http_stream","format":[{"format_name":"flv","codec":[{ __AVAIL__ "current_qn":400,"base_url":__LOCAL__,"url_info":[{"host":"file:///","extra":""}]}]}]}]}}}} """.Replace("__AVAIL__",availability).Replace("__LOCAL__",JsonSerializer.Serialize(local[8..])));
            }
            if (path == "/x/web-interface/nav") return Response("{\"code\":0,\"data\":{\"wbi_img\":{\"img_url\":\"https://example.invalid/01234567890123456789012345678901.png\",\"sub_url\":\"https://example.invalid/01234567890123456789012345678901.png\"}}}");
            return Response("{\"code\":-400,\"message\":\"local fixture\"}");
        }
    }
}
