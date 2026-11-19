using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Models;
using BiliTouch.ViewModels;
using BiliTouch.Views;

/// <summary>
/// 复现"点进评论详情再返回后, 触屏拖不动评论列表"(2026-11-04)。
///
/// 用户在真机上报: 视频播放页 → 评论 → 点某条评论进详情 → 返回 → 评论列表展开态,
/// **手指上下拖不动**, 但滚轮/触控板可能还正常。
///
/// 这个探针不测"手感", 只测一条能自动断言的因果链:
///   1. 进出详情前后, InfoScroll 的 PanningMode 是否被改成了 None;
///   2. CommentList 切回 Visible 之后, 外层 ScrollViewer 的 ExtentHeight 是否还是 0
///      (高度为 0 时 PanningMode 即使正确, 也滚不动 —— ScrollableHeight <= 0);
///   3. PageHost 是否残留了一个固定的 Height, 把内层内容压成 0。
///
/// 判定只看数字, 不依赖合成触摸(那个在无头环境里本来就推不动 WPF 原生平移)。
/// </summary>
internal static class CommentSwipeProbe
{
    public static void Run(PlayerWindow player, string output)
    {
        var report = new List<string>();
        void Line(string text)
        {
            report.Add(text);
            Console.WriteLine(text);
            Console.Out.Flush();
        }

        var infoScroll = Find<ScrollViewer>(player, "InfoScroll");
        var commentList = Find<ItemsControl>(player, "CommentList");
        var detailPanel = Find<FrameworkElement>(player, "CommentDetailPanel");
        var pageHost = Find<FrameworkElement>(player, "PageHost");
        var commentsPanel = Find<FrameworkElement>(player, "CommentsPanel");
        var infoPanel = Find<FrameworkElement>(player, "InfoPanel");
        if (infoScroll == null || commentList == null || detailPanel == null ||
            pageHost == null || commentsPanel == null || infoPanel == null)
        {
            Line("PROBE missing elements — 结构变了, 先核对 x:Name");
            return;
        }

        void Snapshot(string stage)
        {
            Line($"--- {stage} ---");
            Line($"  InfoScroll: PanningMode={infoScroll.PanningMode} PanningRatio={infoScroll.PanningRatio:0.00} " +
                 $"Offset={infoScroll.VerticalOffset:0} Scrollable={infoScroll.ScrollableHeight:0} " +
                 $"Extent={infoScroll.ExtentHeight:0} Viewport={infoScroll.ViewportHeight:0} " +
                 $"ActualH={infoScroll.ActualHeight:0}");
            Line($"  CommentList: Visibility={commentList.Visibility} Items={commentList.Items.Count} ActualH={commentList.ActualHeight:0}");
            Line($"  CommentDetailPanel: Visibility={detailPanel.Visibility}");
            Line($"  PageHost: Height={(double.IsNaN(pageHost.Height) ? "NaN(auto)" : pageHost.Height.ToString("0"))} " +
                 $"Actual={pageHost.ActualHeight:0}");
            Line($"  CommentsPanel: Visibility={commentsPanel.Visibility} ActualH={commentsPanel.ActualHeight:0}");
            Line($"  InfoPanel: Actual={infoPanel.ActualWidth:0}x{infoPanel.ActualHeight:0}");
        }

        // ---- 切到评论 tab ----
        var tabComments = Find<RadioButton>(player, "TabComments");
        if (tabComments == null) { Line("PROBE no TabComments"); return; }
        tabComments.IsChecked = true;
        Pump(700);
        Snapshot("评论 tab 已打开");

        // ---- 造几条评论并展开(模拟"评论呈展开状态") ----
        // ★ 播放器的评论直接挂在 CommentList.ItemsSource 上(没有独立 VM 集合),
        //   所以这里直接替换 ItemsSource —— 与"真实加载完成后列表是满的"效果一致。
        var seeded = new System.Collections.ObjectModel.ObservableCollection<CommentItem>();
        for (var i = 0; i < 12; i++)
        {
            var c = new CommentItem
            {
                Rpid = 900000 + i,
                UserName = $"探针用户{i}",
                Content = $"这是第 {i} 条用于验证触摸滚动的评论, 正文要够长才会占多行, " +
                          $"这样列表才有可滚动的高度, 否则 ExtentHeight 会小于视口。",
                LikeCount = i,
                ReplyCount = i % 3,
            };
            if (i % 3 == 0)
                c.Replies.Add(new CommentItem { Rpid = 950000 + i, UserName = "楼中楼", Content = "展开的回复" });
            seeded.Add(c);
        }
        commentList.ItemsSource = seeded;
        Pump(800);
        Snapshot("评论已填充(展开态)");

        var baseline = infoScroll.PanningMode;
        var baselineExtent = infoScroll.ExtentHeight;

        // ---- 进详情 ----
        Call(player, "ShowCommentDetail", seeded[0], seeded[0]);
        Pump(900);
        Snapshot("已进入评论详情");

        // ---- 返回列表 ----
        Call(player, "CloseCommentDetail");
        Pump(900);
        Snapshot("已返回评论列表");

        // ---- 断言 ----
        if (infoScroll.PanningMode == PanningMode.None && baseline != PanningMode.None)
            Line($"FAIL PanningMode 被清成 None(基线 {baseline}) —— 这就是拖不动的原因");
        else
            Line($"PASS PanningMode 保持 {infoScroll.PanningMode}");

        if (infoScroll.ExtentHeight <= infoScroll.ViewportHeight && baselineExtent > infoScroll.ViewportHeight)
            Line($"FAIL 返回后 ExtentHeight 塌了({baselineExtent:0} → {infoScroll.ExtentHeight:0})");
        else
            Line($"PASS 可滚动高度仍在 Extent={infoScroll.ExtentHeight:0} Viewport={infoScroll.ViewportHeight:0}");

        if (infoScroll.ScrollableHeight <= 0)
            Line("FAIL ScrollableHeight=0 —— 没有可滚动空间, 手指怎么拖都不动");
        else
            Line($"PASS ScrollableHeight={infoScroll.ScrollableHeight:0}");

        // ---- 真滚一下, 验证"能不能动" ----
        var before = infoScroll.VerticalOffset;
        infoScroll.ScrollToVerticalOffset(before + 200);
        Pump(300);
        var after = infoScroll.VerticalOffset;
        if (Math.Abs(after - before) < 1 && infoScroll.ScrollableHeight > 0)
        {
            // 滚不动且确实有空间 —— 但这是程序化滚动, 不受 PanningMode 影响;
            // 若这里都不动, 说明约束层面就坏了
            Line($"FAIL 程序化滚动也无效({before:0} → {after:0})");
        }
        else
        {
            Line($"PASS 程序化滚动有效({before:0} → {after:0})");
        }

        File.WriteAllLines(Path.Combine(output, "comment-swipe-report.txt"), report);
    }

    private static T? Find<T>(FrameworkElement root, string name) where T : FrameworkElement
        => root.FindName(name) as T ?? Descendants<T>(root).FirstOrDefault(e => e.Name == name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Call(object target, string name, params object[] args)
        => target.GetType().GetMethod(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public)!.Invoke(target, args);

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
