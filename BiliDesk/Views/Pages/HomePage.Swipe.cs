using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 首页五个 tab 的**横滑切换**(平板范式)。
///
/// 安卓平板/手机的主页是"手指在内容区左右划 → 换到相邻 tab", 而不是只能点顶部标签。
/// 这里把同一套逻辑搬过来:
///   · 横滑超过 70px 且明显横于竖 → 切到相邻 tab(左滑=下一个, 右滑=上一个);
///   · 竖直手势一律不碰, 交给各 tab 自己的 ScrollViewer;
///   · 从按钮/输入框/滑块起手时不抢;
///   · 拖动超过 10px 后抬手不算点击(卡片不会"划一下点开一个视频")。
///
/// ★ 为什么不做跟手翻页(像详情页那样): 首页五个 tab 各自挂着一面**常驻卡片墙**
///   (见 _fills 的说明), 五面墙同时可见会让布局一次量五份, 而它们本来就是常驻的 ——
///   为了滑动动画把五面墙都摆进视口, 每次滑动要多测几百张卡, 反而更卡。
///   这里只做"划过去就换 tab", 切换本身仍是原来的显隐(零重建)。
/// </summary>
public partial class HomePage
{
    private Point _swipeStart;
    private bool _swipeArmed, _swipeDecided, _swipeMoved;
    private const double SwipeSlop = 14;
    private const double SwipeDistance = 70;

    private void HookTabSwipe()
    {
        TabHost.PreviewTouchDown += OnSwipeTouchDown;
        TabHost.PreviewTouchMove += OnSwipeTouchMove;
        TabHost.PreviewTouchUp += OnSwipeTouchUp;
        TabHost.PreviewStylusDown += OnSwipeStylusDown;
        TabHost.PreviewStylusMove += OnSwipeStylusMove;
        TabHost.PreviewStylusUp += OnSwipeStylusUp;
        TabHost.PreviewMouseLeftButtonUp += OnSwipeMouseUp;
    }

    private void SwipeDown(Point p, DependencyObject? source)
    {
        _swipeStart = p;
        _swipeArmed = SwipeAllowed(source);
        _swipeDecided = false;
        _swipeMoved = false;
    }

    private bool SwipeMove(Point p)
    {
        var dx = p.X - _swipeStart.X;
        var dy = p.Y - _swipeStart.Y;
        if (Math.Abs(dx) > 10 || Math.Abs(dy) > 10) _swipeMoved = true;

        if (!_swipeDecided)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < SwipeSlop) return false;
            _swipeDecided = true;
            if (Math.Abs(dx) <= Math.Abs(dy) * 1.2) return false;   // 竖直: 让给列表
            if (!_swipeArmed) return false;
        }
        if (Math.Abs(dx) < SwipeDistance || Math.Abs(dx) < Math.Abs(dy) * 1.2) return false;

        // 一次手势只切一次
        _swipeDecided = false;
        _swipeArmed = false;
        SwitchTabBy(dx < 0 ? 1 : -1);
        return true;
    }

    private void SwitchTabBy(int delta)
    {
        var order = new[] { RecommendTab, PopularTab, RankingTab, LiveTab, BangumiTab };
        var current = 0;
        for (var i = 0; i < order.Length; i++)
            if (order[i].IsChecked == true) { current = i; break; }
        var next = current + delta;
        if (next < 0 || next >= order.Length) return;   // 到头不绕圈
        order[next].IsChecked = true;
    }

    private bool SwipeAllowed(DependencyObject? source)
    {
        var d = source;
        while (d != null && d != TabHost)
        {
            if (d is ButtonBase or Slider or TextBox or PasswordBox or Selector or Hyperlink) return false;
            d = d is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(d);
        }
        return true;
    }

    private void OnSwipeTouchDown(object? sender, TouchEventArgs e) =>
        SwipeDown(e.GetTouchPoint(TabHost).Position, e.OriginalSource as DependencyObject);

    private void OnSwipeTouchMove(object? sender, TouchEventArgs e)
    {
        if (SwipeMove(e.GetTouchPoint(TabHost).Position)) e.Handled = true;
    }

    private void OnSwipeTouchUp(object? sender, TouchEventArgs e)
    {
        if (_swipeMoved) e.Handled = true;
        _swipeDecided = false;
    }

    private void OnSwipeStylusDown(object? sender, StylusEventArgs e) =>
        SwipeDown(e.GetPosition(TabHost), e.OriginalSource as DependencyObject);

    private void OnSwipeStylusMove(object? sender, StylusEventArgs e)
    {
        if (SwipeMove(e.GetPosition(TabHost))) e.Handled = true;
    }

    private void OnSwipeStylusUp(object? sender, StylusEventArgs e)
    {
        if (_swipeMoved) e.Handled = true;
        _swipeDecided = false;
    }

    /// <summary>触摸/笔提升出来的鼠标: 拖动过就不是点击(卡片不会误开)</summary>
    private void OnSwipeMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice == null) return;   // 真鼠标不管
        if (_swipeMoved) { e.Handled = true; _swipeMoved = false; }
    }
}
