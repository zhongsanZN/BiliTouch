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
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Pages;

internal static class TabletModeTests
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<bool, string> check, string artifacts)
    {
        RunDeviceProfileTests(check);
        RunPostureClassificationTests(check);
        RunSettingsTests(check, artifacts);
        RunMainWindowPhase0Tests(check, artifacts);
        RunPhase1ShellAndEmbeddedPlayerTests(check, artifacts);
        RunPhase2CommunityAndLiveTests(check, artifacts);
        RunPhase3LongTailPagesTests(check, artifacts);
    }

    private static void RunDeviceProfileTests(Action<bool, string> check)
    {
        check(DeviceProfile.IsSurfaceHardware("Microsoft Corporation", "Surface Pro 9"),
            "IsSurfaceHardware recognizes Microsoft Surface Pro 9");
        check(DeviceProfile.IsSurfaceHardware("Microsoft Corporation", "Surface Go 3"),
            "IsSurfaceHardware recognizes Microsoft Surface Go 3");
        check(DeviceProfile.IsSurfaceHardware("Microsoft", "surface laptop studio 2"),
            "IsSurfaceHardware matches case-insensitively");
        check(!DeviceProfile.IsSurfaceHardware("LENOVO", "ThinkPad X1 Carbon"),
            "IsSurfaceHardware rejects non-Surface Lenovo hardware");
        check(!DeviceProfile.IsSurfaceHardware("Dell Inc.", "Surface-like OEM"),
            "IsSurfaceHardware requires Microsoft manufacturer");
        check(!DeviceProfile.IsSurfaceHardware(null, "Surface Pro") && !DeviceProfile.IsSurfaceHardware("Microsoft Corporation", ""),
            "IsSurfaceHardware handles null/empty strings safely");

        check(DeviceProfile.ShouldShowSurfaceTabletHint(true, TabletMode.Off, false),
            "Surface hint shows when Surface + TabletMode.Off + not dismissed");
        check(!DeviceProfile.ShouldShowSurfaceTabletHint(true, TabletMode.Off, true),
            "Surface hint stays hidden once dismissed");
        check(!DeviceProfile.ShouldShowSurfaceTabletHint(true, TabletMode.Auto, false),
            "Surface hint stays hidden when TabletMode is already Auto");
        check(!DeviceProfile.ShouldShowSurfaceTabletHint(false, TabletMode.Off, false),
            "Surface hint never shows on non-Surface devices");
    }

    private static void RunPostureClassificationTests(Action<bool, string> check)
    {
        check(Responsive.ClassifyPosture(720, 1280, LayoutPosture.Desktop, TabletMode.Off) == LayoutPosture.Desktop,
            "TabletMode.Off always resolves to Desktop even on 720x1280 portrait");
        check(Responsive.ClassifyPosture(1280, 800, LayoutPosture.Desktop, TabletMode.Always) == LayoutPosture.TabletPortrait,
            "TabletMode.Always forces TabletPortrait even on 1280x800 landscape");
        check(Responsive.ClassifyPosture(720, 1280, LayoutPosture.Desktop, TabletMode.Auto) == LayoutPosture.TabletPortrait,
            "TabletMode.Auto enters TabletPortrait on 720x1280");
        check(Responsive.ClassifyPosture(960, 1440, LayoutPosture.Desktop, TabletMode.Auto) == LayoutPosture.TabletPortrait,
            "TabletMode.Auto enters TabletPortrait on Surface Pro 960x1440");
        check(Responsive.ClassifyPosture(1040, 1600, LayoutPosture.Desktop, TabletMode.Auto) == LayoutPosture.Desktop,
            "TabletMode.Auto does not enter TabletPortrait when width >= 1040");
        check(Responsive.ClassifyPosture(1060, 1600, LayoutPosture.TabletPortrait, TabletMode.Auto) == LayoutPosture.TabletPortrait,
            "TabletMode.Auto hysteresis holds TabletPortrait inside 1040..1080 dead zone");
        check(Responsive.ClassifyPosture(1080, 1600, LayoutPosture.TabletPortrait, TabletMode.Auto) == LayoutPosture.Desktop,
            "TabletMode.Auto exits TabletPortrait once width reaches 1080");
        check(Responsive.ClassifyPosture(800, 820, LayoutPosture.Desktop, TabletMode.Auto) == LayoutPosture.Desktop,
            "TabletMode.Auto ignores near-square window inside aspect hysteresis band");
        check(Responsive.ClassifyPosture(820, 800, LayoutPosture.TabletPortrait, TabletMode.Auto) == LayoutPosture.TabletPortrait,
            "TabletMode.Auto keeps TabletPortrait inside aspect hysteresis band");
        check(Responsive.ClassifyPosture(1280, 800, LayoutPosture.TabletPortrait, TabletMode.Auto) == LayoutPosture.Desktop,
            "TabletMode.Auto exits to Desktop when rotated to landscape 1280x800");
        check(Responsive.ClassifyPosture(0, 0, LayoutPosture.TabletPortrait, TabletMode.Auto) == LayoutPosture.TabletPortrait,
            "ClassifyPosture preserves current posture on 0x0 unmeasured layout");
    }

    private static void RunSettingsTests(Action<bool, string> check, string artifacts)
    {
        var oldMode = Svc.Settings.TabletMode;
        var bindingErrors = new List<string>();
        var listener = new BindingTraceCollector(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

        Window? win = null;
        try
        {
            Svc.Settings.SetTabletMode(TabletMode.Off);
            var vm = new SettingsViewModel();
            var page = new SettingsPage { DataContext = vm };
            win = new Window { Width = 1000, Height = 860, Content = page, ShowActivated = false };
            win.Show();
            Pump(220);

            var radios = FindVisualChildren<RadioButton>(page).Where(r => r.GroupName == "TabletMode" && r.IsVisible).ToList();
            var offBtn = radios.FirstOrDefault(r => Equals(r.Content, "关闭"));
            var autoBtn = radios.FirstOrDefault(r => Equals(r.Content, "竖屏自动"));
            var alwaysBtn = radios.FirstOrDefault(r => Equals(r.Content, "始终开启"));

            check(offBtn != null && autoBtn != null && alwaysBtn != null,
                "SettingsPage renders all three 平板模式 radio buttons (关闭 / 竖屏自动 / 始终开启)");
            check(offBtn!.IsChecked == true && autoBtn!.IsChecked == false && alwaysBtn!.IsChecked == false,
                "平板模式 defaults to 关闭");

            void ClickRadio(RadioButton btn) => typeof(RadioButton).GetMethod("OnClick", Hidden)!.Invoke(btn, null);

            ClickRadio(autoBtn!);
            Pump(80);
            check(Svc.Settings.TabletMode == TabletMode.Auto, "clicking 竖屏自动 sets TabletMode.Auto");
            check(SaveAndCheckNonEmpty(win, Path.Combine(artifacts, "settings-tablet-mode.png")),
                "SettingsPage 平板模式 card renders non-empty pixels");

            var settingsPath = Path.Combine(AppPaths.DataDir, "settings.json");
            var savedJson = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "";
            using (var doc = JsonDocument.Parse(savedJson))
            {
                var persisted = doc.RootElement.TryGetProperty("TabletMode", out var prop) ? prop.ToString() : "";
                check(string.Equals(persisted, "Auto", StringComparison.OrdinalIgnoreCase) || persisted == "1",
                    "TabletMode.Auto persists cleanly to settings.json");
            }

            ClickRadio(alwaysBtn!);
            Pump(80);
            check(Svc.Settings.TabletMode == TabletMode.Always, "clicking 始终开启 sets TabletMode.Always");

            ClickRadio(offBtn!);
            Pump(80);
            check(Svc.Settings.TabletMode == TabletMode.Off, "clicking 关闭 restores TabletMode.Off");
            check(bindingErrors.Count == 0,
                $"SettingsPage 平板模式 produces zero WPF binding errors: {string.Join(" | ", bindingErrors)}");
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            win?.Close();
            Svc.Settings.SetTabletMode(oldMode);
        }
    }

    private static void RunMainWindowPhase0Tests(Action<bool, string> check, string artifacts)
    {
        var oldMode = Svc.Settings.TabletMode;
        var oldDismissed = Svc.Settings.SurfaceTabletHintDismissed;
        var oldOverride = DeviceProfile.SurfaceDetectorOverrideForTests;
        MainWindow? main = null;
        try
        {
            DeviceProfile.SurfaceDetectorOverrideForTests = () => true;
            Svc.Settings.SurfaceTabletHintDismissed = false;
            Svc.Settings.SetTabletMode(TabletMode.Off);

            main = new MainWindow { Width = 1100, Height = 760, ShowActivated = false };
            main.Show();
            Pump(300);

            var root = (Grid)main.FindName("RootGrid");
            var banner = (Border)main.FindName("SurfaceTabletHintBanner");
            var enableBtn = (Button)main.FindName("BtnEnableSurfaceTabletMode");

            check(banner.Visibility == Visibility.Visible,
                "MainWindow displays one-time Surface tablet mode hint banner on Surface devices");
            check(Math.Abs(main.MinWidth - 840) < 1,
                $"MainWindow keeps desktop MinWidth=840 when TabletMode is Off (got {main.MinWidth:0.#})");
            check(Responsive.GetPosture(root) == LayoutPosture.Desktop,
                "MainWindow posture starts in Desktop when TabletMode is Off");
            check(SaveAndCheckNonEmpty(main, Path.Combine(artifacts, "surface-tablet-hint.png")),
                "Surface tablet hint banner renders non-empty pixels to surface-tablet-hint.png");

            typeof(ButtonBase).GetMethod("OnClick", Hidden)!.Invoke(enableBtn, null);
            Pump(150);

            check(Svc.Settings.TabletMode == TabletMode.Auto && Svc.Settings.SurfaceTabletHintDismissed,
                "clicking 立即开启 enables TabletMode.Auto and marks Surface hint dismissed");
            check(banner.Visibility == Visibility.Collapsed,
                "Surface hint banner hides immediately after enabling tablet mode");
            check(Math.Abs(main.MinWidth - 480) < 1,
                $"MainWindow relaxes MinWidth to 480 once TabletMode is enabled (got {main.MinWidth:0.#})");

            main.Width = 720;
            main.Height = 1060;
            Pump(250);
            check(Responsive.GetPosture(root) == LayoutPosture.TabletPortrait,
                $"resizing MainWindow to 720x1060 switches Responsive.Posture to TabletPortrait (got {Responsive.GetPosture(root)})");

            main.Width = 1200;
            main.Height = 760;
            Pump(250);
            check(Responsive.GetPosture(root) == LayoutPosture.Desktop,
                $"resizing MainWindow back to landscape 1200x760 returns Posture to Desktop (got {Responsive.GetPosture(root)})");

            Svc.Settings.SetTabletMode(TabletMode.Always);
            Pump(150);
            check(Responsive.GetPosture(root) == LayoutPosture.TabletPortrait,
                "TabletMode.Always forces TabletPortrait posture immediately on live MainWindow");

            Svc.Settings.SetTabletMode(TabletMode.Off);
            Pump(150);
            main.RefreshSurfaceTabletHint();
            check(Responsive.GetPosture(root) == LayoutPosture.Desktop && Math.Abs(main.MinWidth - 840) < 1,
                "TabletMode.Off restores Desktop posture and MinWidth=840");
            check(banner.Visibility == Visibility.Collapsed,
                "dismissed Surface hint never reappears even after returning TabletMode to Off");
        }
        finally
        {
            CloseMainForTest(main);
            Pump(150);
            DeviceProfile.SurfaceDetectorOverrideForTests = oldOverride;
            Svc.Settings.SurfaceTabletHintDismissed = oldDismissed;
            Svc.Settings.SetTabletMode(oldMode);
        }
    }

    private static void RunPhase1ShellAndEmbeddedPlayerTests(Action<bool, string> check, string artifacts)
    {
        check(CardWall.ColumnsFor(680, 0, LayoutPosture.TabletPortrait) == 2,
            "CardWall uses 2-column layout on narrow tablet portrait (680 DIP)");
        check(CardWall.ColumnsFor(853, 0, LayoutPosture.TabletPortrait) == 3,
            "CardWall uses 3-column layout on wider tablet portrait (853 DIP)");

        var oldMode = Svc.Settings.TabletMode;
        var oldDismissed = Svc.Settings.SurfaceTabletHintDismissed;
        var oldVmem = Svc.Settings.UseVmemRenderer;
        MainWindow? main = null;
        PlayerWindow? player = null;
        try
        {
            Svc.Settings.SetVmemRenderer(true);
            Svc.Settings.SurfaceTabletHintDismissed = true;
            Svc.Settings.SetTabletMode(TabletMode.Auto);

            main = new MainWindow { Width = 720, Height = 1080, ShowActivated = false };
            main.Show();
            Pump(350);

            var root = (Grid)main.FindName("RootGrid");
            var navRail = (Border)main.FindName("NavRail");
            var bottomNav = (Border)main.FindName("BottomNavBar");
            var tabletTop = (StackPanel)main.FindName("TabletTopActions");

            check(Responsive.GetPosture(root) == LayoutPosture.TabletPortrait,
                "720x1080 MainWindow enters TabletPortrait posture automatically");
            check(navRail.Visibility == Visibility.Collapsed && bottomNav.Visibility == Visibility.Visible,
                "TabletPortrait hides left NavRail and displays bottom navigation bar");
            check(tabletTop.Visibility == Visibility.Visible,
                "TabletPortrait surfaces compact message + theme actions in top bar");
            var bottomRadios = FindVisualChildren<RadioButton>(bottomNav).ToList();
            check(bottomRadios.Count == 4,
                $"BottomNavBar keeps the exact 4 desktop navigation items (got {bottomRadios.Count})");
            var homePage = FindVisualChildren<HomePage>(main).FirstOrDefault();
            var searchCard = homePage?.FindName("SearchCard") as FrameworkElement;
            if (searchCard != null)
            {
                var scRect = searchCard.TransformToAncestor(root).TransformBounds(new Rect(searchCard.RenderSize));
                var topRect = tabletTop.TransformToAncestor(root).TransformBounds(new Rect(tabletTop.RenderSize));
                check(scRect.Right <= topRect.Left - 4,
                    $"HomePage SearchCard does not overlap TabletTopActions (searchRight={scRect.Right:0.#}, topActionsLeft={topRect.Left:0.#})");
            }
            check(SaveAndCheckNonEmpty(main, Path.Combine(artifacts, "tablet-home-portrait.png")),
                "TabletPortrait home shell renders non-empty pixels to tablet-home-portrait.png");

            player = new PlayerWindow();
            var attached = player.AttachToMainWindow(main);
            Pump(300);

            var mainContentGrid = (Grid)main.FindName("MainContentGrid");
            check(attached && player.IsEmbeddedInMain && main.IsEmbeddedPlayerVisible && !player.IsVisible,
                "PlayerWindow mounts directly inside MainWindow without opening a second visible window");
            check(mainContentGrid.Visibility == Visibility.Collapsed && bottomNav.Visibility == Visibility.Collapsed,
                "mounting embedded player hides underlying MainContentGrid & BottomNavBar to prevent bleed-through");
            check(player.IsTabletPortraitLayout,
                "embedded PlayerWindow automatically mounts Android 3-tier TabletPortrait layout");

            var surface = (Grid)player.FindName("VideoSurface");
            var infoPanel = (Grid)player.FindName("InfoPanel");
            var stage = (Grid)player.FindName("PlayerStage");
            var backBtn = (Button)player.FindName("BtnEmbeddedBack");
            var rows = Enumerable.Range(1, 24)
                .Select(i => new CommentItem { Rpid = i, UserName = $"平板用户 {i}", Content = "安卓竖屏三段式布局测试评论，支持上视频下简介/评论及沉浸抽屉。", LikeCount = i * 2, RcTime = "2026-11-19" })
                .ToArray();
            ((ItemsControl)player.FindName("CommentList")).ItemsSource = rows;
            ((TextBlock)player.FindName("UpNameText")).Text = "BiliTouch 平板适配";
            ((TextBlock)player.FindName("VideoTitleText")).Text = "单窗口内嵌安卓竖屏播放页测试";
            ((TextBlock)player.FindName("DescText")).Text = "顶部吸顶播放，下方简介与评论支持横滑切换。";

            player.UpdateDetectedVideoSize(1920, 1080, fromMetadata: true);
            Pump(250);
            var sRect = surface.TransformToAncestor(stage).TransformBounds(new Rect(surface.RenderSize));
            var iRect = infoPanel.TransformToAncestor(stage).TransformBounds(new Rect(infoPanel.RenderSize));
            var tabBar = (FrameworkElement)player.FindName("ContentTabBar");
            var upHeader = (FrameworkElement)player.FindName("UpHeader");
            var actionBar = (FrameworkElement)player.FindName("ActionBar");
            var introPanel = (Panel)player.FindName("IntroPanel");
            var commentsPanel = (FrameworkElement)player.FindName("CommentsPanel");
            var tabRect = tabBar.TransformToAncestor(stage).TransformBounds(new Rect(tabBar.RenderSize));
            var upRect = upHeader.TransformToAncestor(stage).TransformBounds(new Rect(upHeader.RenderSize));
            check(backBtn.Visibility == Visibility.Visible,
                "embedded tablet player shows top-left back button");
            check(Math.Abs(sRect.Width - stage.ActualWidth) < 2 && Math.Abs(sRect.Height - stage.ActualWidth * 9 / 16) < 4,
                $"landscape video fills full width at 16:9 on top ({sRect.Width:0.#}x{sRect.Height:0.#})");
            check(iRect.Top >= sRect.Bottom - 2 && Math.Abs(iRect.Width - stage.ActualWidth) < 2,
                $"InfoPanel sits full-width below the top video (videoBottom={sRect.Bottom:0.#}, infoTop={iRect.Top:0.#})");
            check(tabRect.Top <= iRect.Top + 12 && upRect.Top >= tabRect.Bottom - 2 && introPanel.Children.Contains(actionBar),
                $"Android layout pins 简介|评论 directly under video and hosts UP/title/actions inside IntroPanel (tabBottom={tabRect.Bottom:0.#}, upTop={upRect.Top:0.#})");
            check(SaveAndCheckNonEmpty(main, Path.Combine(artifacts, "tablet-player-landscape-video.png")),
                "embedded tablet player (landscape video intro tab) renders non-empty pixels");

            // Switch to 评论 tab: UP/title/actions hide with IntroPanel, comments start right under 简介|评论
            ((RadioButton)player.FindName("TabComments")).IsChecked = true;
            Pump(200);
            var cmtRect = commentsPanel.TransformToAncestor(stage).TransformBounds(new Rect(commentsPanel.RenderSize));
            check(!upHeader.IsVisible && cmtRect.Top <= tabRect.Bottom + 24,
                $"评论 tab hides UP/title/actions and starts comments right under 简介|评论 (tabBottom={tabRect.Bottom:0.#}, commentsTop={cmtRect.Top:0.#})");
            check(SaveAndCheckNonEmpty(main, Path.Combine(artifacts, "tablet-player-comments-tab.png")),
                "embedded tablet player (comments tab) renders non-empty pixels");
            ((RadioButton)player.FindName("TabIntro")).IsChecked = true;
            Pump(150);

            player.UpdateDetectedVideoSize(1080, 1920, fromMetadata: true);
            Pump(250);
            var tallRect = surface.TransformToAncestor(stage).TransformBounds(new Rect(surface.RenderSize));
            check(tallRect.Height > sRect.Height + 40,
                $"portrait video expands top viewport vertically ({tallRect.Height:0.#} > {sRect.Height:0.#})");
            check(SaveAndCheckNonEmpty(main, Path.Combine(artifacts, "tablet-player-portrait-video.png")),
                "embedded tablet player (portrait video) renders non-empty pixels");

            ((RadioButton)player.FindName("TabComments")).IsChecked = true;
            Pump(150);
            var infoScroll = (ScrollViewer)player.FindName("InfoScroll");
            infoScroll.ScrollToVerticalOffset(150);
            Pump(250);
            var compactRect = surface.TransformToAncestor(stage).TransformBounds(new Rect(surface.RenderSize));
            check(compactRect.Height < tallRect.Height - 80,
                $"scrolling comments collapses tall portrait video compactly ({tallRect.Height:0.#} -> {compactRect.Height:0.#})");

            typeof(BiliTouch.Views.PlayerWindow).GetMethod("ToggleFullscreen", Hidden)!.Invoke(player, null);
            Pump(250);
            var drawerBtn = (Button)player.FindName("BtnPortraitImmersiveComments");
            var drawerOverlay = (Grid)player.FindName("PortraitCommentDrawerOverlay");
            check(drawerBtn.Visibility == Visibility.Visible,
                "portrait immersive fullscreen surfaces floating comment drawer button");
            player.SetPortraitCommentDrawerOpen(true);
            Pump(250);
            var controlBar = (FrameworkElement)player.FindName("ControlBar");
            check(drawerOverlay.Visibility == Visibility.Visible && controlBar.Visibility == Visibility.Collapsed,
                "opening comment drawer displays opaque 65%-height bottom sheet and hides ControlBar");
            check(SaveAndCheckNonEmpty(main, Path.Combine(artifacts, "tablet-player-portrait-drawer.png")),
                "portrait immersive comment drawer renders non-empty pixels");

            check(player.HandleEmbeddedBack() && drawerOverlay.Visibility == Visibility.Collapsed,
                "1st Back closes portrait comment drawer");
            check(player.HandleEmbeddedBack() && drawerBtn.Visibility == Visibility.Collapsed && player.IsTabletPortraitLayout,
                "2nd Back exits fullscreen back to tablet portrait player");

            main.Width = 1200;
            main.Height = 760;
            Pump(300);
            check(!player.IsTabletPortraitLayout && player.IsEmbeddedInMain && surface.ActualWidth > 200 && surface.ActualHeight > 200,
                "rotating to landscape mid-playback smoothly exits TabletPortrait without breaking the video surface");

            check(player.HandleEmbeddedBack() && !player.IsEmbeddedInMain && !main.IsEmbeddedPlayerVisible,
                "final Back detaches embedded player and returns cleanly to MainWindow feed");
        }
        finally
        {
            player?.DetachFromMainWindow(pausePlayback: true);
            player?.Close();
            CloseMainForTest(main);
            Pump(200);
            Svc.Settings.SetVmemRenderer(oldVmem);
            Svc.Settings.SurfaceTabletHintDismissed = oldDismissed;
            Svc.Settings.SetTabletMode(oldMode);
        }
    }

    private static void RunPhase2CommunityAndLiveTests(Action<bool, string> check, string artifacts)
    {
        var oldMode = Svc.Settings.TabletMode;
        var oldDismissed = Svc.Settings.SurfaceTabletHintDismissed;
        MainWindow? main = null;
        LiveRoomWindow? live = null;
        Window? pageHostWin = null;
        var bindingErrors = new List<string>();
        var listener = new BindingTraceCollector(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            Svc.Settings.SurfaceTabletHintDismissed = true;
            Svc.Settings.SetTabletMode(TabletMode.Always);

            var followVm = new FollowViewModel();
            followVm.Users.Add(new FollowUser { Mid = 101, Name = "测试UP一", NewCount = 3 });
            followVm.Users.Add(new FollowUser { Mid = 102, Name = "测试UP二", NewCount = 0 });
            followVm.Users.Add(new FollowUser { Mid = 103, Name = "测试UP三", NewCount = 1 });
            followVm.Dynamics.Add(new DynamicItem
            {
                Id = "dyn-1",
                AuthorName = "测试UP一",
                PubTimeText = "刚刚",
                Text = "平板竖屏动态流测试：顶部为横向 UP 头像条，下方为全宽单列动态流。"
            });
            var followPage = new FollowPage { DataContext = followVm };
            var followGrid = new Grid();
            Responsive.SetSource(followGrid, true);
            followGrid.Children.Add(followPage);
            pageHostWin = new Window { Width = 720, Height = 1060, Content = followGrid, ShowActivated = false };
            pageHostWin.Show();
            ((Grid)followPage.FindName("FollowMainGrid")).Visibility = Visibility.Visible;
            Pump(250);

            var contactsScroll = (FrameworkElement)followPage.FindName("ContactsScroll");
            var tabletStrip = (FrameworkElement)followPage.FindName("TabletUpStrip");
            var feedBody = (FrameworkElement)followPage.FindName("FollowFeedBody");
            check(contactsScroll.Visibility == Visibility.Collapsed && tabletStrip.Visibility == Visibility.Visible,
                "FollowPage hides 252px left column and displays horizontal TabletUpStrip in TabletPortrait");
            check(feedBody.ActualWidth > 660,
                $"FollowFeedBody expands to full width in TabletPortrait (actual={feedBody.ActualWidth:0.#})");
            check(SaveAndCheckNonEmpty(pageHostWin, Path.Combine(artifacts, "tablet-follow-portrait.png")),
                "FollowPage tablet portrait renders non-empty pixels to tablet-follow-portrait.png");

            var detailItem = new DynamicItem
            {
                Id = "dyn-detail-1",
                AuthorName = "BiliTouch 测试",
                PubTimeText = "10 分钟前",
                Text = "动态详情页在平板竖屏下由左右分栏自动切换为上下单列流：正文在上、评论与输入框在下。"
            };
            var detailPage = new DynamicDetailPage(detailItem);
            detailPage.ViewModel.Comments.Add(new CommentItem
            {
                Rpid = 9001,
                UserName = "平板访客",
                Content = "上下单列布局在 720px 竖屏上清晰无挤压。",
                LikeCount = 18,
                RcTime = "刚刚"
            });
            var detailGrid = new Grid();
            Responsive.SetSource(detailGrid, true);
            detailGrid.Children.Add(detailPage);
            pageHostWin.Content = detailGrid;
            Pump(250);

            var bodyScroll = (FrameworkElement)detailPage.FindName("DynamicBodyScroll");
            var commentsPane = (FrameworkElement)detailPage.FindName("CommentsPane");
            var bodyRect = bodyScroll.TransformToAncestor(detailGrid).TransformBounds(new Rect(bodyScroll.RenderSize));
            var commentsRect = commentsPane.TransformToAncestor(detailGrid).TransformBounds(new Rect(commentsPane.RenderSize));
            check(commentsRect.Top >= bodyRect.Bottom - 2 && Math.Abs(commentsRect.Width - bodyRect.Width) < 4,
                $"DynamicDetailPage stacks comments below body in TabletPortrait (bodyBottom={bodyRect.Bottom:0.#}, commentsTop={commentsRect.Top:0.#})");
            check(SaveAndCheckNonEmpty(pageHostWin, Path.Combine(artifacts, "tablet-dynamic-detail-portrait.png")),
                "DynamicDetailPage tablet portrait renders non-empty pixels to tablet-dynamic-detail-portrait.png");

            var overview = new MineOverviewPage();
            SetProp(overview.ViewModel, "IsLoggedIn", true);
            SetProp(overview.ViewModel, "Name", "平板体验官");
            SetProp(overview.ViewModel, "Mid", 123456L);
            SetProp(overview.ViewModel, "Sign", "Windows 平板竖屏适配中，紧凑边距与自适应个人头部卡片。");
            SetProp(overview.ViewModel, "FollowingText", "128");
            SetProp(overview.ViewModel, "FollowersText", "2.4万");
            SetProp(overview.ViewModel, "VideoCountText", "36");
            var mineGrid = new Grid();
            Responsive.SetSource(mineGrid, true);
            mineGrid.Children.Add(overview);
            pageHostWin.Content = mineGrid;
            Pump(250);

            var overviewStack = (StackPanel)overview.FindName("OverviewStack");
            var overviewAvatar = (Border)overview.FindName("ProfileAvatarBox");
            check(Math.Abs(overviewStack.Margin.Left - 16) < 1 && Math.Abs(overviewAvatar.Width - 64) < 1,
                "MineOverviewPage tightens outer margin to 16 DIP and scales avatar to 64 DIP in TabletPortrait");
            check(SaveAndCheckNonEmpty(pageHostWin, Path.Combine(artifacts, "tablet-mine-portrait.png")),
                "MineOverviewPage tablet portrait renders non-empty pixels to tablet-mine-portrait.png");

            var spaceVm = new UserSpaceViewModel();
            SetProp(spaceVm, "Name", "测试UP主");
            SetProp(spaceVm, "Mid", 654321L);
            SetProp(spaceVm, "Sign", "简介文本测试");
            SetProp(spaceVm, "Level", 6);
            SetProp(spaceVm, "FollowerText", "5.2万");
            SetProp(spaceVm, "FollowingText", "88");
            SetProp(spaceVm, "VideoCountText", "142");
            SetProp(spaceVm, "Loading", false);
            var spacePage = new UserSpacePage { DataContext = spaceVm };
            var spaceGrid = new Grid();
            Responsive.SetSource(spaceGrid, true);
            spaceGrid.Children.Add(spacePage);
            pageHostWin.Content = spaceGrid;
            Pump(200);
            var spaceHeader = (Grid)spacePage.FindName("SpaceHeaderGrid");
            var spaceAvatar = (Border)spacePage.FindName("SpaceAvatarBox");
            check(Math.Abs(spaceHeader.Margin.Left - 16) < 1 && Math.Abs(spaceAvatar.Width - 56) < 1,
                "UserSpacePage tightens margins to 16 DIP and scales avatar to 56 DIP in TabletPortrait");

            pageHostWin.Close();
            pageHostWin = null;

            Svc.Settings.SetTabletMode(TabletMode.Auto);
            main = new MainWindow { Width = 720, Height = 1080, ShowActivated = false };
            main.Show();
            Pump(300);

            live = new LiveRoomWindow();
            live.Model.Title = "平板竖屏单窗口内嵌直播间测试";
            live.Model.Anchor = "BiliTouch 主播";
            live.Model.Area = "科技 · 软件";
            live.Model.Watched = "1.2万人在看";
            live.Model.Messages.Add(new LiveChatEntry { User = "观众A", Text = "竖屏上下堆叠弹幕区测试", Kind = LiveChatKind.Danmaku });
            live.Model.Messages.Add(new LiveChatEntry { User = "观众B", Text = "醒目留言测试卡片", Kind = LiveChatKind.SuperChat });
            var liveAttached = live.AttachToMainWindow(main);
            Pump(300);

            var liveVideoPanel = (Grid)live.FindName("VideoPanel");
            var liveChatPanel = (Border)live.FindName("ChatPanel");
            var liveRoomLayout = (Grid)live.FindName("RoomLayout");
            var liveBackBtn = (Button)live.FindName("BtnLiveEmbeddedBack");
            var liveTitleRoot = (Grid)live.FindName("Root");
            var lvRect = liveVideoPanel.TransformToAncestor(liveRoomLayout).TransformBounds(new Rect(liveVideoPanel.RenderSize));
            var lcRect = liveChatPanel.TransformToAncestor(liveRoomLayout).TransformBounds(new Rect(liveChatPanel.RenderSize));

            check(liveAttached && live.IsEmbeddedInMain && main.IsEmbeddedPlayerVisible && !live.IsVisible,
                "LiveRoomWindow embeds inside MainWindow in TabletPortrait without opening a second window");
            check(ReferenceEquals(liveTitleRoot.DataContext, live.Model),
                "embedded LiveRoomWindow preserves LiveRoomViewModel DataContext on Root");
            check(live.IsTabletStackedLayout && liveBackBtn.Visibility == Visibility.Visible,
                "embedded LiveRoomWindow enters stacked portrait layout with top-left back button");
            check(lcRect.Top >= lvRect.Bottom - 2 && Math.Abs(lcRect.Width - liveRoomLayout.ActualWidth) < 2,
                $"LiveRoomWindow stacks ChatPanel full-width below 16:9 VideoPanel (videoBottom={lvRect.Bottom:0.#}, chatTop={lcRect.Top:0.#})");
            check(SaveAndCheckNonEmpty(main, Path.Combine(artifacts, "tablet-live-portrait.png")),
                "embedded tablet LiveRoomWindow renders non-empty pixels to tablet-live-portrait.png");

            check(live.HandleEmbeddedBack() && !live.IsEmbeddedInMain && !main.IsEmbeddedPlayerVisible,
                "LiveRoomWindow HandleEmbeddedBack detaches cleanly back to MainWindow");
            check(bindingErrors.Count == 0,
                $"Phase 2 tablet portrait views produce zero WPF binding errors: {string.Join(" | ", bindingErrors)}");
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            try { pageHostWin?.Close(); } catch { }
            live?.DetachFromMainWindow(stopPlayback: true);
            live?.Close();
            CloseMainForTest(main);
            Pump(200);
            Svc.Settings.SurfaceTabletHintDismissed = oldDismissed;
            Svc.Settings.SetTabletMode(oldMode);
        }
    }

    private static void RunPhase3LongTailPagesTests(Action<bool, string> check, string artifacts)
    {
        var oldMode = Svc.Settings.TabletMode;
        Window? win = null;
        var bindingErrors = new List<string>();
        var listener = new BindingTraceCollector(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            Svc.Settings.SetTabletMode(TabletMode.Always);

            var msgVm = new MessagesViewModel();
            var session1 = new MsgSession { TalkerId = 10001, Name = "B 站小助手", Preview = "欢迎体验平板竖屏私信两级导航", UnreadCount = 2 };
            var session2 = new MsgSession { TalkerId = 10002, Name = "测试好友", Preview = "晚上一起看直播吗？", UnreadCount = 0 };
            msgVm.Sessions.Add(session1);
            msgVm.Sessions.Add(session2);
            msgVm.Messages.Add(new MsgItem { Text = "你好！这是平板竖屏全宽私信对话页测试。", TimeText = "12:00", IsMine = false });
            msgVm.Messages.Add(new MsgItem { Text = "点击左上角「← 会话」可返回会话列表。", TimeText = "12:01", IsMine = true });
            SetProp(msgVm, "Current", session1);
            SetProp(msgVm, "CurrentTitle", session1.DisplayName);

            var msgPage = new MessagesPage { DataContext = msgVm };
            var hostGrid = new Grid();
            Responsive.SetSource(hostGrid, true);
            hostGrid.Children.Add(msgPage);
            win = new Window { Width = 720, Height = 1060, Content = hostGrid, ShowActivated = false };
            win.Show();
            Pump(250);

            var sessionScroll = (ScrollViewer)msgPage.FindName("SessionScroll");
            var threadPane = (Grid)msgPage.FindName("PrivateThreadPane");
            var backBtn = (Button)msgPage.FindName("BtnPrivateBack");

            check(sessionScroll.Visibility == Visibility.Visible && threadPane.Visibility == Visibility.Collapsed,
                "MessagesPage TabletPortrait starts in full-width session list level");
            check(SaveAndCheckNonEmpty(win, Path.Combine(artifacts, "tablet-messages-list.png")),
                "MessagesPage tablet session list renders non-empty pixels");

            var openSession = typeof(MessagesPage).GetMethod("OpenSessionAsync", Hidden)!;
            _ = (Task)openSession.Invoke(msgPage, new object[] { session1 })!;
            Pump(250);

            check(msgPage.IsTabletShowingThread && sessionScroll.Visibility == Visibility.Collapsed && threadPane.Visibility == Visibility.Visible && backBtn.Visibility == Visibility.Visible,
                "selecting a session drills down to full-width thread with back button");
            check(SaveAndCheckNonEmpty(win, Path.Combine(artifacts, "tablet-messages-thread.png")),
                "MessagesPage tablet thread renders non-empty pixels");

            check(msgPage.TryGoBackTabletThread() && !msgPage.IsTabletShowingThread && sessionScroll.Visibility == Visibility.Visible,
                "TryGoBackTabletThread returns cleanly to full-width session list");

            var settingsPage = new SettingsPage { DataContext = new SettingsViewModel() };
            var settingsGrid = new Grid();
            Responsive.SetSource(settingsGrid, true);
            settingsGrid.Children.Add(settingsPage);
            win.Content = settingsGrid;
            Pump(200);
            var settingsStack = (StackPanel)settingsPage.FindName("SettingsContentStack");
            check(Math.Abs(settingsStack.Margin.Left - 14) < 1,
                $"SettingsPage tightens content margin to 14 DIP in TabletPortrait (got {settingsStack.Margin.Left:0.#})");

            var favPage = new FavoritesPage { DataContext = new FavoritesViewModel() };
            var favGrid = new Grid();
            Responsive.SetSource(favGrid, true);
            favGrid.Children.Add(favPage);
            win.Content = favGrid;
            Pump(200);
            var favHeader = (Grid)favPage.FindName("FavHeaderGrid");
            var favScroll = (ScrollViewer)favPage.FindName("FavListScroll");
            check(Math.Abs(favHeader.Margin.Left - 16) < 1 && Math.Abs(favScroll.Margin.Left - 12) < 1,
                "FavoritesPage tightens header/list margins in TabletPortrait");

            var cachePage = new CachePage { DataContext = new CacheViewModel() };
            var cacheGrid = new Grid();
            Responsive.SetSource(cacheGrid, true);
            cacheGrid.Children.Add(cachePage);
            win.Content = cacheGrid;
            Pump(200);
            var cacheHeader = (Grid)cachePage.FindName("CacheHeaderGrid");
            var groupCols = FindVisualChildren<UniformGrid>(cachePage).FirstOrDefault();
            check(Math.Abs(cacheHeader.Margin.Left - 16) < 1 && (groupCols == null || groupCols.Columns == 1),
                $"CachePage tightens margins and switches to 1-column cards in 720px TabletPortrait (cols={groupCols?.Columns})");
            check(SaveAndCheckNonEmpty(win, Path.Combine(artifacts, "tablet-cache-portrait.png")),
                "CachePage tablet portrait renders non-empty pixels");

            var searchPage = new SearchPage { DataContext = new SearchViewModel() };
            var searchGrid = new Grid();
            Responsive.SetSource(searchGrid, true);
            searchGrid.Children.Add(searchPage);
            win.Content = searchGrid;
            Pump(200);
            var searchCard = (FrameworkElement)searchPage.FindName("SearchCard");
            var searchKindTabs = (FrameworkElement)searchPage.FindName("SearchKindTabs");
            check(searchCard.HorizontalAlignment == HorizontalAlignment.Left && searchCard.Width <= 480 && Math.Abs(searchKindTabs.Margin.Left - 12) < 1,
                $"SearchPage aligns SearchCard left (w={searchCard.Width:0.#}) and tightens SearchKindTabs margin in TabletPortrait");
            check(SaveAndCheckNonEmpty(win, Path.Combine(artifacts, "tablet-search-portrait.png")),
                "SearchPage tablet portrait renders non-empty pixels");

            check(bindingErrors.Count == 0,
                $"Phase 3 tablet portrait pages produce zero WPF binding errors: {string.Join(" | ", bindingErrors)}");
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            try { win?.Close(); } catch { }
            Pump(150);
            Svc.Settings.SetTabletMode(oldMode);
        }
    }

    private static void CloseMainForTest(MainWindow? main)
    {
        if (main == null) return;
        var closeField = typeof(App).GetField("<IsExiting>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        var prevExit = closeField != null && (bool)closeField.GetValue(null)!;
        closeField?.SetValue(null, true);
        try { main.Close(); } catch { }
        closeField?.SetValue(null, prevExit);
    }

    private static void SetProp(object target, string propertyName, object? value)
    {
        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop?.SetMethod != null) prop.SetValue(target, value);
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
