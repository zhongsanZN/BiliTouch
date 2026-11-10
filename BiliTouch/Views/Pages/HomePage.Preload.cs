using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.ViewModels;
using BiliTouch.Views.Controls;

namespace BiliTouch.Views.Pages;

public partial class HomePage
{
    private HomeViewModel? _preparingVm;
    private CancellationTokenSource? _preparationCancellation;
    private readonly SemaphoreSlim _coverPreparationSlots = new(2);
    private readonly HashSet<string> _requestedPreparationCovers = new();
    private readonly Queue<int> _preparationQueue = new();
    private readonly HashSet<int> _queuedPreparation = new();
    private DispatcherTimer? _preparationTimer;
    private bool IsHomeGestureActive => _swipeArmed || _wheelPreviewActive || _activeSnapComplete != null;

    private void StartHomePreparation()
    {
        StopHomePreparation();
        if (Vm is not { } vm) return;
        _preparingVm = vm;
        _preparationCancellation = new CancellationTokenSource();
        vm.IsPublicationDeferred = () => IsLoaded && IsHomeGestureActive;
        vm.PropertyChanged += OnPreparationDataChanged;
        _ = vm.PreloadInitialTabsAsync(CurrentTabIndex);
        QueuePreparedTabs();
    }

    private void StopHomePreparation()
    {
        _preparationCancellation?.Cancel();
        _preparationCancellation?.Dispose(); _preparationCancellation = null;
        if (_preparingVm != null)
        {
            _preparingVm.PropertyChanged -= OnPreparationDataChanged;
            _preparingVm.IsPublicationDeferred = null;
            _preparingVm = null;
        }
        _preparationTimer?.Stop();
        _preparationQueue.Clear(); _queuedPreparation.Clear(); _requestedPreparationCovers.Clear();
    }

    private void OnPreparationDataChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HomeViewModel.RecommendCount) or nameof(HomeViewModel.PopularCount)
            or nameof(HomeViewModel.RankingCount) or nameof(HomeViewModel.LiveCount)
            or nameof(HomeViewModel.HasMyBangumi) or nameof(HomeViewModel.HasBangumiRecommend)) QueuePreparedTabs();
    }

    private void QueuePreparedTabs()
    {
        if (!IsLoaded || _preparationCancellation == null || _preparationCancellation.IsCancellationRequested) return;
        var current = CurrentTabIndex;
        foreach (var index in new[] { current, current + 1, current - 1, 0, 1, 2, 3, 4 }.Distinct())
            if (index is >= 0 and < 5 && HasPreparationData(index) && _queuedPreparation.Add(index)) _preparationQueue.Enqueue(index);
        if (_preparationQueue.Count == 0) return;
        if (_preparationTimer == null)
        {
            _preparationTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(32) };
            _preparationTimer.Tick += (_, _) => PrepareNextTab();
        }
        _preparationTimer.Start();
    }

    private bool HasPreparationData(int index) => _preparingVm is { } vm && (index == 4
        ? vm.MyBangumi.Count + vm.BangumiRecommend.Count > 0
        : ItemsOf(KindOfIndex(index), vm) is System.Collections.ICollection items && items.Count > 0);

    private void PrepareNextTab()
    {
        if (!IsLoaded || _preparationCancellation == null) { _preparationTimer?.Stop(); return; }
        if (IsHomeGestureActive || TabHost.ActualWidth < 1) return;
        if (_preparationQueue.Count == 0) { _preparationTimer?.Stop(); return; }
        var index = _preparationQueue.Dequeue(); _queuedPreparation.Remove(index);
        if (ScrollOf(KindOfIndex(index)) is not TabViewportScrollViewer scroll) return;
        scroll.PreparationSuspended = () => IsHomeGestureActive || !IsLoaded;
        scroll.KeepPreparedLayout();
        ApplyReadyCovers(scroll);
        if (index == CurrentTabIndex) return; // visible covers already own foreground download slots
        var width = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : TabHost.ActualWidth;
        var columns = CardWall.ColumnsFor(width, 0);
        var cardWidth = Math.Max(1, width / Math.Max(1, columns) - CardWall.Gap);
        var aspect = index == 4 ? .75 : 16d / 9;
        var height = Math.Max(1, TabHost.ActualHeight);
        var count = Math.Clamp(columns * ((int)Math.Ceiling(height / (cardWidth / aspect + 98)) + 1), 4, 48);
        var decodeWidth = CoverLoader.DecodeWidthFor(cardWidth, VisualTreeHelper.GetDpi(this).DpiScaleX);
        var covers = index == 4
            ? _preparingVm!.MyBangumi.Concat(_preparingVm.BangumiRecommend).Select(item => item.Cover)
            : ((System.Collections.IEnumerable)ItemsOf(KindOfIndex(index), _preparingVm!)!).Cast<BiliTouch.Models.VideoItem>().Select(item => item.Cover);
        var token = _preparationCancellation.Token;
        foreach (var cover in covers.Where(url => !string.IsNullOrWhiteSpace(url)).Take(count).Distinct())
            if (_requestedPreparationCovers.Add($"{decodeWidth}:{cover}")) _ = PrepareCoverAsync(index, cover, decodeWidth, token);
        TraceInput($"home.prepare tab={index} covers={count}");
    }

    private async Task PrepareCoverAsync(int index, string url, int width, CancellationToken token)
    {
        try
        {
            await _coverPreparationSlots.WaitAsync(token);
            try
            {
                while (IsHomeGestureActive) await Task.Delay(32, token);
                token.ThrowIfCancellationRequested();
                // No per-control interest: an already-started background image
                // remains useful to the cache after navigating away.
                await CoverLoader.LoadAsync(url, width);
                token.ThrowIfCancellationRequested();
                if (!IsHomeGestureActive && ScrollOf(KindOfIndex(index)) is { } scroll) ApplyReadyCovers(scroll);
                else if (_queuedPreparation.Add(index))
                {
                    _preparationQueue.Enqueue(index);
                    _preparationTimer?.Start();
                }
            }
            finally { _coverPreparationSlots.Release(); }
        }
        catch (OperationCanceledException) { }
    }

    private static void ApplyReadyCovers(DependencyObject root)
    {
        if (root is Border border) Cover.TryApplyCached(border);
        if (root is VideoCard card) card.PrepareForTabReveal();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) ApplyReadyCovers(VisualTreeHelper.GetChild(root, i));
    }
}
