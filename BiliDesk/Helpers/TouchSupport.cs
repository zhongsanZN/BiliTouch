using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

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
    /// </summary>
    public static bool IsTouchPanning(ScrollViewer viewer) =>
        ReferenceEquals(_touchViewer, viewer) &&
        Environment.TickCount64 - _touchViewerAt < 1500;

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
            new EventHandler<TouchEventArgs>((sender, _) =>
            {
                if (sender is not ScrollViewer viewer) return;
                // Belt and braces for a viewer that was never resized before the first finger.
                ConfigureScrollViewer(viewer);
                SmoothScroll.Stop(viewer);
                MarkTouchPanning(viewer);
            }), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>((sender, _) =>
            {
                if (sender is ScrollViewer viewer && ReferenceEquals(_touchViewer, viewer))
                    _touchViewerAt = Environment.TickCount64;
            }), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>((sender, _) =>
            {
                if (sender is ScrollViewer viewer && ReferenceEquals(_touchViewer, viewer)) ClearTouchPanning();
            }), true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.LostTouchCaptureEvent,
            new EventHandler<TouchEventArgs>((sender, _) =>
            {
                if (sender is ScrollViewer viewer && ReferenceEquals(_touchViewer, viewer)) ClearTouchPanning();
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
    }

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
