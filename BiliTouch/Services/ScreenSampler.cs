using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BiliTouch.Services;

/// <summary>
/// 从**屏幕**上按矩形取一块像素并算平均亮度 —— 用来判断"视频区是不是黑的"。
///
/// ★★★ 为什么必须抓屏幕, 而不是用 libvlc 的 <c>TakeSnapshot</c>：
///   <c>TakeSnapshot</c> 取的是**解码器吐出来的帧**。它只能证明"解码还在出图"。
///   而用户说的"视频变黑"是**画面上没有东西** —— 完全可能解码正常、音频正常、
///   <c>player.Time</c> 一路推进, 而 vout 那一路什么都没画。
///   这两种情况在 <c>Time</c> / <c>TakeSnapshot</c> 上**看不出任何区别**,
///   所以 0.2.9 的探针当时"14 轮全过"却漏掉了黑屏：它只测了播放器时间。
///   ⇒ 判断黑屏只能看屏幕本身。
///
/// ★ 只做 GDI 的 BitBlt, 不引第三方库：本工程 csproj 里
///   <c>&lt;Using Remove="System.Drawing" /&gt;</c>, 而 WIC/BitmapSource 本来就是 WPF 自带。
/// </summary>
internal static class ScreenSampler
{
    /// <summary>一次取样：亮度均值、亮像素占比、以及原图（给人工复核用）。</summary>
    public readonly record struct Sample(int Width, int Height, double Luminance, double BrightRatio, BitmapSource? Image)
    {
        /// <summary>尺寸够大且真的拿到了像素。false 时 <see cref="Luminance"/> 无意义（-1）。</summary>
        public bool IsValid => Width >= 8 && Height >= 8 && Image != null && Luminance >= 0;
    }

    /// <summary>
    /// 把 WPF 元素换算成**物理像素**的屏幕矩形。
    ///
    /// ★ 用 <see cref="UIElement.PointToScreen"/> 而不是自己拿 Left/Top 加 DPI 乘：
    ///   多显示器时 PointToScreen 才认得出负坐标（副屏在主屏左侧）和每屏不同的 DPI。
    /// </summary>
    public static (int X, int Y, int W, int H) ScreenRect(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return (0, 0, 0, 0);
        Point topLeft;
        try { topLeft = element.PointToScreen(new Point(0, 0)); }
        catch (InvalidOperationException) { return (0, 0, 0, 0); }   // 还没进可视树
        var dpi = VisualTreeHelper.GetDpi(element);
        var w = (int)Math.Round(element.ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Round(element.ActualHeight * dpi.DpiScaleY);
        return ((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), w, h);
    }

    /// <summary>抓一块屏幕像素并统计。任何一步失败都返回 <c>IsValid == false</c>，绝不抛。</summary>
    public static Sample Capture(int x, int y, int width, int height)
    {
        var invalid = new Sample(width, height, -1, 0, null);
        if (width < 8 || height < 8) return invalid;

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return invalid;
            memDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero) return invalid;
            previous = SelectObject(memDc, bitmap);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SrcCopy | CaptureBlt)) return invalid;
            SelectObject(memDc, previous);
            previous = IntPtr.Zero;

            var stride = width * 4;
            var buffer = new byte[stride * height];
            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,        // 负高度 = 自上而下, 与 BitmapSource 的行序一致
                Planes = 1,
                BitCount = 32,
                Compression = 0,         // BI_RGB
                SizeImage = (uint)(stride * height)
            };
            if (GetDIBits(memDc, bitmap, 0, (uint)height, buffer, ref info, 0) == 0) return invalid;

            // BGRA。亮度用 ITU-R BT.601 的权重, 与"看起来亮不亮"一致。
            double sum = 0;
            long bright = 0;
            for (var offset = 0; offset < buffer.Length; offset += 4)
            {
                var b = buffer[offset];
                var g = buffer[offset + 1];
                var r = buffer[offset + 2];
                var luminance = 0.114 * b + 0.587 * g + 0.299 * r;
                sum += luminance;
                if (luminance > 24) bright++;
            }
            var pixels = (double)(width * height);
            var mean = sum / pixels;
            var ratio = bright / pixels;

            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
            image.Freeze();
            return new Sample(width, height, mean, ratio, image);
        }
        catch
        {
            return invalid;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>把取样原图存成 PNG（人工复核"到底是黑屏还是取错地方"）。失败静默。</summary>
    public static void SavePng(BitmapSource? image, string path)
    {
        if (image == null) return;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = System.IO.File.Create(path);
            encoder.Save(stream);
        }
        catch { /* 存图失败不影响验证结论 */ }
    }

    // ---------------------------------------------------------------- P/Invoke

    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;   // 连分层窗口一起抓, 否则会漏掉部分合成内容

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines,
        byte[] bits, ref BitmapInfoHeader info, uint usage);
}
