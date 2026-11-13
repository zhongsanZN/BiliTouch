using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BiliTouch.Helpers;

/// <summary>
/// 直播画面弹幕渲染器(2026-11-05 重写)。
///
/// ★★★ 为什么重写 —— 用户报"视频弹幕怎么好像看不见了":
///   旧版靠 <c>CompositionTarget.Rendering</c> 驱动位移。那条链有两个致命前提:
///     ① 每一帧都要有人真的在渲染(窗口最小化/被挡住/只截图不呈现时, 这条事件会停);
///     ② 加弹幕那一刻画布已经有真实尺寸。
///   任一前提不成立, 文本就会**停在初始位置**(画布右缘之外), 用户看到的就是"弹幕没了"。
///   探针截图 live-room-video-overlay.png 里那几行"这是—"贴着右边缘不动, 正是这个症状。
///   ⇒ 0.1.6 一度改成 16ms DispatcherTimer 自己走时，但它受系统定时器粒度限制
///     实际 ~30ms 一跳，把整个浮层呈现帧率钉在 ~33fps（见 docs/FPS-REGRESSION-2026-11-09.md）。
///   ⇒ 现在: 主时钟仍是 CompositionTarget.Rendering 跟 DWM 合成走，另加一条 250ms
///     低频看门狗，只在合成真的停摆时补一次推进；位置由 Stopwatch 外推，停多久都不丢进度。
///     预加载队列继续承担"画布还没尺寸时不丢弹幕"。
///
/// ★ "弹幕要预加载, 权重要高"(用户原话)的落地:
///   · <see cref="Add"/> 在画布还没尺寸时**不丢弃**, 而是进 pending 队列, 等第一次
///     有尺寸的 Tick 立刻铺上去 —— 于是直播间一打开就能看到最近几条弹幕,
///     不必等 WebSocket 推一条新的;
///   · 房间一进来(进房历史)就把最近几条塞进队列, 与视频流并行, 不占起播路径。
///
/// ★ 仍保持"有界": 最多 48 条同屏、12 条泳道、超长文本截断。
/// </summary>
public sealed class LiveOverlayRenderer : IDisposable
{
    private const int MaxItems = 48;
    private const int LaneCount = 12;

    private readonly Canvas _canvas;
    private readonly List<(FrameworkElement text, TranslateTransform move, long start, double width)> _items = new();
    private readonly Queue<(string message, uint color)> _pending = new();
    private readonly long[] _lanes = new long[LaneCount];
    private readonly DispatcherTimer _watchdog;
    private long _lastRenderTicks;
    private bool _clockAttached, _disposed, _outline;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private double _fontSize = 20, _area = .35, _opacity = .8, _speed = 150;

    public LiveOverlayRenderer(Canvas canvas)
    {
        _canvas = canvas;
        // ★ 主时钟跟 DWM 合成节奏（CompositionTarget.Rendering），不用 16ms 定时器：
        //   后者受系统定时器粒度限制实际 ~30ms 一跳，会把浮层呈现帧率钉在 ~33fps
        //   （实测见 docs/FPS-REGRESSION-2026-11-09.md）。
        //   低频看门狗只在合成停摆（窗口被遮挡/最小化）时补一次，避免弹幕停在右缘外。
        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _watchdog.Tick += (_, _) => WatchdogTick();
        canvas.Unloaded += (_, _) => StopClock();
        canvas.Loaded += (_, _) => EnsureClock();
    }

    /// <summary>应用弹幕外观(字号/区域/不透明度/速度档位/描边)。会清空当前画面 —— 与旧行为一致。</summary>
    public void Configure(double fontSize, double area, double opacity, int speedLevel, bool outline = false)
    {
        Clear();
        _fontSize = Math.Clamp(fontSize, 12, 60);
        _area = Math.Clamp(area, .25, 1);
        _opacity = Math.Clamp(opacity, 0, 1);
        _outline = outline;
        // 速度档位 → 每秒像素。DanmakuMotion.ScrollSeconds 是"横穿一屏要几秒",
        // 这里按 1200px 视作一屏宽折算成速度, 与点播弹幕手感一致。
        _speed = 1200 / DanmakuMotion.ScrollSeconds(speedLevel);
    }

    /// <summary>
    /// 塞一条弹幕。画布还没尺寸时先进队列(预加载), 不会丢。
    /// </summary>
    public void Add(string message, uint color)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message)) return;
        if (_canvas.ActualWidth < 1 || _canvas.ActualHeight < 1)
        {
            // 预加载阶段: 画布还没量出尺寸。留最近 24 条, 多了没有意义(同屏也就十几条)。
            if (_pending.Count >= 24) _pending.Dequeue();
            _pending.Enqueue((message, color));
            EnsureClock();
            return;
        }
        Emit(message, color);
    }

    private void Emit(string message, uint color)
    {
        if (_items.Count >= MaxItems) return;
        var now = Stopwatch.GetTimestamp();
        var lineHeight = _fontSize * 1.5;
        var lanes = Math.Clamp((int)(_canvas.ActualHeight * _area / lineHeight), 1, LaneCount);

        // 找一条"已经让出"的泳道: 上一行整条尾巴完全进入画面才允许复用。
        var lane = Array.FindIndex(_lanes, 0, lanes, released => released <= now);
        if (lane < 0) return;   // 满屏了, 这条丢掉(直播弹幕以"实时感"为先, 不排队补播)

        var msg = message.Length > 500 ? message[..500] : message;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
        brush.Freeze();
        FrameworkElement text = _outline
            ? new OutlinedDanmakuText(msg, brush, _fontSize) { Opacity = _opacity }
            : new TextBlock
            {
                Text = msg,
                FontSize = _fontSize,
                Opacity = _opacity,
                Foreground = brush,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, ShadowDepth = 1, BlurRadius = 2, Opacity = .9 }
            };
        text.Measure(new Size(double.PositiveInfinity, lineHeight));
        var width = text.DesiredSize.Width;

        var move = new TranslateTransform(_canvas.ActualWidth, lane * lineHeight + 12);
        text.RenderTransform = move;
        // 长弹幕要走更久才让出泳道, 否则后一条会追尾。
        _lanes[lane] = now + (long)((width + 50) / _speed * Stopwatch.Frequency);

        _canvas.Children.Add(text);
        _items.Add((text, move, now, width));
        EnsureClock();
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
        // 先补预加载队列: 画布一有尺寸就把攒着的弹幕铺上去。
        if (_pending.Count > 0 && _canvas.ActualWidth >= 1 && _canvas.ActualHeight >= 1)
        {
            while (_pending.Count > 0) { var (message, color) = _pending.Dequeue(); Emit(message, color); }
        }

        var now = Stopwatch.GetTimestamp();
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            var x = _canvas.ActualWidth - Stopwatch.GetElapsedTime(item.start, now).TotalSeconds * _speed;
            if (x < -item.width - 8) { _canvas.Children.Remove(item.text); _items.RemoveAt(i); }
            else item.move.X = x;
        }

        // 没东西可动就停表: 直播挂机时不空转 UI 线程。
        if (_items.Count == 0 && _pending.Count == 0) StopClock();
    }

    private void WatchdogTick()
    {
        if (!_clockAttached || _disposed) return;
        // Rendering 正常跑时这里几乎零成本：距上次回调还不到 200ms 就直接返回。
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
        _items.Clear();
        _pending.Clear();
        _canvas.Children.Clear();
        Array.Clear(_lanes);
    }

    public void Dispose()
    {
        _disposed = true;
        Clear();
    }
}
