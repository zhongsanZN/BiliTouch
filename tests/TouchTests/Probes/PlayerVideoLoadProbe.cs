using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Views;

/// <summary>
/// 视频播放中 vs 未播放，同一个播放器窗口的评论滚动帧表现。
///
/// ★★★ 为什么必须单独一个探针（2026-11-09）：
///   现有的 --player-comments-scroll 用**真实 PlayerWindow**，但**没有播放视频**。
///   而用户报的现象是"无论分栏还是网页布局，详情页都比上游卡" —— 两种布局的共同背景
///   恰好就是"视频正在播"。没有视频的那份测量里，VLC 的 vout 不存在、弹幕时钟不跑、
///   合成侧也没有持续的画面更新，于是它测出来的 109fps 与用户手上"比上游卡"的体感对不上。
///   这个探针把那条缺口补上：**同一棵树、同一份评论、同一套手势**，只切"视频播不播"。
///
/// ★ 判据仍然用 PresentedScrollProbe（截屏比对真实上屏帧），不是 UI 线程自报的帧率 ——
///   自报帧率会把"UI 很快但合成被卡住"的情况算成流畅，那正是要区分的场景。
/// </summary>
internal static class PlayerVideoLoadProbe
{
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    public static void Run(Action<bool, string> check)
    {
        var video = LocateVideo();
        if (video == null)
        {
            Console.WriteLine("VIDEOLOAD skipped: no local fixture video");
            check(true, "video-load probe skipped without a local fixture");
            return;
        }

        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        PlayerWindow? player = null;
        try
        {
            player = new PlayerWindow { Width = 1320, Height = 860 };
            player.Show();
            Pump(300);

            var comments = (ItemsControl)player.FindName("CommentList");
            comments.ItemsSource = Enumerable.Range(1, 80).Select(i => new CommentItem
            {
                Rpid = i, UserName = "评论用户 " + i,
                Content = "滚动跟手测试。连续快速滚轮应保留完整距离，反向和点击立即接管。",
                LikeCount = i
            }).ToArray();
            ((StackPanel)player.FindName("IntroPanel")).Visibility = Visibility.Collapsed;
            ((StackPanel)player.FindName("CommentsPanel")).Visibility = Visibility.Visible;
            Pump(300);

            var scroll = (ScrollViewer)player.FindName("InfoScroll");

            // ---- ① 不播视频：基线（与现有探针的场景一致）----
            Console.WriteLine("=== ① 未播放视频 ===");
            PlayerCommentsScrollTests.Cost(scroll, "no-video");
            PlayerCommentsScrollTests.ScrollBench(scroll, comments, "no-video");
            Pump(200);

            // ---- ② 开始播放本地视频：真实使用的背景负载 ----
            Console.WriteLine("=== ② 视频播放中 ===");
            player.PlayLocalFile(video);
            // 等首帧真的出来：vout 起来、解码在跑，这段等待不可省 ——
            // 太早测等于又回到了"没有视频"的场景。
            Pump(2500);

            // ★ 必须**重新摆一次 tab 状态 + 重新填评论数据**：
            //   PlayLocalFile 走的是换片流程，会做两件事（都是**正确的产品行为**）：
            //     ① 把 tab 复位到「简介」，于是 CommentsPanel 被收起；
            //     ② 清掉 CommentList.ItemsSource（:1133 那段注释写得很清楚：
            //        "原来没清, 于是同一个播放器窗口换视频之后评论 tab 里一直挂着上一条视频的评论"）。
            //   探针要测的是"视频在播时滚评论区"，所以得把这两件事自己补回去。
            //   第一次跑栽在 ①，第二次跑栽在 ②（scrollable=0）—— 都不是产品问题。
            ((StackPanel)player.FindName("IntroPanel")).Visibility = Visibility.Collapsed;
            ((StackPanel)player.FindName("CommentsPanel")).Visibility = Visibility.Visible;
            comments.ItemsSource = Enumerable.Range(1, 80).Select(i => new CommentItem
            {
                Rpid = i, UserName = "评论用户 " + i,
                Content = "滚动跟手测试。连续快速滚轮应保留完整距离，反向和点击立即接管。",
                LikeCount = i
            }).ToArray();
            Pump(500);

            var videoView = player.FindName("VideoView") as FrameworkElement;
            Console.WriteLine($"VIDEOLOAD surface={(videoView?.ActualWidth ?? 0):F0}x{(videoView?.ActualHeight ?? 0):F0}" +
                              $" scrollable={scroll.ScrollableHeight:F0}");

            PlayerCommentsScrollTests.Cost(scroll, "video-playing");
            PlayerCommentsScrollTests.ScrollBench(scroll, comments, "video-playing");
            Pump(200);

            // ---- ③ 换一个**全新窗口**（不放视频）复测，排除"同进程漂移" ----
            //   ★ 为什么不是在同一窗口里"停掉视频"：PlayerWindow 没有公开的 Stop，
            //     PlayLocalFile 对空路径是直接 return。而换一个干净窗口本身也更严谨 ——
            //     如果 ③ 和 ① 接近，就说明差异确实来自视频，而不是预热/GC 的漂移。
            Console.WriteLine("=== ③ 新窗口（无视频）复测 ===");
            player.Close();
            Pump(400);
            player = new PlayerWindow { Width = 1320, Height = 860 };
            player.Show();
            Pump(300);
            comments = (ItemsControl)player.FindName("CommentList");
            comments.ItemsSource = Enumerable.Range(1, 80).Select(i => new CommentItem
            {
                Rpid = i, UserName = "评论用户 " + i,
                Content = "滚动跟手测试。连续快速滚轮应保留完整距离，反向和点击立即接管。",
                LikeCount = i
            }).ToArray();
            ((StackPanel)player.FindName("IntroPanel")).Visibility = Visibility.Collapsed;
            ((StackPanel)player.FindName("CommentsPanel")).Visibility = Visibility.Visible;
            Pump(400);
            scroll = (ScrollViewer)player.FindName("InfoScroll");
            Console.WriteLine($"VIDEOLOAD fresh scrollable={scroll.ScrollableHeight:F0}");
            PlayerCommentsScrollTests.Cost(scroll, "fresh-no-video");
            PlayerCommentsScrollTests.ScrollBench(scroll, comments, "fresh-no-video");

            check(true, "video-load probe completed");
        }
        finally
        {
            player?.Close();
            Pump(200);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    /// <summary>
    /// 找一个本地 mp4 当夹具。找不到就跳过，不让探针变成环境依赖。
    /// ★ 不用固定的 "..\..\..\..\" 层数：bin\Release\net10.0-windows 到仓库根差五层，
    ///   而不同的构建配置/输出路径会变；逐级向上找 artifacts\probes\rateaudio 更稳。
    /// </summary>
    private static string? LocateVideo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir != null && depth < 8; depth++, dir = dir.Parent)
        {
            var probeDir = Path.Combine(dir.FullName, "artifacts", "probes", "rateaudio");
            if (!Directory.Exists(probeDir)) continue;
            var hit = Directory.EnumerateFiles(probeDir, "*.mp4", SearchOption.AllDirectories)
                               .OrderByDescending(f => new FileInfo(f).Length)
                               .FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }
}
