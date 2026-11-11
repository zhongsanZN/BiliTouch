using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.Views;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

internal static class VideoDecodeTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Settings(Action<bool, string> check)
    {
        var root = AppPaths.DataDir;
        var original = Svc.Settings.DecoderAdapterId;
        var profile = Path.Combine(root, "decoder-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        typeof(AppPaths).GetProperty(nameof(AppPaths.DataDir))!.SetValue(null, profile);
        try
        {
            foreach (var id in new[] { "auto", "software", "00000000:00ABCDEF" })
            {
                Svc.Settings.SetDecoderAdapter(id); Svc.Settings.SaveNow();
                var loaded = new SettingsStore(); loaded.Load();
                check(loaded.DecoderAdapterId == id, "decoder selection survives saving and loading: " + id);
            }
            Svc.Settings.SetDecoderAdapter("bad adapter id"); Svc.Settings.SaveNow();
            check(Svc.Settings.DecoderAdapterId == "auto", "invalid decoder identifiers fall back to automatic selection");
            File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"EnhancementAdapterId\":\"00000000:00abcdef\"}");
            var migrated = new SettingsStore(); migrated.Load();
            check(migrated.DecoderAdapterId == "00000000:00ABCDEF", "legacy explicit adapter preference migrates to decoder selection");
            File.WriteAllText(Path.Combine(profile, "settings.json"), "{}");
            var defaults = new SettingsStore(); defaults.Load();
            check(defaults.DecoderAdapterId == "auto", "profiles without a decoder selection retain automatic hardware decoding");
            var choices = VideoDecode.Choices;
            check(choices[0].Id == "auto" && choices[^1].Id == "software", "decoder choices retain automatic and compatible playback modes");
            check(choices.Select(c => c.Id).Distinct().Count() == choices.Count, "available decoder devices have unique identifiers");
        }
        finally
        {
            Svc.Settings.SetDecoderAdapter(original); Svc.Settings.SaveNow();
            typeof(AppPaths).GetProperty(nameof(AppPaths.DataDir))!.SetValue(null, root);
        }
    }

    internal static void Hardware(Action<bool, string> check, string[] args)
    {
        var clip = args.First(a => a.StartsWith("--clip="))[7..];
        var selection = args.FirstOrDefault(a => a.StartsWith("--adapter="))?[10..] ?? "auto";
        var choice = VideoDecode.Choices.First(c => c.Id == selection || c.Name.Contains(selection, StringComparison.OrdinalIgnoreCase));
        Svc.Settings.SetDecoderAdapter(choice.Id); Svc.Settings.SetPlayerVolume(0);
        var core = VlcCore.GetVmem();
        var hardwareMessages = new List<string>();
        core.Log += (_, e) =>
        {
            if (e.Message.Contains("Using D3D11VA", StringComparison.OrdinalIgnoreCase))
                lock (hardwareMessages) hardwareMessages.Add(e.Message);
        };
        var player = new PlayerWindow { Width = 1280, Height = 820, ShowActivated = false, Topmost = true };
        try
        {
            player.Show(); Pump(150);
            var mp = (MediaPlayer)typeof(PlayerWindow).GetField("_mp", Hidden)!.GetValue(player)!;
            var view = (VmemVideoView)player.FindName("VmemView");
            for (int iteration = 0; iteration < 4; iteration++)
            {
                var before = view.PresentedFrames;
                var watch = Stopwatch.StartNew();
                player.PlayLocalFile(clip);
                var synchronousMs = watch.Elapsed.TotalMilliseconds;
                Until(() => mp.IsPlaying && view.PresentedFrames >= before + 12);
                check(view.Source != null, $"selected decoder repeatedly starts live composited video: {iteration}");
                Console.WriteLine($"DECODER iteration={iteration} synchronousReplaceMs={synchronousMs:F1} mode={view.PresentationMode} source={view.VideoSize}");
            }
            var first = Fingerprint(view.CaptureFrame()); Pump(220);
            check(first != Fingerprint(view.CaptureFrame()), "GPU frame pixels change during real playback");
            if (choice.Id is not "auto" and not "software")
            {
                lock (hardwareMessages)
                    check(hardwareMessages.Count >= 4 && hardwareMessages.All(m => m.Contains(VideoDecode.SelectedDeviceName)),
                        "each native decoder uses the explicitly selected physical GPU");
                check(VideoDecode.RedirectedDeviceCreations >= 4, "native decoder device ownership survives repeated media changes");
            }
            else if (choice.Id == "software")
                lock (hardwareMessages) check(hardwareMessages.Count == 0, "compatible decode does not silently request the GPU decoder");
            player.WindowState = WindowState.Minimized; Pump(180);
            var presented = view.PresentedFrames; var time = mp.Time;
            Pump(650);
            check(view.PresentedFrames <= presented + 1 && mp.Time > time,
                "minimized playback keeps its clock running without uploading video frames");
            player.WindowState = WindowState.Normal; Pump(650);
            check(view.PresentedFrames > presented + 10, "restoring the player resumes live frame presentation");
            mp.SetPause(true); Pump(350);
            player.Width = 1000; Pump(400);
            check(view.CaptureFrame() is { PixelWidth: > 0 }, "resizing paused playback preserves its last frame");
            lock (hardwareMessages) foreach (var line in hardwareMessages) Console.WriteLine("DECODE " + line);
        }
        finally { player.Close(); Pump(1500); }
    }

    private static long Fingerprint(BitmapSource? frame)
    {
        if (frame == null) return 0;
        var height = Math.Min(100, frame.PixelHeight);
        var pixels = new byte[frame.PixelWidth * height * 4];
        frame.CopyPixels(new Int32Rect(0, 0, frame.PixelWidth, height), pixels, frame.PixelWidth * 4, 0);
        long hash = 17;
        for (int i = 0; i < pixels.Length; i += 131) hash = unchecked(hash * 31 + pixels[i]);
        return hash;
    }
    private static void Until(Func<bool> done)
    {
        var deadline = Stopwatch.StartNew();
        while (!done() && deadline.ElapsedMilliseconds < 10000) Pump(15);
        if (!done()) throw new TimeoutException("Selected decoder did not start and present frames");
    }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
