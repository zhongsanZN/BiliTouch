using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.ViewModels;

namespace BiliTouch.Views.Pages;

/// <summary>
/// 「动态」页: 左栏 = 全部动态 + 关注 UP 列表(有未读时头像左侧带红点),
/// 右栏 = 选中对象的内容, 两档排版(单列动态卡 / 多列卡片墙)由右上角的两枚 chip 切换。
/// 选中态与数据都在 FollowViewModel; 这个 code-behind 只做事件转发、滚动加载与排版 chip 的选中态。
/// </summary>
public partial class FollowPage : UserControl
{
    private const double AutoLoadThreshold = 240;

    /// <summary>看板轮询间隔: "关注的主播开播了"要能自己冒出来, 不能靠用户去点刷新。</summary>
    private static readonly TimeSpan LiveBoardPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>未读红点轮询间隔: 比直播状态慢一拍, 避免每分钟多打一次动态接口。</summary>
    private static readonly TimeSpan UnreadPollInterval = TimeSpan.FromSeconds(45);

    private DispatcherTimer? _liveBoardTimer;
    private DispatcherTimer? _unreadTimer;

    public FollowPage()
    {
        InitializeComponent();
        ContactSelection.Install(ContactsScroll,item=>{if(DataContext is FollowViewModel vm){if(item is FollowUser user)_=vm.SelectUpAsync(user);else if(item is FollowViewModel)_=vm.SelectAllAsync();}});
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // 屏蔽把动态流筛空后补下一页(否则会停在一片空白上)
        FilterService.Instance.Changed += OnFilterApplied;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is FollowViewModel vm)
        {
            FilterService.Instance.Attach(vm.Dynamics);
            FilterService.Instance.Attach(vm.LiveNow);
            await vm.InitAsync();
            if (!IsLoaded) return;
            StartPolling();
            // 检查有没有新动态(有就浮一条「N 条新动态」, 不打断当前列表)
            _ = vm.CheckNewDynamicsAsync();
        }
    }

    /// <summary>屏蔽条件落地后, 等布局量完再判断要不要补一页动态</summary>
    private void OnFilterApplied()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
        {
            if (!IsLoaded || DataContext is not FollowViewModel vm) return;
            var sv = FindScrollViewer(DynamicList);
            if (sv == null) return;
            var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
            var contentShort = sv.ScrollableHeight < sv.ActualHeight * 0.8;
            if (distanceToBottom > AutoLoadThreshold && !contentShort) return;
            await vm.LoadMoreFeedAsync();
        }));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面实例不会跟着导航销毁(MainWindow 缓存), 定时器必须自己停:
        // 否则用户在别的页面上, 这边还在定时打接口。
        _liveBoardTimer?.Stop();
        _unreadTimer?.Stop();
    }

    private void StartPolling()
    {
        _liveBoardTimer ??= new DispatcherTimer { Interval = LiveBoardPollInterval };
        _liveBoardTimer.Tick -= OnLiveBoardTick;
        _liveBoardTimer.Tick += OnLiveBoardTick;
        _liveBoardTimer.Start();

        _unreadTimer ??= new DispatcherTimer { Interval = UnreadPollInterval };
        _unreadTimer.Tick -= OnUnreadTick;
        _unreadTimer.Tick += OnUnreadTick;
        _unreadTimer.Start();
    }

    private void OnLiveBoardTick(object? sender, EventArgs e)
    {
        if (!IsPageActive()) return;
        if (DataContext is FollowViewModel vm) _ = vm.RefreshLiveBoardAsync(showLoading: false);
    }

    private void OnUnreadTick(object? sender, EventArgs e)
    {
        if (!IsPageActive()) return;
        if (DataContext is FollowViewModel vm) _ = vm.RefreshUnreadBadgesAsync();
    }

    /// <summary>最小化到托盘 / 切到别的页面时不再轮询; 重新可见后下一拍自然恢复。</summary>
    private bool IsPageActive()
    {
        var win = Window.GetWindow(this);
        return IsVisible && win != null && win.IsVisible &&
               win.WindowState != WindowState.Minimized;
    }

    // ------------------------------------------------------------ 右栏排版切换(已废弃)

    // ★ 2026-11-04 删除「动态卡 / 卡片墙」两枚 chip 与 UpdateLayoutChips:
    //   用户明确要求"要综合, 不要纯视频, 也不要什么卡片墙", 官方 PC 客户端的动态页
    //   只有一条混合时间线。排版档位(UseCardWall)不再有 UI 入口, 两个 handler
    //   与 chip 同步逻辑一并删掉 —— 留着会让人以为还有别的档位可选。
    //   对应地 FollowViewModel.UseCardWall 仍保留(配置兼容), 但不再参与任何可见性判断。

    /// <summary>点「N 条新动态」: 把攒下的新动态插到列表最前面</summary>
    private void OnApplyNewDynamics(object sender, RoutedEventArgs e)
    {
        if (DataContext is FollowViewModel vm)
        {
            vm.ApplyNewDynamics();
            if (vm.Dynamics.Count > 0) DynamicList.ScrollIntoView(vm.Dynamics[0]);
        }
    }

    /// <summary>
    /// 点「发布动态」(2026-11-04): 开原生发布窗, 发完把新动态直接插到时间线最前面。
    /// ★ 为什么不整页刷新: 刷新会把当前滚动位置和已加载的几页全部丢掉,
    ///   用户刚发完一条就被弹回顶部, 是很差的体验。插一条进去, 位置不动。
    /// ★ 拿不到 id 时退化成刷新(至少让用户看见自己发的), 而不是什么都不做。
    /// </summary>
    private async void OnPublishClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FollowViewModel vm) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("发布动态需要先登录"); return; }

        var window = new DynamicPublishWindow { Owner = Window.GetWindow(this) };
        var accepted = window.ShowDialog();
        if (accepted != true) return;

        var id = window.PublishedId;
        if (id.Length == 0)
        {
            // 服务端没回 id: 只能整页刷新, 但先滚回顶部, 免得用户在中间找不到自己那条
            await vm.RefreshAsync();
            DynamicList.ScrollIntoView(vm.Dynamics.FirstOrDefault());
            return;
        }

        // 拉一次刚发出去的动态, 拿到服务端版(头像/时间/统计都是真值)再插进列表
        var detail = await Svc.Api.GetDynamicDetailAsync(id);
        if (detail.ok && detail.item is { } item)
        {
            vm.Dynamics.Insert(0, item);
            DynamicList.ScrollIntoView(item);
        }
        else
        {
            // 拉不到就先把列表刷新一遍, 下一轮轮询会补上
            await vm.RefreshAsync();
        }
    }

    // ------------------------------------------------------------ 选择

    /// <summary>点「全部动态」: 右栏显示关注 UP 近期的视频投稿</summary>
    private void OnSelectAll(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is FollowViewModel vm) _ = vm.SelectAllAsync();
    }

    /// <summary>点某个 UP: 右栏只显示 TA 的投稿视频</summary>
    private void OnSelectUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (((FrameworkElement)sender).DataContext is not FollowUser u) return;
        if (DataContext is FollowViewModel vm) _ = vm.SelectUpAsync(u);
    }

    // ------------------------------------------------------------ 关注直播看板(2026-10-31)

    /// <summary>点看板标题行: 折叠/展开主播列表</summary>
    private void OnToggleLiveBoard(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FollowViewModel vm) vm.LiveBoardOpen = !vm.LiveBoardOpen;
    }

    /// <summary>点看板里的一个主播: 直接进 TA 的直播间</summary>
    private void OnLiveItemClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (((FrameworkElement)sender).DataContext is not VideoItem item) return;
        if (DataContext is FollowViewModel vm) vm.OpenLive(item);
    }

    // ------------------------------------------------------------ 滚动加载

    /// <summary>右栏: 内容滚动到底自动补下一页(全部动态 = 游标; 单 UP = 页码)</summary>
    private void FeedScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not FollowViewModel vm) return;
        if (e.OriginalSource is not ScrollViewer sv) return;
        if (e.VerticalChange <= 0 && e.ExtentHeightChange <= 0) return;
        if (vm.Error.Length > 0) return;
        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        if (distanceToBottom > AutoLoadThreshold) return;
        _ = vm.LoadMoreFeedAsync();
    }

    /// <summary>左栏: UP 列表滚到底自动补(一页 30 个)</summary>
    private void UpListScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not FollowViewModel vm) return;
        var sv = (ScrollViewer)sender;
        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        if (distanceToBottom > AutoLoadThreshold) return;
        if (vm.Users.Count == 0) return;
        _ = vm.LoadMoreAsync();
    }

    // ------------------------------------------------------------ 左栏条目的右键操作

    private void OnVisitClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            Svc.Navigate?.OpenUserSpace(u.Mid.ToString());
        }
    }

    /// <summary>取消关注该 UP 主, 成功后从列表移除(移除的是选中项时自动回到全部动态)</summary>
    private async void OnUnfollowClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.DataContext is not FollowUser u) return;
        if (DataContext is not FollowViewModel vm) return;

        var (ok, err) = await Svc.Api.FollowUpAsync(u.Mid, follow: false);
        if (ok)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => vm.RemoveUser(u));
            Svc.Toast.Show($"已取消关注 {u.Name}");
        }
        else
        {
            Svc.Toast.Show("取消关注失败: " + (err ?? "未知错误"));
        }
    }

    /// <summary>右键头像 → 预览大图</summary>
    private void OnViewAvatar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            MediaActions.ShowImage(u.Face, u.Name + " 的头像", Window.GetWindow(this));
        }
    }

    /// <summary>右键头像 → 下载到本地</summary>
    private async void OnDownloadAvatar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            await MediaActions.SaveImageAsync(u.Face, u.Name + "_头像");
        }
    }
}
