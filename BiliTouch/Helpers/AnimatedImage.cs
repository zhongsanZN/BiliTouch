using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BiliTouch.Helpers;

/// <summary>
/// 已在后台线程合成并冻结的多帧动图（GIF / 动态 WebP）。
/// 所有帧均为不可变冻结的 Pbgra32 <see cref="BitmapSource"/>，可在 UI 线程零拷贝切帧。
/// </summary>
public sealed class AnimatedBitmap
{
    public BitmapSource[] Frames { get; }
    public int[] DelaysMs { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public long EstimatedBytes { get; }
    public bool IsAnimated => Frames.Length > 1;
    public BitmapSource FirstFrame => Frames[0];

    public AnimatedBitmap(BitmapSource[] frames, int[] delaysMs, int pixelWidth, int pixelHeight, long estimatedBytes)
    {
        Frames = frames;
        DelaysMs = delaysMs;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        EstimatedBytes = estimatedBytes;
    }
}

/// <summary>
/// 图片加载结果：要么是常规静态 <see cref="BitmapImage"/>，要么是多帧 <see cref="AnimatedBitmap"/>。
/// </summary>
public readonly struct LoadedImage
{
    public BitmapImage? Static { get; }
    public AnimatedBitmap? Animated { get; }
    public bool HasImage => Animated != null || Static != null;
    public bool IsAnimated => Animated is { IsAnimated: true };
    public BitmapSource? FirstFrame => Animated?.FirstFrame ?? (BitmapSource?)Static;
    public int PixelWidth => Animated?.PixelWidth ?? Static?.PixelWidth ?? 0;
    public int PixelHeight => Animated?.PixelHeight ?? Static?.PixelHeight ?? 0;

    public LoadedImage(BitmapImage? staticImage)
    {
        Static = staticImage;
        Animated = null;
    }

    public LoadedImage(AnimatedBitmap? animated)
    {
        Static = null;
        Animated = animated;
    }
}

/// <summary>
/// 纯托管后台动图解码器（零第三方依赖）。
/// 支持完整 GIF89a 帧合成（逻辑画布尺寸、局部帧偏移、4 种 Disposal 方式、透明混合、逐帧时长），
/// 以及 Windows WIC 支持多帧时的动态 WebP 解码，并内置像素预算与按需降采样防止巨型动图撑爆内存。
/// </summary>
internal static class AnimatedImageDecoder
{
    /// <summary>缩略图/表情等常规尺寸单张动图的内存上限（16 MB）。</summary>
    private const long ThumbnailBudgetBytes = 16L * 1024 * 1024;
    /// <summary>全屏/大图查看模式单张动图的内存上限（48 MB）。</summary>
    private const long PreviewBudgetBytes = 48L * 1024 * 1024;
    /// <summary>单张动图解码帧数上限（极端超长 GIF 按步长抽帧并累加延时保持真实时长）。</summary>
    private const int MaxDecodedFrames = 240;

    /// <summary>
    /// 极速魔数判断：仅 GIF87a/GIF89a 与含动画标记的 WebP 进入动图解码路径，
    /// JPEG / 静态 PNG / 静态 WebP 直接走原有静态快速路径（零开销）。
    /// </summary>
    public static bool IsPotentiallyAnimated(ReadOnlySpan<byte> header)
    {
        if (IsGifHeader(header)) return true;
        if (IsAnimatedWebpHeader(header)) return true;
        return false;
    }

    public static bool IsGifHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 6
        && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F'
        && header[3] == (byte)'8' && (header[4] == (byte)'7' || header[4] == (byte)'9')
        && header[5] == (byte)'a';

