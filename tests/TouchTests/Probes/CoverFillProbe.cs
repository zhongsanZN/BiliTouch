using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Helpers;

/// <summary>
/// 验证 Cover.Fill 附加属性是否真的传到了 ImageBrush(2026-11-04)。
///
/// 用户报"视频封面被裁切", 我加了 Cover.Fill="True" 让它走 Stretch=Uniform,
/// 但截图里封面仍然被切。这个探针把链路拆开逐段确认:
///   ① DP 能不能设、读回来是不是 true;
///   ② ApplyDecoded 之后的 Background(ImageBrush) 的 Stretch 到底是多少;
///   ③ 容器比例和图片比例不一致时, Uniform 应该留边、UniformToFill 才裁。
/// 只有 ② 真的等于 Uniform 才说明代码生效; 否则是绑定/时序问题。
/// </summary>
internal static class CoverFillProbe
{
    public static void Run(string output)
    {
        var lines = new List<string>();
        void Line(string s) { lines.Add(s); Console.WriteLine(s); Console.Out.Flush(); }

        var window = new Window { Width = 900, Height = 700, ShowInTaskbar = false };
        window.Show();

        // 用一张已知比例的图(16:9 的横图), 放进一个**竖**框里 ——
        // Uniform 会留边(完整显示), UniformToFill 会裁掉两侧。两者一眼能分开。
        const string Url = "https://i0.hdslb.com/bfs/archive/5129bf9b480c7d316b1709533b5cd75cf50bf605.jpg";

        Border Build(bool fill)
        {
            var inner = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                // ★ 必须有**非 null 的 Background 或子元素**, 否则空 Border 在布局里
                //   ActualWidth 可以是 0(Cover 的 WaitForRealWidth 会一直等下去, 图永远不贴)。
                //   这也是"为什么探针里 Background=null"的直接原因 —— 不是 Fill 没生效。
                Background = Brushes.Transparent,
            };
            Cover.SetFill(inner, fill);
            Cover.SetSource(inner, Url);
            var frame = new Border { Width = 200, Height = 400, ClipToBounds = true, Child = inner };
            return frame;
        }

        var withFill = Build(true);
        var withoutFill = Build(false);
        var host = new StackPanel { Orientation = Orientation.Horizontal };
        host.Children.Add(withFill);
        host.Children.Add(withoutFill);
        window.Content = host;

        // 等 Cover 的异步下载 + 解码 + ApplyDecoded 落地
        for (var i = 0; i < 40; i++) Pump(150);

        void Report(string name, Border frame)
        {
            var inner = frame.Child as Border;
            var brush = inner?.Background as ImageBrush;
            Line($"[{name}] Fill DP = {Cover.GetFill(inner!)}");
            Line($"[{name}] Background = {(brush == null ? "null(还没贴上)" : brush.GetType().Name)}");
            if (brush != null) Line($"[{name}] Stretch = {brush.Stretch}");
        }
        Report("Fill=True ", withFill);
        Report("Fill=False", withoutFill);

        var okFill = ((withFill.Child as Border)?.Background as ImageBrush)?.Stretch == Stretch.Uniform;
        var okDefault = ((withoutFill.Child as Border)?.Background as ImageBrush)?.Stretch == Stretch.UniformToFill;
        Line(okFill ? "PASS Fill=True 走 Uniform(不裁)" : "FAIL Fill=True 没有走 Uniform");
        Line(okDefault ? "PASS 默认走 UniformToFill(裁, 保持首页观感)" : "FAIL 默认没走 UniformToFill");

        window.Close();
        File.WriteAllLines(Path.Combine(output, "cover-fill-report.txt"), lines);
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
