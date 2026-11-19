using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Views;

internal static class DanmakuFilteringTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    internal static void Run(Action<bool, string> check)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var snapshot = typeof(SettingsStore).GetMethod("Snapshot", Hidden)!;
        var apply = typeof(SettingsStore).GetMethod("ApplySanitized", Hidden)!;
        var original = (AppSettings)snapshot.Invoke(Svc.Settings, null)!;
        PlayerWindow? player = null;
        try
        {
            Svc.Settings.UpdateDanmakuSettings(true, 35, false, blockKeywords: "禁止", colorful: true,
                filterScroll: true, filterFixed: true, filterColorful: true, filterAdvanced: true, opacityPercent: 80);
            player = new PlayerWindow { Width = 1200, Height = 800 };
            player.Show(); Pump(250);
            var raw = Field<List<RawDanmaku>>(player, "_danmakuRaw");
            var visible = Field<List<RawDanmaku>>(player, "_danmaku");
            raw.AddRange(Enumerable.Range(0, 60_000).Select(i => new RawDanmaku(i / 60d, i % 2 == 0 ? "禁止" : "保留", 1)));
            var first = (Task)Call(player, "RefilterDanmakuAsync", true)!;
            Svc.Settings.UpdateDanmakuSettings(true, 35, false, blockKeywords: "保留");
            var newest = (Task)Call(player, "RefilterDanmakuAsync", true)!;
            Await(newest); Await(first);
            check(visible.Count == 30_000 && visible.All(d => d.Text == "禁止"),
                "latest keyword snapshot wins when a 60,000-comment filter is replaced in flight");
            check(raw.Count == 60_000, "background filtering preserves the original seekable timeline");

            Call(player, "CalibrateDanmakuClock", 10d);
            Call(player, "SetDanmakuClockRunning", false);
            Call(player, "AddDanmaku", "调透明度时不应清屏", 1, 10d, 0xffffffu);
            var canvas = (Canvas)player.FindName("DanmakuCanvas");
            var element = canvas.Children.Cast<FrameworkElement>().First();
            var count = canvas.Children.Count;
            Svc.Settings.UpdateDanmakuSettings(true, 35, false, opacityPercent: 50);
            Pump(80);
            check(canvas.Children.Count == count && canvas.Children.Contains(element),
                "opacity changes preserve already visible rolling comments instead of clearing them");
            check(Math.Abs(element.Opacity - .5) < .001, "opacity slider immediately updates already visible rolling comments");
            check(visible.Count == 30_000, "appearance-only changes retain the filtered membership");

            var canceled = (Task)Call(player, "RefilterDanmakuAsync", true)!;
            Call(player, "OnWebMediaChanging");
            visible.Clear(); visible.Add(new RawDanmaku(0, "下一片的时间轴", 1));
            Await(canceled); Pump(80);
            check(visible.Count == 1 && visible[0].Text == "下一片的时间轴",
                "media change prevents an old background filter from overwriting the new timeline");

            var pending = (Task)Call(player, "RefilterDanmakuAsync", true)!;
            player.Close(); Await(pending); Pump(800);
            check(Field<bool>(player, "_closing"), "closing with pending filtering completes without reviving the player");
        }
        finally
        {
            player?.Close(); Pump(300);
            apply.Invoke(Svc.Settings, new object[] { original }); Svc.Settings.SaveNow();
        }

        var rules = DanmakuKeywordFilter.Build("re:^(a+)+$;剧透")!;
        var pathological = new string('a', 2048) + "!";
        var watch = Stopwatch.StartNew();
        var blocked = false;
        for (var i = 0; i < 2000; i++) blocked |= rules.IsBlocked(pathological);
        watch.Stop();
        check(!blocked && watch.ElapsedMilliseconds < 1500,
            $"one timed-out regex is quarantined for the remaining timeline ({watch.ElapsedMilliseconds}ms / 2,000 candidates)");
        check(rules.IsBlocked("这是剧透"), "quarantining a bad regex preserves other valid keyword rules");
    }

    private static void Await(Task task)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!task.IsCompleted && Environment.TickCount64 < deadline) Pump(15);
        if (!task.IsCompleted) throw new TimeoutException("Background filter did not complete");
        task.GetAwaiter().GetResult();
    }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
