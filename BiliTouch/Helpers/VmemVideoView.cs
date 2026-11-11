using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliTouch.Helpers;

/// <summary>
/// Presents LibVLC vmem frames as a compositable WPF image. Attach before Play.
/// Each negotiated format owns its buffers until LibVLC invokes its cleanup callback;
/// disposing the visual must not free memory that the decoder can still be writing.
/// </summary>
public sealed class VmemVideoView : Image, IDisposable
{
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, int bytes);

    // LibVLCSharp 3.8 wraps format/lock/display user data, but forwards cleanup
    // directly with an incompatible ref IntPtr signature. Register this callback
    // set together with the native ABI so each format has the same opaque value.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VideoCleanupCallback(IntPtr opaque);
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl, EntryPoint = "libvlc_video_set_format_callbacks")]
    private static extern void SetFormatCallbacks(IntPtr player, MediaPlayer.LibVLCVideoFormatCb format, VideoCleanupCallback cleanup);
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl, EntryPoint = "libvlc_video_set_callbacks")]
    private static extern void SetCallbacks(IntPtr player, MediaPlayer.LibVLCVideoLockCb videoLock,
        MediaPlayer.LibVLCVideoUnlockCb unlock, MediaPlayer.LibVLCVideoDisplayCb display, IntPtr opaque);

    private sealed class FrameBuffers
    {
        // The opaque handle also roots the view/delegates throughout the native lifetime.
        internal readonly VmemVideoView Owner;
        internal readonly object Gate = new();
        internal readonly int Width, Height, Pitch, Lines, Bytes;
        internal readonly bool Nv12;
        internal IntPtr Decode, Present, DecodeAllocation, PresentAllocation;
        internal GCHandle Handle;
        internal bool Retired;
        internal int Pending;

        internal FrameBuffers(VmemVideoView owner, uint width, uint height, bool nv12)
        {
            Owner = owner;
            Nv12 = nv12;
            Width = checked((int)width); Height = checked((int)height);
            Pitch = checked((Width * (nv12 ? 1 : 4) + 31) & ~31);
            Lines = checked((Height + 31) & ~31);
            Bytes = nv12 ? checked(Pitch * Lines + Pitch * (Lines / 2)) : checked(Pitch * Lines);
            try
            {
                DecodeAllocation = Marshal.AllocHGlobal(checked(Bytes + 31));
                PresentAllocation = Marshal.AllocHGlobal(checked(Bytes + 31));
                Decode = new IntPtr((DecodeAllocation.ToInt64() + 31) & ~31L);
                Present = new IntPtr((PresentAllocation.ToInt64() + 31) & ~31L);
                Handle = GCHandle.Alloc(this);
            }
            catch { Release(); throw; }
        }

        internal void Release()
        {
            if (DecodeAllocation != IntPtr.Zero) Marshal.FreeHGlobal(DecodeAllocation);
            if (PresentAllocation != IntPtr.Zero) Marshal.FreeHGlobal(PresentAllocation);
            Decode = Present = DecodeAllocation = PresentAllocation = IntPtr.Zero;
            if (Handle.IsAllocated) Handle.Free();
        }
    }

    private FrameBuffers? _format, _bitmapFormat;
    private WriteableBitmap? _bitmap;
    private GpuVideoPresenter? _gpu;
    private FrameBuffers? _gpuFormat;
    private Window? _ownerWindow;
    private bool _gpuAvailable;
    private bool _presentationSizeDirty;
    private DispatcherTimer? _resizeTimer;
    private volatile bool _disposed;
    private long _delivered, _coalesced, _presented;
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly VideoCleanupCallback _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    public long DeliveredFrames => Interlocked.Read(ref _delivered);
    public long CoalescedFrames => Interlocked.Read(ref _coalesced);
    public long PresentedFrames => Interlocked.Read(ref _presented);
    public bool UsesGpuPresentation => _gpuAvailable && Volatile.Read(ref _format)?.Nv12 == true;
    public string PresentationMode => UsesGpuPresentation ? "显卡合成" : "兼容合成";
    public bool UseCompatibleColorConversion { get; set; }
    public (uint Width, uint Height) VideoSize
    {
        get { var format = Volatile.Read(ref _format); return format == null ? (0u, 0u) : ((uint)format.Width, (uint)format.Height); }
    }

    public VmemVideoView()
    {
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        Loaded += (_, _) =>
        {
            if (_ownerWindow != null) _ownerWindow.StateChanged -= OnWindowStateChanged;
            _ownerWindow = Window.GetWindow(this);
            if (_ownerWindow != null) _ownerWindow.StateChanged += OnWindowStateChanged;
            OnWindowStateChanged(this, EventArgs.Empty);
        };
        IsVisibleChanged += (_, _) => OnWindowStateChanged(this, EventArgs.Empty);
        SizeChanged += (_, _) => SchedulePresentationResize();
        Unloaded += (_, _) =>
        {
            if (_ownerWindow != null) _ownerWindow.StateChanged -= OnWindowStateChanged;
            _ownerWindow = null;
            _resizeTimer?.Stop();
            if (_gpu?.Pending != true) _retryTimer?.Stop();
        };
        _formatCb = OnFormat; _cleanupCb = OnCleanup;
        _lockCb = OnLock; _unlockCb = OnUnlock; _displayCb = OnDisplay;
    }

    public void Attach(MediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_gpu == null && Svc.Settings.HardwareAccelerationEnabled &&
            Environment.GetEnvironmentVariable("BT_DISABLE_GPU_PRESENTATION") != "1")
        {
            try
            {
                _gpu = new GpuVideoPresenter();
                _gpu.FrameReady += OnGpuFrameReady;
                _gpu.FrontBufferRestored += OnGpuFrontBufferRestored;
                _gpu.Configure(64, 64); // Verify the driver's input, shared output and conversion support before negotiation.
                _gpuAvailable = true;
            }
            catch (Exception ex)
            {
                _gpu?.Dispose(); _gpu = null;
                App.ReportError(new InvalidOperationException("显卡合成初始化失败，已使用兼容合成", ex));
            }
        }
        SetFormatCallbacks(player.NativeReference, _formatCb, _cleanupCb);
        SetCallbacks(player.NativeReference, _lockCb, _unlockCb, _displayCb, IntPtr.Zero);
    }

    private unsafe uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        if (_disposed || width == 0 || height == 0) return 0;
        try
        {
            // Odd dimensions retain the compatible RGB path. NV12 uses two even-sized planes.
            var sourceChroma = unchecked((uint)Marshal.ReadInt32(chroma));
            var eightBit420 = sourceChroma is 0x31315844 or 0x3231564E or 0x30323449 or 0x32315659; // DX11, NV12, I420, YV12.
            var nv12 = _gpuAvailable && !UseCompatibleColorConversion && eightBit420 && (width & 1) == 0 && (height & 1) == 0;
            var format = new FrameBuffers(this, width, height, nv12);
            Marshal.WriteInt32(chroma, nv12 ? 0x3231564E : 0x32335652); // NV12 or opaque RV32/BGRX.
            pitches = (uint)format.Pitch; lines = (uint)format.Lines;
            if (nv12)
            {
                fixed (uint* pitch = &pitches, line = &lines)
                { pitch[1] = pitches; line[1] = (uint)format.Lines / 2; }
            }
            opaque = GCHandle.ToIntPtr(format.Handle);
            Volatile.Write(ref _format, format);
            return 1;
        }
        catch { return 0; } // Never propagate managed allocation/overflow exceptions through native code.
    }

    private void OnCleanup(IntPtr opaque)
    {
        if (opaque == IntPtr.Zero) return;
        var format = (FrameBuffers)GCHandle.FromIntPtr(opaque).Target!;
        lock (format.Gate)
        {
            format.Retired = true;
            Interlocked.CompareExchange(ref _format, null, format);
            format.Release();
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        var format = (FrameBuffers)GCHandle.FromIntPtr(opaque).Target!;
        // Still valid after visual disposal: only the matching native cleanup frees this buffer.
        Marshal.WriteIntPtr(planes, format.Decode);
        if (format.Nv12) Marshal.WriteIntPtr(planes, IntPtr.Size, format.Decode + format.Pitch * format.Lines);
        return format.Decode;
    }

    private static void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_disposed) return;
        var format = (FrameBuffers)GCHandle.FromIntPtr(opaque).Target!;
        if (!ReferenceEquals(format, Volatile.Read(ref _format))) return;
        Interlocked.Increment(ref _delivered);
        bool schedule;
        lock (format.Gate)
        {
            if (format.Retired || _disposed) return;
            // VLC's vmem prepare/display callbacks are serialized. Hand the completed
            // buffer to WPF and let the next lock write into the old presentation buffer.
            // No full-frame copy on the video thread, including when the UI is busy.
            (format.Decode, format.Present) = (format.Present, format.Decode);
            schedule = Interlocked.Exchange(ref format.Pending, 1) == 0;
            if (!schedule)
                Interlocked.Increment(ref _coalesced);
        }
        if (schedule) Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => PresentFrame(format)));
    }

    private DispatcherTimer? _retryTimer;
    private bool CanPresent => IsLoaded && IsVisible && _ownerWindow?.WindowState != WindowState.Minimized;

    private void SchedulePresentationResize()
    {
        if (_disposed || !_gpuAvailable) return;
        _resizeTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _resizeTimer.Stop();
        _resizeTimer.Tick -= OnPresentationResize;
        _resizeTimer.Tick += OnPresentationResize;
        _resizeTimer.Start();
    }

    private void OnPresentationResize(object? sender, EventArgs e)
    {
        _resizeTimer?.Stop();
        _presentationSizeDirty = true;
        if (Volatile.Read(ref _format) is { } format)
        {
            lock (format.Gate) if (!format.Retired) format.Pending = 1;
            PresentFrame(format);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        SchedulePresentationResize();
    }

    private (int Width, int Height) PresentationSize(FrameBuffers format)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = Math.Min(1, Math.Min(ActualWidth * dpi.DpiScaleX / format.Width,
            ActualHeight * dpi.DpiScaleY / format.Height));
        if (scale <= 0) return (format.Width, format.Height);
        // Preserve the exact aspect ratio and round up to cover every displayed pixel.
        var a = format.Width; var b = format.Height;
        while (b != 0) (a, b) = (b, a % b);
        var units = Math.Clamp((int)Math.Ceiling(a * scale), 1, a);
        return (format.Width / a * units, format.Height / a * units);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _format) is { } format && (CanPresent || _gpu?.Pending == true)) PresentFrame(format);
        else if (_gpu?.Pending != true) _retryTimer?.Stop();
    }

    private void RetryPresentation()
    {
        if (_retryTimer == null)
        {
            _retryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(2) };
            _retryTimer.Tick += (_, _) =>
            {
                _retryTimer.Stop();
                if (Volatile.Read(ref _format) is { } current) PresentFrame(current);
                else CompleteGpuFrame();
            };
        }
        _retryTimer.Start();
    }

    private bool CompleteGpuFrame()
    {
        if (_gpu?.Pending != true) return true;
        if (!_gpu.Complete()) return false; // The GPU fence signals the dispatcher; no periodic polling.
        Interlocked.Increment(ref _presented);
        return true;
    }

    private void OnGpuFrameReady()
    {
        if (_disposed) return;
        if (Volatile.Read(ref _format) is { } format) PresentFrame(format);
        else CompleteGpuFrame();
    }
    private void OnGpuFrontBufferRestored()
    {
        if (_disposed) return;
        if (Volatile.Read(ref _format) is { } format)
        {
            lock (format.Gate) if (!format.Retired) format.Pending = 1;
            PresentFrame(format);
        }
    }

    private void PresentFrame(FrameBuffers format)
    {
        try { PresentFrameCore(format); }
        catch (Exception ex) when (_gpu != null)
        {
            // Keep the current NV12 negotiation playable if the driver is removed
            // mid-stream; the next negotiation will request the compatible RGB format.
            _gpuAvailable = false;
            try { _gpu.Dispose(); } catch { }
            _gpu = null; _gpuFormat = null; _bitmapFormat = null;
            App.ReportError(new InvalidOperationException("显卡合成中断，已切换兼容合成", ex));
            PresentFrameCore(format);
        }
    }

    private void PresentFrameCore(FrameBuffers format)
    {
        if (_disposed) return;
        if (!CompleteGpuFrame() || !CanPresent || !ReferenceEquals(format, Volatile.Read(ref _format)) || Volatile.Read(ref format.Pending) == 0) return;
        if (format.Nv12 && _gpu != null)
        {
            if (!ReferenceEquals(_gpuFormat, format) || _presentationSizeDirty)
            {
                var output = PresentationSize(format);
                _gpu.Configure(format.Width, format.Height, output.Width, output.Height);
                _gpuFormat = format;
                _presentationSizeDirty = false;
                Source = _gpu.Image;
            }
            lock (format.Gate)
            {
                if (format.Retired || _disposed || format.Pending == 0) return;
                if (!_gpu.Begin(format.Present, format.Pitch))
                {
                    // Lost presentation surfaces wake from their availability event.
                    // Poll only a busy copy, never a locked/disconnected desktop.
                    if (_gpu.FrontBufferAvailable) RetryPresentation();
                    return;
                }
                format.Pending = 0;
            }
            return;
        }
        if (!ReferenceEquals(_bitmapFormat, format) || _bitmap == null || _bitmap.IsFrozen)
        {
            _gpuFormat = null;
            _bitmap = new WriteableBitmap(format.Width, format.Height, 96, 96, PixelFormats.Bgr32, null);
            _bitmapFormat = format;
            Source = _bitmap;
        }
        var bitmap = _bitmap!;
        // Lock() can wait on the render thread. A missed upload must not stall gestures.
        if (!bitmap.TryLock(new Duration(TimeSpan.Zero)))
        {
            // Retry even when the source pauses immediately after this frame. One
            // bounded timer avoids spinning at Render priority or losing its last frame.
            RetryPresentation();
            return;
        }
        try
        {
            lock (format.Gate)
            {
                if (format.Retired || _disposed || format.Pending == 0) return;
                // Clear while holding the buffer lock: a frame arriving immediately
                // after this upload remains pending, even if playback then pauses.
                format.Pending = 0;
                if (format.Nv12)
                    ConvertNv12(format, bitmap.BackBuffer, bitmap.BackBufferStride);
                else if (bitmap.BackBufferStride == format.Pitch)
                    CopyMemory(bitmap.BackBuffer, format.Present, checked(format.Pitch * format.Height));
                else
                    for (var row = 0; row < format.Height; row++)
                        CopyMemory(IntPtr.Add(bitmap.BackBuffer, row * bitmap.BackBufferStride),
                            IntPtr.Add(format.Present, row * format.Pitch), format.Width * 4);
            }
            bitmap.AddDirtyRect(new Int32Rect(0, 0, format.Width, format.Height));
            Interlocked.Increment(ref _presented);
        }
        finally { bitmap.Unlock(); }
    }

    private static unsafe void ConvertNv12(FrameBuffers format, IntPtr destination, int stride)
    {
        var source = (byte*)format.Present;
        var uv = source + format.Pitch * format.Lines;
        var hd = format.Height >= 720;
        static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
        for (var y = 0; y < format.Height; y++)
        {
            var pixels = (uint*)((byte*)destination + y * stride);
            var luma = source + y * format.Pitch;
            var chroma = uv + y / 2 * format.Pitch;
            for (var x = 0; x < format.Width; x++)
            {
                var c = Math.Max(0, luma[x] - 16) * 298;
                var d = chroma[x & ~1] - 128; var e = chroma[(x & ~1) + 1] - 128;
                var r = Clamp((c + (hd ? 459 : 409) * e + 128) >> 8);
                var g = Clamp((c - (hd ? 55 : 100) * d - (hd ? 136 : 208) * e + 128) >> 8);
                var b = Clamp((c + (hd ? 541 : 516) * d + 128) >> 8);
                pixels[x] = 0xff000000u | (uint)(r << 16 | g << 8 | b);
            }
        }
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _retryTimer?.Stop();
        _resizeTimer?.Stop();
        Source = null;
        _bitmap = null;
        _bitmapFormat = null;
        _gpu?.Dispose(); _gpu = null; _gpuFormat = null;
        if (_ownerWindow != null) _ownerWindow.StateChanged -= OnWindowStateChanged;
        _ownerWindow = null;
        // OnCleanup owns native buffers. A 4K RV32 frame exceeds the old 16 MiB fallback.
    }

    public BitmapSource? CaptureFrame()
    {
        VerifyAccess();
        if (_disposed) return null;
        if (_gpuFormat != null && _gpu != null)
        {
            try { return _gpu.Capture(); }
            catch { return null; } // A transition can use its cover if the device vanished.
        }
        if (_bitmap == null) return null;
        var frame = _bitmap.Clone(); frame.Freeze(); return frame;
    }
}
