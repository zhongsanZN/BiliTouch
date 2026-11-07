using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.ViewModels;
using BiliTouch.Views.Pages;

internal static class DynamicModeTests
{
    public static void Run(Action<bool, string> check, string output)
    {
        var vm = new FollowViewModel();
        var post = new DynamicItem { Id = "1", Kind = DynamicKind.Text, Text = "一条文字动态" };
        var video = new DynamicItem { Id = "2", Kind = DynamicKind.Video,
            AuthorName = "示例 UP", Video = new VideoItem { Bvid = "BV1234567890", Title = "测试视频", Author = "示例 UP" } };
        var forward = new DynamicItem { Id = "3", Kind = DynamicKind.Forward, Original = video };
        vm.Dynamics.Add(post);
        vm.Dynamics.Add(video);
        vm.Dynamics.Add(forward);
        vm.Dynamics.Add(new DynamicItem { Id = "4", Kind = DynamicKind.Video });
        check(!vm.VideoOnly && vm.ShowDynamicFeed && !vm.ShowVideoFeed, "默认综合模式");
        check(vm.VideoFeed.Cast<DynamicItem>().SequenceEqual(new[] {video}), "视频模式仅展示可播放的原视频投稿");
        vm.ShowVideoFeedCommand.Execute(null);
        check(vm.VideoOnly && !vm.ShowDynamicFeed && vm.ShowVideoFeed, "切换视频模式");
        check(vm.Dynamics.Count == 4 && vm.FeedTagVideo == "on" && vm.FeedTagAll == "off", "切换保留综合内容且选中态互斥");
        var next = new DynamicItem { Id = "5", Kind = DynamicKind.Video, Video = new VideoItem { Aid = 5, Title = "下一页视频" } };
        vm.Dynamics.Add(next);
        check(vm.VideoFeed.Cast<DynamicItem>().Count() == 2, "翻页追加同步到视频视图");
        vm.Dynamics.Insert(0, new DynamicItem { Id = "6", Kind = DynamicKind.Text });
        check(vm.VideoFeed.Cast<DynamicItem>().Count() == 2, "新发布图文不进入视频模式");
        var oldTitleFilter = FilterService.Instance.TitleKeyword;
        try
        {
            FilterService.Instance.TitleKeyword = "测试视频";
            vm.VideoFeed.Refresh();
            check(vm.VideoFeed.Cast<DynamicItem>().SequenceEqual(new[] {next}), "全局屏蔽与视频筛选叠加");
        }
        finally
        {
            FilterService.Instance.TitleKeyword = oldTitleFilter;
            vm.VideoFeed.Refresh();
        }
        typeof(FollowViewModel).GetProperty(nameof(FollowViewModel.SelectedUp))!
            .SetValue(vm, new FollowUser { Mid = 42, Name = "示例 UP" });
        check(vm.VideoOnly && vm.ShowVideoFeed, "选择单 UP 保留视频模式");
        vm.Dynamics.Remove(video);
        check(vm.VideoFeed.Cast<DynamicItem>().SequenceEqual(new[] {next}), "删除同步到视频视图");
        vm.ShowAllFeedCommand.Execute(null);
        check(vm.ShowDynamicFeed && vm.FeedTagAll == "on", "返回综合模式");
        vm.Dynamics.Clear();
        check(vm.VideoFeed.IsEmpty, "刷新清空视频视图");
        vm.Dynamics.Add(video);
        FilterService.Instance.Attach(vm.Dynamics);
        var page = new FollowPage { DataContext = vm, Width = 1120, Height = 760 };
        page.Measure(new Size(1120, 760));
        page.Arrange(new Rect(0, 0, 1120, 760));
        page.UpdateLayout();
        var all = (Button)page.FindName("AllFeedModeButton");
        var videos = (Button)page.FindName("VideoFeedModeButton");
        var mixedList = (ListBox)page.FindName("DynamicList");
        var videoScroll = (ScrollViewer)page.FindName("VideoFeedScroll");
        check((string)all.Tag == "on" && (string)videos.Tag == "off", "综合按钮绑定正确");
        videos.Command.Execute(null);
        check(mixedList.Visibility == Visibility.Collapsed && videoScroll.Visibility == Visibility.Visible,
            "视频按钮切换列表可见性");
        check((string)videos.Tag == "on" && (string)all.Tag == "off", "视频按钮选中态绑定正确");
        all.Command.Execute(null);
        check(mixedList.Visibility == Visibility.Visible && videoScroll.Visibility == Visibility.Collapsed,
            "综合按钮恢复时间线");
    }
}
