using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Services;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

internal static class VmemPerformanceProbe
{
    internal static void Run(Action<bool, string> check, string[] args)
    {
        var path = args.First(a => a.StartsWith("--clip="))[7..];
        var hardware = args.Contains("--hardware");
        var view = new VmemVideoView();
        var window = new Window { Width = 1280, Height = 820, Content = view, ShowActivated = false };
        var core = VlcCore.GetVmem();
        var hardwareLogs = new List<string>();
        core.Log += (_, e) => {
            if (e.Message.Contains("hardware decoding", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("Using D3D", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("using hw decoder", StringComparison.OrdinalIgnoreCase))
                lock (hardwareLogs) hardwareLogs.Add(e.Message);
        };
        var player = new MediaPlayer(core) { Volume = 0 };
        if (hardware) player.EnableHardwareDecoding = true;
        view.Attach(player);
        using var media = new Media(core, path, FromType.FromPath);
        try
        {
            window.Show();
            player.Play(media);
            Pump(1800);
            var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var delivered = view.DeliveredFrames;
            var presented = view.PresentedFrames;
            var watch = Stopwatch.StartNew();
            var gaps = new List<double>();
            var last = watch.Elapsed.TotalMilliseconds;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
            heartbeat.Tick += (_, _) => { var now = watch.Elapsed.TotalMilliseconds; gaps.Add(now - last); last = now; };
            heartbeat.Start();
            Pump(4000);
            heartbeat.Stop();
            var seconds = watch.Elapsed.TotalSeconds;
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds / seconds;
            var size = view.VideoSize;
            var stats = media.Statistics;
            var gpu = typeof(VmemVideoView).GetField("_gpu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view);
            Console.WriteLine($"PRESENTATION mode={view.PresentationMode} loaded={view.IsLoaded} visible={view.IsVisible} source={view.Source?.GetType().Name} pending={gpu?.GetType().GetProperty("Pending")?.GetValue(gpu)} front={(view.Source as System.Windows.Interop.D3DImage)?.IsFrontBufferAvailable}");
            Console.WriteLine($"GPU attempts={gpu?.GetType().GetProperty("Attempts")?.GetValue(gpu)} misses={gpu?.GetType().GetProperty("LockMisses")?.GetValue(gpu)} totalPresented={view.PresentedFrames} state={window.WindowState}");
            gaps.Sort();
            Console.WriteLine($"VMEMPERF hardwareRequested={hardware} size={size.Width}x{size.Height} " +
                $"delivered={(view.DeliveredFrames - delivered) / seconds:F1} uploads={(view.PresentedFrames - presented) / seconds:F1} " +
                $"cpuMsPerSec={cpuMs:F0} inputP95={gaps[(int)(gaps.Count * .95)]:F1} inputMax={gaps[^1]:F1} " +
                $"decoded={stats.DecodedVideo} displayed={stats.DisplayedPictures} lost={stats.LostPictures}");
            lock (hardwareLogs) foreach (var line in hardwareLogs.Distinct()) Console.WriteLine("DECODE " + line);
            check(view.PresentedFrames > 10 && view.Source != null, "composited video delivers and uploads changing frames");
        }
        finally
        {
            Task.Run(player.Stop).GetAwaiter().GetResult();
            player.Dispose(); view.Dispose(); window.Close();
        }
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
