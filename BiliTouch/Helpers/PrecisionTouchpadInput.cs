using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace BiliTouch.Helpers;

/// <summary>
/// Physical two-finger motion, before Windows' wheel acceleration and inertia.
/// Only claims horizontal gestures in a page that explicitly supplies a target.
/// Vertical/zoom input continues through DefWindowProc's normal wheel handling.
/// </summary>
internal sealed class PrecisionTouchpadInput : IDisposable
{
    internal sealed record Target(Action Begin, Action<double> Preview, Action<bool> End, string Name = "");
    private readonly HwndSource _source;
    private readonly Func<Point, Target?> _resolve;
    private readonly ContactMotion _motion = new();
    private readonly TouchInfo[] _frame = new TouchInfo[16];
    private Target? _target;
    private uint _lastFrame = uint.MaxValue;
    private bool _claimed, _ended, _eligible;
    private readonly ContactLifetime _lifetime = new();
    private readonly ProgressFilter _progressFilter = new();
    private bool _panEnabled = true;
    private double _direction = 1;
    private long _suppressWheelUntil;
    internal static Action<string>? InputTrace;
    internal bool Registered { get; }
    internal bool SuppressHorizontalWheel
    {
        get
        {
            var now = Environment.TickCount64;
            var suppress = _eligible || _claimed || now < _suppressWheelUntil;
            // Even pre-recognition frames may have reached DefWindowProc. Drain
            // their generated horizontal wheel tail until it actually goes idle;
            // none of it is evidence of further physical contact displacement.
            if (suppress) _suppressWheelUntil = now + 180;
            return suppress;
        }
    }

    internal PrecisionTouchpadInput(HwndSource source, Func<Point, Target?> resolve)
    {
        _source = source; _resolve = resolve;
        try { Registered = RegisterTouchpadCapableWindow(source.Handle, true); }
        catch (EntryPointNotFoundException) { }
        if (Registered) source.AddHook(WndProc);
        Trace($"registered={Registered}");
    }

