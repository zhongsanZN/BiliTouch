using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.ViewModels;
using BiliTouch.Views.Controls;

namespace BiliTouch.Views.Pages;

public partial class MinePage : UserControl
{
    private const double SwipePageRatio = 0.50;
    private const double SwipeEdgeResist = 0.35;

    public Func<PageKey, FrameworkElement>? SectionFactory { get; set; }
    private readonly Dictionary<int, FrameworkElement> _sections = new();
    private readonly PageKey[] _keys = { PageKey.Mine, PageKey.History, PageKey.Favorites, PageKey.WatchLater, PageKey.Cache };
    internal int CurrentTabIndex { get; private set; }
    private readonly TranslateTransform _slide = new();
    private Point _start;
    private bool _armed, _horizontal, _decided, _suppressClick;
    private int _pendingWheel;
    private double _dx;
    private VideoCard? _gestureCard;
    private ScrollViewer? _frozen;
    private PanningMode _panning;
    private double _frozenOffset;
    private FrameworkElement? _peerSection;
    private Action? _activeSnapComplete, _cancelActiveSnap;
    private int _snapTarget = -1;
    private double _wheelContactDx;
    private double _touchDragOrigin, _wheelDragOrigin;
    private bool _wheelPreviewActive;

    private double TakeOverSnap()
    {
        var target = _snapTarget; var previous = CurrentTabIndex;
        _cancelActiveSnap?.Invoke();
        if (target >= 0 && target != previous)
        {
            var width = Math.Max(1, SectionHost.ActualWidth);
            var x = _slide.X + (target - previous) * width;
            ClearPeerSection();
            Tabs[target].IsChecked = true;
            EnsurePeerSectionVisible(previous, previous < target ? -width : width);
            _slide.X = x;
            MineTabs?.PreviewSwipeProgress(target, previous, Math.Abs(x) / width);
        }
        return _slide.X;
    }

    public MinePage()
    {
        InitializeComponent();
        SectionHost.RenderTransform = _slide;
        OverviewTab.IsChecked = true;
        SectionHost.PreviewTouchDown += (_, e) => SwipeDown(e.GetTouchPoint(null).Position, e.OriginalSource as DependencyObject);
        SectionHost.PreviewTouchMove += (_, e) => { if (SwipeMove(e.GetTouchPoint(null).Position)) e.Handled = true; };
        SectionHost.PreviewTouchUp += (_, e) => { if (SwipeUp()) e.Handled = true; };
        SectionHost.PreviewMouseLeftButtonUp += (_, e) => { if (_suppressClick) { e.Handled = true; _suppressClick = false; } };
        Unloaded += (_, _) => { CompleteActiveSnap(); RestorePanning(); ClearPeerSection(); _armed = false; };
    }

    private RadioButton[] Tabs => new[] { OverviewTab, HistoryTab, FavoritesTab, LaterTab, CacheTab };

