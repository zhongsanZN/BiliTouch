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
        internal IntPtr Decode, Present, DecodeAllocation, PresentAllocation;
        internal GCHandle Handle;
        internal bool Retired;
        internal int Pending;

        internal FrameBuffers(VmemVideoView owner, uint width, uint height)
        {
            Owner = owner;
            Width = checked((int)width); Height = checked((int)height);
            Pitch = checked((Width * 4 + 31) & ~31);
            Lines = checked((Height + 31) & ~31);
            Bytes = checked(Pitch * Lines);
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
    public (uint Width, uint Height) VideoSize
    {
        get { var format = Volatile.Read(ref _format); return format == null ? (0u, 0u) : ((uint)format.Width, (uint)format.Height); }
    }

    public VmemVideoView()
    {
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        _formatCb = OnFormat; _cleanupCb = OnCleanup;
        _lockCb = OnLock; _unlockCb = OnUnlock; _displayCb = OnDisplay;
    }

    public void Attach(MediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetFormatCallbacks(player.NativeReference, _formatCb, _cleanupCb);
        SetCallbacks(player.NativeReference, _lockCb, _unlockCb, _displayCb, IntPtr.Zero);
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        if (_disposed || width == 0 || height == 0) return 0;
        try
        {
            var format = new FrameBuffers(this, width, height);
            Marshal.WriteInt32(chroma, 0x32335652); // RV32, little endian: BGRA on Windows.
            pitches = (uint)format.Pitch; lines = (uint)format.Lines;
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
        return format.Decode;
    }

    private static void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_disposed) return;
        var format = (FrameBuffers)GCHandle.FromIntPtr(opaque).Target!;
        if (!ReferenceEquals(format, Volatile.Read(ref _format))) return;
        Interlocked.Increment(ref _delivered);
        lock (format.Gate)
        {
            if (format.Retired || _disposed) return;
            // Replace queued pixels even when the UI callback is already pending.
            // Coalescing must retain the newest frame during a busy layout.
            CopyMemory(format.Present, picture, format.Bytes);
        }
        if (Interlocked.Exchange(ref format.Pending, 1) == 1)
        {
            Interlocked.Increment(ref _coalesced);
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => PresentFrame(format)));
    }

    private void PresentFrame(FrameBuffers format)
    {
        try
        {
            if (_disposed || !ReferenceEquals(format, Volatile.Read(ref _format))) return;
            if (!ReferenceEquals(_bitmapFormat, format))
            {
                _bitmap = new WriteableBitmap(format.Width, format.Height, 96, 96, PixelFormats.Bgra32, null);
                _bitmapFormat = format;
                Source = _bitmap;
            }
            var bitmap = _bitmap!;
            // Lock() can wait on the render thread. A missed upload must not stall gestures.
            if (!bitmap.TryLock(new Duration(TimeSpan.Zero))) return;
            try
            {
                lock (format.Gate)
                {
                    if (format.Retired || _disposed) return;
                    if (bitmap.BackBufferStride == format.Pitch)
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
        finally { Interlocked.Exchange(ref format.Pending, 0); }
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        Source = null;
        _bitmap = null;
        _bitmapFormat = null;
        // OnCleanup owns native buffers. A 4K RV32 frame exceeds the old 16 MiB fallback.
    }
}
