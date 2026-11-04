using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliDesk.Views.Controls;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 首页五个 tab 的**手机式跟手翻页**(2026-11-04 重写)。
///
/// ## 上一版为什么是错的(用户原话: "轻轻拖一下直接到底了, 不合逻辑")
/// 上一版是"位移超过 70px 就立刻切到相邻 tab", 三个致命问题:
///   ① **没有跟手**: 手指在动, 页面不动, 松手瞬间才一下跳过去 —— 这就是"不合逻辑"的观感;
///   ② **没有速度判定**: 慢慢拖 71px 会切, 快速轻扫 40px 却不切, 手感完全反了;
///   ③ **切完不复位**: 一次手势里 `_swipeDecided=false` 被重置, 继续拖会**再切一页** ——
///      于是"轻轻一拖直接到底"(连切好几页)。
///
/// ## 现在照 B 站手机端(以及本项目的详情页右栏)那套做
///   · **跟手**: 手指横向移动多少, 内容就平移多少(带 35% 橡皮筋抑制越界);
///   · **吸附**: 松手时按 `位移 ≥ 屏宽 18%` **或** `速度 ≥ 0.35 px/ms` 决定翻不翻,
///     否则 180ms 弹回原位;
///   · **一次手势只翻一页**: `_swipeConsumed` 置位后本次手势不再响应;
///   · 竖直手势完全不碰(交给各 tab 自己的 ScrollViewer)。
///
/// ## 为什么五个 tab 用同一个平移容器
/// 五个 ScrollViewer 是**常驻**的(见 HomePage._fills 的说明), 切 tab 只切 Visibility。
/// 翻页动画不能把五面墙同时摆进视口(会一次量五份卡片, 反而卡), 所以这里平移的是
/// **整个内容区**(TabHost 的 RenderTransform), 观感上就是"整页跟着手指走"。
/// 切完之后把平移归零、只留新 tab 可见 —— 与手机端"一页一页翻"的观感一致。
/// </summary>
public partial class HomePage
{
    private Point _swipeStart;
    private long _swipeStartTick;
    private bool _swipeArmed, _swipeDirKnown, _swipeHorizontal, _swipeConsumed;
    /// <summary>这次按下之后有没有移动过(判定"点击 vs 拖动")</summary>
    private bool _swipeMoved;
    private double _swipeDx;
    private TranslateTransform? _tabTrans;

    /// <summary>起手要超过这个位移才判定方向(避免手指微抖就锁成横向)。</summary>
    private const double SwipeSlop = 12;
    /// <summary>松手时位移达到**内容宽度的这个比例**就翻页(B 站手机端约 1/5 ~ 1/4)。</summary>
    private const double SwipePageRatio = 0.18;
    /// <summary>快速轻扫的速度阈值(px/ms)。超过它即使位移小也翻页。</summary>
    private const double SwipeFlingVelocity = 0.35;
    /// <summary>到头了还拖的橡皮筋系数。</summary>
    private const double SwipeEdgeResist = 0.35;
    /// <summary>吸附动画时长。</summary>
    private const int SwipeSnapMs = 180;

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

    /// <summary>TabHost 的平移变换(懒建)。</summary>
    private TranslateTransform TabTrans
    {
        get
        {
            if (_tabTrans != null) return _tabTrans;
            _tabTrans = TabHost.RenderTransform as TranslateTransform ?? new TranslateTransform();
            TabHost.RenderTransform = _tabTrans;
            return _tabTrans;
        }
    }

    internal void SwipeDown(Point p, DependencyObject? source)
    {
        _swipeStart = p;
        _swipeStartTick = Environment.TickCount64;
        _swipeArmed = SwipeAllowed(source);
        _swipeDirKnown = false;
        _swipeHorizontal = false;
        _swipeConsumed = false;
        _swipeMoved = false;
        _swipeDx = 0;
        _tapCard = FindCard(source);
    }

    private VideoCard? _tapCard;

