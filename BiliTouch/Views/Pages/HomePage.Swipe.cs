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
/// 首页五个 tab 的跟手翻页与触控板全幅双页并排中间态。
/// </summary>
public partial class HomePage
{
    private Point _swipeStart;
    private bool _swipeArmed, _swipeDirKnown, _swipeHorizontal, _swipeConsumed;
    /// <summary>这次按下之后有没有移动过(判定"点击 vs 拖动")</summary>
    private bool _swipeMoved;
    private double _swipeDx;
    private TranslateTransform? _tabTrans;
    private Action? _activeSnapComplete, _cancelActiveSnap;
    private int _snapTarget = -1;
    private double _touchDragOrigin, _wheelDragOrigin;
    private double _touchContactDx, _touchCommittedShift;
    private double _wheelContactDx;
    private bool _wheelPreviewActive;
    private bool _contactActive, _ownsTouchCapture;
    private StylusDevice? _pendingSwipeStylus;
    internal static Action<string>? InputTrace;
    private void TraceInput(string route) => InputTrace?.Invoke($"{Environment.TickCount64} {route} tab={CurrentTabIndex} x={TabTrans.X:F2} armed={_swipeArmed} horizontal={_swipeHorizontal}");

    private double TakeOverSnap()
    {
        var target = _snapTarget;
        var previous = CurrentTabIndex;
        _cancelActiveSnap?.Invoke();
        if (target >= 0 && target != previous)
        {
            // The previous release already chose this target. Rebase onto it
            // without moving either visible page, so another contact starts
            // from that decision rather than repeatedly repaying the same page.
            var width = Math.Max(1, TabHost.ActualWidth);
            var x = TabTrans.X + (target - previous) * width;
            ClearPeerPreview();
            SwitchTabTo(target);
            EnsurePeerVisible(previous, previous < target ? -width : width);
            TabTrans.X = x;
            PreviewTabIndicator(target, previous, Math.Abs(x) / width);
        }
        return TabTrans.X;
    }

    /// <summary>起手要超过这个位移才判定方向(避免手指微抖就锁成横向)。</summary>
    private const double SwipeSlop = 12;
    /// <summary>松手时位移需自然过半（内容宽度的 50%）才触发翻页。</summary>
    private const double SwipePageRatio = 0.50;
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
    private const int SwipeSnapMs = 170;

    private void HookTabSwipe()
    {
        TabHost.AddHandler(PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnSwipeTouchDown), true);
        TabHost.AddHandler(PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(OnSwipeTouchMove), true);
        TabHost.AddHandler(PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(OnSwipeTouchUp), true);
        TabHost.LostTouchCapture += OnSwipeLostTouchCapture;
        TabHost.AddHandler(PreviewStylusDownEvent, new StylusDownEventHandler(OnSwipeStylusDown), true);
        TabHost.AddHandler(PreviewStylusMoveEvent, new StylusEventHandler(OnSwipeStylusMove), true);
        TabHost.AddHandler(PreviewStylusUpEvent, new StylusEventHandler(OnSwipeStylusUp), true);
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