    public static bool IsAnimatedWebpHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 21) return false;
        if (header[0] != (byte)'R' || header[1] != (byte)'I' || header[2] != (byte)'F' || header[3] != (byte)'F')
            return false;
        if (header[8] != (byte)'W' || header[9] != (byte)'E' || header[10] != (byte)'B' || header[11] != (byte)'P')
            return false;
        // VP8X 扩展头：chunk FourCC = "VP8X"，第 20 字节 bit 1 (0x02) 表示 Animation
        if (header[12] == (byte)'V' && header[13] == (byte)'P' && header[14] == (byte)'8' && header[15] == (byte)'X')
            return (header[20] & 0x02) != 0;
        return false;
    }

    public static bool IsPotentiallyAnimatedFile(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[32];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = fs.Read(header);
            return IsPotentiallyAnimated(header[..read]);
        }
        catch
        {
            return false;
        }
    }

    public static AnimatedBitmap? TryDecodeFile(string path, int maxDecodeWidth)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return TryDecodeBytes(bytes, maxDecodeWidth);
        }
        catch
        {
            return null;
        }
    }

    public static AnimatedBitmap? TryDecodeBytes(byte[] bytes, int maxDecodeWidth)
    {
        if (bytes.Length < 12) return null;
        if (IsGifHeader(bytes))
            return DecodeGif(bytes, maxDecodeWidth);
        if (IsAnimatedWebpHeader(bytes))
            return DecodeAnimatedWebp(bytes, maxDecodeWidth);
        return null;
    }

    private static AnimatedBitmap? DecodeGif(byte[] bytes, int maxDecodeWidth)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var decoder = new GifBitmapDecoder(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frameCount = decoder.Frames.Count;
            if (frameCount <= 0) return null;

            // 读取逻辑画布尺寸；部分不规范 GIF 缺失逻辑屏元数据，用所有帧的外包盒兜底
            int canvasW = ReadMetaInt(decoder.Metadata as BitmapMetadata, "/logscrdesc/Width", 0);
            int canvasH = ReadMetaInt(decoder.Metadata as BitmapMetadata, "/logscrdesc/Height", 0);

            var frameInfos = new (int Left, int Top, int Width, int Height, int DelayMs, int Disposal)[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                var frame = decoder.Frames[i];
                var meta = frame.Metadata as BitmapMetadata;
                int left = Math.Max(0, ReadMetaInt(meta, "/imgdesc/Left", 0));
                int top = Math.Max(0, ReadMetaInt(meta, "/imgdesc/Top", 0));
                int fw = frame.PixelWidth;
                int fh = frame.PixelHeight;
                int rawDelay = ReadMetaInt(meta, "/grctlext/Delay", 10);
                // 浏览器通用规则（Chrome/Edge/Firefox）：delay <= 1（0ms 或 10ms）按 100ms 计，避免 0ms 狂飙 CPU
                int delayMs = rawDelay <= 1 ? 100 : Math.Clamp(rawDelay * 10, 20, 10000);
                int disposal = ReadMetaInt(meta, "/grctlext/Disposal", 0);
                frameInfos[i] = (left, top, fw, fh, delayMs, disposal);

                if (left + fw > canvasW) canvasW = left + fw;
                if (top + fh > canvasH) canvasH = top + fh;
            }

            if (canvasW <= 0 || canvasH <= 0) return null;

            // 单帧 GIF 若正好铺满画布，不需要走多帧合成缓存（交给静态路径更省内存），
            // 但若首帧有局部偏移或小于逻辑画布，仍合成一张完整的单帧以防裁切。
            bool isSingleFullFrame = frameCount == 1
                && frameInfos[0].Left == 0 && frameInfos[0].Top == 0
                && frameInfos[0].Width == canvasW && frameInfos[0].Height == canvasH;
            if (isSingleFullFrame) return null;

            var (targetW, targetH, frameStride) = ComputeTargetDimensions(canvasW, canvasH, frameCount, maxDecodeWidth);
            int canvasStride = canvasW * 4;
            var canvas = new byte[canvasStride * canvasH];
            byte[]? previousSnapshot = null;

            var outFrames = new List<BitmapSource>(Math.Min(frameCount, MaxDecodedFrames));
            var outDelays = new List<int>(Math.Min(frameCount, MaxDecodedFrames));
            int pendingDelay = 0;

            for (int i = 0; i < frameCount; i++)
            {
                var info = frameInfos[i];

                // Disposal = 3 (RestoreToPrevious) 需要在绘制当前帧之前快照画布
                if (info.Disposal == 3)
                {
                    previousSnapshot ??= new byte[canvas.Length];
                    Buffer.BlockCopy(canvas, 0, previousSnapshot, 0, canvas.Length);
                }

                // 将当前帧转为 Pbgra32 并按 alpha-over 混合到逻辑画布
                var rawFrame = decoder.Frames[i];
                BitmapSource src = rawFrame.Format == PixelFormats.Pbgra32
                    ? rawFrame
                    : new FormatConvertedBitmap(rawFrame, PixelFormats.Pbgra32, null, 0);

                int fw = src.PixelWidth;
                int fh = src.PixelHeight;
                if (fw > 0 && fh > 0)
                {
                    int srcStride = fw * 4;
                    var srcPixels = new byte[srcStride * fh];
                    src.CopyPixels(srcPixels, srcStride, 0);
                    BlitAlphaOver(canvas, canvasW, canvasH, srcPixels, fw, fh, info.Left, info.Top);
                }

                pendingDelay += info.DelayMs;
                bool keepFrame = frameStride <= 1 || (i % frameStride == 0) || (i == frameCount - 1);
                if (keepFrame)
                {
                    var composited = CreateFrozenFrame(canvas, canvasW, canvasH, targetW, targetH);
                    outFrames.Add(composited);
                    outDelays.Add(Math.Max(20, pendingDelay));
                    pendingDelay = 0;
                }

                // 应用当前帧的 Disposal 供下一帧使用
                if (info.Disposal == 2)
                {
                    ClearRect(canvas, canvasW, canvasH, info.Left, info.Top, info.Width, info.Height);
                }
                else if (info.Disposal == 3 && previousSnapshot != null)
                {
                    Buffer.BlockCopy(previousSnapshot, 0, canvas, 0, canvas.Length);
                }
            }

            if (outFrames.Count == 0) return null;
            if (pendingDelay > 0) outDelays[^1] += pendingDelay;

            long estimatedBytes = (long)targetW * targetH * 4 * outFrames.Count;
            return new AnimatedBitmap(outFrames.ToArray(), outDelays.ToArray(), targetW, targetH, estimatedBytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 尝试使用 Windows WIC 解码动态 WebP（当系统 WIC WebP 扩展返回多帧时生效），
    /// 并直接从 RIFF/WEBP ANMF chunk 解析精确的逐帧延时。
    /// </summary>
    private static AnimatedBitmap? DecodeAnimatedWebp(byte[] bytes, int maxDecodeWidth)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            int frameCount = decoder.Frames.Count;
            if (frameCount <= 1) return null;

            var anmfDelays = ParseWebpAnmfDurations(bytes);
            int canvasW = decoder.Frames[0].PixelWidth;
            int canvasH = decoder.Frames[0].PixelHeight;
            if (canvasW <= 0 || canvasH <= 0) return null;

            var (targetW, targetH, frameStride) = ComputeTargetDimensions(canvasW, canvasH, frameCount, maxDecodeWidth);
            var outFrames = new List<BitmapSource>(Math.Min(frameCount, MaxDecodedFrames));
            var outDelays = new List<int>(Math.Min(frameCount, MaxDecodedFrames));
            int pendingDelay = 0;

            for (int i = 0; i < frameCount; i++)
            {
                int delay = i < anmfDelays.Count && anmfDelays[i] > 0 ? anmfDelays[i] : 100;
                delay = Math.Clamp(delay, 20, 10000);
                pendingDelay += delay;

                bool keepFrame = frameStride <= 1 || (i % frameStride == 0) || (i == frameCount - 1);
                if (!keepFrame) continue;

                var frame = decoder.Frames[i];
                BitmapSource src = frame.Format == PixelFormats.Pbgra32
                    ? frame
                    : new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);

                int fw = src.PixelWidth;
                int fh = src.PixelHeight;
                if (fw <= 0 || fh <= 0) continue;

                var buf = new byte[fw * 4 * fh];
                src.CopyPixels(buf, fw * 4, 0);
                outFrames.Add(CreateFrozenFrame(buf, fw, fh, targetW, targetH));
                outDelays.Add(Math.Max(20, pendingDelay));
                pendingDelay = 0;
            }

            if (outFrames.Count <= 1) return null;
            if (pendingDelay > 0) outDelays[^1] += pendingDelay;

            long estimatedBytes = (long)targetW * targetH * 4 * outFrames.Count;
            return new AnimatedBitmap(outFrames.ToArray(), outDelays.ToArray(), targetW, targetH, estimatedBytes);
        }
        catch
        {
            return null;
        }
    }

    private static List<int> ParseWebpAnmfDurations(byte[] bytes)
    {
        var delays = new List<int>();
        int pos = 12; // 跳过 "RIFF" + size(4) + "WEBP"
        while (pos + 8 <= bytes.Length)
        {
            bool isAnmf = bytes[pos] == (byte)'A' && bytes[pos + 1] == (byte)'N'
                       && bytes[pos + 2] == (byte)'M' && bytes[pos + 3] == (byte)'F';
            uint chunkSize = (uint)(bytes[pos + 4] | (bytes[pos + 5] << 8) | (bytes[pos + 6] << 16) | (bytes[pos + 7] << 24));
            int payloadStart = pos + 8;
            if (isAnmf && payloadStart + 16 <= bytes.Length)
            {
                // ANMF 载荷偏移 12..14 为 24 位小端帧时长（毫秒）
                int duration = bytes[payloadStart + 12]
                             | (bytes[payloadStart + 13] << 8)
                             | (bytes[payloadStart + 14] << 16);
                delays.Add(duration <= 10 ? 100 : duration);
            }
            long next = payloadStart + (long)chunkSize + (chunkSize & 1);
            if (next <= pos || next > bytes.Length) break;
            pos = (int)next;
        }
        return delays;
    }

    private static (int TargetW, int TargetH, int FrameStride) ComputeTargetDimensions(
        int canvasW, int canvasH, int frameCount, int maxDecodeWidth)
    {
        int targetW = canvasW;
        int targetH = canvasH;
        if (maxDecodeWidth > 0 && targetW > maxDecodeWidth)
        {
            targetH = Math.Max(1, (int)Math.Round(canvasH * (double)maxDecodeWidth / canvasW));
            targetW = maxDecodeWidth;
        }

        bool isPreview = maxDecodeWidth <= 0 || maxDecodeWidth > CoverLoader.MaxCoverDecodeWidth;
        long budget = isPreview ? PreviewBudgetBytes : ThumbnailBudgetBytes;
        int minDimFloor = isPreview ? 480 : 220;

        int effectiveFrames = Math.Min(frameCount, MaxDecodedFrames);
        int frameStride = frameCount > MaxDecodedFrames
            ? (int)Math.Ceiling((double)frameCount / MaxDecodedFrames)
            : 1;

        long bytesNeeded = (long)targetW * targetH * 4 * effectiveFrames;
        if (bytesNeeded > budget && targetW > minDimFloor)
        {
            double shrink = Math.Sqrt((double)budget / bytesNeeded);
            int shrunkW = Math.Max(minDimFloor, (int)Math.Floor(targetW * shrink));
            if (shrunkW < targetW)
            {
                targetH = Math.Max(1, (int)Math.Round(canvasH * (double)shrunkW / canvasW));
                targetW = shrunkW;
            }
        }

        bytesNeeded = (long)targetW * targetH * 4 * effectiveFrames;
        if (bytesNeeded > budget)
        {
            int maxKeep = Math.Max(2, (int)(budget / Math.Max(1L, (long)targetW * targetH * 4)));
            int budgetStride = (int)Math.Ceiling((double)frameCount / maxKeep);
            if (budgetStride > frameStride) frameStride = budgetStride;
        }

        return (targetW, targetH, frameStride);
    }

    private static BitmapSource CreateFrozenFrame(byte[] canvas, int canvasW, int canvasH, int targetW, int targetH)
    {
        int canvasStride = canvasW * 4;
        if (targetW == canvasW && targetH == canvasH)
        {
            var copy = new byte[canvas.Length];
            Buffer.BlockCopy(canvas, 0, copy, 0, canvas.Length);
            var bmp = BitmapSource.Create(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32, null, copy, canvasStride);
            bmp.Freeze();
            return bmp;
        }

        // 先构造临时整幅位图再做缩放并物化像素，避免冻结后的 TransformedBitmap 继续持有整幅大图内存
        var full = BitmapSource.Create(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32, null, canvas, canvasStride);
        var scaled = new TransformedBitmap(full, new ScaleTransform((double)targetW / canvasW, (double)targetH / canvasH));
        int sw = Math.Max(1, scaled.PixelWidth);
        int sh = Math.Max(1, scaled.PixelHeight);
        int outStride = sw * 4;
        var outPixels = new byte[outStride * sh];
        scaled.CopyPixels(outPixels, outStride, 0);
        var materialized = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Pbgra32, null, outPixels, outStride);
        materialized.Freeze();
        return materialized;
    }

    private static void BlitAlphaOver(
        byte[] dst, int dstW, int dstH,
        byte[] src, int srcW, int srcH,
        int offsetX, int offsetY)
    {
        int startY = Math.Max(0, offsetY);
        int endY = Math.Min(dstH, offsetY + srcH);
        int startX = Math.Max(0, offsetX);
        int endX = Math.Min(dstW, offsetX + srcW);
        if (startX >= endX || startY >= endY) return;

        for (int y = startY; y < endY; y++)
        {
            int sy = y - offsetY;
            int dstRow = y * dstW * 4;
            int srcRow = sy * srcW * 4;
            for (int x = startX; x < endX; x++)
            {
                int sx = x - offsetX;
                int si = srcRow + sx * 4;
                byte sa = src[si + 3];
                if (sa == 0) continue;

                int di = dstRow + x * 4;
                if (sa == 255)
                {
                    dst[di] = src[si];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                    dst[di + 3] = 255;
                }
                else
                {
                    int inv = 255 - sa;
                    dst[di] = (byte)(src[si] + (dst[di] * inv + 127) / 255);
                    dst[di + 1] = (byte)(src[si + 1] + (dst[di + 1] * inv + 127) / 255);
                    dst[di + 2] = (byte)(src[si + 2] + (dst[di + 2] * inv + 127) / 255);
                    dst[di + 3] = (byte)(sa + (dst[di + 3] * inv + 127) / 255);
                }
            }
        }
    }

    private static void ClearRect(byte[] canvas, int canvasW, int canvasH, int left, int top, int width, int height)
    {
        int startY = Math.Max(0, top);
        int endY = Math.Min(canvasH, top + height);
        int startX = Math.Max(0, left);
        int endX = Math.Min(canvasW, left + width);
        if (startX >= endX || startY >= endY) return;

        int clearBytes = (endX - startX) * 4;
        for (int y = startY; y < endY; y++)
        {
            Array.Clear(canvas, (y * canvasW + startX) * 4, clearBytes);
        }
    }

    private static int ReadMetaInt(BitmapMetadata? meta, string query, int fallback)
    {
        if (meta == null) return fallback;
        try
        {
            if (meta.ContainsQuery(query))
            {
                var raw = meta.GetQuery(query);
                if (raw != null) return Convert.ToInt32(raw);
            }
        }
        catch { /* 非标准元数据字段，走默认值 */ }
        return fallback;
    }
}