    private static void Trace(string text) => InputTrace?.Invoke($"{Environment.TickCount64} ptp {text}");

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg is not (0x245 or 0x246 or 0x247 or 0x24C)) return IntPtr.Zero;
        var id = (uint)(wp.ToInt64() & 0xffff);
        if (!GetPointerInfo(id, out var info))
        {
            Trace($"pointer.failed msg={msg:X} error={Marshal.GetLastPInvokeError()}");
            handled = _claimed;
            Cancel();
            return IntPtr.Zero;
        }
        if (info.Type != 5) { Trace($"pointer.other type={info.Type} msg={msg:X}"); return IntPtr.Zero; }
        if (msg == 0x24C || (info.Flags & 0x8000) != 0)
        {
            Trace($"cancel msg={msg:X} flags={info.Flags:X} claimed={_claimed}");
            // A cancellation can arrive while another finger is still down.
            // Consume the rest of this claimed stream; do not let its remaining
            // updates become accelerated wheel events on the fallback path.
            if (_claimed && !_ended) _target?.End(false);
            _ended = true;
            _suppressWheelUntil = Environment.TickCount64 + 180;
            handled = _claimed;
            uint remaining = (uint)_frame.Length;
            if (GetPointerFrameTouchpadInfo(id, ref remaining, _frame))
            {
                int inContact = 0;
                for (int i = 0; i < remaining; i++) if ((_frame[i].Info.Flags & 4) != 0) inContact++;
                _lifetime.Update(false, inContact);
                if (inContact == 0) { _claimed = _eligible = false; _motion.Clear(); }
            }
            else Cancel();
            return IntPtr.Zero;
        }
        uint count = (uint)_frame.Length;
        if (!GetPointerFrameTouchpadInfo(id, ref count, _frame) || count == 0)
        {
            Trace($"frame.failed error={Marshal.GetLastPInvokeError()}");
            handled = _claimed;
            Cancel();
            return IntPtr.Zero;
        }
        if (info.Frame == _lastFrame) { handled = _claimed; return IntPtr.Zero; }
        _lastFrame = info.Frame;

        var hasDown = false;
        var active = 0;
        for (int i = 0; i < count; i++)
        {
            hasDown |= (_frame[i].Info.Flags & 0x10000) != 0;
            if ((_frame[i].Info.Flags & 4) != 0) active++;
        }
        if (_lifetime.Update(hasDown, active))
        {
            _claimed = _eligible = false; _ended = false; _target = null; _motion.Clear();
            ReadPreferences();
            _target = _panEnabled ? _resolve(new Point(info.Pixel.X, info.Pixel.Y)) : null;
            _eligible = _target != null && GetPointerDeviceRects(info.Device, out var device, out _) && device.Right > device.Left;
            _motion.Reset(_eligible ? DeviceWidth(info.Device) : 1);
            _progressFilter.Reset();
            Trace($"down eligible={_eligible} width={(_eligible ? DeviceWidth(info.Device) : 0):F0} target={_target?.Name}");
            _ended = false;
        }
        if (!_eligible) return IntPtr.Zero;

        // Include the last positions of released contacts in the final sample.
        // Stable pointer identities prevent centroid jumps when the fingers lift
        // one at a time. No packet timestamps or velocities affect displacement.
        for (int i = 0; i < count; i++)
        {
            var p = _frame[i].Info;
            _motion.Update(p.Id, p.Himetric.X, p.Himetric.Y, (p.Flags & 4) != 0);
        }
        if (!_ended && _motion.ContactCount >= 2)
        {
            var (x, y) = _motion.Displacement;
            if (!_claimed && Math.Abs(x) >= .012 && Math.Abs(x) > Math.Abs(y) * 1.2)
            {
                _claimed = true;
                _target!.Begin();
                Trace($"begin target={_target.Name}");
            }
            if (_claimed)
            {
                var progress = Math.Clamp(-x * _direction, -.999, .999);
                if (_progressFilter.Accept(progress))
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    _target!.Preview(progress);
                    var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Trace($"move progress={progress:F4} contacts={active}");
                    if (ms > 8) Trace($"preview.slow ms={ms:F2}");
                }
            }
        }
        if (!_ended && active < 2 && _motion.ContactCount >= 2)
        {
            _ended = true;
            _suppressWheelUntil = Environment.TickCount64 + 180;
            if (_claimed)
            {
                _target!.End(true);
                Trace("up");
            }
        }
        handled = _claimed;
        if (handled) SkipPointerFrameMessages(id);
        if (active == 0) { _claimed = false; _eligible = false; _motion.Clear(); }
        return IntPtr.Zero;
    }

    private static double DeviceWidth(IntPtr device) => GetPointerDeviceRects(device, out var rect, out _) ? Math.Max(1, rect.Right - rect.Left) : 1;
    internal static bool ContainsViewportPoint(FrameworkElement element, Point screenPoint)
    {
        if (!element.IsVisible) return false;
        try
        {
            var local = element.PointFromScreen(screenPoint);
            // Hit-test the stationary viewport, not the page that is currently
            // sliding out of it. Otherwise an interrupted snap rejects contacts
            // over its already-visible neighbour and falls back to wheel input.
            if (element.RenderTransform is System.Windows.Media.TranslateTransform t)
                local.Offset(t.X, t.Y);
            return new Rect(element.RenderSize).Contains(local);
        }
        catch (InvalidOperationException) { return false; }
    }
    private void ReadPreferences()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
        _panEnabled = (key?.GetValue("PanEnabled") as int? ?? 1) != 0;
        _direction = (key?.GetValue("ScrollDirection") as int? ?? 0) == 0 ? 1 : -1;
    }

    internal void Cancel()
    {
        if (_eligible || _claimed) _suppressWheelUntil = Environment.TickCount64 + 180;
        if (_claimed && !_ended) _target?.End(false);
        _claimed = _eligible = false; _ended = true; _target = null; _motion.Clear();
        _lifetime.Clear();
    }

    public void Dispose()
    {
        Cancel();
        if (Registered && !_source.IsDisposed) { _source.RemoveHook(WndProc); RegisterTouchpadCapableWindow(_source.Handle, false); }
    }

    internal sealed class ContactMotion
    {
        private readonly Dictionary<uint, (double startX, double startY, double x, double y)> _contacts = new();
        private double _width = 1;
        internal bool Started => _contacts.Count > 0;
        internal int ContactCount => _contacts.Count;
        internal void Reset(double width) { Clear(); _width = Math.Max(1, width); }
        internal void Clear() => _contacts.Clear();
        internal void Update(uint id, double x, double y, bool active)
        {
            if (_contacts.TryGetValue(id, out var old)) _contacts[id] = (old.startX, old.startY, x, y);
            else if (active) _contacts[id] = (x, y, x, y);
        }
        internal (double x, double y) Displacement => _contacts.Count == 0 ? (0, 0) :
            (_contacts.Values.Average(p => p.x - p.startX) / _width,
             _contacts.Values.Average(p => p.y - p.startY) / _width);
    }

    /// <summary>A new session requires every contact from the previous one to leave.</summary>
    internal sealed class ContactLifetime
    {
        private bool _open;
        internal bool Update(bool hasDown, int activeContacts)
        {
            var start = hasDown && !_open;
            if (start) _open = true;
            if (activeContacts == 0) _open = false;
            return start;
        }
        internal void Clear() => _open = false;
    }

    internal sealed class ProgressFilter
    {
        private double _previous = double.NaN;
        internal void Reset() => _previous = double.NaN;
        internal bool Accept(double value)
        {
            if (value == _previous) return false;
            _previous = value;
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerInfo
    {
        public uint Type, Id, Frame, Flags;
        public IntPtr Device, Window;
        public NativePoint Pixel, Himetric, PixelRaw, HimetricRaw;
        public uint Time, History;
        public int InputData;
        public uint KeyStates;
        public ulong Performance;
        public uint ButtonChange;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TouchInfo
    {
        public PointerInfo Info;
        public uint Flags, Mask;
        public NativeRect Contact, ContactRaw;
        public uint Orientation, Pressure;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterTouchpadCapableWindow(IntPtr hwnd, bool enable);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPointerInfo(uint id, out PointerInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPointerFrameTouchpadInfo(uint id, ref uint count, [Out] TouchInfo[] frame);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPointerDeviceRects(IntPtr device, out NativeRect deviceRect, out NativeRect screenRect);
    [DllImport("user32.dll")] private static extern bool SkipPointerFrameMessages(uint id);
}
