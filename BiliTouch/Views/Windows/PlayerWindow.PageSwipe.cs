using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BiliTouch.Views;

/// <summary>
/// 右栏「简介 / 评论（或网页模式：简介 / 推荐）」的跟手横滑翻页。
/// </summary>
public partial class PlayerWindow
{
    private const double PageDragSlop = 12;
    private const double PageSwitchDistance = 70;
    private const double PageSwitchRatio = 3.0 / 8.0;
    private const double PageEdgeResistance = 0.35;

    private Point _infoSwipeStart;
    private bool _infoSwipeArmed, _infoHorizontalDecided, _infoDraggingPage;
    private bool _infoPageAnimating, _infoGestureMoved, _infoPageSwitching;
    private double _infoDragDx;
    private Action? _infoActiveSettle, _cancelInfoSettle;
    private int _infoSettleTarget = -1;
    private double _infoWheelContactDx;
    private double _infoTouchOrigin, _infoWheelOrigin;
    private bool _infoWheelPreviewActive, _capturingInfoTouch;
    private PanningMode? _savedInfoPanning;

    private void SuspendInfoScrollPanning()
    {
        if (InfoScroll == null || _savedInfoPanning != null) return;
        _savedInfoPanning = InfoScroll.PanningMode;
        if (_savedInfoPanning != PanningMode.None)
            InfoScroll.SetCurrentValue(ScrollViewer.PanningModeProperty, PanningMode.None);
        BiliTouch.Helpers.TouchSupport.CancelPendingTap();
    }

    private void RestoreInfoScrollPanning()
    {
        if (InfoScroll == null || _savedInfoPanning is not { } saved) return;
        _savedInfoPanning = null;
        InfoScroll.SetCurrentValue(ScrollViewer.PanningModeProperty, saved);
    }

    private double TakeOverInfoSettle()
    {
        var target = _infoSettleTarget;
        var previous = PageIndex;
        _cancelInfoSettle?.Invoke();
        if (target >= 0 && target != previous)
        {
            var old = CurrentPagePanel(); var next = OtherPagePanel();
            if (old != null && next != null)
            {
                var oldX = TransformOf(old).X; var nextX = TransformOf(next).X;
                _infoPageSwitching = true;
                try { if (target == 1) SecondaryPageButton().IsChecked = true; else TabIntro.IsChecked = true; }
                finally { _infoPageSwitching = false; }
                old.Visibility = next.Visibility = Visibility.Visible;
                TransformOf(old).X = oldX; TransformOf(next).X = nextX;
            }
        }
        var current = CurrentPagePanel();
        return current != null ? TransformOf(current).X : 0;
    }

    private void CompleteInfoPageSettle()
    {
        if (_infoActiveSettle == null) return;
        var finish = _infoActiveSettle;
        _infoActiveSettle = null;
        finish();
    }

    internal void InfoDown(Point p, DependencyObject? source)
    {
        _pageWheelIdle?.Stop();
        _pageWheelAccum = 0;
        _horizontalWheelRegion = HorizontalWheelRegion.None;
        _infoTouchOrigin = TakeOverInfoSettle();
        _infoWheelPreviewActive = false;
        _infoSwipeStart = p;
        _infoSwipeArmed = InfoSwipeAllowed(source);
        _infoHorizontalDecided = false;
        _infoDraggingPage = false;
        _infoGestureMoved = false;
        _infoDragDx = _infoTouchOrigin;
    }

    private void OnInfoTouchDownCapture(object? sender, TouchEventArgs e)
    {
        _pendingCaptureDevice = e.TouchDevice;
        OnInfoTouchDown(sender, e);
    }

    private TouchDevice? _pendingCaptureDevice;

