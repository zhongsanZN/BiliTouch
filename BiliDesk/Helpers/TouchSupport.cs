using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BiliDesk.Helpers;

/// <summary>Native WPF finger panning and sliders; mouse and pen keep their existing behavior.</summary>
public static class TouchSupport
{
    private sealed class SliderDrag
    {
        public TouchDevice? Device;
        public double InitialValue;
    }

    private static readonly ConditionalWeakTable<Slider, SliderDrag> Drags = new();
    private static bool _installed;

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
            }), true);
        // Suppress window bounce at the end of a list, while retaining native pan inertia.
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.ManipulationBoundaryFeedbackEvent,
            new EventHandler<ManipulationBoundaryFeedbackEventArgs>((_, e) => e.Handled = true));
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
