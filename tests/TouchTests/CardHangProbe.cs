using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Helpers;
using BiliDesk.Views.Controls;

/// <summary>
/// 用**真实卡死样本的 JSON** 离线复现, 并且逐块拆解 DynamicFeedCard(2026-11-04)。
///
/// 为什么要这个探针:
///   · FeedHangProbe 只能告诉我们"哪一条卡死", 那条数据每次都可能不同(接口在变);
///   · 把样本落成 JSON 夹具后, 这里可以**不联网**反复跑, 也不受线上数据变化影响;
///   · 拆解方式: 先整卡复现, 再依次关掉热评预取 / 投票预取 / 富文本, 看哪一块是触发器。
/// </summary>
internal static class CardHangProbe
{
    public static void Run(string output)
    {
        var fixture = Path.Combine(output, "hang-item.json");
        if (!File.Exists(fixture))
        {
            Console.WriteLine("CARD no fixture at " + fixture + " (先跑 --live-hang)");
            return;
        }

        DynamicItem? item;
        using (var doc = JsonDocument.Parse(File.ReadAllText(fixture)))
            item = DynamicParser.Parse(doc.RootElement);
        if (item == null) { Console.WriteLine("CARD fixture did not parse"); return; }

        Console.WriteLine($"CARD fixture: kind={item.Kind} text={item.Text.Length} pics={item.Pictures.Count} " +
                          $"video={(item.Video != null ? item.Video.Bvid : "-")} cover={(item.Video?.Cover ?? "-")} " +
                          $"rich={item.RichNodes.Count} vote={(item.Vote != null ? item.Vote.Id : "-")}");
        Console.Out.Flush();

        var window = new Window { Width = 1000, Height = 760, ShowInTaskbar = false };
        window.Show();
        var results = new List<string>();

        void Case(string name, Func<FrameworkElement> build)
        {
            var host = new Border();
            window.Content = host;
            var element = build();
            host.Child = element;
            Pump();
            var ok = TryPump(1500);
            results.Add((ok ? "ok   " : "HANG ") + name);
            Console.WriteLine(results[^1]);
            Console.Out.Flush();
            host.Child = null;
            if (!ok)
            {
                File.WriteAllLines(Path.Combine(output, "card-hang-report.txt"), results);
                throw new OperationCanceledException("hang: " + name);
            }
            Pump();
        }

        try
        {
            // ★ 先分离"构造"和"布局": 这两件事出问题的修法完全不同。
            //   构造卡死 = 模板解析/XAML 层; 布局卡死 = Measure/Arrange 层。
            Case("construct-only", () =>
            {
                var card = new DynamicFeedCard { DataContext = item };
                return new Border { Child = card, Visibility = Visibility.Collapsed };
            });

            // 只做 Measure, 不挂到可视树 —— 排除父级约束的影响
            Case("measure-only", () =>
            {
                var card = new DynamicFeedCard { DataContext = item };
                card.Measure(new Size(640, double.PositiveInfinity));
                Console.WriteLine($"  measured to {card.DesiredSize}");
                return new Border { Child = card, Visibility = Visibility.Collapsed };
            });

            // 挂到树里让它真正布局
            Case("full-layout", () => new DynamicFeedCard { DataContext = item });

            // ★ 决定性测试: 把视频封面那个 Height=220 改成不限高(等于去掉那个固定高度),
            //   若不再卡死, 就是"固定高度 + 内容自适应宽度"这对组合触发了重复测量。
            Case("full-layout-video-no-fixed-height", () =>
            {
                var card = new DynamicFeedCard { DataContext = item };
                var patched = PatchFixedHeights(card);
                Console.WriteLine($"  patched {patched} fixed heights");
                return card;
            });
        }
        catch (OperationCanceledException ex)
        {
            results.Add("STOPPED: " + ex.Message);
            Console.WriteLine(results[^1]);
        }
        finally
        {
            window.Close();
            File.WriteAllLines(Path.Combine(output, "card-hang-report.txt"), results);
            Console.WriteLine("CARD report written; no network used for rendering");
        }
    }

    /// <summary>
    /// 把卡里所有"固定 Height 且宽度自适应"的 Border 解除高度限制(2026-11-04 诊断用)。
    /// 返回改了几个。目的只有一个: 验证这个组合是不是自旋源。
    /// </summary>
    private static int PatchFixedHeights(DependencyObject root)
    {
        var patched = 0;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border { Height: > 0 } border && double.IsNaN(border.Width))
            {
                border.Height = double.NaN;
                patched++;
            }
            patched += PatchFixedHeights(child);
        }
        return patched;
    }

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

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