    private static TranslateTransform PeerTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        element.RenderTransform = t;
        return t;
    }

    private void CompleteActiveSnap()
    {
        if (_activeSnapComplete == null) return;
        var finish = _activeSnapComplete;
        _activeSnapComplete = null;
        finish();
    }

    private FrameworkElement EnsureSection(int index)
    {
        if (!_sections.TryGetValue(index, out var page))
        {
            page = CreateSection(index);
            _sections[index] = page;
            SectionHost.Children.Add(page);
        }
        return page;
    }

    private void EnsurePeerSectionVisible(int targetIndex, double offsetX, bool createIfMissing = true)
    {
        if (targetIndex is < 0 or > 4 || targetIndex == CurrentTabIndex)
        {
            ClearPeerSection();
            return;
        }
        if (!_sections.TryGetValue(targetIndex, out var peer))
        {
            if (!createIfMissing) return;
            peer = EnsureSection(targetIndex);
        }
        if (!ReferenceEquals(_peerSection, peer))
        {
            ClearPeerSection();
            _peerSection = peer;
            peer.Visibility = Visibility.Visible;
        }
        var pt = PeerTransform(peer);
        pt.BeginAnimation(TranslateTransform.XProperty, null);
        pt.X = offsetX;
    }

    private void ClearPeerSection()
    {
        if (_peerSection == null) return;
        var peer = _peerSection;
        _peerSection = null;
        var pt = PeerTransform(peer);
        pt.BeginAnimation(TranslateTransform.XProperty, null);
        pt.X = 0;
        if (!_sections.TryGetValue(CurrentTabIndex, out var active) || !ReferenceEquals(active, peer))
            peer.Visibility = Visibility.Collapsed;
    }

    private FrameworkElement CreateSection(int index)
    {
        FrameworkElement page = index == 0 ? new MineOverviewPage() : SectionFactory?.Invoke(_keys[index]) ?? index switch
        {
            1 => new HistoryPage { DataContext = new HistoryViewModel() },
            2 => new FavoritesPage { DataContext = new FavoritesViewModel() },
            3 => new WatchLaterPage { DataContext = new WatchLaterViewModel() },
            _ => new CachePage { DataContext = new CacheViewModel() }
        };
        if (index == 0 && page.FindName("PageHeader") is FrameworkElement header) header.Visibility = Visibility.Collapsed;
        else if (page is ContentControl { Content: Grid layout })
        {
            var toolbar = layout.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetRow(g) == 0);
            if (toolbar != null) { toolbar.Margin = new Thickness(36, 4, 36, 6); toolbar.MinHeight = Math.Max(40, toolbar.MinHeight); }
        }
        return page;
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (SectionHost == null || sender is not RadioButton tab || !int.TryParse(tab.Tag?.ToString(), out var index)) return;
        _cancelActiveSnap?.Invoke();
        _snapTarget = -1;
        _activeSnapComplete = null;
        _armed = false;
        RestorePanning();
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.X = 0;
        CurrentTabIndex = index;
        _pendingWheel = 0;
        _peerSection = null;
        EnsureSection(index);
        foreach (var item in _sections)
        {
            var pt = PeerTransform(item.Value);
            pt.BeginAnimation(TranslateTransform.XProperty, null);
            pt.X = 0;
            item.Value.Visibility = item.Key == index ? Visibility.Visible : Visibility.Collapsed;
        }
        MineTabs?.SnapToSelected(true);
    }

    public void SelectSection(PageKey key)
    {
        var index = Array.IndexOf(_keys, key);
        if (index >= 0) Tabs[index].IsChecked = true;
    }

    internal void SwipeDown(Point point, DependencyObject? source)
    {
        _touchDragOrigin = TakeOverSnap();
        _wheelPreviewActive = false;
        _gestureCard = source == null ? null : VisualTreeUtil.FindAncestor<VideoCard>(source);
        _suppressClick = false;
        _start = point;
        _dx = _touchDragOrigin;
        _decided = false;
        _horizontal = false;
        _armed = true;
        for (var d = source; d != null && d != SectionHost; d = d is ContentElement c ? ContentOperations.GetParent(c) ?? (c as FrameworkContentElement)?.Parent : VisualTreeHelper.GetParent(d))
            if (d is TextBoxBase or Slider or PasswordBox or Selector or Hyperlink) { _armed = false; break; }
    }

    internal bool SwipeMove(Point point)
    {
        if (!_armed) return false;
        _dx = point.X - _start.X;
        var dy = point.Y - _start.Y;
        if (!_decided)
        {
            if (Math.Max(Math.Abs(_dx), Math.Abs(dy)) < 12) return false;
            _horizontal = Math.Abs(_dx) > Math.Abs(dy) * 1.3;
            _decided = _horizontal;
            if (_horizontal) CompleteActiveSnap();
        }
        if (!_horizontal) return false;

        TouchSupport.CancelPendingTap();
        _gestureCard?.CancelGestureTap();
        if (_frozen == null && _sections.TryGetValue(CurrentTabIndex, out var page))
        {
            _frozen = FindScroll(page);
            if (_frozen != null)
            {
                _panning = _frozen.PanningMode;
                _frozenOffset = _frozen.VerticalOffset;
                _frozen.PanningMode = PanningMode.None;
                TrackpadScroll.Cancel(_frozen);
            }
        }
        if (_frozen != null && Math.Abs(_frozen.VerticalOffset - _frozenOffset) > 0.5)
            _frozen.ScrollToVerticalOffset(_frozenOffset);

        _dx += _touchDragOrigin;
        var width = Math.Max(1, SectionHost.ActualWidth);
        ApplyDisplacement(_dx, width);
        return true;
    }

    private void ApplyDisplacement(double dx, double width)
    {
        var target = CurrentTabIndex + (dx < 0 ? 1 : -1);
        var edge = target is < 0 or > 4;
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        if (!edge)
        {
            EnsurePeerSectionVisible(target, dx < 0 ? width : -width);
            _slide.X = dx;
            MineTabs?.PreviewSwipeProgress(CurrentTabIndex, target, Math.Min(1.0, Math.Abs(dx) / width));
        }
        else
        {
            ClearPeerSection();
            _slide.X = dx * SwipeEdgeResist;
        }
    }

    internal bool SwipeUp()
    {
        if (!_armed) return false;
        _armed = false;
        RestorePanning();
        var horizontal = _horizontal;
        _horizontal = false;
        if (!horizontal) { if (Math.Abs(_touchDragOrigin) > .5) Snap(CurrentTabIndex); return false; }
        _gestureCard?.FinishGestureTap();
        _gestureCard = null;
        _suppressClick = true;
        var width = Math.Max(1, SectionHost.ActualWidth);
        var change = Math.Abs(_dx) >= width * SwipePageRatio;
        Snap(change ? Math.Clamp(CurrentTabIndex + (_dx < 0 ? 1 : -1), 0, 4) : CurrentTabIndex);
        return true;
    }

    internal void CancelPendingSwipe()
    {
        _armed = false;
        RestorePanning();
        ClearPeerSection();
    }

    private void RestorePanning()
    {
        if (_frozen != null) _frozen.PanningMode = _panning;
        _frozen = null;
    }

    internal void PreviewHorizontalWheel(double progress)
    {
        if (_horizontal) return;
        if (!_wheelPreviewActive) { _wheelDragOrigin = TakeOverSnap(); _wheelPreviewActive = true; }
        var width = Math.Max(1, SectionHost.ActualWidth);
        _wheelContactDx = -Math.Clamp(progress, -1, 1) * width;
        var dx = Math.Clamp(_wheelDragOrigin + _wheelContactDx, -width * .999, width * .999);
        _dx = dx;
        ApplyDisplacement(dx, width);
    }

    internal void SettleHorizontalWheelPreview(bool commit)
        => SettleWheelPreview(commit, false);

    internal void SettlePrecisionTouchpadPreview(bool commit)
        => SettleWheelPreview(commit, true);

    internal bool ContainsSwipePoint(Point screenPoint) => PrecisionTouchpadInput.ContainsViewportPoint(SectionHost, screenPoint);

    private void SettleWheelPreview(bool commit, bool physicalContact)
    {
        if (_horizontal) return;
        _wheelPreviewActive = false;
        if (Math.Abs(_slide.X) < 0.5 && _peerSection == null) return;
        var target = Math.Clamp(CurrentTabIndex + (_dx < 0 ? 1 : -1), 0, 4);
        _wheelPreviewActive = false;
        var distance = physicalContact ? (Math.Sign(_wheelContactDx) == Math.Sign(_dx) ? Math.Abs(_wheelContactDx) : 0) : Math.Abs(_dx);
        Snap(commit && distance >= SectionHost.ActualWidth * SwipePageRatio ? target : CurrentTabIndex);
    }

    internal void HandleHorizontalWheel(int direction)
    {
        if (direction == 0) return;
        CompleteActiveSnap();
        Snap(Math.Clamp(CurrentTabIndex + Math.Sign(direction), 0, 4));
    }

    private void Snap(int target)
    {
        CompleteActiveSnap();
        var width = Math.Max(1, SectionHost.ActualWidth);
        if (target != CurrentTabIndex)
            EnsurePeerSectionVisible(target, target > CurrentTabIndex ? width : -width, createIfMissing: true);

        var animation = new DoubleAnimation(
            _slide.X,
            target == CurrentTabIndex ? 0 : (target > CurrentTabIndex ? -1 : 1) * width,
            TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var done = false;
        _snapTarget = target != CurrentTabIndex ? target : -1;
        var fallback = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(170)
        };

        void Complete()
        {
            if (done) return;
            done = true;
            _activeSnapComplete = null;
            _cancelActiveSnap = null;
            _snapTarget = -1;
            fallback.Stop();
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = 0;
            ClearPeerSection();
            var pending = _pendingWheel;
            _pendingWheel = 0;
            for (int i = 0; i < Tabs.Length; i++)
                if (i != target && Tabs[i].IsChecked == true) Tabs[i].IsChecked = false;
            if (Tabs[target].IsChecked != true) Tabs[target].IsChecked = true;
            if (CurrentTabIndex != target) OnTabChecked(Tabs[target], new RoutedEventArgs());
            else MineTabs?.SnapToSelected(true);
            if (pending != 0) HandleHorizontalWheel(pending);
        }

        _activeSnapComplete = Complete;
        _cancelActiveSnap = () =>
        {
            var x = _slide.X;
            done = true;
            fallback.Stop();
            _activeSnapComplete = _cancelActiveSnap = null;
            _snapTarget = -1;
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = x;
        };
        fallback.Tick += (_, _) => Complete();
        animation.Completed += (_, _) => Complete();
        fallback.Start();
        _slide.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is FrameworkElement fe && !fe.IsVisible) return null;
        if (root is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScroll(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
