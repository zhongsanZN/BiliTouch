using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Views.Controls;

namespace BiliDesk.Helpers;

/// <summary>Native WPF finger panning and sliders; mouse and pen keep their existing behavior.</summary>
public static class TouchSupport
{
    private sealed class SliderDrag
    {
        public TouchDevice? Device;
        public double InitialValue;
    }

    /// <summary>One pen drag-to-scroll gesture on a ScrollViewer.</summary>
    private sealed class PenPan
    {
        public StylusDevice? Device;
        public Point Start;
        public double StartOffset;
        public bool Started;
    }

    private static readonly ConditionalWeakTable<Slider, SliderDrag> Drags = new();
    private static readonly ConditionalWeakTable<ScrollViewer, PenPan> Pens = new();
    private static bool _installed;

    /// <summary>The ScrollViewer a finger is currently panning (null when idle).</summary>
    private static ScrollViewer? _touchViewer;
    private static long _touchViewerAt;

    // 触摸点击转发: ScrollViewer 捕获触摸后卡片收不到 TouchUp, 由这里补一次点击。
    private static ScrollViewer? _tapViewer;
    private static Point _tapPoint;
    private static DependencyObject? _tapSource;
    private static bool _tapMoved;
    private const double TapSlop = 10;

    /// <summary>从命中元素往上找最近的 VideoCard。找不到就返回 null(不是卡片, 不转发)。</summary>
    private static VideoCard? TapCard(DependencyObject source)
    {
        var d = source;
        while (d != null)
        {
            if (d is VideoCard card) return card;
            d = d is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static void MarkTouchPanning(ScrollViewer viewer)
    {
        _touchViewer = viewer;
        _touchViewerAt = Environment.TickCount64;
    }

    private static void ClearTouchPanning() => _touchViewer = null;

    /// <summary>
    /// True while a finger is down on this ScrollViewer (1.5s safety timeout for a lost up event).
    /// Pages use it to defer work that changes the scrollable content — the WPF equivalent of
    /// Chromium's compositor-thread scrolling, which never appends content mid-scroll.
    ///
    /// ★★★ 2026-11-04: 超时后**主动把残留清掉**, 不只返回 false。
    ///   否则 _touchViewer 会一直指着一个早已不可见的 viewer, 每次询问都走一遍超时判断,
    ///   而任何依赖"它现在是不是 null"的逻辑(例如 IsTouchPanning 之外的新调用点)都会误判。
    ///   这条与 Up/LostCapture 里的无条件清空是同一套自愈: 宁可放过一次"其实还在滚",
    ///   也不要让一个残留标记永久污染后续所有手势。
    /// </summary>
    public static bool IsTouchPanning(ScrollViewer viewer)
    {
        if (_touchViewer == null) return false;
        if (Environment.TickCount64 - _touchViewerAt >= 1500) { _touchViewer = null; return false; }
        return ReferenceEquals(_touchViewer, viewer);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public static bool IsTouchMouse(MouseEventArgs e) =>
        e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch;

    /// <summary>
    /// True when the machine exposes a touch digitizer; used to show touch-only affordances.
    /// SM_DIGITIZER (94) bits 0/1 are integrated/external touch. Reading it is instant, while
    /// Tablet.TabletDevices would start WPF's tablet input service on the UI thread.
    /// </summary>
    public static bool HasTouchDevice => (GetSystemMetrics(94) & 0x03) != 0;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        // Loaded is a direct event: WPF broadcasts it through the owner type, and a class handler
        // registered on FrameworkElement only ever observes the Window (verified with a probe on
        // .NET 10). SizeChanged is a normal bubbling event and always fires once the first layout
        // gives the viewer a real size, so that is the reliable place to enable panning.
        EventManager.RegisterClassHandler(typeof(ScrollViewer), FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler((sender, _) => { if (sender is ScrollViewer viewer) ConfigureScrollViewer(viewer); }));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>((sender, e) =>
            {
                if (sender is not ScrollViewer viewer) return;
                // Belt and braces for a viewer that was never resized before the first finger.
                ConfigureScrollViewer(viewer);
                SmoothScroll.Stop(viewer);
                MarkTouchPanning(viewer);
                // ★ 记下按下的位置与命中的卡片: ScrollViewer 一开 PanningMode 就会在按下时
                //   捕获触摸, 卡片自己的 TouchUp 收不到 —— 点击只能由这里在抬起时补发。
                //   (首页直播卡"点不进去"就是这个原因: 不是数据问题, 是触摸被滚动抢走了。)
                //   ★ 只记**第一次**(最外层): 嵌套 ScrollViewer 会让每个祖先都走一遍,
                //     后写的会覆盖先写的, 抬起时命中就乱了。
                if (_tapSource == null)
                {
                    // ★ 用**根坐标**(null)记录: 嵌套 ScrollViewer 里内外层的局部坐标不同,
                    //   按下与移动若用了不同的参照物, 位移永远是 0, 拖动会被当成点击。
                    _tapPoint = e.GetTouchPoint(null).Position;
                    _tapSource = e.OriginalSource as DependencyObject;
                    _tapMoved = false;
                }
                _tapViewer = viewer;
            }), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>((sender, e) =>
            {
                if (sender is not ScrollViewer viewer) return;
                if (ReferenceEquals(_touchViewer, viewer)) _touchViewerAt = Environment.TickCount64;
                // ★ 这里**不能**要求 viewer == _touchViewer: 隧道阶段最外层先执行, 而
                //   _touchViewer 记的是最内层(被最后写的), 两者永不相等 —— 于是 _tapMoved
                //   永远是 false, 任何拖动都被当成点击(表现: 划列表时误开视频)。
                //   只要这次按下记过来源, 就用**同一个基准**累计位移。
                if (_tapSource != null && !_tapMoved)
                {
                    var p = e.GetTouchPoint(null).Position;
                    if (Math.Abs(p.X - _tapPoint.X) > TapSlop || Math.Abs(p.Y - _tapPoint.Y) > TapSlop)
                        _tapMoved = true;
                }
            }), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>((sender, e) =>
            {
                if (sender is not ScrollViewer viewer) return;
                // ★★★ 2026-11-04 修"点进评论详情再返回后触屏拖不动":
                //
                //   原来是 `if (ReferenceEquals(_touchViewer, viewer)) ClearTouchPanning();`
                //   —— 只清**记录的那个** viewer。而 _touchViewer 记的是**最内层**
                //   (MarkTouchPanning 在预览阶段被每个祖先写一遍, 最后写的是最内层)。
                //
                //   页面一切换(进详情时 CommentList 被 Collapsed), 那个内层 viewer 就
                //   **再也收不到 TouchUp** —— 它的位置已经不在这棵可视树的有效路径上。
                //   于是 _touchViewer 永久残留, IsTouchPanning(...) 一直为 true:
                //   页面据此认为"手指还在滚动", 于是推迟所有会改变滚动内容的工作,
                //   表现就是"手指怎么拖都不动"。
                //
                //   修法: 手指抬起这件事是**全局**的, 任何一个 ScrollViewer 收到 Up 都说明
                //   这一次触摸结束了, 应当无条件清空。_touchViewer 只是"最近一次触摸落在
                //   哪个 viewer 上"的提示, 不是需要精确匹配的所有权。
                ClearTouchPanning();
                // ★ 不看 _tapViewer: 嵌套 ScrollViewer 里最内层的 Up 先到, 它清空后外层就再没有
                //   机会补发点击了。只要这次按下记过来源, 就由**第一个**收到 Up 的处理器消费。
                var moved = _tapMoved;
                var source = _tapSource;
                _tapViewer = null;
                _tapSource = null;
                _tapMoved = false;
                if (moved || source == null) return;
                // 命中测试后转发给卡片: 这一下是"点", 不是"滑"
                TapCard(source)?.OpenVideo();
            }), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.LostTouchCaptureEvent,
            new EventHandler<TouchEventArgs>((sender, _) =>
            {
                // 同上: 任何一个 viewer 丢捕获都代表这次触摸已经不由我们掌握, 无条件清
                ClearTouchPanning();
            }), true);
        // Suppress window bounce at the end of a list, while retaining native pan inertia.
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.ManipulationBoundaryFeedbackEvent,
            new EventHandler<ManipulationBoundaryFeedbackEventArgs>((_, e) => e.Handled = true));
        // WPF only provides *finger* panning. A stylus drag over the right-side info/comment panel
        // did nothing, so pen users could not scroll it. This adds a 1:1 pen drag-to-scroll that
        // still lets taps on buttons/links/sliders through (it only starts after a small slop).
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewStylusDownEvent,
            new StylusDownEventHandler(OnPenDown), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewStylusMoveEvent,
            new StylusEventHandler(OnPenMove), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewStylusUpEvent,
            new StylusEventHandler(OnPenUp), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.LostStylusCaptureEvent,
            new StylusEventHandler(OnPenLostCapture), true);
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(OnSliderDown));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(OnSliderMove));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(OnSliderUp));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.LostTouchCaptureEvent,
            new EventHandler<TouchEventArgs>(OnSliderLostCapture));
        // ★★★ 2026-11-04 触控板(precision touchpad)滚动:
        //   精确触控板的两指滚动是**高频小 delta 的滚轮事件**, 不走触摸 —— 所以
        //   PanningMode / PanningRatio 对它一律无效, 这就是"触控板从来没人管"的真相。
        //   WPF 默认每个小 delta 按"一行"滚, 密集派发时会飞出去。
        //   这里统一接管: 小 delta 按像素滚(≈1.2px/单位), 整格(±120)仍交给原生/平滑滚动。
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnTouchpadWheel), true);
    }

    /// <summary>
    /// 触控板滚动。只接管"小 delta"(<120) —— 那是精密触控板/高精度滚轮的特征;
    /// 整格滚轮(±120 的整数倍)原样放行, 保持鼠标滚轮与 SmoothScroll 的既有手感。
    ///
    /// ★ 为什么用 PreviewMouseWheel + handledEventsToo: SmoothScroll 也在 Preview 上挂了处理器,
    ///   两个都要能看到事件。这里只在判定为触控板时才 Handled, 否则让它继续走自己的路。
    /// ★ 已被内层处理过(e.Handled)的事件不再碰: 嵌套列表时应该是最内层负责滚动。
    /// </summary>
    private static void OnTouchpadWheel(object? sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.Handled) return;
        if (e.Delta == 0 || Math.Abs(e.Delta) >= 120) return;   // 整格 = 鼠标滚轮, 不接管
        if (viewer.ScrollableHeight <= 0) return;

        var target = Math.Clamp(viewer.VerticalOffset - e.Delta * TouchpadPixelPerDelta,
            0, viewer.ScrollableHeight);
        viewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    /// <summary>触控板每 1 单位 delta 对应多少像素(1.2 ≈ 手指滑一半滚半屏, 接近 Chrome 手感)</summary>
    private const double TouchpadPixelPerDelta = 1.2;

    private static void ConfigureScrollViewer(ScrollViewer viewer)
    {
        // Fast path: once configured, resize storms (dragging the window edge) cost one DP read.
        if (viewer.PanningMode != PanningMode.None) return;
        // Respect explicit settings, including the internal editors in TextBox controls.
        if (DependencyPropertyHelper.GetValueSource(viewer, ScrollViewer.PanningModeProperty)
            .BaseValueSource != BaseValueSource.Default) return;
        var horizontal = viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;
        var vertical = viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;
        viewer.SetCurrentValue(ScrollViewer.PanningModeProperty,
            horizontal && !vertical ? PanningMode.HorizontalOnly : PanningMode.VerticalFirst);
        // 75% of the finger travel: full-strength WPF panning felt twitchy on precision touchpads
        // that stream their gesture as touch input. Inertia is unaffected.
        viewer.SetCurrentValue(ScrollViewer.PanningRatioProperty, 0.75);
    }

    private static bool IsPen(StylusEventArgs e) =>
        e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Stylus;

    /// <summary>Buttons/sliders/text boxes/selectors/hyperlinks keep the pen click they deserve.</summary>
    private static bool IsInteractiveSource(DependencyObject? source, ScrollViewer viewer)
    {
        while (source != null && source != viewer)
        {
            if (source is ButtonBase or Slider or TextBoxBase or Selector or Hyperlink) return true;
            source = source is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private static void OnPenDown(object? sender, StylusEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !IsPen(e)) return;
        // Item-scrolled viewers count offsets in rows: a pixel delta there would jump whole cards.
        if (viewer.CanContentScroll) return;
        if (IsInteractiveSource(e.OriginalSource as DependencyObject, viewer)) return;
        var pan = Pens.GetOrCreateValue(viewer);
        pan.Device = e.StylusDevice;
        pan.Start = e.GetPosition(viewer);
        pan.StartOffset = viewer.VerticalOffset;
        pan.Started = false;
        // Deliberately not handled: a pen tap that never moves must still click what is under it.
    }

    private static void OnPenMove(object? sender, StylusEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (!Pens.TryGetValue(viewer, out var pan) || pan.Device == null || pan.Device != e.StylusDevice) return;
        var dy = e.GetPosition(viewer).Y - pan.Start.Y;
        if (!pan.Started)
        {
            if (Math.Abs(dy) < 10) return;
            if (!pan.Device.Capture(viewer)) { pan.Device = null; return; }
            pan.Started = true;
        }
        e.Handled = true;
        viewer.ScrollToVerticalOffset(Math.Clamp(pan.StartOffset - dy, 0, Math.Max(0, viewer.ScrollableHeight)));
    }

    private static void OnPenUp(object? sender, StylusEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (!Pens.TryGetValue(viewer, out var pan) || pan.Device == null || pan.Device != e.StylusDevice) return;
        var started = pan.Started;
        pan.Device = null;
        pan.Started = false;
        if (e.StylusDevice.Captured == viewer) e.StylusDevice.Capture(null);
        if (started) e.Handled = true; // a drag must not turn into a click
    }

    private static void OnPenLostCapture(object? sender, StylusEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (Pens.TryGetValue(viewer, out var pan) && pan.Device == e.StylusDevice)
        {
            pan.Device = null;
            pan.Started = false;
        }
    }

    private static void OnSliderDown(object? sender, TouchEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!slider.IsEnabled) return;
        // No Track part (custom or not-yet-applied template) means coordinates cannot drive it:
        // let WPF's own mouse promotion handle the finger instead of swallowing the touch.
        if (slider.Template?.FindName("PART_Track", slider) is not Track) return;
        var drag = Drags.GetOrCreateValue(slider);
        e.Handled = true;
        if (drag.Device != null) return; // A second finger must not steal the first drag.
        if (!e.TouchDevice.Capture(slider)) return;
        drag.Device = e.TouchDevice;
        drag.InitialValue = slider.Value;
        // The player's existing handler sets _isSeeking BEFORE the first value change.
        slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        UpdateSlider(slider, e);
    }

    private static void UpdateSlider(Slider slider, TouchEventArgs e)
    {
        if (slider.Template?.FindName("PART_Track", slider) is not Track track) return;
        var value = track.ValueFromPoint(e.GetTouchPoint(track).Position);
        if (!double.IsFinite(value)) return;
        value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        if (slider.IsSnapToTickEnabled)
        {
            var ticks = slider.Ticks;
            if (ticks.Count > 0)
                value = ticks.Append(slider.Minimum).Append(slider.Maximum).MinBy(t => Math.Abs(t - value));
            else if (slider.TickFrequency > 0)
                value = slider.Minimum + Math.Round((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
        }
        slider.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(value, slider.Minimum, slider.Maximum));
    }

    private static void OnSliderMove(object? sender, TouchEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!Drags.TryGetValue(slider, out var drag) || drag.Device == null) return;
        e.Handled = true;
        if (drag.Device == e.TouchDevice) UpdateSlider(slider, e);
    }

    private static void OnSliderUp(object? sender, TouchEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!Drags.TryGetValue(slider, out var drag) || drag.Device == null) return;
        e.Handled = true;
        if (drag.Device != e.TouchDevice) return;
        UpdateSlider(slider, e);
        FinishSlider(slider, drag, canceled: false);
    }

    private static void OnSliderLostCapture(object? sender, TouchEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (Drags.TryGetValue(slider, out var drag) && drag.Device == e.TouchDevice)
            FinishSlider(slider, drag, canceled: true);
    }

    private static void FinishSlider(Slider slider, SliderDrag drag, bool canceled)
    {
        var device = drag.Device;
        drag.Device = null; // Release can synchronously raise LostTouchCapture.
        if (canceled) slider.SetCurrentValue(RangeBase.ValueProperty, drag.InitialValue);
        slider.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
        if (device?.Captured == slider) device.Capture(null);
    }
}