    /// <summary>Returns true when the move was consumed by the page drag.</summary>
    internal bool InfoMove(Point p)
    {
        if (!_infoSwipeArmed) return false;
        if (_infoPageAnimating) CompleteInfoPageSettle();
        var dx = p.X - _infoSwipeStart.X;
        var dy = p.Y - _infoSwipeStart.Y;
        if (Math.Abs(dx) > 10 || Math.Abs(dy) > 10) _infoGestureMoved = true;

        if (!_infoHorizontalDecided)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < PageDragSlop) return false;
            if (Math.Abs(dx) <= Math.Abs(dy) * 1.2)
            {
                _infoSwipeArmed = false;
                return false;
            }
            _infoHorizontalDecided = true;
            if (!_infoSwipeArmed) return false;
            _infoDragDx = dx;
            SuspendInfoScrollPanning();
            if (_pendingCaptureDevice != null)
            {
                _capturingInfoTouch = true;
                try { InfoPanel.CaptureTouch(_pendingCaptureDevice); }
                catch { /* 已被别处捕获 */ }
                finally { _capturingInfoTouch = false; }
                _pendingCaptureDevice = null;
            }
            _infoDraggingPage = BeginPageDrag();
            if (!_infoDraggingPage) RestoreInfoScrollPanning();
        }
        _infoDragDx = dx + _infoTouchOrigin;
        if (!_infoDraggingPage) return true;
        ApplyPageDrag(_infoDragDx);
        return true;
    }

    /// <summary>Returns true when the release was consumed (page animation or click suppression).</summary>
    internal bool InfoUp()
    {
        if (!_infoSwipeArmed) return false;
        _infoSwipeArmed = false;
        var width = Math.Max(1, PageHost?.ActualWidth ?? 360);
        var switchThreshold = Math.Max(PageSwitchDistance, width * PageSwitchRatio);
        var crossesThreshold = Math.Abs(_infoDragDx) >= switchThreshold;

        if (!_infoDraggingPage)
        {
            var degraded = _infoHorizontalDecided && crossesThreshold;
            var degradeTarget = PageIndex + (_infoDragDx < 0 ? 1 : -1);
            _infoHorizontalDecided = false;
            if (degraded && degradeTarget is 0 or 1)
            {
                _infoPageSwitching = true;
                try
                {
                    if (degradeTarget == 1) SecondaryPageButton().IsChecked = true;
                    else TabIntro.IsChecked = true;
                }
                finally { _infoPageSwitching = false; }
                return true;
            }
            if (Math.Abs(_infoTouchOrigin) > .5) FinishPageDrag(false, PageIndex);
            return false;
        }
        _infoDraggingPage = false;
        var target = PageIndex + (_infoDragDx < 0 ? 1 : -1);
        var valid = target is 0 or 1;
        var shouldSwitch = valid && crossesThreshold;
        FinishPageDrag(shouldSwitch, target);
        return true;
    }

    private bool BeginPageDrag()
    {
        CompleteInfoPageSettle();
        if (PageHost == null || PageHost.ActualWidth <= 1) return false;
        var current = CurrentPagePanel();
        var other = OtherPagePanel();
        if (current == null || other == null) return false;
        if (IsWebVideoLayout) UpdateWebRecommendationsHint();
        _infoDraggingPage = true;
        if (current.ActualHeight > 1)
            PageHost.Height = current.ActualHeight;
        var c = TransformOf(current);
        c.BeginAnimation(TranslateTransform.XProperty, null);
        c.X = 0;
        var t = TransformOf(other);
        t.BeginAnimation(TranslateTransform.XProperty, null);
        t.X = _infoDragDx < 0 ? PageHost.ActualWidth : -PageHost.ActualWidth;
        other.Opacity = 1;
        other.Visibility = Visibility.Visible;
        return true;
    }

    private void ApplyPageDrag(double dx)
    {
        if (PageHost == null) return;
        var current = CurrentPagePanel();
        var other = OtherPagePanel();
        if (current == null || other == null) return;
        var width = Math.Max(1, PageHost.ActualWidth);
        var c = TransformOf(current);
        var o = TransformOf(other);
        c.BeginAnimation(TranslateTransform.XProperty, null);
        o.BeginAnimation(TranslateTransform.XProperty, null);
        var target = PageIndex + (dx < 0 ? 1 : -1);
        if (target is not (0 or 1))
        {
            c.X = dx * PageEdgeResistance;
            if (other.Visibility == Visibility.Visible) other.Visibility = Visibility.Collapsed;
            return;
        }
        if (other.Visibility != Visibility.Visible)
        {
            other.Opacity = 1;
            other.Visibility = Visibility.Visible;
        }
        c.X = dx;
        o.X = dx + (dx < 0 ? width : -width);
    }

    private void FinishPageDrag(bool switchPage, int target)
    {
        CompleteInfoPageSettle();
        if (PageHost == null) return;
        var current = CurrentPagePanel();
        var other = OtherPagePanel();
        if (current == null || other == null)
        {
            _infoPageAnimating = false;
            return;
        }
        var width = Math.Max(1, PageHost.ActualWidth);
        var c = TransformOf(current);
        var o = TransformOf(other);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(switchPage ? 180 : 150);
        _infoPageAnimating = true;

        var done = false;
        _infoSettleTarget = switchPage ? target : -1;
        void Complete()
        {
            if (done) return;
            done = true;
            _infoActiveSettle = null;
            _cancelInfoSettle = null;
            _infoSettleTarget = -1;
            c.BeginAnimation(TranslateTransform.XProperty, null);
            o.BeginAnimation(TranslateTransform.XProperty, null);
            c.X = 0;
            o.X = 0;
            current.Opacity = 1;
            other.Opacity = 1;
            if (switchPage)
            {
                _infoPageSwitching = true;
                try
                {
                    if (target == 1) SecondaryPageButton().IsChecked = true;
                    else TabIntro.IsChecked = true;
                }
                finally
                {
                    _infoPageSwitching = false;
                }
                current.Visibility = Visibility.Collapsed;
                other.Visibility = Visibility.Visible;
            }
            else
            {
                other.Visibility = Visibility.Collapsed;
                current.Visibility = Visibility.Visible;
            }
            PageHost.Height = double.NaN;
            _infoPageAnimating = false;
            _infoHorizontalDecided = false;
            _infoDraggingPage = false;
            RestoreInfoScrollPanning();
        }
        _infoActiveSettle = Complete;
        _cancelInfoSettle = () =>
        {
            var cx = c.X; var ox = o.X;
            done = true;
            _infoActiveSettle = _cancelInfoSettle = null;
            _infoSettleTarget = -1;
            c.BeginAnimation(TranslateTransform.XProperty, null);
            o.BeginAnimation(TranslateTransform.XProperty, null);
            c.X = cx; o.X = ox;
            _infoPageAnimating = false;
            RestoreInfoScrollPanning();
        };

        if (switchPage)
        {
            var outX = target > PageIndex ? -width : width;
            var moveOut = new DoubleAnimation(c.X, outX, dur) { EasingFunction = ease };
            var moveIn = new DoubleAnimation(o.X, 0, dur) { EasingFunction = ease };
            moveIn.Completed += (_, _) => Complete();
            c.BeginAnimation(TranslateTransform.XProperty, moveOut);
            o.BeginAnimation(TranslateTransform.XProperty, moveIn);
        }
        else
        {
            var back = new DoubleAnimation(c.X, 0, dur) { EasingFunction = ease };
            var backSide = new DoubleAnimation(o.X, o.X < 0 ? -width : width, dur) { EasingFunction = ease };
            back.Completed += (_, _) => Complete();
            c.BeginAnimation(TranslateTransform.XProperty, back);
            o.BeginAnimation(TranslateTransform.XProperty, backSide);
        }
    }

    /// <summary>Tab 点击的入场动画: 从一侧 44px 滑入 + 淡入, 不再是硬切。</summary>
    private void AnimatePageEntrance(FrameworkElement panel, bool fromRight)
    {
        if (_infoPageSwitching || _infoPageAnimating || panel == null) return;
        var t = TransformOf(panel);
        t.BeginAnimation(TranslateTransform.XProperty, null);
        panel.BeginAnimation(OpacityProperty, null);
        t.X = fromRight ? 44 : -44;
        panel.Opacity = 0.55;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        t.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    private RadioButton SecondaryPageButton() => IsWebVideoLayout ? TabRecommended : TabComments;

    private FrameworkElement? SecondaryPagePanel() => IsWebVideoLayout ? WebRecommendationsPanel : CommentsPanel;

    private int PageIndex => SecondaryPageButton()?.IsChecked == true ? 1 : 0;

    private FrameworkElement? CurrentPagePanel() =>
        PageIndex == 1 ? SecondaryPagePanel() : IntroPanel;

    private FrameworkElement? OtherPagePanel() =>
        PageIndex == 1 ? IntroPanel : SecondaryPagePanel();

    private static TranslateTransform TransformOf(FrameworkElement panel)
    {
        if (panel.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        panel.RenderTransform = t;
        return t;
    }

    private bool InfoSwipeAllowed(DependencyObject? source)
    {
        var d = source;
        while (d != null && d != InfoPanel)
        {
            if (d is Slider or TextBox or PasswordBox or Selector) return false;
            d = d is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(d);
        }
        return true;
    }

    private static bool IsPromotedTouchOrPen(MouseEventArgs e) =>
        e.StylusDevice?.TabletDevice.Type is TabletDeviceType.Touch or TabletDeviceType.Stylus;

    private bool IsInsideRichTextBox(DependencyObject? source)
    {
        var d = source;
        while (d != null && d != InfoPanel)
        {
            if (d is RichTextBox) return true;
            d = d is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnInfoTouchDown(object? sender, TouchEventArgs e) =>
        InfoDown(e.GetTouchPoint(InfoPanel).Position, e.OriginalSource as DependencyObject);

    private void OnInfoTouchMove(object? sender, TouchEventArgs e)
    {
        if (InfoMove(e.GetTouchPoint(InfoPanel).Position)) e.Handled = true;
    }

    private void OnInfoTouchUp(object? sender, TouchEventArgs e)
    {
        if (InfoUp()) e.Handled = true;
        _infoHorizontalDecided = false;
        _pendingCaptureDevice = null;
        try { InfoPanel.ReleaseTouchCapture(e.TouchDevice); } catch { }
        RestoreInfoScrollPanning();
    }

    private void OnInfoTouchLostCapture(object? sender, TouchEventArgs e)
    {
        if (_capturingInfoTouch || !ReferenceEquals(e.OriginalSource, InfoPanel) || ReferenceEquals(e.TouchDevice.Captured, InfoPanel)) return;
        _infoSwipeArmed = false;
        if (_infoDraggingPage) FinishPageDrag(switchPage: false, PageIndex);
        _infoDraggingPage = false;
        _infoHorizontalDecided = false;
        _infoDragDx = 0;
        RestoreInfoScrollPanning();
    }

    private void OnInfoStylusDown(object? sender, StylusEventArgs e)
    {
        if (e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch) return;
        InfoDown(e.GetPosition(InfoPanel), e.OriginalSource as DependencyObject);
    }

    private void OnInfoStylusMove(object? sender, StylusEventArgs e)
    {
        if (e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch) return;
        if (InfoMove(e.GetPosition(InfoPanel))) e.Handled = true;
    }

    private void OnInfoStylusUp(object? sender, StylusEventArgs e)
    {
        if (e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch) return;
        if (InfoUp()) e.Handled = true;
        _infoHorizontalDecided = false;
    }

    private void OnInfoMouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (!IsPromotedTouchOrPen(e)) return;
        if (IsInsideRichTextBox(e.OriginalSource as DependencyObject)) e.Handled = true;
    }

    private void OnInfoMouseMove(object? sender, MouseEventArgs e)
    {
        if (!IsPromotedTouchOrPen(e)) return;
        if (IsInsideRichTextBox(e.OriginalSource as DependencyObject)) e.Handled = true;
    }

    private void OnInfoMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (!IsPromotedTouchOrPen(e)) return;
        if (_infoGestureMoved)
        {
            e.Handled = true;
            _infoGestureMoved = false;
            return;
        }
        if (IsInsideRichTextBox(e.OriginalSource as DependencyObject)) e.Handled = true;
    }
}
