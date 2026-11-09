using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliTouch.Helpers;

/// <summary>
/// 把 libvlc 的 vmem 解码帧画成一个**普通 WPF 元素**（Image）。
///
/// ★ 为什么需要它（2026-11-09 第七/八轮）：
///   现在的播放器用 LibVLCSharp 的 `VideoView`，它是 `HwndHost` —— 原生窗口不参与
///   WPF 合成。后果是画面**不能圆角、不能挂阴影、不能逐帧连续变形**，小窗停靠只能拿
///   一块假卡片盖在上面，怎么调都到不了澎湃OS 那种"窗口本体变形"。
///   走 vmem 之后画面是一张 `WriteableBitmap`，上面这些全都能做，而且
///   `RenderTargetBitmap` 也能截到它（HwndHost 截不到）。
///
/// ★ 实测代价（artifacts/vmem-probe，本机）：1080p60 交付 59.8fps、WPF 合成 116fps，
///   每帧拷贝 0.51ms + 上传 0.44ms ≈ 1ms，占 16.7ms 预算 6%。
///
/// ★ 用法：**先 Attach(player) 再 Play**（vmem 回调必须在起播前挂好），
///   并且这个 MediaPlayer 必须来自 <see cref="Services.VlcCore.GetVmem"/>（带 --vout=vmem）。
/// </summary>
public sealed class VmemVideoView : Image, IDisposable
{
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, int bytes);

    private readonly object _gate = new();
    private WriteableBitmap? _bitmap;
    private IntPtr _buffer, _present;
    private int _bytes;
    private uint _width, _height;
    private int _pending;
    private bool _disposed;

    /// <summary>
    /// ★ 保命垫：Dispose 之后 libvlc 的 vout 线程**仍可能再调一次 lock/display**
    ///   （实测退出时 0xC0000005 访问违例）。若那时把指针写成 0，原生侧会解引用空指针。
    ///   所以释放后所有 lock 都返回这块**永不释放**的垫子内存，display 直接丢弃。
    ///   一份 16MB，进程内只分配一次。
    /// </summary>
    private static IntPtr _deadBuffer;
    private const int DeadBufferBytes = 16 * 1024 * 1024;
    private static IntPtr DeadBuffer()
    {
        if (_deadBuffer == IntPtr.Zero) _deadBuffer = Marshal.AllocHGlobal(DeadBufferBytes);
        return _deadBuffer;
    }

    // ★★ 委托必须**存字段**保活。SetVideoCallbacks 把函数指针交给 libvlc 原生侧，
    //    托管侧若没有强引用，GC 会回收这些委托 → 原生回调跳进已释放的桩
    //    → 进程 0xC0000005。这是 vmem 的经典坑，实测退出时必崩。
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    /// <summary>已交付的解码帧总数（诊断用）。</summary>
    public long DeliveredFrames => Interlocked.Read(ref _delivered);
    private long _delivered;

    /// <summary>因为上一帧还没画完而被合并掉的帧数（诊断用，正常应在个位数）。</summary>
    public long CoalescedFrames => Interlocked.Read(ref _coalesced);
    private long _coalesced;

    /// <summary>视频实际像素尺寸（格式协商后才有值）。</summary>
    public (uint Width, uint Height) VideoSize => (_width, _height);

    public VmemVideoView()
    {
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        // 画面来自解码器，直接贴、不要 WPF 再做一次高质量重采样（那是每帧全图卷积）。
        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _unlockCb = OnUnlock;
        _displayCb = OnDisplay;
    }

    /// <summary>把 vmem 回调挂到这个播放器上。必须在 Play 之前调用。</summary>
    public void Attach(MediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // RV32 = BGRA，和 WPF 的 Bgra32 完全一致，省掉一次色彩转换
        Marshal.Copy(new[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0, chroma, 4);
        width = width;
        height = height;
        pitches = width * 4;
        lines = height;

        lock (_gate)
        {
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
            if (_present != IntPtr.Zero) { Marshal.FreeHGlobal(_present); _present = IntPtr.Zero; }
            _width = width; _height = height;
            _bytes = (int)(pitches * lines);
            _buffer = Marshal.AllocHGlobal(_bytes);
            _present = Marshal.AllocHGlobal(_bytes);
        }

        // WriteableBitmap 只能在 UI 线程建
        var cw = width; var ch = height;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            _bitmap = new WriteableBitmap((int)cw, (int)ch, 96, 96, PixelFormats.Bgra32, null);
            // ★ 不要 Freeze：冻结后的 WriteableBitmap 不能再 Lock/写 BackBuffer。
            //   这张位图的创建与写入都在 UI 线程，本来就不需要跨线程访问。
            Source = _bitmap;
        }), DispatcherPriority.Loaded);
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_gate)
        {
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
            if (_present != IntPtr.Zero) { Marshal.FreeHGlobal(_present); _present = IntPtr.Zero; }
            _bytes = 0;
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        // ★ 见 DeadBuffer 的说明：释放后也要给原生侧一块能写的内存，绝不能给 0。
        var buf = _disposed || _buffer == IntPtr.Zero ? DeadBuffer() : _buffer;
        Marshal.WriteIntPtr(planes, 0, buf);
        return buf;
    }

    private static void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_disposed) return;
        Interlocked.Increment(ref _delivered);
        var bmp = _bitmap;
        if (bmp == null || _present == IntPtr.Zero) return;

        // ★ 合并：如果上一帧还在排队没画，就别再排一个（否则 60fps 会堆出 60 个
        //   Dispatcher 操作，UI 线程反而被自己的队列拖慢）。最新帧已经在 _present 里，
        //   等那一次更新把它带走即可。
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            Interlocked.Increment(ref _coalesced);
            return;
        }

        var bytes = _bytes;
        lock (_gate) { if (_present != IntPtr.Zero) CopyMemory(_present, _buffer, bytes); }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            try
            {
                if (_disposed || _bitmap == null) return;
                _bitmap.Lock();
                try
                {
                    // ★ 必须在这里再确认一次 _present/缓冲区还活着：
                    //   Stop()/Dispose() 之后队列里可能还压着几帧渲染回调，
                    //   此时 _present 已经被 OnCleanup 释放成 IntPtr.Zero ——
                    //   直接 CopyMemory 从空指针读就是 0xC0000005（实测必崩）。
                    var len = 0;
                    lock (_gate)
                    {
                        if (_present == IntPtr.Zero || _bytes <= 0) return;
                        len = Math.Min(_bytes, _bitmap.BackBufferStride * (int)_height);
                        if (len <= 0) return;
                        CopyMemory(_bitmap.BackBuffer, _present, len);
                    }
                    _bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, (int)_width, (int)_height));
                }
                finally { _bitmap.Unlock(); }
            }
            catch { /* 播放器已失效 */ }
            finally { Interlocked.Exchange(ref _pending, 0); }
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
            if (_present != IntPtr.Zero) { Marshal.FreeHGlobal(_present); _present = IntPtr.Zero; }
        }
        Source = null;
        _bitmap = null;
    }
}
