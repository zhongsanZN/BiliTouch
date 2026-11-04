using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using BiliDesk.Views.Pages;
using BiliDesk.Views.Controls;

/// <summary>
/// 定位"动态流卡死"到底是哪一条数据(2026-11-04)。
///
/// 背景: touch.4 基线本身在真实动态流上也会卡死(CPU 单核跑满 67%, 不是等网络),
/// 说明是布局死循环, 与这一轮新加的投票/附加卡/富文本无关。
/// 这个探针**逐条**挂载 DynamicFeedCard, 每条给 2 秒; 超时的就是罪魁。
///
/// ★ 为什么逐条而不是整页: 整页挂载后卡死, 拿不到"是哪条";
///   逐条测能把范围缩到一条, 之后照着那条的 JSON 写回归测试。
/// ★ 判定用"能否在 2 秒内完成一轮布局"而不是耗时阈值:
///   死循环的特征是被打断时依然在跑, 正常卡片都在 100ms 内完成。
/// </summary>
internal static class FeedHangProbe
{
    public static void Run(string output)
    {
        OutputDir = output;
        var sessionFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Local", "BiliDesk", "session.json");
        if (!File.Exists(sessionFile)) { Console.WriteLine("HANG unavailable: no session"); return; }
        var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(sessionFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (session == null || string.IsNullOrEmpty(session.SessData))
        { Console.WriteLine("HANG unavailable: session incomplete"); return; }
        typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session, session);
        foreach (var pair in new[]{("_buvid3",session.Buvid3),("_buvid4",session.Buvid4)})
            typeof(ApiClient).GetField(pair.Item1, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(Svc.Api, pair.Item2);

        var feed = Svc.Api.GetDynamicFeedFullAsync();
        Wait(feed);
        if (!feed.Result.ok || feed.Result.items is not { Count: > 0 } items)
        { Console.WriteLine("HANG feed failed: " + feed.Result.err); return; }

        // ★ 把整页原始 JSON 落一份 —— 卡死那条就在里面, 之后可以离线复现。
        //   直接用 HttpClient 打同一个接口, 不依赖 ApiClient 内部实现(那层可能随改动漂移)。
        try
        {
            var cookie = typeof(SessionManager).GetMethod("BuildCookieHeader",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
                .Invoke(Svc.Session, null) as string ?? "";
            using var http = new System.Net.Http.HttpClient();
            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get,
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation("Referer", "https://t.bilibili.com/");
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
            using var response = http.Send(request);
            var raw = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, "hang-feed-raw.json"), raw);
            Console.WriteLine($"HANG raw saved: {raw.Length} chars");
        }
        catch (Exception ex) { Console.WriteLine("HANG raw save failed: " + ex.Message); }

        Console.WriteLine($"HANG items={items.Count}");
        Console.Out.Flush();
        var window = new Window { Width = 1100, Height = 780, ShowInTaskbar = false };
        window.Show();

        var report = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            // 描述只写"能区分"的部分, 不打印正文全文
            var shape = $"[{i}] kind={item.Kind} id={item.Id} text={item.Text.Length} pics={item.Pictures.Count} " +
                        $"video={(item.Video != null ? "y" : "n")} forward={item.IsForward} article={item.ArticleTitle.Length} " +
                        $"live={item.LiveRoomId} vote={(item.Vote != null ? item.Vote.Id : "-")} common={(item.Common != null ? "y" : "n")} " +
                        $"rich={item.RichNodes.Count} len={item.Text.Length}";
            Console.WriteLine(shape);
            Console.Out.Flush();

            var card = new DynamicFeedCard { DataContext = item };
            window.Content = card;
            var ok = TryPump(2000);
            report.Add(ok ? "ok   " + shape : "HANG " + shape);
            if (!ok)
            {
                Console.WriteLine("*** HANG DETECTED *** " + shape);
                // 把这一条的原始 JSON 从整页原文里挖出来, 落成单条夹具
                try
                {
                    var rawPath = Path.Combine(output, "hang-feed-raw.json");
                    if (File.Exists(rawPath))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(rawPath));
                        if (doc.RootElement.TryGetProperty("data", out var dd) &&
                            dd.TryGetProperty("items", out var arr))
                        {
                            foreach (var e in arr.EnumerateArray())
                            {
                                if (e.TryGetProperty("id_str", out var idEl) &&
                                    idEl.GetString() == item.Id)
                                {
                                    File.WriteAllText(Path.Combine(output, "hang-item.json"),
                                        JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true }));
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("item json extract failed: " + ex.Message); }
                break;
            }
            window.Content = null;   // 摘掉, 让下一张干净挂载
            Pump();
        }
        window.Close();
        File.WriteAllLines(Path.Combine(output, "hang-report.txt"), report);
        Console.WriteLine("HANG probe finished; report written");
    }

    /// <summary>
    /// 跑 ms 毫秒; 返回 false 表示这段时间里 UI 线程一直没空闲(布局死循环的特征)。
    ///
    /// ★ 卡住时把**单条数据的 JSON 原样落地** —— 这是能复现问题的最小材料。
    ///   有了它就能在离线测试里钉一条回归, 不用每次都连真接口。
    /// ★ 这里不做线程栈采样: 定时器回调跑在别的线程上, 拿不到 UI 线程的栈;
    ///   而 UI 线程自旋时 Dispatcher 根本不给任何回调机会。栈采样要靠外部进程挂调试器,
    ///   本探针的目标是"定位到哪一条", 那一步用二分 + JSON 落地就够了。
    /// </summary>
    private static bool TryPump(int ms)
    {
        var finished = false;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); finished = true; frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return finished;
    }

    /// <summary>输出目录(由外部传进来, 与其它探针一致)</summary>
    private static string OutputDir = "";

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Wait(Task task)
    {
        var limit = DateTime.UtcNow.AddSeconds(25);
        while (!task.IsCompleted && DateTime.UtcNow < limit) Pump();
        if (!task.IsCompleted) throw new TimeoutException("feed read timed out");
        task.GetAwaiter().GetResult();
    }
}
