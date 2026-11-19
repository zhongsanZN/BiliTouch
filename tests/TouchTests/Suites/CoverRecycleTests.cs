using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Services;

/// <summary>
/// 复现「首页下滑再回滚，原来加载过的卡片全灰」。
///
/// 根因在 CoverLoader 的兴趣标记：
///   InterestOf()   → slot.Current ??= new LoadInterest();   （只在 null 时新建）
///   AbandonInterest() → slot.Current?.Abandon();           （只作废，不清空）
/// 容器第一次被虚拟化回收时标记被作废，但**槽位一直留着那个作废对象**；
/// 之后每次换源，InterestOf 都把这个"已作废"的标记原样交出去，
/// CoverLoader.LoadAsync 见到 IsAbandoned 立刻返回 null ⇒ 封面永远不再加载 ⇒ 一直灰。
///
/// 本测试模拟"同一个容器被回收后再复用"：连续换三次源，第三次必须还能出图。
/// </summary>
internal static class CoverRecycleTests
{
    private static void Pump(int ms = 150)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static string MakeCachedCover(string name)
    {
        var url = "https://example.invalid/" + name + ".png";
        var file = Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(url) + ".jpg");
        if (!File.Exists(file))
        {
            var px = new byte[320 * 180 * 4];
            for (var i = 0; i < px.Length; i += 4) { px[i] = 30; px[i + 1] = 140; px[i + 2] = 210; px[i + 3] = 255; }
            var bmp = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, px, 320 * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(file);
            enc.Save(fs);
        }
        return url;
    }

    private static bool HasCover(Border b) => b.Background is ImageBrush;

    public static void Run(Action<bool, string> check, string artifacts)
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        var url1 = MakeCachedCover("recycle-a");
        var url2 = MakeCachedCover("recycle-b");
        var url3 = MakeCachedCover("recycle-c");

        var border = new Border { Width = 320, Height = 180, Background = Brushes.Gray };
        var window = new Window { Width = 400, Height = 240, Content = border, ShowActivated = false, WindowStyle = WindowStyle.None };
        window.Show();
        Pump(250);

        try
        {
            Cover.SetSource(border, url1); Pump(300);
            check(HasCover(border), "first load shows a cover");

            // 第一次换源 = 容器被回收一次（AbandonInterest 会被调用）
            Cover.SetSource(border, url2); Pump(300);
            check(HasCover(border), "second load after one recycle still shows a cover");

            // 第二次换源：如果兴趣标记没被换新，从这一步开始永远拿不到图
            Cover.SetSource(border, url3); Pump(300);
            check(HasCover(border), "third load (two recycles later) still shows a cover");

            Cover.SetSource(border, url1); Pump(300);
            check(HasCover(border), "switching back to an earlier url still shows a cover");

            var slotField = typeof(CoverLoader).GetField("Interests", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            check(slotField != null, "interest table is present (guard against refactor drift)");
        }
        catch (Exception ex)
        {
            check(false, "cover recycle probe threw: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            try { window.Close(); } catch { }
            Pump(150);
        }
    }
}
