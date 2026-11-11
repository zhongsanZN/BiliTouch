using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Helpers;

// Optional local diagnostic. Only hashes of a strip inside the fixture window leave the process.
//
// Two readings of the same gesture: PRESENT (what the screen actually got) and AUDIT (what the UI
// thread spent getting there). A low presented rate is only an argument once you know which side
// of the pipeline was holding the frame.
//
// ★★★ Input is injected from the render callback, not from a DispatcherTimer. A timer fires at the
//   system clock resolution (~15.6 ms), which used to cap every measurement at ~45 updates/s and
//   made the fixture look like the app. One packet per rendered frame means the only thing left to
//   limit the update rate is the app.
internal static class PresentedScrollProbe
{
    internal static bool Enabled => Environment.GetCommandLineArgs().Contains("--presented-scroll");

    private const double StartTime = .6, EndTime = 3.6, MeasureSeconds = EndTime - StartTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low, High; }

    [DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out FileTime creation, out FileTime exit,
                                              out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    /// <summary>CPU time used by the calling thread. The probe only ever runs on the UI thread.</summary>
    private static double UiCpuMs()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user)) return double.NaN;
        double Ms(FileTime t) => (((long)t.High << 32) | t.Low) / 10_000.0;      // 100ns ticks
        return Ms(kernel) + Ms(user);
    }

    internal static void Run(ScrollViewer viewer, string label)
    {
        if (!Enabled) return;
        var window = Window.GetWindow(viewer)!;
        var wasTopmost = window.Topmost;
        var before = viewer.VerticalOffset;
        var low = Math.Min(700, viewer.ScrollableHeight * .15);
        var high = Math.Min(low + 2700, viewer.ScrollableHeight - 30);
        if (high <= low + 100) throw new InvalidOperationException("Insufficient fixture scroll range: " + label);
        window.Topmost = true;
        TrackpadScroll.Cancel(viewer);
        viewer.ScrollToVerticalOffset(low);
        window.UpdateLayout();

        var point = viewer.PointToScreen(new Point(viewer.ActualWidth * .28, viewer.ActualHeight * .40));
        var args = $"-hide_banner -loglevel error -f lavfi -i ddagrab=framerate=120:offset_x={(int)point.X}:offset_y={(int)point.Y}:video_size=240x192:draw_mouse=0,hwdownload,format=bgra -t 4 -threads 1 -fps_mode passthrough -f framemd5 -flush_packets 1 pipe:1";
        using var capture = new Process
        {
            StartInfo = new ProcessStartInfo("ffmpeg", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };

        var clock = new Stopwatch();
        var frame = new DispatcherFrame();
        var marks = new List<(double Time, string Hash)>();
        var tick = 1.0 / 120;
        var state = new RunState(viewer, clock, low, high);

        capture.OutputDataReceived += (_, e) =>
        {
            var line = e.Data;
            if (line == null) return;
            if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
            {
                var ratio = line.Split(':')[1].Trim().Split('/');
                tick = double.Parse(ratio[0], CultureInfo.InvariantCulture) / double.Parse(ratio[1], CultureInfo.InvariantCulture);
            }
            if (!line.StartsWith("0,", StringComparison.Ordinal)) return;
            var fields = line.Split(',');
            marks.Add((long.Parse(fields[2], CultureInfo.InvariantCulture) * tick, fields[^1].Trim()));
            if (marks.Count == 1) viewer.Dispatcher.BeginInvoke(() =>
            {
                timeBeginPeriod(1);
                clock.Restart();
                state.Start();
                CompositionTarget.Rendering += state.OnFrame;
            });
        };
        capture.EnableRaisingEvents = true;
        capture.Exited += (_, _) => viewer.Dispatcher.BeginInvoke(() => frame.Continue = false);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timeout.Tick += (_, _) => frame.Continue = false;
        try
        {
            capture.Start();
            capture.BeginOutputReadLine();
            timeout.Start();
            Dispatcher.PushFrame(frame);
            if (!capture.HasExited) capture.Kill();
            capture.WaitForExit();
            if (capture.ExitCode != 0 || marks.Count < 25) throw new Exception("Desktop capture failed " + capture.StandardError.ReadToEnd());

            var start = marks[0].Time + StartTime;
            var stop = marks[0].Time + EndTime;
            var changes = new List<double>();
            for (int i = 1; i < marks.Count; i++)
                if (marks[i].Time >= start && marks[i].Time <= stop && marks[i].Hash != marks[i - 1].Hash)
                    changes.Add(marks[i].Time);
            var gaps = changes.Zip(changes.Skip(1), (a, b) => (b - a) * 1000).Order().ToArray();
            double Percentile(double p) => gaps.Length == 0 ? double.NaN : gaps[(int)(p * (gaps.Length - 1))];
            Console.WriteLine($"PRESENT {label} changed/s={changes.Count / MeasureSeconds:F1} " +
                              $"p50={Percentile(.5):F1}ms p95={Percentile(.95):F1}ms max={(gaps.Length > 0 ? gaps[^1] : double.NaN):F1}ms " +
                              $"captured={marks.Count}");
            state.Report(label, viewer, changes.Count / MeasureSeconds);
        }
        finally
        {
            CompositionTarget.Rendering -= state.OnFrame;
            state.Stop();
            timeEndPeriod(1);
            timeout.Stop();
            TrackpadScroll.Cancel(viewer);
            viewer.ScrollToVerticalOffset(before);
            window.Topmost = wasTopmost;
        }
    }

    /// <summary>Drives the gesture and keeps the UI-side counters for the same window.</summary>
    private sealed class RunState(ScrollViewer viewer, Stopwatch clock, double low, double high)
    {
        // A steady 1.6 s down, 1.6 s up, inside the measured 0.6 s ~ 3.6 s of the capture.
        private const double HalfCycleSeconds = 1.6;

        private readonly List<double> _intervals = [];
        private TimeSpan _last = TimeSpan.MinValue;
        private double _lastClockSeconds = -1;
        private Counters _begin, _end;
        private int _packets, _offsets;

        /// <summary>
        /// 每帧回调。★ 必须是 <c>internal</c>: 外层 Run 方法用 lambda 把它挂到
        /// CompositionTarget.Rendering 上, private 会让那个 lambda 编不过(CS0122)。
        /// </summary>
        internal void OnFrame(object? sender, EventArgs e)
        {
            var time = ((RenderingEventArgs)e).RenderingTime;
            if (time == _last) return;
            var previous = _last;
            _last = time;
            if (!_beginTaken) { _begin = Snapshot(); _beginTaken = true; }
            var seconds = clock.Elapsed.TotalSeconds;
            if (seconds >= StartTime)
            {
                if (seconds <= EndTime && _lastClockSeconds >= StartTime)
                    _intervals.Add((time - previous).TotalMilliseconds);
                Drive(seconds);
            }
            if (seconds >= EndTime && !_endTaken) { _end = Snapshot(); _endTaken = true; }
            _lastClockSeconds = seconds;
        }

        private bool _beginTaken, _endTaken;

        /// <summary>
        /// 每帧推进手势。★ 这里必须用 <c>PrecisionPixelsPerDeltaUnit</c>——
        /// 它就是 TrackpadScroll 里精密包的真实标定值; 曾经写成 <c>PixelsPerDeltaUnit</c>
        /// 是笔误(那个名字不存在), 会让探针根本编不过。
        /// ★ 2026-11-08: 该常数 0.15 → 0.45。这里除以它是**故意的**: 探针要模拟的是
        ///   "手指以固定像素速度移动", 所以 delta 的**单位数**要跟着常数反比缩放,
        ///   保证每帧落地的**像素距离**与标定值无关 —— 改常数不该改变探针的手势强度,
        ///   否则前后两组 PRESENT 数字就不能对比了。
        /// </summary>
        private void Drive(double seconds)
        {
            if (seconds > EndTime) return;
            var dt = Math.Min(0.05, seconds - Math.Max(_lastDrive, StartTime));
            _lastDrive = seconds;
            if (dt <= 0) return;
            // Constant finger speed, so the distance per frame only depends on how long the frame was.
            var speed = Math.Min(900, (high - low) / HalfCycleSeconds);
            var forward = seconds % (2 * HalfCycleSeconds) < HalfCycleSeconds;
            var delta = (int)Math.Round(speed * dt / TrackpadScroll.PrecisionPixelsPerDeltaUnit);
            if (delta == 0) return;
            _packets++;
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, forward ? -delta : delta)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer });
        }

        private double _lastDrive = -1;

        private ScrollChangedEventHandler? _watch;
        internal void Start()
        {
            _watch = (_, e) =>
            {
                if (e.VerticalChange != 0 && clock.Elapsed.TotalSeconds is >= StartTime and <= EndTime)
                    _offsets++;
            };
            viewer.ScrollChanged += _watch;
        }
        internal void Stop()
        {
            if (_watch != null) viewer.ScrollChanged -= _watch;
            _watch = null;
        }

        private Counters Snapshot() => new(
            UiCpuMs(), clock.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread(),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            TrackpadScroll.OffsetWrites, viewer.VerticalOffset,
            Find<VirtualizingCardWallPanel>(viewer).FirstOrDefault()?.MeasurePasses ?? -1,
            Find<VirtualizingCardWallPanel>(viewer).FirstOrDefault()?.RealizedCount ?? -1,
            ViewportImageQueue.Started);

        internal void Report(string label, ScrollViewer ignored, double presented)
        {
            if (!_endTaken) { Console.WriteLine($"AUDIT {label} window not reached"); return; }
            var span = _end.WallMs - _begin.WallMs;
            if (span <= 0) { Console.WriteLine($"AUDIT {label} window not captured"); return; }
            _intervals.Sort();
            double Percentile(double p) => _intervals.Count == 0 ? double.NaN
                : _intervals[(int)(p * (_intervals.Count - 1))];
            var writes = _end.OffsetWrites - _begin.OffsetWrites;
            Console.WriteLine(
                $"AUDIT {label} frames/s={(_intervals.Count + 1) / (span / 1000):F1} median={Percentile(.5):F1}ms " +
                $"p95={Percentile(.95):F1}ms max={(_intervals.Count > 0 ? _intervals[^1] : double.NaN):F1}ms " +
                $"ui-busy={(_end.CpuMs - _begin.CpuMs) / span * 100:F0}% presented={presented:F1}/s " +
                $"writes/s={writes / (span / 1000):F1} packets={_packets} offsets={_offsets} " +
                $"measures={_end.MeasurePasses - _begin.MeasurePasses} realized={_end.Realized} " +
                $"images={_end.ImagesStarted - _begin.ImagesStarted} " +
                $"gc={_end.Gen0 - _begin.Gen0}/{_end.Gen1 - _begin.Gen1}/{_end.Gen2 - _begin.Gen2} " +
                $"alloc={(_end.Allocated - _begin.Allocated) / 1048576.0:F1}MiB");
        }
    }

    private readonly record struct Counters(double CpuMs, double WallMs, long Allocated,
                                            int Gen0, int Gen1, int Gen2, long OffsetWrites, double Offset,
                                            int MeasurePasses, int Realized, int ImagesStarted);

    private static List<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is T match) found.Add(match);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                stack.Push(VisualTreeHelper.GetChild(current, i));
        }
        return found;
    }
}
