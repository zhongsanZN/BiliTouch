using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Pages;

internal static class PortraitLayoutTests
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static T Element<T>(PlayerWindow p, string name) => (T)p.FindName(name);
    private static Rect Bounds(FrameworkElement e, Visual ancestor) => e.TransformToAncestor(ancestor).TransformBounds(new Rect(e.RenderSize));

    public static void Run(Action<bool, string> check, string artifacts)
    {
        RunDimensionParsingTests(check);
        RunSettingsToggleTests(check, artifacts);
        RunPlayerPortraitLayoutTests(check, artifacts);
    }

    private static void RunDimensionParsingTests(Action<bool, string> check)
    {
        using var normalPortrait = JsonDocument.Parse("""{"dimension":{"width":1080,"height":1920,"rotate":0}}""");
        var d1 = ApiClient.ParseDimension(normalPortrait.RootElement);
        check(d1 == (1080, 1920), $"ParseDimension keeps unrotated portrait 1080x1920 (got {d1.Width}x{d1.Height})");

        using var rotatedPortrait = JsonDocument.Parse("""{"dimension":{"width":1920,"height":1080,"rotate":1}}""");
        var d2 = ApiClient.ParseDimension(rotatedPortrait.RootElement);
        check(d2 == (1080, 1920), $"ParseDimension swaps width/height when rotate=1 (got {d2.Width}x{d2.Height})");

        using var rotated270 = JsonDocument.Parse("""{"dimension":{"width":1920,"height":1080,"rotate":270}}""");
        var d3 = ApiClient.ParseDimension(rotated270.RootElement);
        check(d3 == (1080, 1920), $"ParseDimension swaps width/height when rotate=270 (got {d3.Width}x{d3.Height})");

        using var landscape = JsonDocument.Parse("""{"dimension":{"width":1920,"height":1080,"rotate":0}}""");
        var d4 = ApiClient.ParseDimension(landscape.RootElement);
        check(d4 == (1920, 1080), $"ParseDimension keeps landscape 1920x1080 (got {d4.Width}x{d4.Height})");

        using var missing = JsonDocument.Parse("""{"title":"no-dim"}""");
        var d5 = ApiClient.ParseDimension(missing.RootElement);
        check(d5 == (0, 0), "ParseDimension returns 0x0 when dimension is absent");
    }

    private static void RunSettingsToggleTests(Action<bool, string> check, string artifacts)
    {
        var oldPortrait = Svc.Settings.PortraitVideoOptimization;
        var bindingErrors = new List<string>();
        var listener = new BindingTraceCollector(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        Window? win = null;
        try
        {
            Svc.Settings.SetPortraitVideoOptimization(false);
            var vm = new SettingsViewModel();
            var page = new SettingsPage { DataContext = vm };
            win = new Window { Width = 1000, Height = 820, Content = page, ShowActivated = false };
            win.Show();
            Pump(250);

            var toggle = FindVisualChildren<CheckBox>(page)
                .FirstOrDefault(cb => System.Windows.Automation.AutomationProperties.GetName(cb) == "竖屏优化");
            check(toggle != null && toggle.IsVisible, "SettingsPage renders visible 竖屏优化 toggle under 视频详情");
            check(toggle!.IsChecked == false && !Svc.Settings.PortraitVideoOptimization, "竖屏优化 starts unchecked when disabled");

            typeof(ButtonBase).GetMethod("OnClick", Hidden)!.Invoke(toggle, null);
            Pump(80);
            check(toggle.IsChecked == true && Svc.Settings.PortraitVideoOptimization && vm.PortraitVideoOptimization,
                "clicking 竖屏优化 toggle enables SettingsStore.PortraitVideoOptimization");
            toggle.BringIntoView();
            Pump(120);
            check(SaveAndCheckNonEmpty(page, Path.Combine(artifacts, "settings-portrait-toggle.png")),
                "SettingsPage 竖屏优化 card renders non-empty pixels");

            Svc.Settings.SaveNow();
            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.DataDir, "settings.json"))))
                check(doc.RootElement.GetProperty("PortraitVideoOptimization").GetBoolean(),
                    "PortraitVideoOptimization is persisted into settings.json");

            typeof(ButtonBase).GetMethod("OnClick", Hidden)!.Invoke(toggle, null);
            Pump(80);
            check(toggle.IsChecked == false && !Svc.Settings.PortraitVideoOptimization,
                "clicking 竖屏优化 toggle again disables it cleanly");
            check(bindingErrors.Count == 0, "SettingsPage 竖屏优化 binding produces zero WPF binding errors: " + string.Join("; ", bindingErrors));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            win?.Close();
            Svc.Settings.SetPortraitVideoOptimization(oldPortrait);
        }
    }

    private static void RunPlayerPortraitLayoutTests(Action<bool, string> check, string artifacts)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var oldWeb = Svc.Settings.UseWebPlayerLayout;
        var oldPortrait = Svc.Settings.PortraitVideoOptimization;
        var oldScale = Svc.Settings.UiScalePercent;
        PlayerWindow? player = null;
        try
        {
            Svc.Settings.SetWebPlayerLayout(true);
            Svc.Settings.SetPortraitVideoOptimization(true);
            Svc.Settings.SetUiScalePercent(100);

            player = new PlayerWindow { Width = 1280, Height = 760 };
            player.Show();
            Pump(300);

            var stage = Element<Grid>(player, "PlayerStage");
            var surface = Element<Grid>(player, "VideoSurface");
            var leftPane = Element<Grid>(player, "WebLeftPane");
            var infoPanel = Element<Grid>(player, "InfoPanel");
            var comments = Element<StackPanel>(player, "CommentsPanel");
            var host = Element<ContentControl>(player, "WebCommentsHost");
            var scroll = Element<ScrollViewer>(player, "WebLeftScroll");
            var placeholder = Element<Border>(player, "WebVideoPlaceholder");
            var headerHost = Element<ContentControl>(player, "WebHeaderHost");
            var dock = Element<Border>(player, "WebDockAnchor");
            var video = Element<LibVLCSharp.WPF.VideoView>(player, "VideoView");
            var fab = Element<Button>(player, "CommentFab");
            var controlBar = Element<Border>(player, "ControlBar");
            var miniControls = Element<Border>(player, "WebMiniControls");
            var videoArea = Element<Grid>(player, "VideoAreaRoot");
            var fullBtn = Element<Button>(player, "BtnFullscreen2");

            var rows = Enumerable.Range(1, 30)
                .Select(i => new CommentItem
                {
                    Rpid = i,
                    UserName = $"评论用户 {i}",
                    Content = "竖屏优化模式下，视频靠左竖向铺满，评论区并排放在视频右侧，浏览评论时视频保持固定。",
                    LikeCount = i * 3,
                    RcTime = "2026-11-19"
                }).ToArray();
            Element<ItemsControl>(player, "CommentList").ItemsSource = rows;
            Element<TextBlock>(player, "UpNameText").Text = "图灵坐标";
            Element<TextBlock>(player, "VideoTitleText").Text = "竖屏视频优化布局测试标题";
            Element<TextBlock>(player, "DescText").Text = "更新日志：检测到竖屏视频时，视频放在左侧蓝色区域，评论区放在右侧红色区域，简介和推荐保留在最右栏。";
            Pump(150);

            // 1. Landscape video keeps standard web layout even with PortraitVideoOptimization enabled
            player.UpdateDetectedVideoSize(1920, 1080, fromMetadata: true);
            Pump(250);
            check(Field<bool>(player, "_webLayoutMounted") && !Field<bool>(player, "_portraitLayoutMounted"),
                "landscape 1920x1080 stays in web layout when PortraitVideoOptimization is enabled");

            // Track 0x0 collapses across all transitions
            var zeroSizes = new List<string>();
            SizeChangedEventHandler watchSize = (_, e) =>
            {
                if (e.NewSize.Width < 0.5 || e.NewSize.Height < 0.5)
                    zeroSizes.Add($"{e.PreviousSize.Width:0.#}x{e.PreviousSize.Height:0.#}->{e.NewSize.Width:0.#}x{e.NewSize.Height:0.#}");
            };
            surface.SizeChanged += watchSize;
            video.SizeChanged += watchSize;
            try
            {
                // 2. Portrait video switches Web -> Portrait cleanly
                player.UpdateDetectedVideoSize(1080, 1920, fromMetadata: true);
                Pump(350);

                check(Field<bool>(player, "_portraitLayoutMounted") && !Field<bool>(player, "_webLayoutMounted"),
                    "portrait 1080x1920 switches into portrait optimized layout");
                check(ReferenceEquals(host.Content, comments) && ReferenceEquals(fab.Parent, Element<Grid>(player, "WebCommentChrome")),
                    "portrait layout hosts comments and comment chrome in the side comments column");
                check(ReferenceEquals(video.Parent, surface) && ReferenceEquals(surface.Parent, stage),
                    "video view keeps the same visual parent when entering portrait layout");

                var surfaceRect = Bounds(surface, stage);
                var paneRect = Bounds(leftPane, stage);
                var infoRect = Bounds(infoPanel, stage);
                var commentsTop = comments.TransformToAncestor(stage).Transform(new Point()).Y;

                check(Math.Abs(surfaceRect.Left) < 2 && Math.Abs(surfaceRect.Top) < 2,
                    $"portrait video anchors at top-left of stage (got {surfaceRect.Left:0.#},{surfaceRect.Top:0.#})");
                check(Math.Abs(surfaceRect.Height - stage.ActualHeight) < 2,
                    $"portrait video fills full stage height ({surfaceRect.Height:0.#} vs {stage.ActualHeight:0.#})");
                check(surfaceRect.Width < surfaceRect.Height && surfaceRect.Width >= 260,
                    $"portrait video uses vertical aspect-matched width ({surfaceRect.Width:0.#}x{surfaceRect.Height:0.#})");
                check(Math.Abs(paneRect.Left - surfaceRect.Right) < 2 && Math.Abs(paneRect.Right - infoRect.Left) < 2,
                    $"comments pane fills the exact space between video and right sidebar (videoRight={surfaceRect.Right:0.#}, pane={paneRect.Left:0.#}..{paneRect.Right:0.#}, infoLeft={infoRect.Left:0.#})");
                check(Math.Abs(paneRect.Height - stage.ActualHeight) < 2 && placeholder.Visibility == Visibility.Collapsed && commentsTop < 100,
                    $"comments start near the top of the red column (commentsTop={commentsTop:0.#}, placeholderVis={placeholder.Visibility})");
                check(headerHost.Visibility == Visibility.Collapsed && dock.Visibility == Visibility.Collapsed,
                    "portrait layout hides web dock anchor and lets right info sidebar scroll naturally");
                check(controlBar.Visibility == Visibility.Visible && miniControls.Visibility == Visibility.Collapsed,
                    "portrait video displays normal control bar instead of mini dock controls");
                var fullBtnRect = Bounds(fullBtn, videoArea);
                check(fullBtnRect.Right <= videoArea.ActualWidth + 2 && Element<Grid>(player, "DanmakuComposer").Visibility == Visibility.Collapsed,
                    $"fullscreen button fits inside portrait video width without clipping (btnRight={fullBtnRect.Right:0.#} vs areaW={videoArea.ActualWidth:0.#})");

                // Save visual screenshot & verify non-empty rendered pixels
                var shotPath = Path.Combine(artifacts, "portrait-layout-active.png");
                var nonEmpty = SaveAndCheckNonEmpty(player, shotPath);
                check(nonEmpty, "portrait layout renders non-empty pixels to portrait-layout-active.png");

                // 3. Scrolling comments in portrait layout does NOT dock or move the video
                scroll.ScrollToVerticalOffset(300);
                Pump(250);
                var afterScrollSurface = Bounds(surface, stage);
                check(Math.Abs(afterScrollSurface.Left - surfaceRect.Left) < 1 && Math.Abs(afterScrollSurface.Width - surfaceRect.Width) < 1,
                    "scrolling comments in portrait layout keeps video pinned on the left");

                // 4. Fullscreen toggle & exit preserves portrait layout without 0x0 collapse
                Call(player, "ToggleFullscreen");
                Pump(450);
                check(Field<bool>(player, "_isFullscreen") && !Field<bool>(player, "_portraitLayoutMounted"),
                    "fullscreen temporarily exits portrait layout");
                Call(player, "ToggleFullscreen");
                Pump(550);
                check(!Field<bool>(player, "_isFullscreen") && Field<bool>(player, "_portraitLayoutMounted"),
                    "exiting fullscreen restores portrait layout cleanly");

                // 5. Disabling PortraitVideoOptimization transitions Portrait -> Web
                Svc.Settings.SetPortraitVideoOptimization(false);
                Pump(350);
                check(Field<bool>(player, "_webLayoutMounted") && !Field<bool>(player, "_portraitLayoutMounted") && leftPane.Margin == new Thickness(0),
                    "disabling PortraitVideoOptimization returns to web layout cleanly");

                // 6. Switching to Split layout and enabling PortraitVideoOptimization transitions Split -> Portrait -> Split
                Svc.Settings.SetWebPlayerLayout(false);
                Pump(350);
                check(!Field<bool>(player, "_webLayoutMounted") && !Field<bool>(player, "_portraitLayoutMounted"),
                    "disabling both options restores split layout");

                Svc.Settings.SetPortraitVideoOptimization(true);
                Pump(350);
                check(Field<bool>(player, "_portraitLayoutMounted") && !Field<bool>(player, "_webLayoutMounted"),
                    "PortraitVideoOptimization also works when base layout is 并排布局");

                // 7. Resize & 150% UI scale keep geometry valid
                Svc.Settings.SetUiScalePercent(150);
                player.Width = 840;
                player.Height = 600;
                Pump(400);
                var narrowSurface = Bounds(surface, stage);
                var narrowPane = Bounds(leftPane, stage);
                var narrowInfo = Bounds(infoPanel, stage);
                var narrowFullBtn = Bounds(fullBtn, videoArea);
                check(narrowSurface.Right <= narrowPane.Left + 2 && narrowPane.Right <= narrowInfo.Left + 2 && narrowSurface.Width >= 180 && narrowPane.Width >= 200,
                    $"minimum window at 150% scale keeps portrait video and comments balanced (video={narrowSurface.Width:0.#}, comments={narrowPane.Width:0.#}, info={narrowInfo.Width:0.#})");
                check(narrowFullBtn.Right <= videoArea.ActualWidth + 2,
                    $"fullscreen button stays inside ultra-narrow portrait control bar (btnRight={narrowFullBtn.Right:0.#} vs areaW={videoArea.ActualWidth:0.#})");
                SaveAndCheckNonEmpty(player, Path.Combine(artifacts, "portrait-layout-narrow-scaled.png"));

                // 8. ResetForNewMedia clears detected portrait dimensions and returns to Split layout
                Call(player, "OnWebMediaChanging");
                Pump(350);
                check(!Field<bool>(player, "_portraitLayoutMounted") && double.IsNaN(surface.Width) && ReferenceEquals(comments.Parent, Element<Grid>(player, "PageHost")),
                    "media change resets detected portrait dimensions and restores base split layout");
            }
            finally
            {
                surface.SizeChanged -= watchSize;
                video.SizeChanged -= watchSize;
            }

            check(zeroSizes.Count == 0,
                "VideoSurface never collapses to 0x0 during any portrait/web/split/fullscreen transition: " + string.Join(", ", zeroSizes));
        }
        finally
        {
            player?.Close();
            Pump(250);
            Svc.Settings.SetWebPlayerLayout(oldWeb);
            Svc.Settings.SetPortraitVideoOptimization(oldPortrait);
            Svc.Settings.SetUiScalePercent(oldScale);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static bool SaveAndCheckNonEmpty(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var pixels = new byte[w * h * 4];
        rtb.CopyPixels(pixels, w * 4, 0);
        int nonZero = 0;
        for (int i = 0; i < pixels.Length; i += 16)
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) nonZero++;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
        return nonZero > 500;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class BindingTraceCollector : TraceListener
    {
        private readonly List<string> _errors;
        public BindingTraceCollector(List<string> errors) => _errors = errors;
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _errors.Add(message.Trim());
        }
    }
}
