using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using BiliTouch.Models;
using BiliTouch;
using BiliTouch.Services;

/// <summary>
/// 把 feed/all 的**原始响应**落盘, 供离线分析"为什么列表里没有正文"(2026-11-04)。
///
/// 已知事实(上一轮探针 --live-text):
///   · 同一条动态, 列表接口 text=0, 详情接口 text=125;
///   · 所以"点进详情再返回才显示"完全解释得通 —— 列表确实没有正文。
/// 这一轮要回答的是: 列表接口**能不能**给正文(某个参数?), 还是必须逐条补详情。
///
/// 落盘两份: feed/all 原始 JSON, 以及那条动态的 detail 原始 JSON, 便于逐字段比对。
/// </summary>
internal static class FeedRawDump
{
    public static void Run(string output, string? targetId = null)
    {
        var sessionFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Local", "BiliTouch", "session.json");
        if (!File.Exists(sessionFile)) { Console.WriteLine("DUMP no session"); return; }
        var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(sessionFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (session == null) { Console.WriteLine("DUMP bad session"); return; }
        typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session, session);
        foreach (var pair in new[]{("_buvid3",session.Buvid3),("_buvid4",session.Buvid4)})
            typeof(ApiClient).GetField(pair.Item1, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Svc.Api, pair.Item2);

        var cookie = Svc.Session.BuildCookieHeader();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://t.bilibili.com/");

        string Get(string url)
        {
            using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        // ① 默认 feed/all
        var raw1 = Get("https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all");
        File.WriteAllText(Path.Combine(output, "feed-all.json"), raw1);
        Console.WriteLine($"feed/all: {raw1.Length} chars");

        // ② feed/all + features=itemOpusStyle (官方发布页用的参数, 可能带完整 opus)
        var raw2 = Get("https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all?features=itemOpusStyle");
        File.WriteAllText(Path.Combine(output, "feed-all-opus.json"), raw2);
        Console.WriteLine($"feed/all+opusStyle: {raw2.Length} chars");

        // ③ feed/all + features=itemOpusStyle,onlyfansVote 等全开(官方 wbi 接口的常见组合)
        var raw3 = Get("https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all" +
                       "?features=itemOpusStyle,listOnlyfans,opusBigCover,onlyfansVote,decorationCard,onlyfansAssetsV2,ugcDelete,onlyfansQaCard");
        File.WriteAllText(Path.Combine(output, "feed-all-features.json"), raw3);
        Console.WriteLine($"feed/all+allFeatures: {raw3.Length} chars");

        // ④ 逐条对比: 三个响应的 items 数、正文长度分布
        foreach (var (name, raw) in new[] { ("default", raw1), ("opusStyle", raw2), ("allFeatures", raw3) })
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("items", out var arr)) { Console.WriteLine($"  {name}: no items"); continue; }
                var total = 0; var withText = 0; var lengths = new List<int>();
                foreach (var e in arr.EnumerateArray())
                {
                    total++;
                    var text = "";
                    try
                    {
                        text = e.GetProperty("modules").GetProperty("module_dynamic")
                                .GetProperty("desc").GetProperty("text").GetString() ?? "";
                    }
                    catch { }
                    if (text.Length > 0) withText++;
                    lengths.Add(text.Length);
                }
                Console.WriteLine($"  {name}: items={total}, 有正文={withText}, 正文长度=[{string.Join(",", lengths)}]");
            }
            catch (Exception ex) { Console.WriteLine($"  {name}: parse failed {ex.Message}"); }
        }

        // ⑤ 若指定了 id, 把它在三个响应里的原始节点都挖出来
        if (!string.IsNullOrEmpty(targetId))
        {
            var idx = 0;
            foreach (var (name, raw) in new[] { ("default", raw1), ("opusStyle", raw2), ("allFeatures", raw3) })
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (!doc.RootElement.TryGetProperty("data", out var data) ||
                        !data.TryGetProperty("items", out var arr)) continue;
                    foreach (var e in arr.EnumerateArray())
                    {
                        if (!e.TryGetProperty("id_str", out var idEl) || idEl.GetString() != targetId) continue;
                        File.WriteAllText(Path.Combine(output, $"feed-item-{name}.json"),
                            JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true }));
                        Console.WriteLine($"  item dumped for {name}");
                        idx++;
                        break;
                    }
                }
                catch { }
            }
            // 详情原文
            var detail = Get($"https://api.bilibili.com/x/polymer/web-dynamic/v1/detail?id={targetId}&features=itemOpusStyle");
            File.WriteAllText(Path.Combine(output, "detail-item.json"), detail);
            Console.WriteLine($"  detail: {detail.Length} chars");
        }
    }
}