    private void CompleteActiveSnap()
    {
        if (_activeSnapComplete == null) return;
        var finish = _activeSnapComplete;
        _activeSnapComplete = null;
        finish();
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
            ApplyReadyCovers(peer);
            _peerScroll = peer;
            _peerIndex = targetIndex;
            if (peer is Helpers.TabViewportScrollViewer preview) preview.IsGesturePreview = true;
            peer.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        }
        var pt = PeerTrans(peer);
        pt.BeginAnimation(TranslateTransform.XProperty, null);
        pt.X = offsetX;
    }

    private void ClearPeerPreview()
    {
        if (_peerScroll == null) return;
        var peer = _peerScroll;
        if (peer is Helpers.TabViewportScrollViewer preview) preview.IsGesturePreview = false;
        _peerScroll = null;
        _peerIndex = -1;
        var pt = PeerTrans(peer);
        pt.BeginAnimation(TranslateTransform.XProperty, null);
        pt.X = 0;
        BindingOperations.GetBindingExpression(peer, VisibilityProperty)?.UpdateTarget();
    }

    private void PreviewTabIndicator(int fromIndex, int toIndex, double ratio)
    {
        if (HomeTabs == null || HomeTabs.ActualWidth <= 0) return;
        var fromBtn = TabButtonOf(KindOfIndex(fromIndex));
        var toBtn = TabButtonOf(KindOfIndex(toIndex));
        if (fromBtn == null || toBtn == null || fromBtn.ActualWidth <= 0 || toBtn.ActualWidth <= 0) return;
        var x0 = fromBtn.TransformToAncestor(HomeTabs).Transform(new Point(0, 0)).X + (fromBtn.ActualWidth - TabIndicator.Width) / 2;
        var x1 = toBtn.TransformToAncestor(HomeTabs).Transform(new Point(0, 0)).X + (toBtn.ActualWidth - TabIndicator.Width) / 2;
        var r = Math.Clamp(ratio, 0, 1);
        ClearIndicatorAnimations();
        TabIndicatorTrans.X = x0 + (x1 - x0) * r;
        _tabIndicatorPlaced = true;
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
        _touchDragOrigin = TakeOverSnap();
        _wheelPreviewActive = false;
        _swipeStart = p;
        _swipeArmed = SwipeAllowed(source);
        _swipeDirKnown = false;
        _swipeHorizontal = false;
        _swipeConsumed = false;
        _swipeMoved = false;
        _verticalFrozenOnce = false;
        _swipeDx = _touchDragOrigin;
        _touchContactDx = _touchCommittedShift = 0;
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
        if (!_swipeArmed) return false;
        var dx = p.X - _swipeStart.X;
        var dy = p.Y - _swipeStart.Y;
        if (Math.Abs(dx) > 10 || Math.Abs(dy) > 10) _swipeMoved = true;

        if (!_swipeDirKnown)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < SwipeSlop) return false;
            _swipeHorizontal = _swipeArmed && Math.Abs(dx) > Math.Abs(dy) * 1.2;
            _swipeDirKnown = _swipeHorizontal;
            if (!_swipeHorizontal)
            {
                UnfreezeVertical();
                return false;
            }
            CompleteActiveSnap();
            if (_pendingSwipeTouch != null)
            {
                try { TabHost.CaptureTouch(_pendingSwipeTouch); } catch { }
                _ownsTouchCapture = ReferenceEquals(_pendingSwipeTouch.Captured, TabHost);
            }
            else if (_pendingSwipeStylus != null)
                _pendingSwipeStylus.Capture(TabHost);
            Helpers.TouchSupport.CancelPendingTap();
        }
        if (!_swipeHorizontal || _swipeConsumed) return false;

        if (!_verticalFrozenOnce)
        {
            _verticalFrozenOnce = true;
            FreezeVertical();
        }
        else if (_frozenKind is { } fk && ScrollOf(fk) is { } sv && Math.Abs(sv.VerticalOffset - _frozenOffset) > 0.5)
        {
            sv.ScrollToVerticalOffset(_frozenOffset);
        }

        dx += _touchDragOrigin;
        var width = Math.Max(1, TabHost.ActualWidth);
        var shift = RebaseDisplacement(ref dx, width);
        _touchDragOrigin += shift;
        _touchCommittedShift += shift;
        _touchContactDx = p.X - _swipeStart.X + _touchCommittedShift;
        _swipeDx = dx;
        ApplySwipeDisplacement(dx, width);
        return true;
    }

    // Once the peer completely fills the viewport, make it the origin before
    // continuing. Carry the same shift into the contact's absolute coordinate
    // baseline; otherwise the next packet jumps back to the previous page.
    private double RebaseDisplacement(ref double dx, double width)
    {
        double shift = 0;
        while ((dx <= -width && CurrentTabIndex < 4) || (dx >= width && CurrentTabIndex > 0))
        {
            var step = dx < 0 ? 1 : -1;
            UnfreezeVertical();
            ClearPeerPreview();
            SwitchTabTo(CurrentTabIndex + step);
            dx += step * width;
            shift += step * width;
            if (_swipeHorizontal) FreezeVertical();
        }
        return shift;
    }

    private void ApplySwipeDisplacement(double dx, double width)
    {
        var current = CurrentTabIndex;
        var target = current + (dx < 0 ? 1 : -1);
        var trans = TabTrans;
        trans.BeginAnimation(TranslateTransform.XProperty, null);

        if (Math.Abs(dx) < .01)
        {
            ClearPeerPreview();
            trans.X = 0;
            MoveTabIndicator(KindOfIndex(current), animate: false);
            return;
        }

        if (target is >= 0 and < 5)
        {
            EnsurePeerVisible(target, dx < 0 ? width : -width);
            trans.X = dx;
            PreviewTabIndicator(current, target, Math.Min(1.0, Math.Abs(dx) / width));
        }
        else
        {
            ClearPeerPreview();
            trans.X = Math.Sign(dx) * width * SwipeEdgeResist * (1 - Math.Exp(-Math.Abs(dx) / width));
        }
    }

    private void FreezeVertical()
    {
        var kind = CurrentTabFromVm() ?? HomeTabKind.Recommend;
        var sv = ScrollOf(kind);
        if (sv == null) return;
        if (_frozenKind == null)
        {
            _frozenKind = kind;
            _frozenPanMode = sv.PanningMode;
            _frozenOffset = sv.VerticalOffset;
            sv.SetCurrentValue(ScrollViewer.PanningModeProperty, PanningMode.None);
            Helpers.TrackpadScroll.Cancel(sv);
        }
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

    /// <summary>手指抬起: 按自然过半位移(>=50%)决定翻页还是弹回。</summary>
    internal bool SwipeUp()
    {
        if (!_swipeArmed) return false;
        _swipeArmed = false;
        UnfreezeVertical();
        if (!_swipeHorizontal)
        {
            if (Math.Abs(_touchDragOrigin) > .5) AnimateSnap(false, CurrentTabIndex, Math.Max(1, TabHost.ActualWidth));
            return false;
        }
        _swipeHorizontal = false;
        var width = Math.Max(1, TabHost.ActualWidth);
        var target = CurrentTabIndex + (_swipeDx < 0 ? 1 : -1);
        var valid = target is >= 0 and < 5;

        var shouldSwitch = valid && !_swipeConsumed &&
            (Math.Abs(_swipeDx) >= width * SwipePageRatio ||
             (Math.Sign(_touchContactDx) == Math.Sign(_swipeDx) && Math.Abs(_touchContactDx) >= width * SwipePageRatio));

        _swipeConsumed = true;
        AnimateSnap(shouldSwitch, target, width);
        return true;
    }

    /// <summary>吸附动画: 翻页则双页并排滑向目标再切 tab, 否则弹回原地。</summary>
    private void AnimateSnap(bool switchPage, int target, double width)
    {
        CompleteActiveSnap();
        var trans = TabTrans;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(SwipeSnapMs);

        var done = false;
        _snapTarget = switchPage ? target : -1;
        void Finish()
        {
            if (done) return;
            done = true;
            _activeSnapComplete = null;
            _cancelActiveSnap = null;
            _snapTarget = -1;
            trans.BeginAnimation(TranslateTransform.XProperty, null);
            trans.X = 0;
            ClearPeerPreview();
            if (switchPage) SwitchTabTo(target);
            else MoveTabIndicator(CurrentTabFromVm() ?? HomeTabKind.Recommend);
            TraceInput($"snap.finish switch={switchPage}");
        }
        _activeSnapComplete = Finish;
        _cancelActiveSnap = () =>
        {
            var x = trans.X;
            done = true;
            _activeSnapComplete = _cancelActiveSnap = null;
            _snapTarget = -1;
            trans.BeginAnimation(TranslateTransform.XProperty, null);
            trans.X = x;
        };

        if (!switchPage)
        {
            MoveTabIndicator(CurrentTabFromVm() ?? HomeTabKind.Recommend);
            var back = new DoubleAnimation(trans.X, 0, duration) { EasingFunction = ease };
            back.Completed += (_, _) => Finish();
            trans.BeginAnimation(TranslateTransform.XProperty, back);
            return;
        }

        // 翻页: 确保目标相邻页在侧面并排就位, 随 TabHost 一起滑入视口中央
        EnsurePeerVisible(target, target > CurrentTabIndex ? width : -width);
        MoveTabIndicator(KindOfIndex(target));
        var outX = target > CurrentTabIndex ? -width : width;
        var slide = new DoubleAnimation(trans.X, outX, duration) { EasingFunction = ease };
        slide.Completed += (_, _) => Finish();
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
        QueuePreparedTabs();
    }

    private bool SwipeAllowed(DependencyObject? source)
    {
        var d = source;
        while (d != null && d != TabHost)
        {
            if (d is Slider or TextBoxBase or PasswordBox or Selector or Hyperlink) return false;
            d = d is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(d);
        }
        return true;
    }

    /// <summary>
    /// 触控板双指连续横滑的全幅跟手中间态预览(progress ∈ [-1, 1], 正值代表往右一页翻)。
    /// </summary>
    internal void PreviewHorizontalWheel(double progress)
    {
        if (_swipeHorizontal) return;
        if (!_wheelPreviewActive)
        {
            _wheelDragOrigin = TakeOverSnap();
            _wheelPreviewActive = true;
        }
        var width = Math.Max(1, TabHost.ActualWidth);
        // One wheel/contact session owns one origin and its two neighbours.
        // Never rebase/commit here: arbitrarily many packets, reversals, or a
        // displacement beyond a whole page must wait for this session's release.
        progress = Math.Clamp(progress, -1, 1);
        var dx = Math.Clamp(_wheelDragOrigin - progress * width, -width * .999, width * .999);
        _wheelContactDx = -progress * width;
        _swipeDx = dx;
        ApplySwipeDisplacement(dx, width);
        TraceInput($"wheel progress={progress:F3}");
    }

    /// <summary>
    /// 触控板双指横滑抬手停顿时, 按跟手进度(自然过半)收尾吸附或弹回。
    /// </summary>
    internal void SettleHorizontalWheelPreview(bool commit)
        => SettleWheelPreview(commit, physicalContact: false);

    internal void SettlePrecisionTouchpadPreview(bool commit)
        => SettleWheelPreview(commit, physicalContact: true);

    internal bool ContainsSwipePoint(Point screenPoint)
        => Helpers.PrecisionTouchpadInput.ContainsViewportPoint(TabHost, screenPoint);

    private void SettleWheelPreview(bool commit, bool physicalContact)
    {
        if (_swipeHorizontal) return;
        _wheelPreviewActive = false;
        TraceInput($"wheel.settle commit={commit}");
        if (Math.Abs(TabTrans.X) < 0.5 && _peerScroll == null) return;
        var width = Math.Max(1, TabHost.ActualWidth);
        var target = CurrentTabIndex + (_swipeDx < 0 ? 1 : -1);
        var valid = target is >= 0 and < 5;
        _wheelPreviewActive = false;
        if (commit && valid && ((!physicalContact && Math.Abs(_swipeDx) >= width * SwipePageRatio) ||
            (Math.Sign(_wheelContactDx) == Math.Sign(_swipeDx) && Math.Abs(_wheelContactDx) >= width * SwipePageRatio)))
        {
            AnimateSnap(true, target, width);
        }
        else
        {
            AnimateSnap(false, CurrentTabIndex, width);
        }
    }

    /// <summary>
    /// 触控板横向双指滑动 / 鼠标倾斜滚轮(由 MainWindow.OnHorizontalWheel 转发)。
    /// </summary>
    internal void HandleHorizontalWheel(int direction)
    {
        if (direction == 0) return;
        CompleteActiveSnap();
        var target = CurrentTabIndex + direction;
        var width = Math.Max(1, TabHost.ActualWidth);
        if (target is < 0 or >= 5)
        {
            if (Math.Abs(TabTrans.X) > 0.5) AnimateSnap(false, CurrentTabIndex, width);
            return;
        }
        _swipeDx = direction > 0 ? -1 : 1;
        AnimateSnap(true, target, width);
    }


    private void OnSwipeTouchDown(object? sender, TouchEventArgs e)
    {
        _pendingSwipeTouch = e.TouchDevice;
        BeginContact(RootPoint(e), e.OriginalSource as DependencyObject);
        TraceInput("touch.down");
    }

    private Point RootPoint(TouchEventArgs e) => e.GetTouchPoint(null).Position;

    private Point RootPoint(StylusEventArgs e)
    {
        try
        {
            var pts = e.GetStylusPoints(null);
            if (pts != null && pts.Count > 0) return pts[pts.Count - 1].ToPoint();
        }
        catch { /* 取不到就退回下面 */ }
        return e.GetPosition(null);
    }

    private void OnSwipeTouchMove(object? sender, TouchEventArgs e)
    {
        if (SwipeMove(RootPoint(e))) e.Handled = true;
        TraceInput("touch.move");
    }

    private void OnSwipeTouchUp(object? sender, TouchEventArgs e)
    {
        TraceInput("touch.up");
        if (EndContact()) e.Handled = true;
    }

    private void OnSwipeLostTouchCapture(object? sender, TouchEventArgs e)
    {
        // A child losing capture to this host bubbles through the same event route.
        if (ReferenceEquals(e.TouchDevice.Captured, TabHost)) return;
        TraceInput("touch.lost");
        // ScrollViewer captures on down before a horizontal drag belongs to us.
        // Its child-to-child transfer must not cancel the page's pending contact.
        if (!_ownsTouchCapture) return;
        _ownsTouchCapture = false;
        _contactActive = false;
        _swipeArmed = false;
        if (_swipeHorizontal && !_swipeConsumed)
        {
            _swipeConsumed = true;
            AnimateSnap(false, CurrentTabIndex, Math.Max(1, TabHost.ActualWidth));
        }
        UnfreezeVertical();
        _swipeDirKnown = false;
        _swipeHorizontal = false;
        _pendingSwipeTouch = null;
    }

    private void OnSwipeStylusDown(object? sender, StylusEventArgs e)
    {
        _pendingSwipeStylus = e.StylusDevice;
        BeginContact(RootPoint(e), e.OriginalSource as DependencyObject);
        TraceInput("stylus.down");
    }

    private void OnSwipeStylusMove(object? sender, StylusEventArgs e)
    {
        if (SwipeMove(RootPoint(e))) e.Handled = true;
        TraceInput("stylus.move");
    }

    private void OnSwipeStylusUp(object? sender, StylusEventArgs e)
    {
        TraceInput("stylus.up");
        if (EndContact()) e.Handled = true;
    }

    private void BeginContact(Point point, DependencyObject? source)
    {
        // A physical finger can produce both stylus and touch down. They are
        // two routes for one contact, not a new baseline midway through a drag.
        if (_contactActive) return;
        _contactActive = true;
        _ownsTouchCapture = false;
        SwipeDown(point, source);
    }

    private bool EndContact()
    {
        if (!_contactActive) return _swipeMoved;
        _contactActive = false;
        var moved = _swipeMoved;
        var consumed = SwipeUp();
        _ownsTouchCapture = false;
        var touch = _pendingSwipeTouch;
        var stylus = _pendingSwipeStylus;
        _pendingSwipeTouch = null;
        _pendingSwipeStylus = null;
        try { if (touch?.Captured == TabHost) touch.Capture(null); } catch { }
        try { if (stylus?.Captured == TabHost) stylus.Capture(null); } catch { }
        if (!moved && _tapCard != null) _tapCard.OpenVideo();
        _tapCard = null;
        _swipeDirKnown = false;
        _swipeHorizontal = false;
        return consumed || moved;
    }

    private void OnSwipeMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice == null) return;
        if (_swipeMoved) { e.Handled = true; _swipeMoved = false; }
    }
}