    /// <summary>从命中元素往上找最近的 VideoCard(只认这一层之下的, 别串到别的列表)。</summary>
    private VideoCard? FindCard(DependencyObject? source)
    {
        var d = source;
        while (d != null && d != TabHost)
        {
            if (d is VideoCard card) return card;
            d = d is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Returns true when the move was consumed by the page drag.</summary>
    internal bool SwipeMove(Point p)
    {
        var dx = p.X - _swipeStart.X;
        var dy = p.Y - _swipeStart.Y;
        if (Math.Abs(dx) > 10 || Math.Abs(dy) > 10) _swipeMoved = true;

        if (!_swipeDirKnown)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < SwipeSlop) return false;
            _swipeDirKnown = true;
            // 竖直优先: 滚动列表比翻页常用得多, 斜一点就当滚动
            _swipeHorizontal = _swipeArmed && Math.Abs(dx) > Math.Abs(dy) * 1.2;
            if (!_swipeHorizontal) return false;
        }
        if (!_swipeHorizontal || _swipeConsumed) return false;

        // ★★★ 2026-11-04 修"缓慢滑动时画面抽搐":
        //   一旦判定为横向, 必须**立刻取消 ScrollViewer 的惯性滚动**并锁定翻译值。
        //   否则: 手指慢慢横移时, ScrollViewer 的垂直平移仍在按微小的 dy 抖动调整
        //   (每帧 ±1~2px 的来回), 而 TabHost 同时被我们左右平移 —— 两个位移叠加,
        //   观感就是"画面在抽搐"。这里把纵向彻底钉住, 只让横向生效。
        if (_swipeDx == 0 && dx != 0) FreezeVertical();
        _swipeDx = dx;
        // 到头(已经在第一个/最后一个 tab)还往那个方向拖: 橡皮筋
        var target = CurrentTabIndex + (dx < 0 ? 1 : -1);
        TabTrans.X = target is >= 0 and < 5 ? dx : dx * SwipeEdgeResist;
        return true;
    }

