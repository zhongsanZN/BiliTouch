using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BiliDesk.Views;

/// <summary>
/// 右栏「简介 / 评论」的手机式横滑翻页。
///
/// 手机端的做法(和这里一一对应):
///   1. 手指按住时页面**跟手**平移, 两页并排、都被裁在视口里 —— 不是点一下瞬间换页;
///   2. 松手按**位移 + 速度**吸附: 过了 70px 或快速轻扫就翻, 否则弹回;
///   3. 到头了还拖只有 35% 的位移(橡皮筋), 不让你拖出空白;
///   4. 竖直方向一律交给 ScrollViewer —— 横滑和滚动用方向+阈值区分, 互不抢;
///   5. 触摸/笔在 RichTextBox 上的拖动**不选字**(只对 promoted mouse 拦截,
///      真鼠标拖选复制仍然保留);
///   6. 拖动超过 10px 后吞掉 up, 相关视频/按钮不会"拖了一下就点开";
///   7. 点 tab 也不是硬切: 新页从 44px 外滑入 + 淡入 200ms。
///
/// 翻页期间锁 PageHost 高度, 避免两页高度不同导致布局跳动(手机端也是固定视口)。
/// </summary>
public partial class PlayerWindow
{
    private const double PageDragSlop = 12;
    private const double PageSwitchDistance = 70;
    private const double PageEdgeResistance = 0.35;
    private const double PageFlingVelocity = 0.35; // px/ms

    private Point _infoSwipeStart;
    private long _infoSwipeStartTick;
    private bool _infoSwipeArmed, _infoHorizontalDecided, _infoDraggingPage;
    private bool _infoPageAnimating, _infoGestureMoved, _infoPageSwitching;
    private double _infoDragDx;

    private void InfoDown(Point p, DependencyObject? source)
    {
        _infoSwipeStart = p;
        _infoSwipeStartTick = Environment.TickCount64;
        _infoSwipeArmed = InfoSwipeAllowed(source);
        _infoHorizontalDecided = false;
        _infoDraggingPage = false;
        _infoGestureMoved = false;
        _infoDragDx = 0;
    }

