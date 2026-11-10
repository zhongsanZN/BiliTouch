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
using System.Windows.Media.Imaging;
using System.Windows.Automation.Peers;

namespace BiliTouch.Helpers;

/// <summary>
/// 通用横向页签手势协调器（支持触屏跟手与精确式触控板全幅双页并排中间态、自然过半阈值与连续打断零抽搐）。
/// </summary>
internal sealed class TabNavigationGestures
{
    // A peer is a decorative, noninteractive copy of another page. Exposing its
    // duplicated controls to UI Automation both misrepresents the active page and
    // forces WPF to rebuild/validate a second large accessibility subtree during
    // every slide. The actual page keeps its normal automation peers.
    private sealed class DecorativePreviewPanel : Grid
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePreviewPeer(this);
    }
    private sealed class DecorativePreviewPeer : FrameworkElementAutomationPeer
    {
        internal DecorativePreviewPeer(FrameworkElement owner) : base(owner) { }
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
    /// <summary>自然过半才触发切页（50% 容器宽度）</summary>
    internal const double SwipePageRatio = 0.50;
    private const double SwipeEdgeResist = 0.35;

    private readonly FrameworkElement _content;
    private readonly FrameworkElement _tabs;
    private readonly Func<int, UIElement?>? _peerFactory;
    private readonly TranslateTransform _slide = new();
    private readonly TranslateTransform _peerSlide = new();
    private Grid? _peerHost;
    private readonly Dictionary<int, UIElement> _peerCache = new();
    private readonly Dictionary<int, BitmapSource> _pageSnapshots = new();
    private string? _snapshotAccount;
    private int _peerIndex = -1;
    private Action? _activeSettleComplete, _cancelActiveSettle;
    private int _settleTarget = -1;
    private double _wheelContactDx;
    private double _touchDragOrigin, _wheelDragOrigin;
    private bool _wheelPreviewActive;
    internal bool IsInteracting => _armed || _wheelPreviewActive || _activeSettleComplete != null;

    private double TakeOverSettle()
    {
        var target = _settleTarget; var previous = SelectedIndex;
        _cancelActiveSettle?.Invoke();
        if (target >= 0 && target != previous)
        {
            var oldX = _slide.X; var incomingX = _peerSlide.X;
            Buttons[target].SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            EnsurePeerVisible(previous, oldX);
            _slide.X = incomingX;
            if (_tabs is NavigationTabs tabs) tabs.PreviewSwipeProgress(target, previous, Math.Abs(incomingX) / Math.Max(1, _content.ActualWidth));
        }
        return _slide.X;
    }

    private Point _start;
    private double _dx;
    private bool _armed, _decided, _horizontal, _suppressClick;
    private ScrollViewer? _scroll;
    private PanningMode _panning;
    private double _offset;
    private readonly System.Windows.Threading.DispatcherTimer _wheelIdle = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private int _wheelAccum;
    private bool _wheelConsumed;

    private RadioButton[] Buttons => CollectRadioButtons(_tabs).ToArray();

    internal int SelectedIndex => Array.FindIndex(Buttons, b => b.IsChecked == true);

    internal TabNavigationGestures(
        UserControl page,
        FrameworkElement content,
        FrameworkElement tabs,
        Func<int, UIElement?>? peerFactory = null)
    {
        _content = content;
        _tabs = tabs;
        _peerFactory = peerFactory;
        content.RenderTransform = _slide;

        page.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>((_, e) => Begin(e.GetTouchPoint(null).Position, e.OriginalSource as DependencyObject)), true);
        page.AddHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>((_, e) => { if (Move(e.GetTouchPoint(null).Position)) e.Handled = true; }), true);
        page.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>((_, e) => { if (End()) e.Handled = true; }), true);
        page.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) => { if (_suppressClick) { e.Handled = true; _suppressClick = false; } }), true);

        _wheelIdle.Tick += (_, _) =>
        {
            _wheelIdle.Stop();
            _wheelAccum = 0;
            _wheelConsumed = false;
        };

        tabs.PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta == 0) return;
            _wheelIdle.Stop();
            _wheelIdle.Start();
            if (Math.Sign(_wheelAccum) != Math.Sign(e.Delta)) _wheelAccum = 0;
            _wheelAccum += e.Delta;
            if (!_wheelConsumed && Math.Abs(_wheelAccum) >= 120)
            {
                _wheelConsumed = true;
                Step(e.Delta < 0 ? 1 : -1);
            }
            e.Handled = true;
        };

        page.DataContextChanged += (_, _) =>
        {
            Cancel();
            _pageSnapshots.Clear(); _peerCache.Clear(); _peerHost?.Children.Clear();
            QueuePeerWarmUp();
        };
        page.Loaded += (_, _) => QueuePeerWarmUp();
        page.Unloaded += (_, _) => Cancel();
        page.IsVisibleChanged += (_, _) => { if (!page.IsVisible) Cancel(); };
    }

    private void QueuePeerWarmUp()
    {
        if (!_content.IsLoaded) return;
        _content.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!_content.IsVisible || _armed || _wheelPreviewActive || _activeSettleComplete != null) return;
            if (_peerFactory == null) { CaptureCurrentPage(); return; }
            var width = Math.Max(1, _content.ActualWidth);
            var current = SelectedIndex;
            foreach (var target in new[] { current - 1, current + 1 })
                if (target >= 0 && target < Buttons.Length) EnsurePeerVisible(target, width);
            ClearPeerPreview();
            // The current page will become the outgoing peer if a committed
            // settle is interrupted. Build that copy while idle as well.
            if (_peerHost != null && current >= 0 && !_peerCache.ContainsKey(current) && _peerFactory(current) is { } outgoing)
            {
                outgoing.Visibility = Visibility.Hidden;
                _peerCache[current] = outgoing;
                _peerHost.Children.Add(outgoing);
                outgoing.Measure(new Size(width, Math.Max(1, _content.ActualHeight)));
                outgoing.Arrange(new Rect(0, 0, width, Math.Max(1, _content.ActualHeight)));
            }
        }));
    }

    private static IEnumerable<RadioButton> CollectRadioButtons(DependencyObject root)
    {
        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (child is RadioButton rb) yield return rb;
                else
                {
                    foreach (var sub in CollectRadioButtons(child))
                        yield return sub;
                }
            }
            yield break;
        }
        if (root is Decorator { Child: { } decChild })
        {
            foreach (var sub in CollectRadioButtons(decChild))
                yield return sub;
            yield break;
        }
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton rb) yield return rb;
            else
            {
                foreach (var sub in CollectRadioButtons(child))
                    yield return sub;
            }
        }
    }

    private void CompleteActiveSettle()
    {
        if (_activeSettleComplete == null) return;
        var finish = _activeSettleComplete;
        _activeSettleComplete = null;
        finish();
    }

    private void EnsurePeerVisible(int targetIndex, double offsetX)
    {
        var buttons = Buttons;
        if (targetIndex < 0 || targetIndex >= buttons.Length || targetIndex == SelectedIndex)
        {
            ClearPeerPreview();
            return;
        }

        if (_peerHost == null && _content.Parent is Grid parentGrid)
        {
            _peerHost = new DecorativePreviewPanel
            {
                ClipToBounds = true,
                IsHitTestVisible = false,
                Visibility = Visibility.Hidden,
                RenderTransform = _peerSlide,
                Margin = _content.Margin
            };
            Grid.SetRow(_peerHost, Grid.GetRow(_content));
            Grid.SetColumn(_peerHost, Grid.GetColumn(_content));
            Grid.SetRowSpan(_peerHost, Math.Max(1, Grid.GetRowSpan(_content)));
            Grid.SetColumnSpan(_peerHost, Math.Max(1, Grid.GetColumnSpan(_content)));
            parentGrid.Children.Add(_peerHost);
        }

        if (_peerHost == null) return;

        if (_peerIndex != targetIndex)
        {
            _peerIndex = targetIndex;
            if (!_peerCache.TryGetValue(targetIndex, out var peerContent))
            {
                peerContent = _peerFactory?.Invoke(targetIndex)
                    ?? (_pageSnapshots.TryGetValue(targetIndex, out var snapshot)
                        ? new Image { Source = snapshot, Stretch = Stretch.Fill }
                        : CreateLoadingPeer());
                _peerCache[targetIndex] = peerContent;
                _peerHost.Children.Add(peerContent);
            }
            foreach (UIElement child in _peerHost.Children)
                child.Visibility = ReferenceEquals(child, peerContent) ? Visibility.Visible : Visibility.Hidden;
        }

        _peerHost.Visibility = Visibility.Visible;
        _peerSlide.BeginAnimation(TranslateTransform.XProperty, null);
        _peerSlide.X = offsetX;
    }

    private void CaptureCurrentPage()
    {
        var account = Svc.Session.Current.DedeUserID + ":" + Svc.Session.HasLogin;
        if (account != _snapshotAccount)
        {
            _snapshotAccount = account;
            _pageSnapshots.Clear();
            if (_peerFactory == null) { _peerCache.Clear(); _peerHost?.Children.Clear(); }
        }
        if (_peerFactory != null || Math.Abs(_slide.X) > .5 || !_content.IsVisible) return;
        var index = SelectedIndex;
        if (index < 0 || _content.ActualWidth < 1 || _content.ActualHeight < 1) return;
        try
        {
            var width = _content.ActualWidth; var height = _content.ActualHeight;
            int pixelsW = (int)Math.Min(1600, Math.Ceiling(width));
            int pixelsH = (int)Math.Min(1200, Math.Ceiling(height));
            var bitmap = new RenderTargetBitmap(pixelsW, pixelsH, 96.0 * pixelsW / width, 96.0 * pixelsH / height, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawRectangle(new VisualBrush(_content) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height) }, null, new Rect(0, 0, width, height));
            bitmap.Render(visual); bitmap.Freeze();
            _pageSnapshots[index] = bitmap;
            if (_peerCache.Remove(index, out var old)) _peerHost?.Children.Remove(old);
        }
        catch { }
    }

    private UIElement CreateLoadingPeer()
    {
        return new Grid
        {
            Background = _content.TryFindResource("AppBackgroundBrush") as Brush,
            Children =
            {
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new BiliTouch.Views.Controls.LoadingRing { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = "正在加载…", Margin = new Thickness(0, 10, 0, 0), Foreground = _content.TryFindResource("TextSecondaryBrush") as Brush }
                    }
                }
            }
        };
    }

    private void ClearPeerPreview()
    {
        _peerIndex = -1;
        if (_peerHost == null) return;
        _peerSlide.BeginAnimation(TranslateTransform.XProperty, null);
        _peerSlide.X = 0;
        _peerHost.Visibility = Visibility.Hidden;
    }

    internal void PreviewHorizontalWheel(double progress)
    {
        if (_horizontal) return;
        if (!_wheelPreviewActive) { CaptureCurrentPage(); _wheelDragOrigin = TakeOverSettle(); _wheelPreviewActive = true; }
        var buttons = Buttons;
        var current = SelectedIndex;
        if (current < 0) return;

        var width = Math.Max(1, _content.ActualWidth);
        _wheelContactDx = -Math.Clamp(progress, -1, 1) * width;
        var dx = Math.Clamp(_wheelDragOrigin + _wheelContactDx, -width * .999, width * .999);
        _dx = dx;
        ApplyDragPosition(buttons, current, dx, width);
    }

    private void ApplyDragPosition(RadioButton[] buttons, int current, double dx, double width)
    {
        var target = current + (dx < 0 ? 1 : -1);
        var edge = target < 0 || target >= buttons.Length;
        _slide.BeginAnimation(TranslateTransform.XProperty, null);

        if (!edge && Math.Abs(dx) > 0.5)
        {
            EnsurePeerVisible(target, dx + (dx < 0 ? width : -width));
            _slide.X = dx;
            if (_tabs is NavigationTabs navTabs)
                navTabs.PreviewSwipeProgress(current, target, Math.Min(1.0, Math.Abs(dx) / width));
        }
        else
        {
            ClearPeerPreview();
            _slide.X = dx * SwipeEdgeResist;
        }
    }

    internal void SettleHorizontalWheelPreview(bool commit)
        => SettleWheelPreview(commit, false);

    internal void SettlePrecisionTouchpadPreview(bool commit)
        => SettleWheelPreview(commit, true);

    internal bool ContainsSwipePoint(Point screenPoint) => PrecisionTouchpadInput.ContainsViewportPoint(_content, screenPoint);

    private void SettleWheelPreview(bool commit, bool physicalContact)
    {
        if (_horizontal) return;
        _wheelPreviewActive = false;
        if (Math.Abs(_slide.X) < 0.5 && (_peerHost == null || _peerHost.Visibility != Visibility.Visible)) return;
        var dx = _dx;
        _wheelPreviewActive = false;
        _dx = 0;
        var distance = physicalContact ? (Math.Sign(_wheelContactDx) == Math.Sign(dx) ? Math.Abs(_wheelContactDx) : 0) : Math.Abs(dx);
        if (commit && distance >= _content.ActualWidth * SwipePageRatio)
        {
            StepWithDragContinuation(dx < 0 ? 1 : -1, dx);
        }
        else
        {
            AnimateSnapBack();
        }
    }

    private void AnimateSnapBack()
    {
        _settleTarget = -1;
        var width = Math.Max(1, _content.ActualWidth);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(160);
        if (_tabs is NavigationTabs navTabs) navTabs.SnapToSelected(true);

        var done = false;
        var fallback = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
        { Interval = TimeSpan.FromMilliseconds(220) };
        void Finish()
        {
            if (done) return;
            done = true;
            fallback.Stop();
            _activeSettleComplete = null;
            _cancelActiveSettle = null;
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = 0;
            ClearPeerPreview();
        }
        fallback.Tick += (_, _) => Finish();
        fallback.Start();
        _activeSettleComplete = Finish;
        _cancelActiveSettle = () =>
        {
            var x = _slide.X; var peerX = _peerSlide.X;
            done = true;
            fallback.Stop();
            _activeSettleComplete = _cancelActiveSettle = null;
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _peerSlide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = x; _peerSlide.X = peerX;
        };

        if (_peerHost is { Visibility: Visibility.Visible })
        {
            var peerTarget = _peerSlide.X >= 0 ? width : -width;
            _peerSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_peerSlide.X, peerTarget, dur) { EasingFunction = ease });
        }

        var anim = new DoubleAnimation(_slide.X, 0, dur) { EasingFunction = ease };
        anim.Completed += (_, _) => Finish();
        _slide.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void StepWithDragContinuation(int direction, double dragDx)
    {
        var buttons = Buttons;
        var current = SelectedIndex;
        if (current < 0 || direction == 0)
        {
            AnimateSnapBack();
            return;
        }
        var next = Math.Clamp(current + Math.Sign(direction), 0, buttons.Length - 1);
        if (next == current)
        {
            AnimateSnapBack();
            return;
        }
        _settleTarget = next;

        var width = Math.Max(1, _content.ActualWidth);
        ApplyDragPosition(buttons, current, dragDx, width);
        EnsurePeerVisible(next, dragDx + (direction > 0 ? width : -width));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(160);
        var done = false;
        var fallback = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
        { Interval = TimeSpan.FromMilliseconds(220) };
        void Finish()
        {
            if (done) return;
            done = true;
            fallback.Stop();
            _activeSettleComplete = null;
            _cancelActiveSettle = null;
            _settleTarget = -1;
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = 0;
            ClearPeerPreview();
            buttons[next].SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            if (_tabs is NavigationTabs navTabs) navTabs.SnapToSelected(true);
            QueuePeerWarmUp();
        }
        fallback.Tick += (_, _) => Finish();
        fallback.Start();
        _activeSettleComplete = Finish;
        _cancelActiveSettle = () =>
        {
            var x = _slide.X; var peerX = _peerSlide.X;
            done = true;
            fallback.Stop();
            _activeSettleComplete = _cancelActiveSettle = null;
            _settleTarget = -1;
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _peerSlide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = x; _peerSlide.X = peerX;
        };
        var anim = new DoubleAnimation(_slide.X, direction > 0 ? -width : width, dur) { EasingFunction = ease };
        anim.Completed += (_, _) => Finish();
        _slide.BeginAnimation(TranslateTransform.XProperty, anim);
        _peerSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(_peerSlide.X, 0, dur) { EasingFunction = ease });
    }

    internal void Step(int direction)
    {
        CompleteActiveSettle();
        var dragDx = _dx;
        Cancel();
        StepWithDragContinuation(direction, dragDx);
    }

    internal void Begin(Point point, DependencyObject? source)
    {
        if (_scroll != null) _scroll.PanningMode = _panning;
        _scroll = null;
        _armed = _decided = _horizontal = false;
        CaptureCurrentPage();
        _touchDragOrigin = TakeOverSettle();
        _wheelPreviewActive = false;
        _dx = _touchDragOrigin;
        _suppressClick = false;
        _start = point;
        _armed = true;
        for (var d = source; d != null; d = Parent(d))
        {
            if (d is TextBoxBase or Slider or PasswordBox or Hyperlink or ComboBox or BiliTouch.Views.Controls.ColorPalette)
            {
                _armed = false;
                break;
            }
            if (_scroll == null && d is ScrollViewer viewer) _scroll = viewer;
        }
        if (_scroll != null)
        {
            _panning = _scroll.PanningMode;
            _offset = _scroll.VerticalOffset;
        }
    }

    internal bool Move(Point point)
    {
        if (!_armed) return false;
        _dx = point.X - _start.X;
        var dy = point.Y - _start.Y;
        if (!_decided)
        {
            if (Math.Max(Math.Abs(_dx), Math.Abs(dy)) < 12) return false;
            _horizontal = Math.Abs(_dx) > Math.Abs(dy) * 1.3;
            _decided = _horizontal;
        }
        if (!_horizontal) return false;

        TouchSupport.CancelPendingTap();
        if (_scroll != null)
        {
            _scroll.PanningMode = PanningMode.None;
            TrackpadScroll.Cancel(_scroll);
            if (Math.Abs(_scroll.VerticalOffset - _offset) > 0.5)
                _scroll.ScrollToVerticalOffset(_offset);
        }

        _dx += _touchDragOrigin;
        var buttons = Buttons;
        var current = SelectedIndex;
        if (current < 0) return false;
        var width = Math.Max(1, _content.ActualWidth);
        ApplyDragPosition(buttons, current, _dx, width);
        return true;
    }

    internal bool End()
    {
        if (!_armed) return false;
        var horizontal = _horizontal;
        var dx = _dx;
        if (_scroll != null) _scroll.PanningMode = _panning;
        _scroll = null;
        _armed = _decided = _horizontal = false;
        _dx = 0;

        if (!horizontal)
        {
            if (Math.Abs(_touchDragOrigin) > .5) AnimateSnapBack();
            else ClearPeerPreview();
            return false;
        }

        _suppressClick = true;
        var width = Math.Max(1, _content.ActualWidth);
        var commit = Math.Abs(dx) >= width * SwipePageRatio;
        if (commit)
            StepWithDragContinuation(dx < 0 ? 1 : -1, dx);
        else
            AnimateSnapBack();
        return true;
    }

    private static DependencyObject? Parent(DependencyObject d)
        => d is ContentElement c ? ContentOperations.GetParent(c) ?? (c as FrameworkContentElement)?.Parent : VisualTreeHelper.GetParent(d);

    private void Cancel()
    {
        _cancelActiveSettle?.Invoke();
        _wheelPreviewActive = false;
        _touchDragOrigin = _wheelDragOrigin = 0;
        if (_scroll != null) _scroll.PanningMode = _panning;
        _scroll = null;
        _armed = _decided = _horizontal = false;
        _dx = 0;
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.X = 0;
        ClearPeerPreview();
    }
}
