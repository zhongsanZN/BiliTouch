using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Views.Controls;

/// <summary>
/// 查"动态正文不显示, 点进详情再返回才显示"(2026-11-04 用户报)。
///
/// 现象(用户截图对照):
///   · 初次进动态页: 卡片上**没有正文**, 日期显示短格式;
///   · 点进详情再返回: 正文出现, 日期变成长格式("2026年10月19日 21:04");
///   · 切到别的 UP 再切回来: 正文又不见了。
///
/// 最后一条是决定性的 —— 切 UP 会**重新拉列表**, 拿到的是新的 DynamicItem 对象;
/// 而"点进详情"是在**同一个对象**上跑 RefreshFrom。所以差异只可能来自
/// "列表接口给的字段" vs "详情接口给的字段" 不一致。
///
/// 本探针把两者的差异逐字段列出来, 重点是:
///   ① desc.text 为空但 desc.rich_text_nodes 有内容时, 正文会不会丢;
///   ② 我的 DynamicRichText 的可见性绑的是 HasText(只看 Text), 会不会把它藏掉。
/// </summary>
internal static class DynamicTextProbe
{
    public static void Run(string output)
    {
        var sessionFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Local", "BiliDesk", "session.json");
        if (!File.Exists(sessionFile)) { Console.WriteLine("TEXT unavailable: no session"); return; }
        var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(sessionFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (session == null || string.IsNullOrEmpty(session.SessData))
        { Console.WriteLine("TEXT unavailable: session incomplete"); return; }
        typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session, session);
        foreach (var pair in new[]{("_buvid3",session.Buvid3),("_buvid4",session.Buvid4)})
            typeof(ApiClient).GetField(pair.Item1, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Svc.Api, pair.Item2);

        var feed = Svc.Api.GetDynamicFeedFullAsync();
        Wait(feed);
        if (!feed.Result.ok || feed.Result.items is not { Count: > 0 } items)
        { Console.WriteLine("TEXT feed failed: " + feed.Result.err); return; }

        var lines = new List<string>();
        void Line(string s) { lines.Add(s); Console.WriteLine(s); Console.Out.Flush(); }

        Line($"TEXT items={items.Count}");
        var suspicious = new List<DynamicItem>();
        for (var i = 0; i < items.Count; i++)
        {
            var d = items[i];
            Line($"[{i}] kind={d.Kind} id={d.Id} text={d.Text.Length} rich={d.RichNodes.Count} " +
                 $"hasText={d.HasText} pics={d.Pictures.Count} pubTime='{d.PubTimeText}' pubTs={d.PubTs} " +
                 $"article={d.ArticleTitle.Length} video={(d.Video != null ? "y" : "n")}");
            // 可疑: 正文为空但有富文本节点(或反过来有正文但节点里内容更多)
            if (!d.HasText && d.RichNodes.Count > 0) suspicious.Add(d);
        }

        // ★ 核心对照: 列表 vs 详情
        Line("");
        Line("=== 列表 vs 详情 字段对照 ===");
        var compare = suspicious.Count > 0 ? suspicious : items.Take(6).ToList();
        foreach (var d in compare)
        {
            var detail = Svc.Api.GetDynamicDetailAsync(d.Id);
            Wait(detail);
            if (!detail.Result.ok || detail.Result.item is not { } fresh)
            {
                Line($"  {d.Id}: 详情拉取失败 {detail.Result.err}");
                continue;
            }
            Line($"  {d.Id}:");
            Line($"    列表: text={d.Text.Length} rich={d.RichNodes.Count} pubTime='{d.PubTimeText}' pubTs={d.PubTs}");
            Line($"    详情: text={fresh.Text.Length} rich={fresh.RichNodes.Count} pubTime='{fresh.PubTimeText}' pubTs={fresh.PubTs}");
            if (d.Text.Length != fresh.Text.Length || d.RichNodes.Count != fresh.RichNodes.Count)
            {
                Line($"    ★ 不一致! 列表正文前 60 字: '{Head(d.Text)}'");
                Line($"    ★ 详情正文前 60 字: '{Head(fresh.Text)}'");
                Line($"    ★ 列表 rich 前 3 个: {Nodes(d)}");
                Line($"    ★ 详情 rich 前 3 个: {Nodes(fresh)}");
            }
        }

        // ★ 渲染层: 正文为空但节点有内容时, BodyText 会不会被藏掉
        Line("");
        Line("=== 渲染层检查 ===");
        var window = new Window { Width = 900, Height = 700, ShowInTaskbar = false };
        window.Show();
        var victim = suspicious.FirstOrDefault();
        if (victim != null)
        {
            window.Content = new DynamicFeedCard { DataContext = victim };
            for (var i = 0; i < 8; i++) Pump();
            var body = Descendants<DynamicRichText>(window).FirstOrDefault();
            if (body == null) Line("  找不到 DynamicRichText(模板变了?)");
            else
            {
                Line($"  可疑条目 {victim.Id}: BodyText.Visibility={body.Visibility} " +
                     $"Inlines={body.Inlines.Count} HasText={victim.HasText} Rich={victim.RichNodes.Count}");
                if (body.Visibility != Visibility.Visible && victim.RichNodes.Count > 0)
                    Line("  ★ 复现: 有富文本节点但正文为空 → 整块被 HasText 藏掉");
            }
        }
        else
        {
            Line("  本轮列表里没有'正文空 + 节点有内容'的条目");
        }
        window.Close();

        File.WriteAllLines(Path.Combine(output, "dynamic-text-report.txt"), lines);
    }

    private static string Head(string text) =>
        text.Length <= 60 ? text : text[..60] + "…";

    private static string Nodes(DynamicItem d) =>
        d.RichNodes.Count == 0 ? "(空)"
        : string.Join(" | ", d.RichNodes.Take(3).Select(n => $"{n.Type}:{Head(n.Text)}"));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T m) yield return m;
            foreach (var n in Descendants<T>(child)) yield return n;
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Wait(Task task)
    {
        var limit = DateTime.UtcNow.AddSeconds(25);
        while (!task.IsCompleted && DateTime.UtcNow < limit) Pump();
        if (!task.IsCompleted) throw new TimeoutException("read timed out");
        task.GetAwaiter().GetResult();
    }
}
