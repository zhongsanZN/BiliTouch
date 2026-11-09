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

namespace BiliTouch.Helpers;

/// <summary>
/// 通用横向页签手势协调器（支持触屏跟手与精确式触控板全幅双页并排中间态、自然过半阈值与连续打断零抽搐）。
/// </summary>
internal sealed class TabNavigationGestures
{
    /// <summary>自然过半才触发切页（50% 容器宽度）</summary>
    internal const double SwipePageRatio = 0.50;
    /// <summary>高速轻扫捷径阈值（px/ms），需同时满足最小位移 80px</summary>
    internal const double SwipeFlingVelocity = 2.2;
    private const double SwipeEdgeResist = 0.35;

    private readonly FrameworkElement _content;
    private readonly FrameworkElement _tabs;
    private readonly Func<int, UIElement?>? _peerFactory;
    private readonly TranslateTransform _slide = new();
    private readonly TranslateTransform _peerSlide = new();
    private Border? _peerHost;
    private int _peerIndex = -1;
    private Action? _activeSettleComplete;

    private Point _start;
    private long _started;
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

        page.Unloaded += (_, _) => Cancel();
        page.IsVisibleChanged += (_, _) => { if (!page.IsVisible) Cancel(); };
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
            _peerHost = new Border
            {
                ClipToBounds = true,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
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
            var peerContent = _peerFactory?.Invoke(targetIndex) ?? CreateDefaultPeerCard(buttons[targetIndex]);
            _peerHost.Child = peerContent;
        }

        _peerHost.Visibility = _peerHost.Child != null ? Visibility.Visible : Visibility.Collapsed;
        _peerSlide.BeginAnimation(TranslateTransform.XProperty, null);
        _peerSlide.X = offsetX;
    }

    private UIElement CreateDefaultPeerCard(RadioButton targetButton)
    {
        var label = targetButton.Content?.ToString() ?? "";
        var card = new Border
        {
            Margin = new Thickness(24, 12, 24, 24),
            CornerRadius = new CornerRadius(12),
            Background = _content.TryFindResource("CardBackgroundBrush") as Brush ?? Brushes.Transparent,
            BorderBrush = _content.TryFindResource("CardBorderBrush") as Brush,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = _content.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        return card;
    }

    private void ClearPeerPreview()
    {
        _peerIndex = -1;
        if (_peerHost == null) return;
        _peerSlide.BeginAnimation(TranslateTransform.XProperty, null);
        _peerSlide.X = 0;
        _peerHost.Visibility = Visibility.Collapsed;
        _peerHost.Child = null;
    }

    internal void PreviewHorizontalWheel(double progress)
    {
        if (_horizontal) return;
        CompleteActiveSettle();
        var buttons = Buttons;
        var current = SelectedIndex;
        if (current < 0) return;

        var width = Math.Max(1, _content.ActualWidth);
        var dx = -progress * width;
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
    {
        if (_horizontal) return;
        if (Math.Abs(_slide.X) < 0.5 && (_peerHost == null || _peerHost.Visibility != Visibility.Visible)) return;
        var dx = _dx;
        _dx = 0;
        if (commit && Math.Abs(dx) > 1)
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
        var width = Math.Max(1, _content.ActualWidth);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(160);
        if (_tabs is NavigationTabs navTabs) navTabs.SnapToSelected(true);

        var done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            _activeSettleComplete = null;
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = 0;
            ClearPeerPreview();
        }
        _activeSettleComplete = Finish;

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

        var width = Math.Max(1, _content.ActualWidth);
        var incomingStart = Math.Abs(dragDx) > 1
            ? dragDx + (direction > 0 ? width : -width)
            : (direction > 0 ? Math.Min(width * 0.28, 96) : -Math.Min(width * 0.28, 96));

        ClearPeerPreview();
        buttons[next].SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        if (_tabs is NavigationTabs navTabs) navTabs.SnapToSelected(true);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(160);
        var done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            _activeSettleComplete = null;
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = 0;
        }
        _activeSettleComplete = Finish;

        var anim = new DoubleAnimation(incomingStart, 0, dur) { EasingFunction = ease };
        anim.Completed += (_, _) => Finish();
        _slide.BeginAnimation(TranslateTransform.XProperty, anim);
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
        CompleteActiveSettle();
        Cancel();
        _suppressClick = false;
        _start = point;
        _started = Environment.TickCount64;
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
            _decided = true;
            _horizontal = Math.Abs(_dx) > Math.Abs(dy) * 1.3;
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
        var elapsed = Math.Max(1, Environment.TickCount64 - _started);
        if (_scroll != null) _scroll.PanningMode = _panning;
        _scroll = null;
        _armed = _decided = _horizontal = false;
        _dx = 0;

        if (!horizontal)
        {
            ClearPeerPreview();
            return false;
        }

        _suppressClick = true;
        var width = Math.Max(1, _content.ActualWidth);
        var velocity = Math.Abs(dx) / elapsed;
        var commit = Math.Abs(dx) >= width * SwipePageRatio || (Math.Abs(dx) >= 80 && velocity >= SwipeFlingVelocity);
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
        if (_scroll != null) _scroll.PanningMode = _panning;
        _scroll = null;
        _armed = _decided = _horizontal = false;
        _dx = 0;
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.X = 0;
        ClearPeerPreview();
    }
}