/// <summary>
/// 全局共享调度器的动图播放器。
/// - 全应用仅维护一个 <see cref="DispatcherTimer"/>，屏幕内没有可见动图时定时器完全停转（零 CPU 开销）；
/// - 监听元素 Loaded / Unloaded / IsVisibleChanged，在虚拟化回收、切 Tab、进详情、隐藏缩略图时自动暂停与解绑；
/// - 通过 <see cref="ConditionalWeakTable{TKey, TValue}"/> 与弱引用持有元素，杜绝视觉树泄漏。
/// </summary>
public static class AnimatedImagePlayer
{
    private sealed class PlaybackSlot
    {
        public readonly WeakReference<FrameworkElement> ElementRef;
        public readonly WeakReference<ImageBrush>? BrushRef;
        public AnimatedBitmap Animation;
        public int FrameIndex;
        public long NextFrameAtMs;
        public bool Active;
        public bool Detached;

        public PlaybackSlot(FrameworkElement element, ImageBrush? brush, AnimatedBitmap animation)
        {
            ElementRef = new WeakReference<FrameworkElement>(element);
            BrushRef = brush != null ? new WeakReference<ImageBrush>(brush) : null;
            Animation = animation;
        }
    }

    private static readonly ConditionalWeakTable<FrameworkElement, PlaybackSlot> Slots = new();
    private static readonly List<PlaybackSlot> ActiveSlots = new();
    private static DispatcherTimer? _timer;

