using BiliTouch.Models;
﻿using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.ViewModels;

namespace BiliTouch.Views.Pages;

/// <summary>
/// 「稍后再看」页(2026-09-28 新增): 卡片墙 + 编辑模式(批量移出/清空)。
/// 页面实例被 MainWindow 缓存, 每次切进来 Loaded 都重拉一次 —— 清单会被别的端改,
/// 而且本页只有一次请求, 重拉没有成本压力。
/// </summary>
public partial class WatchLaterPage : UserControl
{
    private WatchLaterViewModel? Vm => DataContext as WatchLaterViewModel;

    public WatchLaterPage()
    {
        InitializeComponent();
        ModuleGestures.Install(this, async () =>
        {
            if(DataContext is WatchLaterViewModel vm) await vm.LoadAsync();
        }, item =>
        {
            if(DataContext is WatchLaterViewModel vm && item is VideoItem video && !vm.Busy)
            { if(!vm.IsEditMode)vm.ToggleEditCommand.Execute(null);video.IsSelected=true; }
        });
        Loaded += async (_, _) =>
        {
            FilterService.Instance.Changed -= OnFilterApplied;
            FilterService.Instance.Changed += OnFilterApplied;
            if (Vm != null && IsVisible)
            {
                FilterService.Instance.Attach(Vm.Items);
                if (Vm.Items.Count == 0 || Vm.IsDirty)
                {
                    Vm.IsDirty = false;
                    await Vm.LoadAsync();
                }
            }
        };
        Unloaded += (_, _) => FilterService.Instance.Changed -= OnFilterApplied;
    }

    private int _autoFillStreak;
    private int _lastAutoFillVisibleCount = -1;

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange > 2) _autoFillStreak = 0;
        if (sender is ScrollViewer sv) MaybeAutoLoad(sv);
    }

    /// <summary>内容接近底部 / 不足一屏就补下一页。滚动与"屏蔽后补内容"共用这一份判断。</summary>
    private void MaybeAutoLoad(ScrollViewer? sv)
    {
        if (sv == null || TouchSupport.IsTouchPanning(sv)) return;
        if (Vm is not { HasMore: true } vm) return;
        if (vm.IsFilling) return;

        var nearBottom = sv.ScrollableHeight - sv.VerticalOffset < 400;
        var contentShort = sv.ScrollableHeight < sv.ActualHeight * 0.8;
        if (!nearBottom && !contentShort) return;

        var visibleCount = vm.TotalCount;
        if (visibleCount != _lastAutoFillVisibleCount)
        {
            _lastAutoFillVisibleCount = visibleCount;
            _autoFillStreak = 0;
        }
        if (_autoFillStreak >= 5) return;

        _autoFillStreak++;
        vm.ShowNextPage();
    }

    /// <summary>屏蔽条件落地后, 等布局量完再补内容</summary>
    private void OnFilterApplied()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (IsLoaded) MaybeAutoLoad(ListScroll);
        }));
    }

    /// <summary>返回「我的」页(入口在那里)</summary>
    private void OnBackToMineClick(object sender, RoutedEventArgs e)
        => BiliTouch.Services.SvcWindow.Main?.OpenMineOverview();
}
