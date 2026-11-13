using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;

internal static class FeatureTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Pump(int ms)
    {
        var f = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); f.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(f);
    }

    private static void WaitWithPump(Task task, int timeoutMs = 15000)
    {
        var ui = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(timeoutMs) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        task.ContinueWith(_ => ui.BeginInvoke(() => { timer.Stop(); frame.Continue = false; }),
            TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        else timer.Stop();
        if (!task.IsCompleted) throw new TimeoutException("Task timed out while pumping WPF dispatcher.");
        task.GetAwaiter().GetResult();
    }

    private static T WaitWithPump<T>(Task<T> task, int timeoutMs = 15000)
    {
        WaitWithPump((Task)task, timeoutMs);
        return task.GetAwaiter().GetResult();
    }

    public static void Run(Action<bool, string> check)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        RunDanmakuOutlineTests(check);
        RunExternalClientCacheTests(check);
        RunBiliDesk172AdoptionTests(check);
        RunSettingsPlayerLayoutTests(check);
        RunSplitLayoutTouchTests(check);
    }

    private static void RunDanmakuOutlineTests(Action<bool, string> check)
    {
        var defaults = new AppSettings();
        check(!defaults.DanmakuOutline, "AppSettings.DanmakuOutline defaults to false");

        var snapshot = typeof(SettingsStore).GetMethod("Snapshot", Hidden)!;
        var apply = typeof(SettingsStore).GetMethod("ApplySanitized", Hidden)!;
        var original = (AppSettings)snapshot.Invoke(Svc.Settings, null)!;
        PlayerWindow? win = null;

        try
        {
            apply.Invoke(Svc.Settings, new object[] { new AppSettings() });
            check(!Svc.Settings.DanmakuOutline, "SettingsStore defaults DanmakuOutline to false");

            var vm = new SettingsViewModel();
            var changed = new HashSet<string>();
            vm.PropertyChanged += (_, e) => { if (e.PropertyName != null) changed.Add(e.PropertyName); };
            check(!vm.DanmakuOutline, "SettingsViewModel.DanmakuOutline initially false");

            vm.DanmakuOutline = true;
            check(Svc.Settings.DanmakuOutline && changed.Contains(nameof(SettingsViewModel.DanmakuOutline)),
                "toggling SettingsViewModel.DanmakuOutline updates SettingsStore and notifies UI");

            Svc.Settings.UpdateDanmakuSettings(true, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter, outline: false);
            check(!Svc.Settings.DanmakuOutline, "UpdateDanmakuSettings(outline: false) updates DanmakuOutline");

            // OutlinedDanmakuText layout & empty-text resilience
            var outlined = new OutlinedDanmakuText("测试描边弹幕", Brushes.White, 22);
            outlined.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            check(outlined.DesiredSize.Width > 20 && outlined.DesiredSize.Height > 10,
                "OutlinedDanmakuText measures non-zero size via FormattedText geometry");
            var empty = new OutlinedDanmakuText("", Brushes.White, 22);
            empty.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            check(empty.DesiredSize.Width == 0 && empty.DesiredSize.Height == 0,
                "OutlinedDanmakuText handles empty text safely");

            // PlayerWindow integration + persistent field preservation across ResetForNewMedia
            win = new PlayerWindow { Width = 1280, Height = 800, Left = 40, Top = 40 };
            win.Show();
            win.Activate();
            Pump(150);

            var chk = (CheckBox)win.FindName("DanmakuOutlineCheck");
            var stateText = (TextBlock)win.FindName("DanmakuOutlineStateText");
            check(chk != null && chk.IsChecked == false && stateText?.Text == "已关闭",
                "PlayerWindow.DanmakuOutlineCheck defaults to unchecked");

            var btnDanmakuArea = (Button)win.FindName("BtnDanmakuArea");
            var danmakuPopup = (System.Windows.Controls.Primitives.Popup)win.FindName("DanmakuAreaPopup");
            btnDanmakuArea.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(80);
            check(danmakuPopup.IsOpen && !chk!.Focusable && danmakuPopup.StaysOpen && System.Windows.Input.Mouse.Captured == null,
                "DanmakuAreaPopup opens non-modally without stealing mouse capture or CheckBox focus");

            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(chk!) as System.Windows.Automation.Peers.ToggleButtonAutomationPeer;
            ((System.Windows.Automation.Provider.IToggleProvider?)peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle))?.Toggle();
            Pump(80);
            check(chk!.IsChecked == true && Svc.Settings.DanmakuOutline && stateText?.Text == "已开启",
                "toggling DanmakuOutlineCheck inside DanmakuAreaPopup enables outline and updates state text");

            // Clicking BtnDanmakuArea again dismisses DanmakuAreaPopup cleanly
            btnDanmakuArea.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(60);
            check(!danmakuPopup.IsOpen, "re-clicking BtnDanmakuArea closes DanmakuAreaPopup cleanly");

            // Reset to unchecked before danmaku canvas check
            Svc.Settings.UpdateDanmakuSettings(Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent,
                Svc.Settings.DanmakuSmartFilter, outline: false);
            Pump(50);

            var danmakuCanvas = (Canvas)win.FindName("DanmakuCanvas");
            typeof(PlayerWindow).GetMethod("EnsureLanes", Hidden)!.Invoke(win, null);
            typeof(PlayerWindow).GetMethod("AddDanmaku", Hidden)!.Invoke(win, new object[] { "在线测试弹幕", 1, 1.0, 0xFFFFFFu });
            check(danmakuCanvas.Children.OfType<Border>().Any(b => b.Child is TextBlock tb && tb.Text == "在线测试弹幕"),
                "initial flying danmaku renders as lightweight TextBlock without outline");

            chk!.IsChecked = true;
            Pump(50);

            var fieldOutline = (bool)typeof(PlayerWindow).GetField("_danmakuOutline", Hidden)!.GetValue(win)!;
            check(fieldOutline && Svc.Settings.DanmakuOutline,
                "checking DanmakuOutlineCheck updates PlayerWindow state and SettingsStore");
            check(danmakuCanvas.Children.OfType<Border>().Any(b => b.Child is OutlinedDanmakuText o && o.Text == "在线测试弹幕"),
                "toggling DanmakuOutline converts active flying danmaku in-place to OutlinedDanmakuText");

            var reset = typeof(PlayerWindow).GetMethod("ResetForNewMedia", Hidden)!;
            reset.Invoke(win, null);
            var afterReset = (bool)typeof(PlayerWindow).GetField("_danmakuOutline", Hidden)!.GetValue(win)!;
            check(afterReset, "ResetForNewMedia preserves _danmakuOutline preference across media switches");

            // LiveOverlayRenderer Configure accepts outline cleanly
            var liveCanvas = new Canvas { Width = 640, Height = 360 };
            using var renderer = new LiveOverlayRenderer(liveCanvas);
            renderer.Configure(20, 0.35, 0.85, 2, outline: true);
            renderer.Add("直播描边验证", 0xFFFFFF);
            check(true, "LiveOverlayRenderer accepts outline=true without error");
        }
        finally
        {
            if (win != null) try { win.Close(); } catch { }
            apply.Invoke(Svc.Settings, new object[] { original });
            try { Pump(60); } catch { }
        }
    }

    private static void RunExternalClientCacheTests(Action<bool, string> check)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "BiliTouchFeatureTests_" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(tempRoot, "appcache");
        var extDir = Path.Combine(tempRoot, "bilibili");
        var downloadDir = Path.Combine(tempRoot, "downloads");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(extDir);
        Directory.CreateDirectory(downloadDir);

        try
        {
            // 1. Construct minimal synthetic Bilibili Windows client m4s (with 9-byte '000000000' obfuscation prefix)
            var videoM4sBytes = BuildObfuscatedM4s("vide", paddingZeros: 9, extraPaddingBytes: 120);
            var lowVideoM4sBytes = BuildObfuscatedM4s("vide", paddingZeros: 9, extraPaddingBytes: 40);
            var audioM4sBytes = BuildObfuscatedM4s("soun", paddingZeros: 9, extraPaddingBytes: 60);

            var entryDir1 = Path.Combine(extDir, "1001");
            Directory.CreateDirectory(entryDir1);
            var p1Hevc = Path.Combine(entryDir1, "1001-1-100023.m4s");
            var p1Avc = Path.Combine(entryDir1, "1001-1-30032.m4s");
            var p1Audio = Path.Combine(entryDir1, "1001-1-30232.m4s");
            File.WriteAllBytes(p1Hevc, videoM4sBytes);
            File.WriteAllBytes(p1Avc, lowVideoM4sBytes);
            File.WriteAllBytes(p1Audio, audioM4sBytes);

            var p1Cover = Path.Combine(entryDir1, "image.jpg");
            SaveSolidPng(p1Cover, 1200, 675);

            var expectedTotalSize1 = videoM4sBytes.Length + audioM4sBytes.Length;
            File.WriteAllText(Path.Combine(entryDir1, ".videoInfo"), JsonSerializer.Serialize(new
            {
                itemId = 1001L,
                bvid = "BV1TESTMULTI",
                cid = 9001L,
                p = 1,
                title = "分P一：基础教学",
                groupTitle = "系列教程合集",
                tabName = "P1 基础",
                uname = "测试UP主",
                qn = 80,
                codecid = 12,
                status = "completed",
                totalSize = expectedTotalSize1,
                loadedSize = expectedTotalSize1,
                createTime = 1760000000000L,
                coverPath = p1Cover
            }));

            // Verify 9-byte prefix detection, track kind detection, and zero-stripping
            using (var fs = File.OpenRead(p1Hevc))
            {
                check(DashRemuxer.DetectM4sStartOffset(fs) == 9,
                    "DashRemuxer.DetectM4sStartOffset skips 9-byte '000000000' obfuscation header");
            }
            check(DashRemuxer.TryDetectTrackKind(p1Hevc) == "vide",
                "DashRemuxer.TryDetectTrackKind identifies vide track after stripping prefix");
            check(DashRemuxer.TryDetectTrackKind(p1Audio) == "soun",
                "DashRemuxer.TryDetectTrackKind identifies soun track after stripping prefix");

            var strippedPath = Path.Combine(cacheDir, "stripped.mp4");
            var stripOk = DashRemuxer.TryStripLeadingZeros(p1Hevc, strippedPath, out var stripErr);
            check(stripOk && File.Exists(strippedPath) && new FileInfo(strippedPath).Length == videoM4sBytes.Length - 9,
                $"DashRemuxer.TryStripLeadingZeros produces exact stripped length (err={stripErr ?? "none"})");
            var strippedHead = File.ReadAllBytes(strippedPath);
            check(Encoding.ASCII.GetString(strippedHead, 4, 4) == "ftyp",
                "stripped mp4 starts directly with ISO BMFF ftyp box");

            // Part 2 of same BVID (audio-less / single-stream video to also test PreparePlayableMediaCoreAsync strip fallback)
            var entryDir2 = Path.Combine(extDir, "1002");
            Directory.CreateDirectory(entryDir2);
            var p2Video = Path.Combine(entryDir2, "1002-1-30080.m4s");
            File.WriteAllBytes(p2Video, videoM4sBytes);
            var p2Cover = Path.Combine(entryDir2, "image.jpg");
            SaveSolidPng(p2Cover, 800, 450);
            File.WriteAllText(Path.Combine(entryDir2, ".videoInfo"), JsonSerializer.Serialize(new
            {
                itemId = 1002L,
                bvid = "BV1TESTMULTI",
                cid = 9002L,
                p = 2,
                title = "分P二：进阶实践",
                groupTitle = "系列教程合集",
                tabName = "正片",
                uname = "测试UP主",
                qn = 112,
                codecid = 7,
                status = "completed",
                totalSize = videoM4sBytes.Length,
                loadedSize = videoM4sBytes.Length,
                createTime = 1760000100000L
            }));

            // Incomplete download folder must be skipped
            var incompleteDir = Path.Combine(extDir, "1003");
            Directory.CreateDirectory(incompleteDir);
            File.WriteAllBytes(Path.Combine(incompleteDir, "1003-1-30032.m4s"), lowVideoM4sBytes);
            File.WriteAllText(Path.Combine(incompleteDir, ".videoInfo"), JsonSerializer.Serialize(new
            {
                itemId = 1003L,
                bvid = "BV1INCOMPLETE",
                cid = 9003L,
                p = 1,
                title = "未完成缓存",
                status = "downloading"
            }));

            var scanned = BilibiliClientCacheScanner.ScanDirectory(extDir);
            check(scanned.Count == 2, $"ScanDirectory picks 2 completed caches and skips downloading folder (got {scanned.Count})");

            var first = scanned.First(x => x.Cid == 9001L);
            check(first.IsExternalClientCache && first.State == OfflineState.Completed,
                "scanned entry is marked IsExternalClientCache + Completed");
            check(first.Title == "系列教程合集" && first.PartTitle == "分P一：基础教学",
                "multi-P same BVID uses groupTitle as Title and part title as PartTitle");
            check(first.Author == "测试UP主" && first.Cover == p1Cover && first.QualityText.Contains("1080P") && first.QualityText.Contains("B站客户端缓存"),
                "scanned entry preserves author, local cover path, and formatted quality label");
            check(string.Equals(first.ExternalVideoPath, p1Hevc, StringComparison.OrdinalIgnoreCase)
                && string.Equals(first.ExternalAudioPath, p1Audio, StringComparison.OrdinalIgnoreCase),
                "multi-stream cache pairs the exact video + audio tracks matching totalSize");
            check(first.HasPlayableMedia, "scanned entry reports HasPlayableMedia=true");

            var second = scanned.First(x => x.Cid == 9002L);
            var preparedSingle = BilibiliClientCacheScanner.PreparePlayableMediaCoreAsync(second, cacheDir).GetAwaiter().GetResult();
            check(File.Exists(preparedSingle) && new FileInfo(preparedSingle).Length == videoM4sBytes.Length - 9,
                "PreparePlayableMediaCoreAsync outputs valid header-stripped mp4 into cacheDir");
            check(BilibiliClientCacheScanner.TryFindRemuxCachePath(second, cacheDir) == preparedSingle,
                "TryFindRemuxCachePath locates cached remux file");
            check(new FileInfo(p2Video).Length == videoM4sBytes.Length,
                "PreparePlayableMediaCoreAsync never modifies original official client m4s file");

            // Test OfflineDownloadService integration with isolated externalCacheDir
            var svc = new OfflineDownloadService((_, _, _) => Task.FromResult((false, (string?)"none", (string?)null, (string?)null, 0)),
                new System.Net.Http.HttpClient(), extDir);
            WaitWithPump(svc.RefreshExternalCachesAsync());
            var extItems = svc.Items.Where(e => e.IsExternalClientCache && e.ExternalDirectory.StartsWith(extDir, StringComparison.OrdinalIgnoreCase)).ToList();
            check(extItems.Count == 2 && svc.Items.Count(e => e.IsExternalClientCache) == 2,
                $"OfflineDownloadService.RefreshExternalCachesAsync populates external caches cleanly (extCount={extItems.Count})");

            bool deleteBlocked = false;
            try { WaitWithPump(svc.DeleteAsync(extItems[0])); }
            catch (IOException) { deleteBlocked = true; }
            check(deleteBlocked && File.Exists(p1Hevc) && File.Exists(p1Cover) && svc.Items.Contains(extItems[0]),
                "OfflineDownloadService.DeleteAsync protects external client cache source files from deletion");

            // Local cover decoding via CoverLoader
            var localBitmap = WaitWithPump(CoverLoader.LoadAsync(p1Cover, decodeWidth: 240));
            check(localBitmap != null && localBitmap.PixelWidth == 256,
                $"CoverLoader decodes local disk cover and quantizes 240 to 256 (got {localBitmap?.PixelWidth})");

            // Read-only probe against real user Windows client cache if present on machine
            if (Directory.Exists(BilibiliClientCacheScanner.DefaultCacheDirectory))
            {
                var realEntries = BilibiliClientCacheScanner.ScanDefault();
                if (realEntries.Count > 0)
                {
                    var sample = realEntries.OrderBy(e => e.Done).First();
                    check(sample.IsExternalClientCache && !string.IsNullOrWhiteSpace(sample.Title) && sample.HasPlayableMedia,
                        $"real Windows client cache recognized ({realEntries.Count} entries, sample='{sample.Title}')");

                    var realCoverMissing = realEntries.Count(e => string.IsNullOrWhiteSpace(e.Cover));
                    check(realCoverMissing == 0,
                        $"all {realEntries.Count} real Windows client cache entries resolved a local cover or coverUrl (missing={realCoverMissing})");

                    var remuxOut = WaitWithPump(BilibiliClientCacheScanner.PreparePlayableMediaCoreAsync(sample, cacheDir));
                    check(File.Exists(remuxOut) && new FileInfo(remuxOut).Length > 1024,
                        $"real Windows client cache muxed cleanly to playable MP4 ({new FileInfo(remuxOut).Length} bytes)");

                    // Verify real desktop multi-BVID collection collapse (groupId grouping -> 2-col groups + level-2 detail)
                    var realSvc = new OfflineDownloadService(
                        (_, _, _) => Task.FromResult((false, (string?)"stub", (string?)null, (string?)null, 0)),
                        new System.Net.Http.HttpClient(),
                        BilibiliClientCacheScanner.DefaultCacheDirectory);
                    var realVm = new CacheViewModel(realSvc);
                    WaitWithPump(realVm.LoadAsync());
                    var shuLiang = realVm.VisibleGroups.FirstOrDefault(g => g.Title.Contains("数量关系") && g.EpisodeCount >= 10);
                    var pianDuan = realVm.VisibleGroups.FirstOrDefault(g => g.Title.Contains("片段阅读") && g.EpisodeCount >= 10);
                    check(shuLiang != null && pianDuan != null,
                        $"multi-BVID series collapse by groupId (数量关系={shuLiang?.EpisodeCount}, 片段阅读={pianDuan?.EpisodeCount})");
                    if (shuLiang != null)
                    {
                        realVm.Activate(shuLiang);
                        check(realVm.IsDetailView && ReferenceEquals(realVm.SelectedGroup, shuLiang) && shuLiang.Episodes.Count >= 10,
                            "activating collapsed collection opens level-2 episode view");
                        realVm.BackFromGroupCommand.Execute(null);
                        check(realVm.IsOverview && realVm.SelectedGroup == null,
                            "BackFromGroupCommand returns cleanly from level-2 to collapsed overview");
                    }
                }
            }

            // Verify live peerFactory (no blurry screenshot fallback) on MessagesPage & SearchPage + smooth pull-to-refresh content offset
            var msgPage = new BiliTouch.Views.Pages.MessagesPage();
            var searchPage = new BiliTouch.Views.Pages.SearchPage();
            var msgPeerFactory = typeof(TabNavigationGestures).GetField("_peerFactory", Hidden)!.GetValue(msgPage.ModuleGestures);
            var searchPeerFactory = typeof(TabNavigationGestures).GetField("_peerFactory", Hidden)!.GetValue(searchPage.ModuleGestures);
            check(msgPeerFactory != null && searchPeerFactory != null,
                "MessagesPage and SearchPage both use live WPF peerFactory instead of blurry screenshot transitions");

            var pullStack = new StackPanel();
            pullStack.Children.Add(new Border { Height = 900, Background = Brushes.Transparent });
            var pullScroll = new ScrollViewer { Content = pullStack };
            var pullRoot = new Grid();
            pullRoot.Children.Add(pullScroll);
            var pullPage = new UserControl { Content = pullRoot };
            var pullCount = 0;
            var pullGesture = ModuleGestures.Install(pullPage, () => { pullCount++; return Task.CompletedTask; });
            var pullWin = new Window { Width = 520, Height = 420, Content = pullPage, ShowActivated = false };
            try
            {
                pullWin.Show();
                Pump(80);
                var beginMethod = typeof(ModuleGestures).GetMethod("Begin", Hidden)!;
                var moveMethod = typeof(ModuleGestures).GetMethod("Move", Hidden)!;
                var endMethod = typeof(ModuleGestures).GetMethod("End", Hidden)!;
                var offsetField = (TranslateTransform)typeof(ModuleGestures).GetField("_contentOffset", Hidden)!.GetValue(pullGesture)!;
                beginMethod.Invoke(pullGesture, new object?[] { new Point(100, 60), pullStack });
                moveMethod.Invoke(pullGesture, new object?[] { new Point(100, 145) }); // 85px < 120 threshold
                var midOffset = offsetField.Y;
                var triggeredShort = (bool)endMethod.Invoke(pullGesture, null)!;
                Pump(280);
                check(midOffset > 20 && !triggeredShort && pullCount == 0,
                    $"pull-to-refresh translates scroll content smoothly ({midOffset:0.0}px) and ignores pulls under 120px threshold");
                beginMethod.Invoke(pullGesture, new object?[] { new Point(100, 60), pullStack });
                moveMethod.Invoke(pullGesture, new object?[] { new Point(100, 200) }); // 140px >= 120 threshold
                var triggeredDeep = (bool)endMethod.Invoke(pullGesture, null)!;
                Pump(80);
                check(triggeredDeep, "pull-to-refresh triggers once past raised 120px threshold");
            }
            finally
            {
                pullWin.Close();
            }
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static void RunBiliDesk172AdoptionTests(Action<bool, string> check)
    {
        // 1. CommentItem.TrimRepliesToPreview
        var root = new CommentItem();
        for (var i = 0; i < 3; i++) root.PreviewReplies.Add(new CommentItem { Content = $"preview-{i}" });
        for (var i = 0; i < 12; i++) root.Replies.Add(new CommentItem { Content = $"reply-{i}" });
        root.RepliesNextPage = 4;
        root.TrimRepliesToPreview();
        check(root.Replies.Count == 3 && root.RepliesNextPage == 1 && root.Replies[0].Content == "preview-0",
            "CommentItem.TrimRepliesToPreview collapses expanded replies back to PreviewReplies and resets page to 1");

        var compact = new CommentItem();
        compact.PreviewReplies.Add(new CommentItem { Content = "p" });
        compact.Replies.Add(new CommentItem { Content = "r" });
        compact.RepliesNextPage = 2;
        compact.TrimRepliesToPreview();
        check(compact.Replies.Count == 1 && compact.RepliesNextPage == 2,
            "CommentItem.TrimRepliesToPreview is a no-op when Replies does not exceed PreviewReplies");

        // 2. CoverLoader intermediate DPI quantization steps (256 / 384 / 576)
        var tempPng = Path.Combine(Path.GetTempPath(), $"cover_step_{Guid.NewGuid():N}.png");
        try
        {
            SaveSolidPng(tempPng, 1200, 675);
            var b384 = WaitWithPump(CoverLoader.LoadAsync(tempPng, decodeWidth: 350));
            var b576 = WaitWithPump(CoverLoader.LoadAsync(tempPng, decodeWidth: 500));
            check(b384 != null && b384.PixelWidth == 384 && b576 != null && b576.PixelWidth == 576,
                $"CoverLoader intermediate DPI steps quantize 350->384 and 500->576 (got {b384?.PixelWidth}/{b576?.PixelWidth})");
        }
        finally
        {
            try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { }
        }

        // 3. MemoryTrim.RequestIdleRelease safety check
        MemoryTrim.RequestIdleRelease();
        check(true, "MemoryTrim.RequestIdleRelease completes cleanly without throwing");
    }

    private static void RunSettingsPlayerLayoutTests(Action<bool, string> check)
    {
        var oldWeb = Svc.Settings.UseWebPlayerLayout;
        Window? win = null;
        try
        {
            Svc.Settings.SetWebPlayerLayout(true);
            var vm = new SettingsViewModel();
            var page = new BiliTouch.Views.Pages.SettingsPage { DataContext = vm };
            win = new Window { Width = 1000, Height = 800, Content = page, ShowActivated = false };
            win.Show();
            Pump(200);

            // Switch to Playback tab and swipe halfway back to mount GeneralTab peer preview (reproducing peer GroupName collision)
            vm.TabIndex = 1;
            Pump(150);
            page.ModuleGestures.PreviewHorizontalWheel(-0.25);
            Pump(60);
            page.ModuleGestures.SettleHorizontalWheelPreview(false);
            Pump(120);
            vm.TabIndex = 0;
            Pump(180);

            var radios = FindVisualChildren<RadioButton>(page).ToList();
            var splitBtn = radios.First(r => Equals(r.Content, "并排布局") && r.IsVisible);
            var webBtn = radios.First(r => Equals(r.Content, "网页布局") && r.IsVisible);
            check(webBtn.IsChecked == true && splitBtn.IsChecked == false,
                "SettingsPage initializes PlayerLayout radio buttons in web layout state");

            void ClickRadio(RadioButton btn)
                => typeof(RadioButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(btn, null);

            // Click 并排布局
            ClickRadio(splitBtn);
            Pump(80);
            check(!Svc.Settings.UseWebPlayerLayout && splitBtn.IsChecked == true && webBtn.IsChecked == false,
                "clicking 并排布局 switches UseWebPlayerLayout to false");

            // Click 网页布局 back (this used to snap back to 并排布局)
            ClickRadio(webBtn);
            Pump(80);
            check(Svc.Settings.UseWebPlayerLayout && webBtn.IsChecked == true && splitBtn.IsChecked == false,
                "clicking 网页布局 switches UseWebPlayerLayout back to true cleanly");

            // Switch to 并排布局 and back one more time
            ClickRadio(splitBtn);
            Pump(60);
            ClickRadio(webBtn);
            Pump(60);
            check(Svc.Settings.UseWebPlayerLayout && webBtn.IsChecked == true && splitBtn.IsChecked == false,
                "repeated 并排布局 <-> 网页布局 toggles reliably");
        }
        finally
        {
            win?.Close();
            Svc.Settings.SetWebPlayerLayout(oldWeb);
        }
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

    private static void RunSplitLayoutTouchTests(Action<bool, string> check)
    {
        var oldWeb = Svc.Settings.UseWebPlayerLayout;
        PlayerWindow? player = null;
        try
        {
            // Start in web layout, then switch back to split layout (and test freshly opened split layout as well)
            Svc.Settings.SetWebPlayerLayout(true);
            player = new PlayerWindow { Width = 1320, Height = 860, ShowActivated = false };
            player.Show();
            Pump(300);

            Svc.Settings.SetWebPlayerLayout(false);
            Pump(350);

            var infoPanel = (Grid)player.FindName("InfoPanel");
            var infoScroll = (ScrollViewer)player.FindName("InfoScroll");
            var introTab = (RadioButton)player.FindName("TabIntro");
            var commentsTab = (RadioButton)player.FindName("TabComments");
            var descText = (TextBlock)player.FindName("DescText");
            var relatedList = (ItemsControl)player.FindName("RelatedList");
            var relatedPanel = (FrameworkElement)player.FindName("RelatedPanel");
            var commentList = (ItemsControl)player.FindName("CommentList");

            descText.Text = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"简介第 {i} 行内容用于测试触控上下滚动。"));
            relatedPanel.Visibility = Visibility.Visible;
            relatedList.ItemsSource = Enumerable.Range(1, 15).Select(i => new VideoItem
            {
                Bvid = $"BV1test{i:00}",
                Title = $"相关视频推荐测试标题 {i}",
                Author = $"UP主{i}"
            }).ToList();
            commentList.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<CommentItem>(
                Enumerable.Range(1, 25).Select(i => new CommentItem
                {
                    Rpid = 800000 + i,
                    UserName = $"评论用户{i}",
                    Content = $"第 {i} 条评论正文：足够多行以测试并排布局评论区的触控上下滚动及左右切换。"
                }));
            Pump(350);

            // 1. Vertical touch scroll on Intro tab
            infoScroll.ScrollToTop();
            Pump(60);
            var introBefore = infoScroll.VerticalOffset;
            var f = new TestFinger(infoScroll);
            var midX = infoScroll.ActualWidth * 0.5;
            var startY = infoScroll.ActualHeight * 0.65;
            f.Down(midX, startY);
            f.Move(midX, startY - 60);
            f.Move(midX, startY - 140);
            f.Move(midX, startY - 220);
            f.Up(midX, startY - 220);
            Pump(200);
            check(infoScroll.VerticalOffset > introBefore + 20,
                $"split layout intro tab scrolls vertically with real touch (offset {introBefore:0}->{infoScroll.VerticalOffset:0}, panning={infoScroll.PanningMode})");

            // 2. Horizontal touch swipe from Intro -> Comments
            infoScroll.ScrollToTop();
            Pump(150);
            f = new TestFinger(infoPanel);
            var w = infoPanel.ActualWidth;
            var swipeY = infoPanel.ActualHeight * 0.65;
            f.Down(w * 0.78, swipeY);
            f.Move(w * 0.58, swipeY);
            f.Move(w * 0.35, swipeY);
            f.Move(w * 0.15, swipeY);
            f.Up(w * 0.15, swipeY);
            Pump(350);
            check(commentsTab.IsChecked == true,
                $"split layout real touch horizontal swipe switches Intro -> Comments (tab={commentsTab.IsChecked})");

            // 3. Vertical touch scroll on Comments tab starting inside CommentText (RichTextBox)
            infoScroll.ScrollToTop();
            Pump(80);
            var commentBefore = infoScroll.VerticalOffset;
            var firstCommentText = FindVisualChildren<BiliTouch.Views.Controls.CommentText>(commentList).FirstOrDefault(ct => ct.IsVisible);
            FrameworkElement touchTarget = (FrameworkElement?)firstCommentText ?? infoScroll;
            f = new TestFinger(touchTarget);
            var tx = Math.Max(20, touchTarget.ActualWidth * 0.4);
            var ty = Math.Max(10, touchTarget.ActualHeight * 0.5);
            // Convert target drag relative to touchTarget using same screen-vertical travel
            f.Down(tx, ty);
            f.Move(tx, ty - 70);
            f.Move(tx, ty - 150);
            f.Move(tx, ty - 230);
            f.Up(tx, ty - 230);
            Pump(200);
            check(infoScroll.VerticalOffset > commentBefore + 20,
                $"split layout comments tab scrolls vertically via touch even from CommentText (offset {commentBefore:0}->{infoScroll.VerticalOffset:0}, panning={infoScroll.PanningMode})");

            // 4. Horizontal touch swipe back Comments -> Intro
            f = new TestFinger(infoPanel);
            f.Down(w * 0.18, swipeY);
            f.Move(w * 0.40, swipeY);
            f.Move(w * 0.65, swipeY);
            f.Move(w * 0.85, swipeY);
            f.Up(w * 0.85, swipeY);
            Pump(350);
            check(introTab.IsChecked == true,
                $"split layout real touch horizontal swipe switches Comments -> Intro (tab={introTab.IsChecked})");
        }
        finally
        {
            player?.Close();
            Pump(150);
            Svc.Settings.SetWebPlayerLayout(oldWeb);
        }
    }

    private sealed class TestFinger : System.Windows.Input.TouchDevice
    {
        private static int _nextId = 9000;
        private readonly FrameworkElement _target;
        private readonly Visual _root;
        private Point _point;
        private System.Windows.Input.TouchAction _action;
        public TestFinger(FrameworkElement target) : base(++_nextId)
        {
            _target = target;
            var source = PresentationSource.FromVisual(target)!;
            _root = source.RootVisual;
            SetActiveSource(source);
        }
        public override System.Windows.Input.TouchPoint GetTouchPoint(IInputElement? relativeTo)
        {
            var p = relativeTo is Visual v ? _root.TransformToDescendant(v).Transform(_point) : _point;
            return new System.Windows.Input.TouchPoint(this, p, new Rect(p, new Size(4, 4)), _action);
        }
        public override System.Windows.Input.TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo) => new() { GetTouchPoint(relativeTo) };
        private void At(double x, double y, System.Windows.Input.TouchAction a) { _point = _target.TranslatePoint(new Point(x, y), (UIElement)_root); _action = a; }
        public void Down(double x, double y) { At(x, y, System.Windows.Input.TouchAction.Down); Activate(); ReportDown(); Pump(25); }
        public void Move(double x, double y) { At(x, y, System.Windows.Input.TouchAction.Move); ReportMove(); Pump(25); }
        public void Up(double x, double y) { At(x, y, System.Windows.Input.TouchAction.Up); ReportUp(); Deactivate(); Pump(25); }
    }

    private static byte[] BuildObfuscatedM4s(string handlerType, int paddingZeros, int extraPaddingBytes)
    {
        var ftyp = MakeBox("ftyp", Encoding.ASCII.GetBytes("iso5\0\0\0\x01iso5dash"));
        var hdlrPayload = new byte[4 + 4 + 4 + 12]; // fullbox(4) + pre_defined(4) + handler_type(4) + reserved(12)
        Encoding.ASCII.GetBytes(handlerType).CopyTo(hdlrPayload, 8);
        var hdlr = MakeBox("hdlr", hdlrPayload);
        var mdia = MakeBox("mdia", hdlr);
        var trak = MakeBox("trak", mdia);
        var moov = MakeBox("moov", trak);
        var free = MakeBox("free", new byte[extraPaddingBytes]);

        var prefix = Enumerable.Repeat((byte)'0', paddingZeros).ToArray();
        var result = new byte[prefix.Length + ftyp.Length + moov.Length + free.Length];
        var at = 0;
        Buffer.BlockCopy(prefix, 0, result, at, prefix.Length); at += prefix.Length;
        Buffer.BlockCopy(ftyp, 0, result, at, ftyp.Length); at += ftyp.Length;
        Buffer.BlockCopy(moov, 0, result, at, moov.Length); at += moov.Length;
        Buffer.BlockCopy(free, 0, result, at, free.Length);
        return result;
    }

    private static byte[] MakeBox(string type, byte[] payload)
    {
        var buf = new byte[payload.Length + 8];
        var len = (uint)buf.Length;
        buf[0] = (byte)(len >> 24);
        buf[1] = (byte)(len >> 16);
        buf[2] = (byte)(len >> 8);
        buf[3] = (byte)len;
        Encoding.ASCII.GetBytes(type).CopyTo(buf, 4);
        Buffer.BlockCopy(payload, 0, buf, 8, payload.Length);
        return buf;
    }

    private static void SaveSolidPng(string path, int width, int height)
    {
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
