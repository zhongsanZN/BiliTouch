using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliTouch.Views.Controls;

namespace BiliTouch.Views.Pages;

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
    /// <summary>被 <see cref="FreezeVertical"/> 关掉平移的那个 tab(null = 没冻)</summary>
    private HomeTabKind? _frozenKind;
    /// <summary>冻结前的 PanningMode, 解冻时原样还回去</summary>
    private PanningMode _frozenPanMode = PanningMode.VerticalFirst;
    /// <summary>冻结时钉住的纵向偏移(常量): 每帧都回到它, 画面就完全不会上下动</summary>
    private double _frozenOffset;

    /// <summary>本次手势是否已经冻结过一次纵向滚动</summary>
    private bool _verticalFrozenOnce;

    /// <summary>双页并排中间态：横滑或触控板跟手期间临时拉进视口并排显示的相邻页</summary>
    private ScrollViewer? _peerScroll;
    private int _peerIndex = -1;
    private TouchDevice? _pendingSwipeTouch;

    /// <summary>吸附动画时长。</summary>
    private const int SwipeSnapMs = 180;

    private void HookTabSwipe()
    {
        TabHost.PreviewTouchDown += OnSwipeTouchDown;
        TabHost.PreviewTouchMove += OnSwipeTouchMove;
        TabHost.PreviewTouchUp += OnSwipeTouchUp;
        TabHost.LostTouchCapture += OnSwipeLostTouchCapture;
        TabHost.PreviewStylusDown += OnSwipeStylusDown;
        TabHost.PreviewStylusMove += OnSwipeStylusMove;
        TabHost.PreviewStylusUp += OnSwipeStylusUp;
        TabHost.PreviewMouseLeftButtonUp += OnSwipeMouseUp;
    }

    private static HomeTabKind KindOfIndex(int index) => index switch
    {
        1 => HomeTabKind.Popular,
        2 => HomeTabKind.Ranking,
        3 => HomeTabKind.Live,
        4 => HomeTabKind.Bangumi,
        _ => HomeTabKind.Recommend,
    };

    private static TranslateTransform PeerTrans(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        element.RenderTransform = t;
        return t;
    }

    private void EnsurePeerVisible(int targetIndex, double offsetX)
    {
        if (targetIndex is < 0 or >= 5 || targetIndex == CurrentTabIndex)
        {
            ClearPeerPreview();
            return;
        }
        var kind = KindOfIndex(targetIndex);
        EnsureFill(kind);
        var peer = ScrollOf(kind);
        if (peer == null) return;
        if (!ReferenceEquals(_peerScroll, peer))
        {
            ClearPeerPreview();
            _peerScroll = peer;
            _peerIndex = targetIndex;
            peer.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        }
        PeerTrans(peer).X = offsetX;
    }

    private void ClearPeerPreview()
    {
        if (_peerScroll == null) return;
        var peer = _peerScroll;
        _peerScroll = null;
        _peerIndex = -1;
        PeerTrans(peer).X = 0;
        BindingOperations.GetBindingExpression(peer, VisibilityProperty)?.UpdateTarget();
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
        _verticalFrozenOnce = false;
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
            if (!_swipeHorizontal)
            {
                // 判定为竖直 ⇒ 这是滚动, 立刻把平移还回去(并解冻任何遗留状态)
                UnfreezeVertical();
                return false;
            }
            if (_pendingSwipeTouch != null)
            {
                try { TabHost.CaptureTouch(_pendingSwipeTouch); } catch { }
                _pendingSwipeTouch = null;
            }
        }
        if (!_swipeHorizontal || _swipeConsumed) return false;

        // ★★★ 2026-11-04 修"缓慢滑动时画面抽搐":
        //   一旦判定为横向, 必须**立刻取消 ScrollViewer 的惯性滚动**并锁定翻译值。
        //   否则: 手指慢慢横移时, ScrollViewer 的垂直平移仍在按微小的 dy 抖动调整
        //   (每帧 ±1~2px 的来回), 而 TabHost 同时被我们左右平移 —— 两个位移叠加,
        //   观感就是"画面在抽搐"。这里把纵向彻底钉住, 只让横向生效。
        // ★ 抽搐的根因已由 RootPoint(根坐标)解决 —— 那才是真正的反馈环。
        //   FreezeVertical 现在只作为**补充保险**在首帧调一次: 每帧都钉反而会和
        //   ScrollViewer 的正常滚动打架(它自己也有轻微的平滑动画)。
        if (!_verticalFrozenOnce)
        {
            _verticalFrozenOnce = true;
            FreezeVertical();
        }
        _swipeDx = dx;
        var width = Math.Max(1, TabHost.ActualWidth);
        // 到头(已经在第一个/最后一个 tab)还往那个方向拖: 橡皮筋; 否则并排展示相邻 tab 中间态
        var target = CurrentTabIndex + (dx < 0 ? 1 : -1);
        if (target is >= 0 and < 5)
        {
            EnsurePeerVisible(target, dx < 0 ? width : -width);
            TabTrans.X = dx;
        }
        else
        {
            ClearPeerPreview();
            TabTrans.X = dx * SwipeEdgeResist;
        }
        return true;
    }

    /// <summary>
    /// 横向手势一旦成立, 就把当前 tab 的纵向滚动**钉死在按下那一刻的位置**。
    ///
    /// ★★★ 2026-11-04 修"缓慢滑动时画面抽搐"(第三版, 找到真因):
    ///
    ///   第一版: 每帧 `ScrollToVerticalOffset(sv.VerticalOffset)` —— 把**当前值**写回去。
    ///           没用: 当前值本身就在被 WPF 的平移改来改去, 等于"抹平一个正在动的量"。
    ///   第二版: 把 PanningMode 设 None —— 也没用: PanningMode 是在**触摸按下那一刻**
    ///           被读取来决定要不要挂 Manipulation 的, 手势中途改成 None **不会取消
    ///           已经在跑的那次平移**。
    ///
    ///   所以两版都还抽搐。真正的做法是:
    ///     **在按下时记住偏移(_frozenOffset), 之后每帧强制回到这个常量。**
    ///   常量不动 ⇒ 画面纵向完全静止; 而横向由 TabHost 的 TranslateTransform 负责。
    ///   两个方向的驱动彻底分离, 不会再互相拉扯。
    ///
    ///   PanningMode=None 仍然设上 —— 它能让**后续**的平移不再启动, 是有益的补充。
    /// </summary>
    private void FreezeVertical()
    {
        var kind = CurrentTabFromVm() ?? HomeTabKind.Recommend;
        var sv = ScrollOf(kind);
        if (sv == null) return;
        if (_frozenKind == null)
        {
            // 首次冻结: 记下此刻的偏移作为"常量", 并关掉后续平移
            _frozenKind = kind;
            _frozenPanMode = sv.PanningMode;
            _frozenOffset = sv.VerticalOffset;
            sv.SetCurrentValue(ScrollViewer.PanningModeProperty, PanningMode.None);
        }
        // 每帧都钉回那个常量(平移期间 WPF 仍可能改动它)
        if (Math.Abs(sv.VerticalOffset - _frozenOffset) > 0.5)
            sv.ScrollToVerticalOffset(_frozenOffset);
    }

    /// <summary>恢复上一次 <see cref="FreezeVertical"/> 关掉的触摸平移</summary>
    private void UnfreezeVertical()
    {
        if (_frozenKind is not { } kind) return;
        var sv = ScrollOf(kind);
        _frozenKind = null;
        if (sv == null) return;
        sv.SetCurrentValue(ScrollViewer.PanningModeProperty, _frozenPanMode);
    }

    /// <summary>手指抬起: 按位移+速度决定翻页还是弹回。</summary>
    internal bool SwipeUp()
    {
        // ★ 无论翻不翻页, 抬手指都要把触摸平移还给 ScrollViewer
        UnfreezeVertical();
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

    /// <summary>吸附动画: 翻页则双页并排滑向目标再切 tab, 否则弹回原地。</summary>
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
                ClearPeerPreview();
                _wheelSwitching = false;
            };
            trans.BeginAnimation(TranslateTransform.XProperty, back);
            return;
        }

        // 翻页: 确保目标相邻页在侧面并排就位, 随 TabHost 一起滑入视口中央
        EnsurePeerVisible(target, target > CurrentTabIndex ? width : -width);
        var outX = target > CurrentTabIndex ? -width : width;
        var slide = new DoubleAnimation(outX, duration) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            trans.BeginAnimation(TranslateTransform.XProperty, null);
            trans.X = 0;
            ClearPeerPreview();
            SwitchTabTo(target);
            _wheelSwitching = false;
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
            // 与详情页 InfoSwipeAllowed 一致: 只排除独占拖拽/选字的滑块、输入框与选择器,
            // 允许从 VideoCard 及其内部元素上起手横滑翻页。
            if (d is Slider or TextBoxBase or PasswordBox or Selector or Hyperlink) return false;
            d = d is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(d);
        }
        return true;
    }

    /// <summary>
    /// 触控板双指连续横滑的跟手中间态预览(progress ∈ [-1, 1], 正值代表往右一页翻)。
    /// </summary>
    internal void PreviewHorizontalWheel(double progress)
    {
        if (_wheelSwitching || _swipeHorizontal) return;
        var width = Math.Max(1, TabHost.ActualWidth);
        var maxPreview = Math.Min(width * 0.38, 220);
        var dx = -progress * maxPreview;
        _swipeDx = dx;
        var target = CurrentTabIndex + (dx < 0 ? 1 : -1);
        if (target is >= 0 and < 5)
        {
            EnsurePeerVisible(target, dx < 0 ? width : -width);
            TabTrans.X = dx;
        }
        else
        {
            ClearPeerPreview();
            TabTrans.X = dx * SwipeEdgeResist;
        }
    }

    /// <summary>
    /// 触控板双指横滑未达整格阈值而抬手停顿时, 按跟手进度收尾吸附或弹回。
    /// </summary>
    internal void SettleHorizontalWheelPreview(bool commit)
    {
        if (_wheelSwitching || _swipeHorizontal) return;
        if (Math.Abs(TabTrans.X) < 0.5 && _peerScroll == null) return;
        var width = Math.Max(1, TabHost.ActualWidth);
        var target = CurrentTabIndex + (_swipeDx < 0 ? 1 : -1);
        var valid = target is >= 0 and < 5;
        if (commit && valid)
        {
            _wheelSwitching = true;
            AnimateSnap(true, target, width);
        }
        else
        {
            AnimateSnap(false, CurrentTabIndex, width);
        }
    }

    /// <summary>
    /// 触控板横向双指滑动 / 鼠标倾斜滚轮(由 MainWindow.OnHorizontalWheel 转发, 2026-11-04)。
    ///
    /// ★ 若前序小 delta 已经启动了跟手中间态(TabTrans.X != 0), 直接从当前双页并排中间位置
    ///   平滑收尾滑入目标页; 若是单次整格倾斜滚轮, 同样先把目标页并排摆到侧面再滑入。
    /// ★ 已经在动画中(_wheelSwitching)时忽略, 否则连划会叠加。
    /// </summary>
    internal void HandleHorizontalWheel(int direction)
    {
        if (direction == 0) return;
        if (_wheelSwitching) return;
        var target = CurrentTabIndex + direction;
        var width = Math.Max(1, TabHost.ActualWidth);
        if (target is < 0 or >= 5)
        {
            if (Math.Abs(TabTrans.X) > 0.5) AnimateSnap(false, CurrentTabIndex, width);
            return;
        }
        _wheelSwitching = true;
        _swipeDx = direction > 0 ? -1 : 1;
        AnimateSnap(true, target, width);
    }

    /// <summary>触控板切页进行中(动画期间拒绝新的切页请求)</summary>
    private bool _wheelSwitching;

    private void OnSwipeTouchDown(object? sender, TouchEventArgs e)
    {
        _pendingSwipeTouch = e.TouchDevice;
        SwipeDown(RootPoint(e), e.OriginalSource as DependencyObject);
    }

    /// <summary>
    /// 取触摸点在**窗口根坐标**里的位置(不是 TabHost 局部坐标)。
    ///
    /// ★★★ 2026-11-04 这是"缓慢横滑时画面抽搐"的**真正根因**, 前面三版都修错了地方:
    ///
    ///   原来用的是 `e.GetTouchPoint(TabHost).Position` —— 这是 **TabHost 自己的局部坐标**。
    ///   而我们做跟手平移时, 改的正是 `TabHost.RenderTransform`。
    ///   于是形成**坐标反馈环**:
    ///     手指不动 → 我们把 TabHost 左移 10px → 同一触摸点在 TabHost 局部坐标里
    ///     变成"右移 10px" → 下一帧算出的 dx 少了 10 → 我们又往右挪回去 →
    ///     局部坐标又变 … 每帧振荡, 观感就是**抽搐**。
    ///   手指划得越慢, 单帧位移越小, 振荡越明显(快划时位移远大于振荡幅度, 反而看不出来)。
    ///
    ///   修法: 用**根坐标**(GetTouchPoint(null)), 它不随 TabHost 的 RenderTransform 变化。
    ///   这与 TouchSupport 里"拖动被当成点击"那个 bug 是同一类问题、同一个解法。
    /// </summary>
    private Point RootPoint(TouchEventArgs e) => e.GetTouchPoint(null).Position;

    /// <summary>笔的根坐标版本(同上)。取不到点时退回 TabHost 局部坐标, 绝不抛。</summary>
    private Point RootPoint(StylusEventArgs e)
    {
        try
        {
            var pts = e.GetStylusPoints(null);
            if (pts != null && pts.Count > 0) return pts[0].ToPoint();
        }
        catch { /* 取不到就退回下面 */ }
        return e.GetPosition(TabHost);
    }

    private void OnSwipeTouchMove(object? sender, TouchEventArgs e)
    {
        if (SwipeMove(RootPoint(e))) e.Handled = true;
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
        _pendingSwipeTouch = null;
        try { TabHost.ReleaseTouchCapture(e.TouchDevice); } catch { }
    }

    private void OnSwipeLostTouchCapture(object? sender, TouchEventArgs e)
    {
        if (_swipeHorizontal && !_swipeConsumed)
            AnimateSnap(false, CurrentTabIndex, Math.Max(1, TabHost.ActualWidth));
        UnfreezeVertical();
        _swipeDirKnown = false;
        _swipeHorizontal = false;
        _pendingSwipeTouch = null;
    }

    private void OnSwipeStylusDown(object? sender, StylusEventArgs e) =>
        SwipeDown(RootPoint(e), e.OriginalSource as DependencyObject);

    private void OnSwipeStylusMove(object? sender, StylusEventArgs e)
    {
        if (SwipeMove(RootPoint(e))) e.Handled = true;
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