    /// <summary>
    /// 横向手势一旦成立, 就把当前 tab 的纵向滚动**冻结**在原地。
    /// ★ 只冻一次(靠 _swipeDx==0 判断首次): 每帧都动 ScrollViewer 反而会打断它的惯性。
    /// </summary>
    private void FreezeVertical()
    {
        var sv = ScrollOf(CurrentTabFromVm() ?? HomeTabKind.Recommend);
        if (sv == null) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset);   // 打断进行中的滚动动画
    }

    /// <summary>手指抬起: 按位移+速度决定翻页还是弹回。</summary>
    internal bool SwipeUp()
    {
        if (!_swipeHorizontal) return false;
        var elapsed = Math.Max(1, Environment.TickCount64 - _swipeStartTick);
        var velocity = _swipeDx / elapsed;
        var width = Math.Max(1, TabHost.ActualWidth);
        var target = CurrentTabIndex + (_swipeDx < 0 ? 1 : -1);
        var valid = target is >= 0 and < 5;

        var shouldSwitch = valid && !_swipeConsumed &&
            (Math.Abs(_swipeDx) >= width * SwipePageRatio || Math.Abs(velocity) >= SwipeFlingVelocity);

        // 一次手势只翻一页: 翻过之后本次手势的剩余移动全部忽略
        if (shouldSwitch) _swipeConsumed = true;
        AnimateSnap(shouldSwitch, target, width);
        return true;
    }

    /// <summary>吸附动画: 翻页则把内容滑出去再切 tab, 否则弹回原地。</summary>
    private void AnimateSnap(bool switchPage, int target, double width)
    {
        var trans = TabTrans;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(SwipeSnapMs);

        if (!switchPage)
        {
            var back = new DoubleAnimation(0, duration) { EasingFunction = ease };
            back.Completed += (_, _) =>
            {
                trans.BeginAnimation(TranslateTransform.XProperty, null);
                trans.X = 0;
            };
            trans.BeginAnimation(TranslateTransform.XProperty, back);
            return;
        }

        // 翻页: 让内容顺着手指方向滑出屏幕, 动画结束后再切 tab 并把平移归零
        var outX = _swipeDx < 0 ? -width : width;
        var slide = new DoubleAnimation(outX, duration) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            trans.BeginAnimation(TranslateTransform.XProperty, null);
            trans.X = 0;
            SwitchTabTo(target);
        };
        trans.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    /// <summary>当前选中的 tab 下标(0=推荐 … 4=追番)</summary>
    internal int CurrentTabIndex
    {
        get
        {
            var order = TabButtons;
            for (var i = 0; i < order.Length; i++)
                if (order[i].IsChecked == true) return i;
            return 0;
        }
    }

    private RadioButton[] TabButtons => new[]
    {
        RecommendTab, PopularTab, RankingTab, LiveTab, BangumiTab
    };

    private void SwitchTabTo(int index)
    {
        var order = TabButtons;
        if (index < 0 || index >= order.Length) return;
        order[index].IsChecked = true;
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

    /// <summary>
    /// 触控板横向双指滑动(由 MainWindow.OnHorizontalWheel 转发, 2026-11-04)。
    ///
    /// ★ 与触摸手势共用同一套"切一页"逻辑, 但**不跟手**: 触控板没有绝对坐标,
    ///   只能拿到离散的 delta, 所以直接切一页 + 走同款吸附动画的尾段。
    /// ★ 已经在动画中(_tabTrans 有活动动画)时忽略, 否则连划会叠加。
    /// </summary>
    internal void HandleHorizontalWheel(int direction)
    {
        if (direction == 0) return;
        // 动画中不响应(连划时避免叠起来)
        if (_tabTrans != null && _tabTrans.HasAnimatedProperties) return;
        var target = CurrentTabIndex + direction;
        if (target is < 0 or >= 5) return;
        // 复用吸附动画: 先把内容挪一小段, 再让它滑出去并切页
        _swipeDx = direction > 0 ? -1 : 1;
        AnimateSnap(true, target, Math.Max(1, TabHost.ActualWidth));
    }

    private void OnSwipeTouchDown(object? sender, TouchEventArgs e) =>
        SwipeDown(e.GetTouchPoint(TabHost).Position, e.OriginalSource as DependencyObject);

    private void OnSwipeTouchMove(object? sender, TouchEventArgs e)
    {
        if (SwipeMove(e.GetTouchPoint(TabHost).Position)) e.Handled = true;
    }

    private void OnSwipeTouchUp(object? sender, TouchEventArgs e)
    {
        var moved = _swipeMoved;
        // ★ 只有"横向翻页"才吞掉 up; 竖直滚动必须让 ScrollViewer 正常收尾
        if (SwipeUp()) e.Handled = true;
        // 没拖动 = 点击: 转发给卡片(ScrollViewer 抢走触摸时这条是唯一来源)
        if (!moved && _tapCard != null) _tapCard.OpenVideo();
        _tapCard = null;
        _swipeDirKnown = false;
        _swipeHorizontal = false;
    }

    private void OnSwipeStylusDown(object? sender, StylusEventArgs e) =>
        SwipeDown(e.GetPosition(TabHost), e.OriginalSource as DependencyObject);

    private void OnSwipeStylusMove(object? sender, StylusEventArgs e)
    {
        if (SwipeMove(e.GetPosition(TabHost))) e.Handled = true;
    }

    private void OnSwipeStylusUp(object? sender, StylusEventArgs e)
    {
        var moved = _swipeMoved;
        if (SwipeUp()) e.Handled = true;
        if (!moved && _tapCard != null) _tapCard.OpenVideo();
        _tapCard = null;
        _swipeDirKnown = false;
        _swipeHorizontal = false;
    }

    /// <summary>触摸/笔提升出来的鼠标: 横向拖动已消费时吞掉, 免得变成点击</summary>
    private void OnSwipeMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice == null) return;   // 真鼠标不管
        if (_swipeMoved) { e.Handled = true; _swipeMoved = false; }
    }
}
