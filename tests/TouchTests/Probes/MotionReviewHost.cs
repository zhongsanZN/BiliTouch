using System.IO;
using System.Text.Json;
using System.Windows;
using BiliTouch;
using BiliTouch.Services;
using BiliTouch.Views.Pages;
using System.Collections.Concurrent;
using System.Windows.Media;
using BiliTouch.Helpers;
using BiliTouch.Views;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class MotionReviewHost
{
    internal static void Configure(string artifacts)
    {
        var root = Path.Combine(artifacts, "motion-review-profile");
        Directory.CreateDirectory(root);
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliTouch", "settings.json");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FontId", "ThemeMode", "SkinId", "SkinEffects", "SkinUseAccent", "UiScalePercent",
            "UseVmemRenderer", "UseWebPlayerLayout", "AcrylicBackground", "AcrylicMaterial",
            "AcrylicOpacityLight", "AcrylicOpacityDark", "FeedWheelScalePercent"
        };
        var values = File.Exists(source)
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(source))!
                .Where(p => allowed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value)
            : new Dictionary<string, JsonElement>();
        // This profile carries visual preferences only, with its own account/settings storage.
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(values));
        typeof(AppPaths).GetProperty(nameof(AppPaths.DataDir))!.SetValue(null, root);
        typeof(AppPaths).GetProperty(nameof(AppPaths.CacheRoot))!.SetValue(null, root);
        Svc.Settings.Load(); Svc.Session.Load(); Svc.Theme.Initialize();
        Svc.Settings.CloseToTray = false;
    }

    internal static void Run()
    {
        var inputLog = Path.Combine(AppPaths.DataDir, "home-input.log");
        var queue = new ConcurrentQueue<string>();
        var stop = new CancellationTokenSource();
        string inputLabel = "等待操作";
        long lastInputAt = 0;
        void Record(string entry)
        {
            queue.Enqueue(entry);
            lastInputAt = Environment.TickCount64;
            if (entry.Contains("ptp move")) inputLabel = "触控板接触：按住中";
            else if (entry.Contains("ptp up")) inputLabel = "触控板接触：已抬手";
            else if (entry.Contains("wheel progress")) inputLabel = "系统横向滚动路径";
            else if (entry.Contains("stylus.move") || entry.Contains("touch.move")) inputLabel = "触屏接触";
        }
        HomePage.InputTrace = Record;
        PrecisionTouchpadInput.InputTrace = Record;
        FluentWindow.NativeInputTrace = Record;
        VirtualizingCardWallPanel.MeasureTrace = Record;
        BiliTouch.ViewModels.HomeViewModel.PreloadTrace = Record;
        TimeSpan? previousFrame = null;
        EventHandler onFrame = (_, e) =>
        {
            if (e is not RenderingEventArgs frame || frame.RenderingTime == previousFrame) return;
            if (Environment.TickCount64 - lastInputAt < 1000 && previousFrame is { } previous && (frame.RenderingTime - previous).TotalMilliseconds > 33)
                queue.Enqueue($"{Environment.TickCount64} frame.gap ms={(frame.RenderingTime - previous).TotalMilliseconds:F2}");
            previousFrame = frame.RenderingTime;
        };
        CompositionTarget.Rendering += onFrame;
        var writer = Task.Run(async () =>
        {
            using var output = new StreamWriter(inputLog, append: false);
            while (!stop.IsCancellationRequested || !queue.IsEmpty)
            {
                while (queue.TryDequeue(out var entry)) await output.WriteLineAsync(entry);
                await output.FlushAsync();
                if (!stop.IsCancellationRequested) await Task.Delay(200);
            }
        });
        Svc.Settings.SetPlayerVolume(0);
        var window = new MainWindow { Title = "BiliTouch · 本地修复预览（未发布）", Width = 1320, Height = 900 };
        // The custom application chrome does not render Window.Title. Put the
        // preview identity in the content itself, so it is visibly distinguishable
        // from the concurrently running installed copy.
        var content = (UIElement)window.Content;
        window.Content = null;
        var root = new DockPanel();
        var status = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        var title = new TextBlock { Text = "触控板修复预览 · 未发布 · 一次接触最多相邻一页", Foreground = Brushes.White, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        var bannerLayout = new DockPanel(); DockPanel.SetDock(status, Dock.Right);
        bannerLayout.Children.Add(status); bannerLayout.Children.Add(title);
        var banner = new Border { Height = 36, Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x62, 0x81)), Child = bannerLayout };
        DockPanel.SetDock(banner, Dock.Top); root.Children.Add(banner); root.Children.Add(content); window.Content = root;
        var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        statusTimer.Tick += (_, _) => status.Text = inputLabel;
        statusTimer.Start();
        Application.Current.MainWindow = window;
        window.Closed += (_, _) => Application.Current.Shutdown();
        Application.Current.Run(window);
        HomePage.InputTrace = null;
        PrecisionTouchpadInput.InputTrace = null;
        FluentWindow.NativeInputTrace = null;
        VirtualizingCardWallPanel.MeasureTrace = null;
        BiliTouch.ViewModels.HomeViewModel.PreloadTrace = null;
        statusTimer.Stop();
        CompositionTarget.Rendering -= onFrame;
        stop.Cancel(); writer.GetAwaiter().GetResult();
    }
}