    /// <summary>Returns true when the move was consumed by the page drag.</summary>
    private bool InfoMove(Point p)
    {
        if (_infoPageAnimating) return false;
        var dx = p.X - _infoSwipeStart.X;
        var dy = p.Y - _infoSwipeStart.Y;
        if (Math.Abs(dx) > 10 || Math.Abs(dy) > 10) _infoGestureMoved = true;

        if (!_infoHorizontalDecided)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < PageDragSlop) return false;
            if (Math.Abs(dx) <= Math.Abs(dy) * 1.2)
            {
                _infoHorizontalDecided = true; // 竖直: 放给 ScrollViewer
                return false;
            }
            _infoHorizontalDecided = true;
            if (!_infoSwipeArmed) return false;
            _infoDragDx = dx;
            if (!BeginPageDrag()) return false;
        }
        if (!_infoDraggingPage) return false;
        _infoDragDx = dx;
        ApplyPageDrag(dx);
        return true;
    }

    /// <summary>Returns true when the release was consumed (page animation or click suppression).</summary>
    private bool InfoUp()
    {
        if (!_infoDraggingPage)
        {
            _infoHorizontalDecided = false;
            return false;
        }
        _infoDraggingPage = false;
        var elapsed = Math.Max(1, Environment.TickCount64 - _infoSwipeStartTick);
        var velocity = _infoDragDx / elapsed;
        var target = PageIndex + (_infoDragDx < 0 ? 1 : -1);
        var valid = target is 0 or 1;
        var shouldSwitch = valid &&
            (Math.Abs(_infoDragDx) >= PageSwitchDistance || Math.Abs(velocity) >= PageFlingVelocity);
        FinishPageDrag(shouldSwitch, target);
        return true;
    }

    private bool BeginPageDrag()
    {
        if (PageHost == null || PageHost.ActualWidth <= 1) return false;
        var current = CurrentPagePanel();
        var other = OtherPagePanel();
        if (current == null || other == null || current.ActualHeight <= 1) return false;
        _infoDraggingPage = true;
        // 两页高度不同: 翻页过程中按当前页锁高, 免得简介/评论切一半时整页跳一下
        PageHost.Height = current.ActualHeight;
        TransformOf(current).X = 0;
        var t = TransformOf(other);
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
        var target = PageIndex + (dx < 0 ? 1 : -1);
        if (target is not (0 or 1))
        {
            // 已经在最左/最右, 橡皮筋
            c.X = dx * PageEdgeResistance;
            if (other.Visibility == Visibility.Visible) other.Visibility = Visibility.Collapsed;
            return;
        }
        c.X = dx;
        o.X = dx + (dx < 0 ? width : -width);
    }

    private void FinishPageDrag(bool switchPage, int target)
    {
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
        var dur = TimeSpan.FromMilliseconds(switchPage ? 200 : 160);
        _infoPageAnimating = true;

        if (switchPage)
        {
            var outX = target > PageIndex ? -width : width;
            var moveOut = new DoubleAnimation(outX, dur) { EasingFunction = ease };
            var moveIn = new DoubleAnimation(0, dur) { EasingFunction = ease };
            moveIn.Completed += (_, _) =>
            {
                _infoPageSwitching = true;
                try
                {
                    if (target == 1) TabComments.IsChecked = true;
                    else TabIntro.IsChecked = true;
                }
                finally
                {
                    _infoPageSwitching = false;
                }
                c.BeginAnimation(TranslateTransform.XProperty, null);
                o.BeginAnimation(TranslateTransform.XProperty, null);
                c.X = 0;
                o.X = 0;
                current.Opacity = 1;
                other.Opacity = 1;
                PageHost.Height = double.NaN;
                _infoPageAnimating = false;
                _infoHorizontalDecided = false;
            };
            c.BeginAnimation(TranslateTransform.XProperty, moveOut);
            o.BeginAnimation(TranslateTransform.XProperty, moveIn);
        }
        else
        {
            var back = new DoubleAnimation(0, dur) { EasingFunction = ease };
            var backSide = new DoubleAnimation(o.X < 0 ? -width : width, dur) { EasingFunction = ease };
            back.Completed += (_, _) =>
            {
                other.Visibility = Visibility.Collapsed;
                c.BeginAnimation(TranslateTransform.XProperty, null);
                o.BeginAnimation(TranslateTransform.XProperty, null);
                c.X = 0;
                o.X = 0;
                PageHost.Height = double.NaN;
                _infoPageAnimating = false;
                _infoHorizontalDecided = false;
            };
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

    private int PageIndex => TabComments?.IsChecked == true ? 1 : 0;

    private FrameworkElement? CurrentPagePanel() =>
        TabComments?.IsChecked == true ? CommentsPanel : IntroPanel;

    private FrameworkElement? OtherPagePanel() =>
        TabComments?.IsChecked == true ? IntroPanel : CommentsPanel;

    private static TranslateTransform TransformOf(FrameworkElement panel)
    {
        if (panel.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        panel.RenderTransform = t;
        return t;
    }

    /// <summary>
    /// 按钮/滑块/输入框/下拉/超链接自己处理按下, 不抢;
    /// RichTextBox(评论正文)是例外 —— 它要允许横滑翻页, 但选字由下面的 promoted mouse 拦截。
    /// </summary>
    private bool InfoSwipeAllowed(DependencyObject? source)
    {
        var d = source;
        while (d != null && d != InfoPanel)
        {
            if (d is ButtonBase or Slider or TextBox or PasswordBox or Selector or Hyperlink) return false;
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
    }

    private void OnInfoStylusDown(object? sender, StylusEventArgs e) =>
        InfoDown(e.GetPosition(InfoPanel), e.OriginalSource as DependencyObject);

    private void OnInfoStylusMove(object? sender, StylusEventArgs e)
    {
        if (InfoMove(e.GetPosition(InfoPanel))) e.Handled = true;
    }

    private void OnInfoStylusUp(object? sender, StylusEventArgs e)
    {
        if (InfoUp()) e.Handled = true;
        _infoHorizontalDecided = false;
    }

    // 触摸/笔会被 WPF 提升成鼠标事件; 只拦 promoted mouse, 真鼠标拖选复制不受影响。
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
            // 拖过就不算点击: 相关视频/按钮不会被"拖一下就点开"
            e.Handled = true;
            _infoGestureMoved = false;
            return;
        }
        if (IsInsideRichTextBox(e.OriginalSource as DependencyObject)) e.Handled = true;
    }
}
