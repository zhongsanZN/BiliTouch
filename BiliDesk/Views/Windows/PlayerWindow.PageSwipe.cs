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

    internal void InfoDown(Point p, DependencyObject? source)
    {
        _infoSwipeStart = p;
        _infoSwipeStartTick = Environment.TickCount64;
        _infoSwipeArmed = InfoSwipeAllowed(source);
        _infoHorizontalDecided = false;
        _infoDraggingPage = false;
        _infoGestureMoved = false;
        _infoDragDx = 0;
    }

    /// <summary>
    /// 触摸在 InfoPanel 上的按下(2026-11-04 加入**捕获**)。
    ///
    /// ★★★ 修"手指向左滑到视频那边时滑动停掉, 右侧面板停在简介/评论中间":
    ///
    ///   根因: 视频区是 **另一个 HWND** —— LibVLC 的 VideoView 通过浮动子窗口渲染,
    ///   它不在 InfoPanel 的可视树里。手指一旦移出 InfoPanel 边界落到那块原生窗口上,
    ///   后续的 TouchMove/TouchUp **再也不会派发到 InfoPanel** ——
    ///   于是 TranslateTransform 停在中途, `FinishPageDrag` 永不执行, 面板就卡在中间。
    ///
    ///   修法: 按下时立刻 `CaptureTouch`, 把这只手指的后续事件**锁定**在本元素上。
    ///   捕获之后即便手指移到视频区、甚至移出窗口, Move/Up 仍然会送到这里,
    ///   收尾逻辑得以正常执行(要么吸附过去, 要么弹回)。
    ///
    /// ★ 只在"够格起步"时才捕获(见 InfoSwipeAllowed): 落在滑块/输入框上的按下
    ///   不该被我们抢走, 否则那些控件自己的拖动手势会失灵。
    /// </summary>
    /// <summary>
    /// 触摸在 InfoPanel 上的按下。
    ///
    /// ★★★ 2026-11-04 修"点进评论详情再返回后触屏拖不动"(用户实测复现):
    ///
    ///   原来这里**一按下就 `InfoPanel.CaptureTouch`**。触摸捕获是排他的 ——
    ///   面板一旦抓住这只手指, 内层 ScrollViewer 就**再也收不到 Manipulation**,
    ///   于是 PanningMode 完全失效: 手指竖着滑, 列表纹丝不动。
    ///
    ///   为什么"平时好像能滚"? 因为绝大多数竖滑的起手点落在别的地方(视频卡片、按钮),
    ///   那条路径没被捕获; 而"从评论详情返回"之后, 起手点很容易落在刚恢复的
    ///   CommentList 区域的空白/文字上, 于是**每次都被捕获**, 表现为"整块拖不动了"。
    ///   (探针对照: 基线一次竖滑 0→2085 正常; 返回后同一次竖滑 Δ0。见
    ///    tests/TouchTests/CommentTouchChainProbe.cs 与 artifacts/chain*.log。)
    ///
    ///   修法: 按下时**不抢捕获**, 等 InfoMove 判出"这是横向翻页"之后再抢。
    ///   竖滑从一开始就留给 ScrollViewer 的原生平移, 互不干扰。
    ///   横滑仍然要捕获 —— 因为手指会滑出 InfoPanel 落到视频那块独立 HWND 上,
    ///   不捕获就收不到后续 Move/Up(见下面 InfoMove 里的说明)。
    /// </summary>
    private void OnInfoTouchDownCapture(object? sender, TouchEventArgs e)
    {
        // 注意: 这里刻意**不做** CaptureTouch, 捕获推迟到 InfoMove 判定为横向之后。
        _pendingCaptureDevice = e.TouchDevice;
        OnInfoTouchDown(sender, e);
    }

    /// <summary>
    /// 还没决定要不要捕获的那只手指。判定为横向时立刻捕获(见 InfoMove),
    /// 竖向/抬手就丢弃 —— 后者交给 ScrollViewer 自己处理。
    /// </summary>
    private TouchDevice? _pendingCaptureDevice;

    /// <summary>Returns true when the move was consumed by the page drag.</summary>
    internal bool InfoMove(Point p)
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
            // ★★★ 2026-11-04: 判定为**横向**了才抢捕获。
            //   抢早了会把竖滑一起吃掉(见 OnInfoTouchDownCapture 的说明);
            //   抢晚了手指可能已经划出 InfoPanel 落到视频那块独立 HWND 上, Move/Up 再也收不到。
            //   判定点(超过 PageDragSlop 且横向大于纵向 1.2 倍)正好在"还没离开面板"之前。
            if (_pendingCaptureDevice != null)
            {
                try { InfoPanel.CaptureTouch(_pendingCaptureDevice); } catch { /* 已被别处捕获 */ }
                _pendingCaptureDevice = null;
            }
            // ★ BeginPageDrag 失败(内容还没布局出高度, 例如面板刚切回来)时**不能就此放弃**:
            //   原来这里 return false, 但 _infoHorizontalDecided 已经是 true, 后续移动全被
            //   挡在 `!_infoDraggingPage` 那一行 —— 整个手势彻底失效, 用户划了没反应。
            //   现在: 拖不起来也继续记位移, 抬手时按位移直接切 tab(降级但失灵)。
            _infoDraggingPage = BeginPageDrag();
        }
        _infoDragDx = dx;
        if (!_infoDraggingPage) return true;   // 已消费(降级路径), 抬手时按位移切
        ApplyPageDrag(dx);
        return true;
    }

    /// <summary>Returns true when the release was consumed (page animation or click suppression).</summary>
    internal bool InfoUp()
    {
        // ★ 降级路径: 拖不起来(BeginPageDrag 失败)但方向已判定为横向 —— 按位移直接切 tab。
        //   这条保证"划了至少有反应", 不会因为布局还没出高度就整段失灵。
        if (!_infoDraggingPage)
        {
            // ★ 降级路径不再要求 _infoSwipeArmed: 切到评论页后它的高度可能还是 0,
            //   BeginPageDrag 一直失败; 若此时又因为起点落在某个按钮上而不切,
            //   用户就会觉得"能划过去、划不回来"。方向已经判定为横向了, 按位移切。
            var degraded = _infoHorizontalDecided && Math.Abs(_infoDragDx) >= PageSwitchDistance;
            var degradeTarget = PageIndex + (_infoDragDx < 0 ? 1 : -1);
            _infoHorizontalDecided = false;
            if (degraded && degradeTarget is 0 or 1)
            {
                _infoPageSwitching = true;
                try
                {
                    if (degradeTarget == 1) TabComments.IsChecked = true;
                    else TabIntro.IsChecked = true;
                }
                finally { _infoPageSwitching = false; }
                return true;
            }
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
                // ★★★ 2026-11-04: 必须在这里清掉 _infoDraggingPage。
                //   原来只有**手指抬起**(InfoUp 里)会清它; 而触控板翻页是**直接调**
                //   FinishPageDrag 进来的, 没有经过 InfoUp ⇒ 这个标志永远留在 true。
                //   后果: 下一次手势(无论触控板还是手指)都会被
                //   `if (_infoDraggingPage) return` 这个守卫挡掉 —— 表现为"划了没反应"。
                _infoDraggingPage = false;
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
                _infoDraggingPage = false;   // 同上: 弹回也要清, 否则下次手势被挡
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
        // ★★★ 2026-11-04 修"从简介页滑到评论页时, 手指初始位置落在推荐视频上就变成硬切":
        //
        //   原因: VideoCard 内部装着 ButtonBase(收藏/更多等), 起手点落在它上面时
        //   这里直接返回 false ⇒ _infoSwipeArmed = false ⇒ InfoMove 判为横向后返回 false
        //   ⇒ BeginPageDrag 从不执行 ⇒ 走 InfoUp 的**降级路径**(按位移直接切 tab)。
        //   降级路径没有跟手、没有动画, 观感就是"硬切"。
        //
        //   修法: 卡片整块是"可滑动的内容区", 不该因为它内部有一个小按钮就否掉整个手势。
        //   真正需要排除的是**独占手势的控件**: 滑块(要拖动)、文本框(要选字)、
        //   下拉框/列表(要自己滚动)。按钮被手指划过时不会误触发(点击有 10px 抖动阈值)。
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
        // 与 InfoMove 里"判定横向才捕获"对称: 收尾时无论有没有抢过都放一次
        // (没抢过的 Release 是无害的空操作 —— WPF 对未持有的捕获直接忽略)
        _pendingCaptureDevice = null;
        try { InfoPanel.ReleaseTouchCapture(e.TouchDevice); } catch { }
    }

    /// <summary>
    /// 触摸捕获被别处抢走(窗口失活、别的元素抢捕获等)。
    /// ★ 必须在这里也做一次收尾: 否则面板会永远停在中途 —— 与"滑出面板卡住"同一个后果。
    /// </summary>
    private void OnInfoTouchLostCapture(object? sender, TouchEventArgs e)
    {
        if (_infoDraggingPage) FinishPageDrag(switchPage: false, PageIndex);
        _infoDraggingPage = false;
        _infoHorizontalDecided = false;
        _infoDragDx = 0;
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
