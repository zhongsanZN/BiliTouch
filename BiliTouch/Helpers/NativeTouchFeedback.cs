using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BiliTouch.Helpers;

/// <summary>Applies the Windows touch-indicator preference to each HWND that hosts app input.</summary>
internal static class NativeTouchFeedback
{
    private const uint GetContactVisualization = 0x2018;
    private const uint TouchContactFeedback = 1;
    private const int SettingChanged = 0x001A;
    private static readonly ConditionalWeakTable<HwndSource, object> AttachedSources = new();

    internal static void Attach(HwndSource source) => AttachedSources.GetValue(source, AttachCore);

    internal static void FollowSource(UIElement surface)
    {
        // LibVLC can move a touch surface to an owned foreground window and back.
        PresentationSource.AddSourceChangedHandler(surface, OnSourceChanged);
        if (PresentationSource.FromVisual(surface) is HwndSource source) Attach(source);
    }

    private static void OnSourceChanged(object sender, SourceChangedEventArgs e)
    {
        if (e.NewSource is HwndSource source) Attach(source);
    }

    private static object AttachCore(HwndSource source)
    {
        Apply(source.Handle);
        source.AddHook(OnWindowMessage);
        return new object();
    }

    private static IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == SettingChanged) Apply(hwnd);
        return IntPtr.Zero;
    }

    private static void Apply(IntPtr hwnd)
    {
        if (!SystemParametersInfo(GetContactVisualization, 0, out var setting, 0)) return;
        // Both standard and high-visibility contact indicators use the same per-window flag.
        int enabled = setting != 0 ? 1 : 0;
        SetWindowFeedbackSetting(hwnd, TouchContactFeedback, 0, sizeof(int), in enabled);
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowFeedbackSetting(IntPtr hwnd, uint feedback, uint flags, uint size, in int enabled);
}
