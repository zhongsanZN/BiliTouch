using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Services;

namespace BiliTouch.Helpers;

/// <summary>
/// 直播画面弹幕渲染器。
///
/// 性能优化架构：
///   1. 与 LiveRoomWindow 的 vmem 合成宿主协同工作：弹幕层与视频画布同处主窗口硬件交换链，
///      彻底消除旧版 VideoView 分层窗口（AllowsTransparency / UpdateLayeredWindow）每帧整窗 GPU→CPU 回读。
///   2. 稳态常驻对象池：弹幕飞出左边界时仅设 Visibility.Collapsed 归池，不从 Canvas.Children 移除，
///      避免反复触发布局树重排与销毁重建 BitmapCache GPU 纹理。
///   3. 帧间平滑节流：单次批量推送（如 40 条）按每渲染帧最多发射 3 条拆散入场，消除单帧建树尖峰与垂直弹幕墙。
///   4. 零布局宽度估算与满泳道快速短路：所有泳道占满时零分配直接丢弃（直播弹幕以实时感为先）。
/// </summary>
public sealed class LiveOverlayRenderer : IDisposable
{
    private const int MaxItems = 48;
    private const int LaneCount = 12;
    private const int MaxBurstQueue = 32;
    private const int MaxEmitPerFrame = 3;

    private static readonly Brush WhiteBrush = CreateFrozenBrush(0xFFFFFF);
    private static readonly BitmapCache SharedBitmapCache = CreateFrozenBitmapCache();
    private static readonly double InvFrequency = 1.0 / Stopwatch.Frequency;

    private readonly Canvas _canvas;
    private readonly List<(FrameworkElement text, TranslateTransform move, long start, double width)> _items = new(MaxItems);
    private readonly Queue<(string message, uint color)> _pending = new(24);
    private readonly Queue<(string message, uint color)> _burst = new(MaxBurstQueue);
    private readonly Stack<TextBlock> _plainPool = new(MaxItems);
    private readonly Stack<OutlinedDanmakuText> _outlinedPool = new(MaxItems);
    private readonly Dictionary<uint, Brush> _brushes = new();
    private readonly long[] _lanes = new long[LaneCount];
    private readonly DispatcherTimer _watchdog;
    private long _lastRenderTicks;
    private bool _clockAttached, _disposed, _outline;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private int _emittedInFrame;
    private double _fontSize = 20, _area = .35, _opacity = .8, _speed = 150;

