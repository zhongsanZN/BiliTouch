using System.IO;
using System.Net;
using System.Net.Http;
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

internal static class MessageUnreadTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Until(Func<bool> condition)
    {
        var end = Environment.TickCount64 + 8000;
        while (!condition() && Environment.TickCount64 < end)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        if (!condition()) throw new TimeoutException("Unread fixture did not finish");
    }
    private static JsonElement J(string value) => JsonDocument.Parse(value).RootElement.Clone();

    internal static void Run(Action<bool, string> check)
    {
        Svc.Session.Load();
        var parsed = ApiClient.ParseMessageUnread(J("{\"reply\":3,\"at\":2,\"like\":4,\"chat\":100,\"sys_msg\":9}"), J("{\"follow_unread\":5,\"unfollow_unread\":6}"));
        check(parsed == new MessageUnreadSnapshot(11, 3, 2, 4), "private counters are summed without counting the unrelated chat field twice");
        check(ApiClient.ParseMessageUnread(null, null) == new MessageUnreadSnapshot(), "missing or failed endpoint results remain unknown rather than pretending to be zero");
        var account = "A"; int calls = 0;
        var responses = new Queue<TaskCompletionSource<MessageUnreadSnapshot>>();
        var service = new MessageUnreadService(() => { calls++; var t = new TaskCompletionSource<MessageUnreadSnapshot>(); responses.Enqueue(t); return t.Task; }, () => account, Dispatcher.CurrentDispatcher);
        var task = service.RefreshAsync(true);
        responses.Dequeue().SetResult(parsed); Until(() => task.IsCompleted);
        check(service.Total == 20 && service.PrivateCount == 11 && service.ReplyCount == 5 && service.HasUnread,
            "sidebar and channel counters share the same unread snapshot");
        task = service.RefreshAsync(true);
        responses.Dequeue().SetResult(new(Private: 7)); Until(() => task.IsCompleted);
        check(service.Total == 16 && service.ReplyCount == 5, "a partially failed poll retains the last confirmed notification counters");
        var oldCalls = calls; service.RefreshAsync().GetAwaiter().GetResult();
        check(calls == oldCalls, "activation refreshes are throttled between timer polls");
        task = service.RefreshAsync(true);
        var same = service.RefreshAsync(true);
        check(ReferenceEquals(task, same), "simultaneous refresh requests share one in-flight poll");
        responses.Dequeue().SetResult(new(2, 1, 1, 1)); Until(() => responses.Count > 0);
        responses.Dequeue().SetResult(new(1, 0, 0, 0)); Until(() => task.IsCompleted);
        check(service.Total == 1, "an acknowledgement during polling triggers one fresh follow-up snapshot");
        task = service.RefreshAsync(true); var stale = responses.Dequeue();
        account = "";
        typeof(MessageUnreadService).GetMethod("OnAccountChanged", Hidden)!.Invoke(service, null);
        check(!service.HasUnread && service.Total == 0, "logout clears every unread badge immediately");
        stale.SetResult(new(90, 90, 90, 90)); Until(() => task.IsCompleted);
        check(service.Total == 0, "late responses from the previous account cannot restore its badges");
        account = "B";
        task = service.RefreshAsync(true); responses.Dequeue().SetResult(new(120, 0, 0, 0)); Until(() => task.IsCompleted);
        check(service.BadgeText == "99+" && service.PrivateTitle.Contains("99+"), "large unread counts remain bounded in the sidebar and channel labels");

        var apply = typeof(MessageUnreadService).GetMethod("Apply", Hidden)!;
        apply.Invoke(Svc.MessageUnread, new object[] { new MessageUnreadSnapshot(3, 1, 2, 0) });
        App.MainVm.Navigate(PageKey.Messages);
        var main = new MainWindow { Width = 1150, Height = 800 };
        try
        {
            main.Show(); main.UpdateLayout();
            var badge = (Border)main.FindName("MessageUnreadBadge");
            check(badge.IsVisible && ((TextBlock)badge.Child).Text == "6", "the actual sidebar renders the shared unread badge");
            var text = (TextBlock)badge.Child;
            foreach (var font in new[] { "HarmonyOS", "JingNan" })
            {
                AppTypography.Apply(font); main.UpdateLayout();
                var face = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
                check(face.TryGetGlyphTypeface(out var glyphs) && "0123456789+".All(c => glyphs.CharacterToGlyphMap.TryGetValue(c, out var glyph) && glyph != 0),
                    $"actual badge resolves numeric glyphs in {font} instead of inheriting its icon button font");
            }
            AppTypography.Apply("HarmonyOS");
            foreach (var count in new[] { 6, 12, 120 })
            {
                apply.Invoke(Svc.MessageUnread, new object[] { new MessageUnreadSnapshot(count, 0, 0, 0) });
                main.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(badge.ActualWidth * 6), 96, 576, 576, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(badge) { Stretch=Stretch.Fill }, null, new Rect(0, 0, badge.ActualWidth, badge.ActualHeight));
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(BiliTouch.Services.AppPaths.DataDir, $"message-badge-{count}.png"));
                encoder.Save(output);
            }
            apply.Invoke(Svc.MessageUnread, new object[] { new MessageUnreadSnapshot(0, 0, 0, 0) });
            main.UpdateLayout();
            check(!badge.IsVisible, "the actual sidebar removes its badge when unread counts reach zero");
        }
        finally { main.Close(); App.MainVm.Navigate(PageKey.Home); service.Stop(); }
    }

    internal static void Live(Action<bool, string> check)
    {
        // Read the account into this test process while all writes still target the
        // isolated test directory. Never print or copy credentials into artifacts.
        Svc.Session.Load();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliTouch", "session.json");
        var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(path))!;
        typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session, session);
        var task = Svc.Api.GetMessageUnreadAsync(); Until(() => task.IsCompleted);
        var counts = task.GetAwaiter().GetResult();
        Console.WriteLine($"UNREAD-LIVE private={counts.Private} reply={counts.Reply} at={counts.At} like={counts.Like}");
        check(counts.Private.HasValue, "the private unread endpoint returns an authenticated counter");
        check(counts.Reply.HasValue && counts.At.HasValue && counts.Like.HasValue, "the notification endpoint returns authenticated category counters");
    }
}