    /// <summary>当前处于活跃播放态的动图数（供单元测试断言自动暂停与零泄漏）。</summary>
    internal static int ActiveCount => ActiveSlots.Count;

    internal static bool IsPlaying(FrameworkElement element, AnimatedBitmap animation)
        => Slots.TryGetValue(element, out var slot) && !slot.Detached && ReferenceEquals(slot.Animation, animation);

    public static void Attach(Image image, AnimatedBitmap animation)
    {
        Detach(image);
        image.Source = animation.FirstFrame;
        if (!animation.IsAnimated) return;
        AttachCore(image, null, animation);
    }

    public static void Attach(Border border, ImageBrush brush, AnimatedBitmap animation)
    {
        Detach(border);
        brush.ImageSource = animation.FirstFrame;
        if (!animation.IsAnimated) return;
        AttachCore(border, brush, animation);
    }

    public static void Detach(FrameworkElement element)
    {
        if (!Slots.TryGetValue(element, out var slot)) return;
        slot.Detached = true;
        Slots.Remove(element);
        element.Loaded -= OnElementStateChanged;
        element.Unloaded -= OnElementStateChanged;
        element.IsVisibleChanged -= OnElementVisibleChanged;
        Deactivate(slot);
    }

    private static void AttachCore(FrameworkElement element, ImageBrush? brush, AnimatedBitmap animation)
    {
        var slot = new PlaybackSlot(element, brush, animation);
        Slots.Add(element, slot);
        element.Loaded += OnElementStateChanged;
        element.Unloaded += OnElementStateChanged;
        element.IsVisibleChanged += OnElementVisibleChanged;
        SyncActiveState(element, slot);
    }