    private static Brush CreateFrozenBrush(uint color)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
        b.Freeze();
        return b;
    }

    private static BitmapCache CreateFrozenBitmapCache()
    {
        var cache = new BitmapCache { EnableClearType = false, RenderAtScale = 1.0, SnapsToDevicePixels = true };
        cache.Freeze();
        return cache;
    }

    public LiveOverlayRenderer(Canvas canvas)
    {
        _canvas = canvas;
        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _watchdog.Tick += (_, _) => WatchdogTick();
        canvas.Unloaded += (_, _) => StopClock();
        canvas.Loaded += (_, _) => { if (_items.Count > 0 || _burst.Count > 0) EnsureClock(); };
    }

    /// <summary>应用弹幕外观(字号/区域/不透明度/速度档位/描边)。会清空当前画面 —— 与旧行为一致。</summary>
    public void Configure(double fontSize, double area, double opacity, int speedLevel, bool outline = false)
    {
        Clear();
        _fontSize = Math.Clamp(fontSize, 12, 60);
        _area = Math.Clamp(area, .25, 1);
        _opacity = Math.Clamp(opacity, 0, 1);
        _outline = outline;
        _speed = 1200 / DanmakuMotion.ScrollSeconds(speedLevel);
    }

    /// <summary>
    /// 塞一条弹幕。画布还没尺寸时先进预加载队列；画布就绪后在单帧预算内立即上屏，超预算则平滑排入后续帧。
    /// </summary>
    public void Add(string message, uint color)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message)) return;
        if (_canvas.ActualWidth < 1 || _canvas.ActualHeight < 1)
        {
            if (_pending.Count >= 24) _pending.Dequeue();
            _pending.Enqueue((message, color));
            EnsureSizeHook();
            return;
        }

        if (_emittedInFrame < MaxEmitPerFrame)
        {
            if (Emit(message, color))
                _emittedInFrame++;
            return;
        }

        if (_items.Count >= MaxItems) return;
        if (_burst.Count >= MaxBurstQueue) _burst.Dequeue();
        _burst.Enqueue((message, color));
        EnsureClock();
    }

    private bool _sizeHooked;
    private void EnsureSizeHook()
    {
        if (_sizeHooked || _disposed) return;
        _sizeHooked = true;
        _canvas.SizeChanged += OnCanvasSizeChanged;
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || _pending.Count == 0) return;
        if (_canvas.ActualWidth < 1 || _canvas.ActualHeight < 1) return;
        while (_pending.Count > 0)
        {
            var (message, color) = _pending.Dequeue();
            Add(message, color);
        }
    }

    private bool Emit(string message, uint color)
    {
        if (_items.Count >= MaxItems) return false;
        var canvasW = _canvas.ActualWidth;
        var canvasH = _canvas.ActualHeight;
        if (canvasW < 1 || canvasH < 1) return false;

        var now = Stopwatch.GetTimestamp();
        var lineHeight = _fontSize * 1.5;
        var lanes = Math.Clamp((int)(canvasH * _area / lineHeight), 1, LaneCount);

        var lane = -1;
        for (int i = 0; i < lanes; i++)
        {
            if (_lanes[i] <= now) { lane = i; break; }
        }
        if (lane < 0) return false;

        var msg = message.Length > 500 ? message[..500] : message;
        var brush = ResolveBrush(color);
        var width = EstimateWidth(msg, _fontSize, _outline);
        var cacheMode = Svc.Settings.HardwareAccelerationEnabled ? SharedBitmapCache : null;

        FrameworkElement text;
        TranslateTransform move;
        if (_outline)
        {
            if (_outlinedPool.TryPop(out var reusedOutlined))
            {
                reusedOutlined.SetContent(msg, brush, _fontSize);
                if (Math.Abs(reusedOutlined.Opacity - _opacity) > 0.004) reusedOutlined.Opacity = _opacity;
                if (reusedOutlined.Visibility != Visibility.Visible) reusedOutlined.Visibility = Visibility.Visible;
                text = reusedOutlined;
            }
            else
            {
                text = new OutlinedDanmakuText(msg, brush, _fontSize) { Opacity = _opacity };
            }
        }
        else
        {
            if (_plainPool.TryPop(out var reusedPlain))
            {
                if (!string.Equals(reusedPlain.Text, msg, StringComparison.Ordinal)) reusedPlain.Text = msg;
                if (Math.Abs(reusedPlain.FontSize - _fontSize) > 0.01) reusedPlain.FontSize = _fontSize;
                if (!ReferenceEquals(reusedPlain.Foreground, brush)) reusedPlain.Foreground = brush;
                if (Math.Abs(reusedPlain.Opacity - _opacity) > 0.004) reusedPlain.Opacity = _opacity;
                if (reusedPlain.Visibility != Visibility.Visible) reusedPlain.Visibility = Visibility.Visible;
                text = reusedPlain;
            }
            else
            {
                text = new TextBlock
                {
                    Text = msg,
                    FontSize = _fontSize,
                    Opacity = _opacity,
                    Foreground = brush,
                    IsHitTestVisible = false
                };
            }
        }

        if (!ReferenceEquals(text.CacheMode, cacheMode))
            text.CacheMode = cacheMode;

        if (text.RenderTransform is TranslateTransform existingMove && !existingMove.IsFrozen)
        {
            move = existingMove;
            move.X = canvasW;
            move.Y = lane * lineHeight + 12;
        }
        else
        {
            move = new TranslateTransform(canvasW, lane * lineHeight + 12);
            text.RenderTransform = move;
        }

        _lanes[lane] = now + (long)((width + 50) / _speed * Stopwatch.Frequency);

        if (VisualTreeHelper.GetParent(text) == null)
            _canvas.Children.Add(text);
        _items.Add((text, move, now, width));
        EnsureClock();
        return true;
    }

    private Brush ResolveBrush(uint color)
    {
        if (color == 0xFFFFFF || color == 0) return WhiteBrush;
        if (_brushes.TryGetValue(color, out var cached)) return cached;
        var brush = CreateFrozenBrush(color);
        _brushes[color] = brush;
        return brush;
    }

    private static double EstimateWidth(string text, double fontSize, bool outline)
    {
        double units = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            units += c <= 0x7F ? 0.58 : 1.0;
        }
        return Math.Max(1.0, Math.Ceiling(units * fontSize + (outline ? 4.0 : 0.0)));
    }

    private void RecycleElement(FrameworkElement element, bool detachFromCanvas)
    {
        if (detachFromCanvas)
        {
            _canvas.Children.Remove(element);
        }
        else
        {
            // 稳态回收不移出 Canvas.Children，避免反复触发布局树重排与释放 BitmapCache GPU 纹理
            element.Visibility = Visibility.Collapsed;
        }

        if (element is OutlinedDanmakuText ot)
        {
            if (_outlinedPool.Count < MaxItems) _outlinedPool.Push(ot);
            else if (!detachFromCanvas) _canvas.Children.Remove(ot);
        }
        else if (element is TextBlock tb)
        {
            if (_plainPool.Count < MaxItems) _plainPool.Push(tb);
            else if (!detachFromCanvas) _canvas.Children.Remove(tb);
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (_lastRenderingTime == frame.RenderingTime) return;
            _lastRenderingTime = frame.RenderingTime;
        }
        _lastRenderTicks = Stopwatch.GetTimestamp();
        if (_disposed) { StopClock(); return; }
        _emittedInFrame = 0;

        var canvasW = _canvas.ActualWidth;
        var canvasH = _canvas.ActualHeight;
        if (canvasW >= 1 && canvasH >= 1)
        {
            while (_pending.Count > 0 && _emittedInFrame < MaxEmitPerFrame)
            {
                var (message, color) = _pending.Dequeue();
                if (Emit(message, color)) _emittedInFrame++;
            }
            while (_burst.Count > 0 && _emittedInFrame < MaxEmitPerFrame)
            {
                var (message, color) = _burst.Dequeue();
                if (Emit(message, color)) _emittedInFrame++;
                else break; // 泳道全满时留到下一帧或自然淘汰
            }
        }

        var now = Stopwatch.GetTimestamp();
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            var w = item.text.DesiredSize.Width > 1 ? item.text.DesiredSize.Width : item.width;
            var elapsedSec = (now - item.start) * InvFrequency;
            var x = canvasW - elapsedSec * _speed;
            if (x < -w - 8)
            {
                RecycleElement(item.text, detachFromCanvas: false);
                _items.RemoveAt(i);
            }
            else
            {
                item.move.X = x;
            }
        }

        if (_items.Count == 0 && _burst.Count == 0 && (_pending.Count == 0 || canvasW < 1 || canvasH < 1))
            StopClock();
    }

    private void WatchdogTick()
    {
        if (!_clockAttached || _disposed) return;
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastRenderTicks, now).TotalMilliseconds < 200) return;
        Tick(this, EventArgs.Empty);
    }

    private void EnsureClock()
    {
        if (_disposed || _clockAttached) return;
        _clockAttached = true;
        _lastRenderingTime = TimeSpan.MinValue;
        _lastRenderTicks = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += Tick;
        _watchdog.Start();
    }

    private void StopClock()
    {
        if (!_clockAttached) return;
        _clockAttached = false;
        CompositionTarget.Rendering -= Tick;
        _watchdog.Stop();
    }

    /// <summary>清空画面与所有状态(换房间/改设置/关闭时调用)。</summary>
    public void Clear()
    {
        StopClock();
        for (var i = _items.Count - 1; i >= 0; i--)
            RecycleElement(_items[i].text, detachFromCanvas: true);
        _items.Clear();
        _pending.Clear();
        _burst.Clear();
        _canvas.Children.Clear();
        Array.Clear(_lanes);
        _emittedInFrame = 0;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_sizeHooked)
        {
            _sizeHooked = false;
            _canvas.SizeChanged -= OnCanvasSizeChanged;
        }
        Clear();
    }
}
