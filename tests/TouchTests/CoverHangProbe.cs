using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliDesk;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Helpers;
using BiliDesk.Views.Controls;

/// <summary>
/// 最小复现: 只放一个视频附加卡的封面, 看是不是 Cover.Source 在无宽度约束下自旋(2026-11-04)。
///
/// 背景: 动态流里任何一条**视频动态**挂载就会把 UI 线程打满, 而首页同样的
/// VideoCard + Cover.Source 是正常的。差别在约束:
///   · 首页卡片墙给了明确宽度(CardWall.ItemWidth), 封面高度也是算好的;
///   · 动态卡里的视频封面写的是 Height="220" 且**宽度跟随内容** ——
///     Border 在 StackPanel 里宽度不确定, Cover.Source 又按 ActualWidth 决定解码宽度,
///     于是"量宽度 → 解码 → 换图 → 重新量"可以形成闭环。
/// 本探针把这个假设拆成几个独立用例, 逐个测是否能在一秒内完成布局。
/// </summary>
internal static class CoverHangProbe
{
    public static void Run(string output)
    {
        var results = new List<string>();
        var window = new Window { Width = 1000, Height = 700, ShowInTaskbar = false };
        window.Show();

        void Case(string name, Func<UIElement> build)
        {
            var host = new Border();
            window.Content = host;
            var element = build();
            host.Child = element;
            var ok = TryPump(1200);
            results.Add((ok ? "ok   " : "HANG ") + name);
            Console.WriteLine(results[^1]);
            Console.Out.Flush();
            host.Child = null;
            if (!ok)
            {
                // 卡住就停下来, 后面也不必再测
                throw new OperationCanceledException("hang: " + name);
            }
            Pump();
        }

        try
        {
            // 1. 封面无固定宽度(模拟动态卡里的写法)
            Case("cover-height-only", () =>
            {
                var b = new Border { Height = 220 };
                Cover.SetSource(b, "https://i0.hdslb.com/bfs/archive/0000000000000000000000000000000000000000.jpg");
                return b;
            });

            // 2. 封面固定宽度(模拟首页写法)
            Case("cover-fixed-width", () =>
            {
                var b = new Border { Width = 236, Height = 132 };
                Cover.SetSource(b, "https://i0.hdslb.com/bfs/archive/0000000000000000000000000000000000000000.jpg");
                return b;
            });

            // 3. 空 URL(不该有任何解码)
            Case("cover-empty-url", () =>
            {
                var b = new Border { Height = 220 };
                Cover.SetSource(b, "");
                return b;
            });

            // 4. 只有 Grid + 固定高度 Border, 不挂封面
            Case("no-cover", () =>
            {
                var grid = new Grid();
                grid.Children.Add(new Border { Height = 220 });
                return grid;
            });
        }
        catch (OperationCanceledException ex)
        {
            results.Add("STOPPED at " + ex.Message);
        }
        finally
        {
            window.Close();
            File.WriteAllLines(Path.Combine(output, "cover-hang-report.txt"), results);
        }
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
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