    private static void OnElementStateChanged(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && Slots.TryGetValue(fe, out var slot))
            SyncActiveState(fe, slot);
    }

    private static void OnElementVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement fe && Slots.TryGetValue(fe, out var slot))
            SyncActiveState(fe, slot);
    }

    private static void SyncActiveState(FrameworkElement element, PlaybackSlot slot)
    {
        if (slot.Detached) { Deactivate(slot); return; }
        bool shouldRun = element.IsLoaded && element.IsVisible;
        if (shouldRun) Activate(slot);
        else Deactivate(slot);
    }

    private static void Activate(PlaybackSlot slot)
    {
        if (slot.Active || slot.Detached) return;
        slot.Active = true;
        slot.NextFrameAtMs = Environment.TickCount64 + slot.Animation.DelaysMs[slot.FrameIndex];
        ActiveSlots.Add(slot);
        EnsureTimerRunning();
    }

    private static void Deactivate(PlaybackSlot slot)
    {
        if (!slot.Active) return;
        slot.Active = false;
        ActiveSlots.Remove(slot);
        if (ActiveSlots.Count == 0 && _timer != null)
            _timer.Stop();
    }

    private static void EnsureTimerRunning()
    {
        if (_timer == null)
        {
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _timer.Tick += OnTick;
        }
        ScheduleNextTick(Environment.TickCount64);
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        if (ActiveSlots.Count == 0)
        {
            _timer?.Stop();
            return;
        }

        long now = Environment.TickCount64;
        for (int i = ActiveSlots.Count - 1; i >= 0; i--)
        {
            var slot = ActiveSlots[i];
            if (slot.Detached || !slot.ElementRef.TryGetTarget(out var element) || !element.IsLoaded || !element.IsVisible)
            {
                slot.Active = false;
                ActiveSlots.RemoveAt(i);
                continue;
            }

            if (now < slot.NextFrameAtMs) continue;

            var anim = slot.Animation;
            int nextIndex = (slot.FrameIndex + 1) % anim.Frames.Length;
            slot.FrameIndex = nextIndex;
            slot.NextFrameAtMs = now + anim.DelaysMs[nextIndex];

            var frame = anim.Frames[nextIndex];
            if (slot.BrushRef != null)
            {
                if (slot.BrushRef.TryGetTarget(out var brush)
                    && element is Border border
                    && ReferenceEquals(border.Background, brush))
                {
                    brush.ImageSource = frame;
                }
                else
                {
                    slot.Active = false;
                    ActiveSlots.RemoveAt(i);
                }
            }
            else if (element is Image img)
            {
                img.Source = frame;
            }
        }

        if (ActiveSlots.Count == 0)
            _timer?.Stop();
        else
            ScheduleNextTick(now);
    }

    private static void ScheduleNextTick(long now)
    {
        if (_timer == null || ActiveSlots.Count == 0) return;
        long soonest = long.MaxValue;
        for (int i = 0; i < ActiveSlots.Count; i++)
        {
            if (ActiveSlots[i].NextFrameAtMs < soonest)
                soonest = ActiveSlots[i].NextFrameAtMs;
        }
        long waitMs = Math.Clamp(soonest - now, 16, 1000);
        _timer.Interval = TimeSpan.FromMilliseconds(waitMs);
        if (!_timer.IsEnabled) _timer.Start();
    }
}
