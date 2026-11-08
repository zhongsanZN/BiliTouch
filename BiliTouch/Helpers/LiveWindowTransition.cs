using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BiliTouch.Helpers;

/// <summary>
/// HyperOS-style continuous window morph for the browser-layout small-player transition.
///
/// ★ 为什么不用「静态截图 / 纯色遮罩」:
///   前者要 TakeSnapshot 写 PNG、解码、轮询, 还得 DwmFlush 与 180ms 停留;
///   后者把整块视频区盖成深灰板, 用户看到的是"灰板 + 图标放大 + 结束帧视频瞬移",
///   完全没有内容连续性 —— 这正是用户说的"生硬"。
///
/// ★ 这里用 DWM Thumbnail 把**原生 vout 的实时画面**镜像到一个独立的顶层窗口:
///   · 镜像由 GPU 合成, 源窗口尺寸不变 ⇒ libvlc 的 d3d11 vout **一次都不重建**;
///   · 逐帧只改目标窗口的屏幕矩形 + 缩略图的 rcDestination / rcSource / 圆角 region,
///     于是"内容本身"连续地移动、缩放、变圆角, 与澎湃OS 的窗口动画同一套机制;
///   · 动画结束帧才把真正的 VideoSurface 一次性移到目标几何; 那一刻镜像仍盖在上面,
///     等 vout 出新帧后再淡出 ⇒ 全程看不到黑帧/白边/瞬移。
///
/// ★ 实测前提(artifacts/dwm-thumb-probe): DwmRegisterThumbnail 的**源必须是顶层窗口**,
///   传子窗口(panel / VLC video output) 一律 E_INVALIDARG。所以源取 PlayerWindow 的
///   HWND, 再用 rcSource 把画面裁到视频区。
///
/// ★ 任何 P/Invoke 失败都会抛, 调用方 catch 后回退到纯 WPF 遮罩路径, 不会更差。
/// </summary>
internal sealed class LiveWindowTransition : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    private const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    private const uint DWM_TNP_RECTSOURCE = 0x00000002;
    private const uint DWM_TNP_OPACITY = 0x00000004;
    private const uint DWM_TNP_VISIBLE = 0x00000008;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT rect);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private Window? _window;
    private IntPtr _targetHwnd, _thumb;
    private bool _disposed;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>当前帧镜像窗口的屏幕矩形（物理像素）；探针/测试读它验证几何连续性。</summary>
    internal Rect CurrentRect { get; private set; } = Rect.Empty;

    /// <summary>
    /// 临界阻尼偏软的 spring(ζ=0.86, ω=14): 先快后慢、末端极轻回弹,
    /// 不像 1-exp(-7t)*(cos(11t)+...) 那种欠阻尼曲线会明显过冲 —— 后者正是"生硬/弹跳感"的来源。
    /// </summary>
    internal static double Spring(double t)
    {
        if (t >= 1) return 1;
        const double zeta = 0.86, omega = 14.0;
        var wd = omega * Math.Sqrt(1 - zeta * zeta);
        return 1 - Math.Exp(-zeta * omega * t) *
            (Math.Cos(wd * t) + (zeta * omega / wd) * Math.Sin(wd * t));
    }

    private static RECT ToRect(Rect r) => new()
    {
        Left = (int)Math.Round(r.Left), Top = (int)Math.Round(r.Top),
        Right = (int)Math.Round(r.Right), Bottom = (int)Math.Round(r.Bottom)
    };

    private static int Hr(int hr) => hr;

    private static void Check(int hr, string what)
    {
        if (hr != 0) throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
    }

    /// <summary>
    /// 跑一次连续变形。所有矩形都是**屏幕物理像素**(from/to 是屏幕坐标, source 是源窗口客户区坐标)。
    /// </summary>
    /// <param name="commit">动画走完那一帧调用: 把真正的 VideoSurface 移到目标几何。</param>
    /// <param name="sourceRect">每帧取一次的视频区(源窗口客户区物理像素), commit 后会自动跟着变。</param>
    internal async Task RunAsync(
        Window owner,
        IntPtr srcRootHwnd,
        Rect from,
        Rect to,
        Func<Rect> sourceRect,
        Action commit,
        double durationMs = 430)
    {
        if (_disposed) return;
        if (from.IsEmpty || to.IsEmpty || from.Width < 2 || from.Height < 2)
            throw new InvalidOperationException("empty transition geometry");

        // 目标窗口必须是**普通(非 layered)顶层 HWND**: DWM Thumbnail 在 AllowsTransparency
        // 的分层窗口上注册会失败(实测 E_INVALIDARG)。owned window 天然在播放器之上且不抢焦点。
        _window = new Window
        {
            Owner = owner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = false,
            Background = Brushes.Black,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            Width = from.Width,
            Height = from.Height,
            Left = from.Left,
            Top = from.Top,
        };
        _window.Show();
        _targetHwnd = new WindowInteropHelper(_window).Handle;
        if (_targetHwnd == IntPtr.Zero) throw new InvalidOperationException("target HWND not created");

        Check(DwmRegisterThumbnail(_targetHwnd, srcRootHwnd, out _thumb), "DwmRegisterThumbnail");
        if (_thumb == IntPtr.Zero) throw new InvalidOperationException("DwmRegisterThumbnail returned null");

        try
        {
            var start = Stopwatch.GetTimestamp();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? frame = null;
            var committed = false;
            long commitAt = 0;

            frame = (_, _) =>
            {
                if (_disposed) { done.TrySetResult(); return; }
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var t = Math.Clamp(elapsedMs / durationMs, 0, 1);
                var p = Spring(t);

                // ① 位置 / 尺寸 / 圆角 同步插值 —— 澎湃OS 那种"整体连续变形"的关键。
                var rect = new Rect(
                    from.Left + (to.Left - from.Left) * p,
                    from.Top + (to.Top - from.Top) * p,
                    Math.Max(2, from.Width + (to.Width - from.Width) * p),
                    Math.Max(2, from.Height + (to.Height - from.Height) * p));
                var radius = 4 + 14 * p;
                CurrentRect = rect;

                if (!SetWindowPos(_targetHwnd, IntPtr.Zero,
                        (int)Math.Round(rect.Left), (int)Math.Round(rect.Top),
                        (int)Math.Round(rect.Width), (int)Math.Round(rect.Height),
                        SWP_NOACTIVATE | SWP_NOZORDER))
                    { done.TrySetResult(); return; }

                // 圆角 region 归系统所有(SetWindowRgn 成功后不要 DeleteObject)。
                var region = CreateRoundRectRgn(0, 0, (int)Math.Round(rect.Width) + 1, (int)Math.Round(rect.Height) + 1,
                    (int)Math.Round(radius * 2), (int)Math.Round(radius * 2));
                if (region != IntPtr.Zero)
                {
                    if (SetWindowRgn(_targetHwnd, region, true) == 0) DeleteObject(region);
                }

                if (!GetClientRect(_targetHwnd, out var client)) { done.TrySetResult(); return; }
                var src = ToRect(sourceRect());
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE | DWM_TNP_OPACITY,
                    rcDestination = client,
                    rcSource = src,
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = false,
                };
                var hr = DwmUpdateThumbnailProperties(_thumb, ref props);
                if (hr != 0) { done.TrySetResult(); return; }

                // ② 到点: 让真实窗口一次性落位。此时镜像仍盖在上面, 用户看不到 vout 重建。
                if (t >= 1 && !committed)
                {
                    committed = true;
                    commitAt = Stopwatch.GetTimestamp();
                    try { commit(); } catch { /* 落位失败也要把镜像撤掉, 由上层兜底 */ }
                }

                // ③ 等 vout 出几帧新画面, 再用 110ms 把镜像淡出 => 无缝交接。
                if (committed)
                {
                    var settleMs = Stopwatch.GetElapsedTime(commitAt).TotalMilliseconds;
                    var fade = (byte)Math.Clamp(255 - settleMs / 110 * 255, 0, 255);
                    props.dwFlags = DWM_TNP_OPACITY | DWM_TNP_VISIBLE;
                    props.opacity = fade;
                    DwmUpdateThumbnailProperties(_thumb, ref props);
                    if (settleMs >= 190) done.TrySetResult();
                }
            };

            CompositionTarget.Rendering += frame;
            try { await done.Task.WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token); }
            finally { CompositionTarget.Rendering -= frame; }
        }
        finally
        {
            if (_thumb != IntPtr.Zero) { DwmUnregisterThumbnail(_thumb); _thumb = IntPtr.Zero; }
            try { _window?.Close(); } catch { }
            _window = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_thumb != IntPtr.Zero) { try { DwmUnregisterThumbnail(_thumb); } catch { } _thumb = IntPtr.Zero; }
        try { _window?.Close(); } catch { }
        _window = null;
    }
}
