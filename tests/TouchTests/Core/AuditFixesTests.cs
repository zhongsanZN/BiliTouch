using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Utils;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Pages;

/// <summary>
/// 全仓审计与重构回归测试（--audit-fixes）：
/// 覆盖本次修复的高危 Bug、防退化不变量与新引入的统一基元（UserStateBus / IHorizontalSwipeHost / AtomicFile / VisualTreeUtil）。
/// 全离线、确定性、无网络依赖。
/// </summary>
internal static class AuditFixesTests
{
    private const BindingFlags AnyPrivate = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Run(Action<bool, string> check)
    {
        TestRateLimiterCooldownNoStampede(check);
        TestWebView2CleanerScriptSyntax(check);
        TestUrlParserSpaceUid(check);
        TestVisualTreeUtilContentElement(check);
        TestAtomicFileNoTempLeaks(check);
        TestUserStateBusSynchronization(check);
        TestWatchLaterDeletionKeepsPagination(check);
        TestUserSpaceTabSwitchPreservesVideoHasMore(check);
        TestHomeViewModelHotLoadingPropertyChanged(check);
        TestPlayerWindowResetCoverageAndBusyFlags(check);
        TestLiveRoomMutePreservation(check);
        TestHorizontalSwipeHostContract(check);
        TestPrecisionTouchpadContactMotion(check);
        TestNativeTouchFeedbackFollowsWindowSource(check);
        TestHomePageTouchSwipePointerIsolation(check);
        TestDashRemuxerCttsLeadingZeros(check);
        TestDynamicLikeIconStability(check);
        TestVideoTailEndingAndElstPatch(check);
        TestOfflineOnlineCommentsAndDanmakuContract(check);
        TestAudioQualityBitrateAndFreezeFixes(check);
        TestSeekAudioSubtitlesAndRightKeyFixes(check);
        TestSeamlessPortraitAndEnhancementInQualityPopup(check);
        TestMacroArchitectureAndRuntimeHealth(check);
    }

    private static void TestNativeTouchFeedbackFollowsWindowSource(Action<bool, string> check)
    {
        const uint contactVisualization = 0x2018;
        const uint touchContactFeedback = 1;
        const int settingChanged = 0x001A;
        check(SystemParametersInfo(contactVisualization, 0, out var setting, 0),
            "Windows contact visualization preference is readable");
        int expected = setting != 0 ? 1 : 0;

        var surface = new Border();
        NativeTouchFeedback.FollowSource(surface);
        var window = new FluentWindow
        {
            Content = surface, Width = 100, Height = 100, ShowInTaskbar = false, ShowActivated = false
        };
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Width = 100, Height = 100,
            ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            CheckFeedback(hwnd, "main window uses the Windows contact preference");

            window.Show();
            window.Content = null;
            overlay.Content = surface;
            overlay.Show();
            overlay.UpdateLayout();
            var overlayHwnd = new WindowInteropHelper(overlay).Handle;
            CheckFeedback(overlayHwnd, "moving the touch surface applies the preference to the overlay HWND");

            int opposite = 1 - expected;
            check(SetWindowFeedbackSetting(overlayHwnd, touchContactFeedback, 0, sizeof(int), in opposite),
                "fixture can change the overlay's per-window contact flag");
            SendMessage(overlayHwnd, settingChanged, IntPtr.Zero, IntPtr.Zero);
            CheckFeedback(overlayHwnd, "an open overlay resyncs its contact preference on WM_SETTINGCHANGE");
        }
        finally
        {
            overlay.Close();
            window.Close();
        }

        void CheckFeedback(IntPtr hwnd, string message)
        {
            uint size = sizeof(int);
            bool configured = GetWindowFeedbackSetting(hwnd, touchContactFeedback, 0, ref size, out var actual);
            check(configured && actual == expected, message);
        }
    }

    private static void TestHomePageTouchSwipePointerIsolation(Action<bool, string> check)
    {
        // 1. 静态与 IL 契约：WM_POINTER 下 GetStylusPoints 读取未旋转原始 HID 坐标，
        //    HomePage.RootPoint(StylusEventArgs) 必须走 GetPosition(null) 且不得调用 GetStylusPoints。
        var stylusRootPoint = typeof(HomePage).GetMethod(
            "RootPoint",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(StylusEventArgs) },
            modifiers: null);
        check(stylusRootPoint != null, "HomePage.RootPoint(StylusEventArgs) exists");
        if (stylusRootPoint != null)
        {
            var il = stylusRootPoint.GetMethodBody()!.GetILAsByteArray()!;
            var calledMethods = new List<string>();
            var module = stylusRootPoint.Module;
            for (int i = 0; i <= il.Length - 5; i++)
            {
                if (il[i] is 0x28 or 0x6F) // call / callvirt
                {
                    int token = BitConverter.ToInt32(il, i + 1);
                    try
                    {
                        if (module.ResolveMember(token) is MethodBase mb)
                            calledMethods.Add(mb.Name);
                    }
                    catch { }
                }
            }
            check(!calledMethods.Contains("GetStylusPoints") && calledMethods.Contains("GetPosition"),
                $"HomePage.RootPoint(StylusEventArgs) uses GetPosition instead of raw GetStylusPoints (calls={string.Join(",", calledMethods)})");
        }

        var isTouchStylus = typeof(HomePage).GetMethod("IsTouchStylus", BindingFlags.Static | BindingFlags.NonPublic);
        check(isTouchStylus != null, "HomePage filters TabletDeviceType.Touch out of PreviewStylus* handlers");

        // 2. 真实窗口路由验证：竖滑不切页且不吞 PreviewTouchUp、横滑正常切页不锁推荐、二指防串扰、脱靶自愈。
        var home = new HomePage { DataContext = new HomeViewModel() };
        var win = new FluentWindow
        {
            Content = home,
            Width = 960,
            Height = 720,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        void Until(Func<bool> ready, int timeoutMs = 1500)
        {
            var start = Environment.TickCount64;
            while (!ready() && Environment.TickCount64 - start < timeoutMs) Pump(20);
        }
        try
        {
            win.Show();
            Pump(120);
            var popular = (RadioButton)home.FindName("PopularTab")!;
            var host = (FrameworkElement)home.FindName("TabHost")!;
            var popularScroll = (ScrollViewer)home.FindName("PopularScroll")!;
            popular.IsChecked = true;
            Pump(100);
            check(home.CurrentTabIndex == 1, "HomePage test fixture starts on Popular tab (index 1)");

            bool? lastTouchUpHandled = null;
            host.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>((_, e) => lastTouchUpHandled = e.Handled), true);
            var tabTransField = typeof(HomePage).GetField("_tabTrans", AnyPrivate)!;
            double CurrentX() => (tabTransField.GetValue(home) as TranslateTransform)?.X ?? 0;

            // 2a. 竖向滑动（手指上划浏览列表，dy < 0 不触发下拉刷新）：不进入横滑、不冻结滚动、抬手不吞 PreviewTouchUp
            var fVert = new SyntheticTouchFinger(host);
            fVert.Down(host.ActualWidth * 0.5, 320);
            fVert.Move(host.ActualWidth * 0.5 + 4, 140);
            Pump(20);
            check(Math.Abs(CurrentX()) < 0.5 && home.CurrentTabIndex == 1 && popularScroll.PanningMode != PanningMode.None,
                $"vertical touch swipe keeps vertical scroll intact (x={CurrentX():F1}, tab={home.CurrentTabIndex}, pan={popularScroll.PanningMode})");
            lastTouchUpHandled = null;
            fVert.Up(host.ActualWidth * 0.5 + 4, 120);
            Pump(60);
            check(lastTouchUpHandled == false && home.CurrentTabIndex == 1,
                $"vertical touch release leaves PreviewTouchUp unhandled for ScrollViewer/ModuleGestures (handled={lastTouchUpHandled}, tab={home.CurrentTabIndex})");

            // 2b. 横向滑动：从热门左滑到排行榜（而非锁死推荐），抬手正确消费并切页，再右滑回热门
            var fHoriz = new SyntheticTouchFinger(host);
            fHoriz.Down(host.ActualWidth * 0.85, 160);
            fHoriz.Move(host.ActualWidth * 0.20, 164);
            Pump(20);
            check(CurrentX() < -host.ActualWidth * 0.4,
                $"horizontal left swipe tracks finger displacement (x={CurrentX():F1})");
            lastTouchUpHandled = null;
            fHoriz.Up(host.ActualWidth * 0.20, 164);
            Until(() => home.CurrentTabIndex == 2 && Math.Abs(CurrentX()) < 1);
            check(lastTouchUpHandled == true && home.CurrentTabIndex == 2,
                $"horizontal left swipe commits to Ranking tab without locking Recommend (handled={lastTouchUpHandled}, tab={home.CurrentTabIndex})");

            var fBack = new SyntheticTouchFinger(host);
            fBack.Down(host.ActualWidth * 0.15, 160);
            fBack.Move(host.ActualWidth * 0.80, 162);
            Pump(20);
            fBack.Up(host.ActualWidth * 0.80, 162);
            Until(() => home.CurrentTabIndex == 1 && Math.Abs(CurrentX()) < 1);
            check(home.CurrentTabIndex == 1,
                $"horizontal right swipe returns cleanly to Popular tab (tab={home.CurrentTabIndex})");

            // 2c. 双指/掌根防串扰：主指竖滑期间第二指横滑不会劫持或跳页
            var fPrimary = new SyntheticTouchFinger(host);
            var fPalm = new SyntheticTouchFinger(host);
            fPrimary.Down(host.ActualWidth * 0.35, 320);
            fPrimary.Move(host.ActualWidth * 0.35, 200);
            fPalm.Down(host.ActualWidth * 0.90, 200);
            fPalm.Move(host.ActualWidth * 0.10, 200);
            fPalm.Up(host.ActualWidth * 0.10, 200);
            Pump(30);
            check(Math.Abs(CurrentX()) < 0.5 && home.CurrentTabIndex == 1,
                $"secondary touch contact cannot hijack primary vertical gesture (x={CurrentX():F1}, tab={home.CurrentTabIndex})");
            fPrimary.Up(host.ActualWidth * 0.35, 180);
            Pump(60);

            // 2d. 脱靶自愈：模拟父级 ModuleGestures 捕获下拉刷新导致 TabHost 收不到 PreviewTouchUp，下一次新触摸仍以新起点起算
            var fPull = new SyntheticTouchFinger(host);
            fPull.Down(host.ActualWidth * 0.85, 120);
            home.CaptureTouch(fPull); // 后续 Move/Up 路由到父级 HomePage，不再进入 TabHost
            fPull.Up(host.ActualWidth * 0.85, 260);
            Pump(40);

            var fNext = new SyntheticTouchFinger(host);
            fNext.Down(host.ActualWidth * 0.15, 320);
            fNext.Move(host.ActualWidth * 0.15, 160);
            Pump(20);
            check(Math.Abs(CurrentX()) < 0.5 && home.CurrentTabIndex == 1,
                $"new touch after off-host capture self-heals instead of diffing stale origin (x={CurrentX():F1}, tab={home.CurrentTabIndex})");
            fNext.Up(host.ActualWidth * 0.15, 160);
            Pump(40);
        }
        finally
        {
            win.Close();
        }
    }

    private sealed class SyntheticTouchFinger : TouchDevice
    {
        private static int _nextId = 19000;
        private readonly FrameworkElement _target;
        private readonly Visual _root;
        private Point _point;
        private TouchAction _action;

        public SyntheticTouchFinger(FrameworkElement target) : base(++_nextId)
        {
            _target = target;
            var source = PresentationSource.FromVisual(target)!;
            _root = source.RootVisual;
            SetActiveSource(source);
        }

        public override TouchPoint GetTouchPoint(IInputElement? relativeTo)
        {
            var p = relativeTo is Visual v ? _root.TransformToDescendant(v).Transform(_point) : _point;
            return new TouchPoint(this, p, new Rect(p, new Size(4, 4)), _action);
        }

        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo) =>
            new() { GetTouchPoint(relativeTo) };

        private void At(double x, double y, TouchAction action)
        {
            _point = _target.TranslatePoint(new Point(x, y), (UIElement)_root);
            _action = action;
        }

        public void Down(double x, double y) { At(x, y, TouchAction.Down); Activate(); ReportDown(); }
        public void Move(double x, double y) { At(x, y, TouchAction.Move); ReportMove(); }
        public void Up(double x, double y) { At(x, y, TouchAction.Up); ReportUp(); Deactivate(); }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowFeedbackSetting(IntPtr hwnd, uint feedback, uint flags, ref uint size, out int enabled);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowFeedbackSetting(IntPtr hwnd, uint feedback, uint flags, uint size, in int enabled);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static void TestRateLimiterCooldownNoStampede(Action<bool, string> check)
    {
        var limiter = new ApiRateLimiter(burst: 10, perSecond: 20);
        // 耗尽桶内令牌
        for (var i = 0; i < 10; i++) limiter.WaitAsync().GetAwaiter().GetResult();
        // 触发 250ms 冷静期；旧代码在冷静期结束后会把整段冷静期折算进令牌补充，导致瞬间攒出满桶踩踏
        limiter.Penalize(TimeSpan.FromMilliseconds(250));
        check(limiter.IsPenalized, "rate limiter enters penalty cooldown");
        Thread.Sleep(290);
        check(!limiter.IsPenalized, "rate limiter exits penalty cooldown after window");
        // 刚刚解封 ~40ms，速率 20/s 时令牌应远小于满桶 10（最多约 1~3 个，绝不该因冷静期攒满）
        check(limiter.AvailableTokens < 5.0,
            $"cooldown does not refill full burst tokens (available={limiter.AvailableTokens:F2})");
    }

    private static void TestWebView2CleanerScriptSyntax(Action<bool, string> check)
    {
        var field = typeof(WebView2Service).GetField("VideoPageCleanerScript", BindingFlags.Static | BindingFlags.NonPublic);
        var script = field?.GetValue(null) as string ?? "";
        check(script.Length > 200, "WebView2Service.VideoPageCleanerScript is accessible");

        var cssStart = script.IndexOf("var CSS = [", StringComparison.Ordinal);
        var cssEnd = script.IndexOf("].join('\\n');", Math.Max(0, cssStart), StringComparison.Ordinal);
        check(cssStart >= 0 && cssEnd > cssStart, "VideoPageCleanerScript contains CSS array");
        var cssSection = cssStart >= 0 && cssEnd > cssStart ? script[cssStart..cssEnd] : "";
        // 检查被拼接出来的 CSS 规则前不存在尾随逗号（例如 "...," 下一行紧跟 "{ display:none"）
        var cssJoined = string.Join("\n",
            System.Text.RegularExpressions.Regex.Matches(cssSection, @"'([^']*)'")
                .Select(m => m.Groups[1].Value));
        var hasTrailingCommaBeforeBrace = System.Text.RegularExpressions.Regex.IsMatch(cssJoined, @",\s*\{");
        check(!hasTrailingCommaBeforeBrace,
            "VideoPageCleanerScript CSS has no trailing comma before rule brace");
        check(!script.Contains(",,", StringComparison.Ordinal),
            "VideoPageCleanerScript has no double-comma JS/selector syntax error");
    }

    private static void TestUrlParserSpaceUid(Action<bool, string> check)
    {
        check(UrlParser.ExtractUid("https://space.bilibili.com/123456") == "123456",
            "ExtractUid parses https://space.bilibili.com/123456");
        check(UrlParser.ExtractUid("//space.bilibili.com/987654/video?tid=0") == "987654",
            "ExtractUid parses scheme-relative space.bilibili.com URL");
        check(UrlParser.ExtractUid("space.bilibili.com/424242/dynamic") == "424242",
            "ExtractUid parses bare hostname space.bilibili.com URL");
        check(UrlParser.ExtractUid("https://www.bilibili.com/space/778899") == "778899",
            "ExtractUid still parses /space/<uid> path");
        check(UrlParser.ExtractUid("https://www.bilibili.com/video/BV1xx411c7mD") == null,
            "ExtractUid returns null for non-space URLs");
    }

    private static void TestVisualTreeUtilContentElement(Action<bool, string> check)
    {
        var run = new Run("clickable");
        var tb = new TextBlock();
        tb.Inlines.Add(run);
        var border = new Border { Child = tb };

        Exception? err = null;
        Border? found = null;
        try { found = VisualTreeUtil.FindAncestor<Border>(run); }
        catch (Exception ex) { err = ex; }

        check(err == null && ReferenceEquals(found, border),
            "VisualTreeUtil.FindAncestor traverses ContentElement (Run -> TextBlock -> Border) without throwing");
        check(VisualTreeUtil.IsDescendantOf(run, border),
            "VisualTreeUtil.IsDescendantOf recognizes ContentElement inside ancestor");
    }

    private static void TestAtomicFileNoTempLeaks(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "BiliTouch_AuditTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "state.json");
            AtomicFile.WriteAllText(path, "{\"v\":1}");
            check(File.ReadAllText(path) == "{\"v\":1}", "AtomicFile.WriteAllText creates target cleanly");

            AtomicFile.WriteAllText(path, "{\"v\":2}");
            check(File.ReadAllText(path) == "{\"v\":2}", "AtomicFile.WriteAllText overwrites target cleanly");

            var bytesPath = Path.Combine(dir, "blob.bin");
            AtomicFile.WriteAllBytesAsync(bytesPath, new byte[] { 1, 2, 3, 4 }).GetAwaiter().GetResult();
            check(File.ReadAllBytes(bytesPath).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "AtomicFile.WriteAllBytesAsync writes binary payload cleanly");

            var leftovers = Directory.GetFiles(dir, "*.tmp");
            check(leftovers.Length == 0, $"AtomicFile leaves zero .tmp files behind (found={leftovers.Length})");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestUserStateBusSynchronization(Action<bool, string> check)
    {
        // 1. FollowChanged 同步 UserSpaceViewModel 与 PersonalHomeViewModel
        var userVm = new UserSpaceViewModel();
        typeof(UserSpaceViewModel).GetField("_mid", AnyPrivate)!.SetValue(userVm, 556677L);
        typeof(UserSpaceViewModel).GetProperty(nameof(UserSpaceViewModel.IsFollowed))!
            .SetValue(userVm, false);
        Svc.UserState.PublishFollow(556677L, true);
        check(userVm.IsFollowed, "UserStateBus.PublishFollow updates matching UserSpaceViewModel.IsFollowed");

        Svc.UserState.PublishFollow(556677L, false);
        check(!userVm.IsFollowed, "UserStateBus.PublishFollow clears UserSpaceViewModel.IsFollowed on unfollow");

        // 2. WatchLater / Favorite / History 标脏同步
        var wlVm = new WatchLaterViewModel();
        check(!wlVm.IsDirty, "WatchLaterViewModel starts clean");
        Svc.UserState.PublishWatchLater(12345, "BV1test", true);
        check(wlVm.IsDirty, "UserStateBus.PublishWatchLater marks WatchLaterViewModel dirty");

        var favVm = new FavoritesViewModel();
        check(!favVm.IsDirty, "FavoritesViewModel starts clean");
        Svc.UserState.PublishFavorite(12345, "BV1test", true);
        check(favVm.IsDirty, "UserStateBus.PublishFavorite marks FavoritesViewModel dirty");

        var histVm = new HistoryViewModel();
        check(!histVm.IsDirty, "HistoryViewModel starts clean");
        Svc.UserState.PublishHistoryRecorded(12345, "BV1test");
        check(histVm.IsDirty, "UserStateBus.PublishHistoryRecorded marks HistoryViewModel dirty");

        // 3. DynamicMutated 计数联动 + AlreadyAppliedTo 去重
        var followVm = new FollowViewModel();
        var item = new DynamicItem { Id = "dyn-audit-1", LikeCount = 10, CommentCount = 3, ForwardCount = 1 };
        followVm.Dynamics.Add(item);

        // 先验证 AlreadyAppliedTo 指向同一实例时不会重复加倍
        item.AdjustCounts(commentDelta: 1, forwardDelta: 0);
        Svc.UserState.PublishDynamicMutated("dyn-audit-1", commentDelta: 1, alreadyAppliedTo: item);
        check(item.CommentCount == 4, $"DynamicMutated deduplicates AlreadyAppliedTo (count={item.CommentCount})");

        // 再验证来自别处副本的变更会同步到该实例
        var otherCopy = new DynamicItem { Id = "dyn-audit-1" };
        Svc.UserState.PublishDynamicMutated("dyn-audit-1", liked: true, commentDelta: 1, repostDelta: 2, alreadyAppliedTo: otherCopy);
        check(item.IsLiked && item.LikeCount == 11 && item.CommentCount == 5 && item.ForwardCount == 3,
            $"DynamicMutated propagates like/comment/forward to other copies (liked={item.IsLiked}, like={item.LikeCount}, comment={item.CommentCount}, fwd={item.ForwardCount})");
    }

    private static void TestWatchLaterDeletionKeepsPagination(Action<bool, string> check)
    {
        var vm = new WatchLaterViewModel();
        var all = (List<VideoItem>)typeof(WatchLaterViewModel).GetField("_all", AnyPrivate)!.GetValue(vm)!;
        var shownField = typeof(WatchLaterViewModel).GetField("_shown", AnyPrivate)!;
        var totalField = typeof(WatchLaterViewModel).GetField("_total", AnyPrivate)!;
        var removeEverywhere = typeof(WatchLaterViewModel).GetMethod("RemoveEverywhere", AnyPrivate)!;

        all.Clear();
        for (var i = 1; i <= 80; i++)
            all.Add(new VideoItem { Aid = i, Bvid = $"BV{i}", Title = $"Video {i}" });
        totalField.SetValue(vm, 80);
        // 模拟第一页已展示 60 条，还有 20 条待加载
        for (var i = 0; i < 60; i++) vm.Items.Add(all[i]);
        shownField.SetValue(vm, 60);

        check(vm.HasMore, "WatchLaterViewModel has more items when 60 of 80 are shown");
        removeEverywhere.Invoke(vm, new object[] { all[9] });

        check(vm.Items.Count == 59, $"RemoveEverywhere removes visible item cleanly (items={vm.Items.Count})");
        check(!vm.IsFilling, "RemoveEverywhere does NOT lock IsFilling=true when deleting on multi-page list");
        check(vm.HasMore, "RemoveEverywhere preserves HasMore=true for unloaded pages");
    }

    private static void TestUserSpaceTabSwitchPreservesVideoHasMore(Action<bool, string> check)
    {
        var vm = new UserSpaceViewModel();
        var videoHasMore = typeof(UserSpaceViewModel).GetField("_videoHasMore", AnyPrivate)!;
        var articlesHasMore = typeof(UserSpaceViewModel).GetField("_articleHasMore", AnyPrivate)!;
        videoHasMore.SetValue(vm, true);
        articlesHasMore.SetValue(vm, false);

        vm.ShowArticles = true;
        check(!vm.HasMore, "UserSpaceViewModel.HasMore reflects article pagination on Articles tab");
        vm.ShowArticles = false;
        check(vm.HasMore, "Switching back to Videos tab preserves video HasMore=true");
    }

    private static void TestHomeViewModelHotLoadingPropertyChanged(Action<bool, string> check)
    {
        var vm = new HomeViewModel();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        typeof(HomeViewModel).GetProperty(nameof(HomeViewModel.HotLoading))!.SetValue(vm, true);
        check(changed.Contains(nameof(HomeViewModel.HotLoading)),
            "HomeViewModel.HotLoading raises PropertyChanged");

        // 确认 EnsureHotSearchAsync 不再在设属性前直写 _hotLoading = true
        var src = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../BiliTouch/ViewModels/HomeViewModel.cs")));
        check(!src.Contains("_hotLoading = true;\r\n        HotLoading = true;", StringComparison.Ordinal) &&
              !src.Contains("_hotLoading = true;\n        HotLoading = true;", StringComparison.Ordinal),
            "HomeViewModel.EnsureHotSearchAsync no longer suppresses HotLoading PropertyChanged");
    }

    private static void TestPlayerWindowResetCoverageAndBusyFlags(Action<bool, string> check)
    {
        var win = new PlayerWindow();
        try
        {
            var perMedia = (string[])typeof(PlayerWindow).GetField("PerMediaStateFields", AnyPrivate)!.GetValue(null)!;
            check(perMedia.Contains("_followBusy"), "PlayerWindow registers _followBusy in PerMediaStateFields");

            // 置脏所有操作忙态标记，确认 ResetForNewMedia 全部归位，避免换片后按钮永久锁死
            var flags = new[] { "_favBusy", "_switchingQuality", "_watchLaterBusy", "_followBusy" };
            foreach (var name in flags)
                typeof(PlayerWindow).GetField(name, AnyPrivate)!.SetValue(win, true);

            typeof(PlayerWindow).GetMethod("ResetForNewMedia", AnyPrivate)!.Invoke(win, null);

            foreach (var name in flags)
            {
                var val = (bool)typeof(PlayerWindow).GetField(name, AnyPrivate)!.GetValue(win)!;
                check(!val, $"PlayerWindow.ResetForNewMedia clears {name}");
            }
        }
        finally
        {
            try { win.Close(); } catch { }
        }
    }

    private static void TestLiveRoomMutePreservation(Action<bool, string> check)
    {
        // 验证 VolumeSliderSync 的 preserveMute=true 语义存在且 MuteClick 传入 preserveMute: true
        var sync = typeof(LiveRoomWindow).GetMethod("VolumeSliderSync", AnyPrivate);
        check(sync != null && sync.GetParameters().Length == 3 && sync.GetParameters()[2].Name == "preserveMute",
            "LiveRoomWindow.VolumeSliderSync supports preserveMute parameter");

        var src = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../BiliTouch/Views/Windows/LiveRoomWindow.xaml.cs")));
        check(src.Contains("preserveMute: true", StringComparison.Ordinal),
            "LiveRoomWindow.MuteClick passes preserveMute: true so muting is not undone when volume > 0");
    }

    private static void TestHorizontalSwipeHostContract(Action<bool, string> check)
    {
        check(typeof(IHorizontalSwipeHost).IsAssignableFrom(typeof(TabNavigationGestures)),
            "TabNavigationGestures implements IHorizontalSwipeHost");
        check(typeof(IHorizontalSwipeHost).IsAssignableFrom(typeof(HomePage)),
            "HomePage implements IHorizontalSwipeHost");
        check(typeof(IHorizontalSwipeHost).IsAssignableFrom(typeof(MinePage)),
            "MinePage implements IHorizontalSwipeHost");
        check(typeof(IHorizontalSwipeHost).IsAssignableFrom(typeof(FollowPage)),
            "FollowPage implements IHorizontalSwipeHost");
    }

    private static void TestPrecisionTouchpadContactMotion(Action<bool, string> check)
    {
        var motion = new PrecisionTouchpadInput.ContactMotion();
        motion.Reset(width: 200);
        motion.Update(1, 100, 100, active: true);
        motion.Update(2, 120, 100, active: true);
        motion.Update(1, 150, 110, active: true);
        motion.Update(2, 170, 110, active: true);
        var (dx, dy) = motion.Displacement;
        check(Math.Abs(dx - 0.25) < 0.001 && Math.Abs(dy - 0.05) < 0.001,
            $"ContactMotion.Displacement computes normalized multi-contact centroid delta (dx={dx:F3}, dy={dy:F3})");
    }

    private static void TestDashRemuxerCttsLeadingZeros(Action<bool, string> check)
    {
        // 通过反射构造含前导 2 个 CtsOffset=0 + 后续 2 个 CtsOffset=1600 的 SourceTrack，
        // 验证 BuildStbl 产出的 ctts box 第 1 条把前 2 个 0 偏移样本补齐，总样本计数 == 4。
        var remuxerType = typeof(DashRemuxer);
        var trackType = remuxerType.GetNestedType("SourceTrack", BindingFlags.NonPublic)!;
        var fragType = remuxerType.GetNestedType("Frag", BindingFlags.NonPublic)!;
        var sampleType = remuxerType.GetNestedType("Sample", BindingFlags.NonPublic)!;

        var track = Activator.CreateInstance(trackType)!;
        trackType.GetField("TrackId")!.SetValue(track, 1);
        trackType.GetField("Stsd")!.SetValue(track, new byte[] { 0, 0, 0, 8, (byte)'s', (byte)'t', (byte)'s', (byte)'d' });

        var frag = Activator.CreateInstance(fragType)!;
        var samplesObj = fragType.GetField("Samples")!.GetValue(frag)!;
        var addMethod = samplesObj.GetType().GetMethod("Add")!;

        int[] offsets = { 0, 0, 1600, 1600 };
        foreach (var off in offsets)
        {
            var s = Activator.CreateInstance(sampleType)!;
            sampleType.GetField("Duration")!.SetValue(s, 1000L);
            sampleType.GetField("Size")!.SetValue(s, 512);
            sampleType.GetField("CtsOffset")!.SetValue(s, off);
            sampleType.GetField("Flags")!.SetValue(s, 0);
            addMethod.Invoke(samplesObj, new[] { s });
        }

        var fragsObj = trackType.GetField("Frags")!.GetValue(track)!;
        fragsObj.GetType().GetMethod("Add")!.Invoke(fragsObj, new[] { frag });

        var buildStbl = remuxerType.GetMethod("BuildStbl", BindingFlags.Static | BindingFlags.NonPublic)!;
        var stbl = (byte[])buildStbl.Invoke(null, new object[] { track, 1024L })!;

        // 在 stbl 字节流里查找 "ctts" box
        var cttsIdx = -1;
        for (var i = 8; i <= stbl.Length - 16; i++)
        {
            if (stbl[i] == (byte)'c' && stbl[i + 1] == (byte)'t' && stbl[i + 2] == (byte)'t' && stbl[i + 3] == (byte)'s')
            {
                cttsIdx = i - 4;
                break;
            }
        }
        check(cttsIdx >= 0, "DashRemuxer.BuildStbl emits ctts box when any sample has non-zero CtsOffset");
        if (cttsIdx >= 0)
        {
            static uint ReadU32(byte[] b, int p) =>
                ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];

            var entryCount = ReadU32(stbl, cttsIdx + 12);
            var firstCount = ReadU32(stbl, cttsIdx + 16);
            var firstOff = (int)ReadU32(stbl, cttsIdx + 20);
            var secondCount = ReadU32(stbl, cttsIdx + 24);
            var secondOff = (int)ReadU32(stbl, cttsIdx + 28);

            check(entryCount == 2 && firstCount == 2 && firstOff == 0 && secondCount == 2 && secondOff == 1600,
                $"DashRemuxer ctts preserves leading zero-offset samples (entries={entryCount}, [{firstCount}x{firstOff}, {secondCount}x{secondOff}])");
        }
    }

    private static void TestDynamicLikeIconStability(Action<bool, string> check)
    {
        // 1. SkinActionIcon 默认 Stretch 必须为 Uniform，防止 1024x1024 的 LikeIconGeometry 在 16x16 盒子下被画到视口外
        var icon = new SkinActionIcon { Width = 16, Height = 16 };
        check(icon.Stretch == System.Windows.Media.Stretch.Uniform,
            $"SkinActionIcon defaults Stretch to Uniform (actual={icon.Stretch})");

        // 2. 全局三连几何资源必须已冻结，避免 ListBox 虚拟化回收共享未冻结 PathGeometry+Transform 引发绘制丢失
        foreach (var key in new[] { "LikeIconGeometry", "CoinIconGeometry", "FavoriteIconGeometry" })
        {
            var geo = Application.Current?.TryFindResource(key) as System.Windows.Media.Geometry;
            check(geo != null && geo.IsFrozen, $"{key} resource exists and is frozen");
        }

        // 3. 动态详情页 / 动态卡片 / 专栏页的点赞图标均显式设置 Stretch="Uniform" 并与按钮前景色同源
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../BiliTouch"));
        var detailXaml = File.ReadAllText(Path.Combine(root, "Views", "Pages", "DynamicDetailPage.xaml"));
        var feedXaml = File.ReadAllText(Path.Combine(root, "Views", "Controls", "DynamicFeedCard.xaml"));
        var articleXaml = File.ReadAllText(Path.Combine(root, "Views", "Pages", "ArticlePage.xaml"));

        check(detailXaml.Contains("Kind=\"Like\"", StringComparison.Ordinal)
              && detailXaml.Contains("Stretch=\"Uniform\"", StringComparison.Ordinal)
              && detailXaml.Contains("RelativeSource={RelativeSource AncestorType=Button}", StringComparison.Ordinal),
            "DynamicDetailPage comment like icon uses Stretch=Uniform and binds Fill to Button.Foreground");
        check(feedXaml.Contains("Fill=\"{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}\"", StringComparison.Ordinal),
            "DynamicFeedCard like icon binds Fill directly to Button.Foreground");
        check(articleXaml.Contains("Stretch=\"Uniform\"", StringComparison.Ordinal),
            "ArticlePage like icons use Stretch=Uniform");
    }

    private static void TestVideoTailEndingAndElstPatch(Action<bool, string> check)
    {
        // 1. 验证 PlayerWindow 登记并重置 _tailVideoOnlyResumed
        var perMedia = (string[])typeof(PlayerWindow).GetField("PerMediaStateFields", AnyPrivate)!.GetValue(null)!;
        check(perMedia.Contains("_tailVideoOnlyResumed"),
            "PlayerWindow registers _tailVideoOnlyResumed in PerMediaStateFields");

        var win = new PlayerWindow();
        try
        {
            var flag = typeof(PlayerWindow).GetField("_tailVideoOnlyResumed", AnyPrivate)!;
            flag.SetValue(win, true);
            typeof(PlayerWindow).GetMethod("ResetForNewMedia", AnyPrivate)!.Invoke(win, null);
            check(!(bool)flag.GetValue(win)!, "ResetForNewMedia clears _tailVideoOnlyResumed");

            flag.SetValue(win, true);
            typeof(PlayerWindow).GetMethod("EnterReplayState", AnyPrivate)!.Invoke(win, null);
            check(!(bool)flag.GetValue(win)!, "EnterReplayState clears _tailVideoOnlyResumed");
        }
        finally
        {
            try { win.Close(); } catch { }
        }

        // 2. 验证 DashRemuxer.ParseMoov 遇到 2 条 elst（第 0 条 media_time=-1 空编辑 + 第 1 条 media_time=1024 主编辑）
        //    时能正确定位到第 1 条主编辑段，不再跳过整张 elst 补丁
        var remuxerType = typeof(DashRemuxer);
        var trackType = remuxerType.GetNestedType("SourceTrack", BindingFlags.NonPublic)!;
        var makeBox = remuxerType.GetMethod("MakeBox", BindingFlags.Static | BindingFlags.NonPublic)!;
        var makeFullBox = remuxerType.GetMethod("MakeFullBox", BindingFlags.Static | BindingFlags.NonPublic)!;
        var concat = remuxerType.GetMethod("Concat", BindingFlags.Static | BindingFlags.NonPublic)!;
        static byte[] Box(MethodInfo m, string t, byte[] p) => (byte[])m.Invoke(null, new object[] { t, p })!;
        static byte[] FullBox(MethodInfo m, string t, byte v, byte[] p) => (byte[])m.Invoke(null, new object[] { t, v, p })!;
        static byte[] Join(MethodInfo m, params byte[][] parts) => (byte[])m.Invoke(null, new object[] { parts })!;

        var mvhd = FullBox(makeFullBox, "mvhd", 0, new byte[96]);
        var tkhd = FullBox(makeFullBox, "tkhd", 0, new byte[80]);
        var mdhdPayload = new byte[20];
        // timescale = 44100 (0x0000AC44) at payload offset 8..11
        mdhdPayload[10] = 0xAC; mdhdPayload[11] = 0x44;
        var mdhd = FullBox(makeFullBox, "mdhd", 0, mdhdPayload);
        var stsd = Box(makeBox, "stsd", new byte[8]);
        var stbl = Box(makeBox, "stbl", stsd);
        var minf = Box(makeBox, "minf", stbl);
        var mdia = Box(makeBox, "mdia", Join(concat, mdhd, minf));

        // elst v0: entry_count=2; entry0=(segDur=11, mediaTime=-1, rate=1.0); entry1=(segDur=0, mediaTime=1024, rate=1.0)
        var elstPayload = new byte[4 + 2 * 12];
        elstPayload[3] = 2;
        elstPayload[7] = 11;
        elstPayload[8] = 0xFF; elstPayload[9] = 0xFF; elstPayload[10] = 0xFF; elstPayload[11] = 0xFF; // media_time = -1
        elstPayload[13] = 1;
        // entry1 starts at payload offset 16 (segDur = 0, mediaTime = 1024)
        elstPayload[22] = 0x04; elstPayload[23] = 0x00; // media_time = 1024
        elstPayload[25] = 1;
        var elst = FullBox(makeFullBox, "elst", 0, elstPayload);
        var edts = Box(makeBox, "edts", elst);

        var trak = Box(makeBox, "trak", Join(concat, tkhd, edts, mdia));
        var moov = Box(makeBox, "moov", Join(concat, mvhd, trak));

        var track = Activator.CreateInstance(trackType)!;
        var parseMoov = trackType.GetMethod("ParseMoov", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var args = new object?[] { moov, "" };
        var ok = (bool)parseMoov.Invoke(track, args)!;
        var isEmpty = (bool)trackType.GetField("ElstFirstEntryIsEmpty")!.GetValue(track)!;
        var segOff = (int)trackType.GetField("ElstSegDurOffset")!.GetValue(track)!;

        check(ok && !isEmpty && segOff > 0,
            $"DashRemuxer locates main media_time>=0 elst entry after leading empty edit (ok={ok}, empty={isEmpty}, off={segOff})");
    }

    private static void TestOfflineOnlineCommentsAndDanmakuContract(Action<bool, string> check)
    {
        // 1. HasBilibiliOnlineIdentity 区分 B 站缓存与本地导入文件
        check(PlayerWindow.HasBilibiliOnlineIdentity(new OfflineEntry { Bvid = "BV1xx411c7mD", Cid = 123 }),
            "HasBilibiliOnlineIdentity accepts BiliTouch cached BV entry");
        check(PlayerWindow.HasBilibiliOnlineIdentity(new OfflineEntry { Bvid = "BV1xx411c7mD", IsExternalClientCache = true }),
            "HasBilibiliOnlineIdentity accepts official Bilibili client cache entry");
        check(!PlayerWindow.HasBilibiliOnlineIdentity(new OfflineEntry { Bvid = "local-abc", ImportSource = "C:\\video.mp4" }),
            "HasBilibiliOnlineIdentity rejects locally imported MP4 file");
        check(!PlayerWindow.HasBilibiliOnlineIdentity(new OfflineEntry { Bvid = "BV1xx411c7mD", ImportSource = "C:\\video.mp4" }),
            "HasBilibiliOnlineIdentity rejects imported file even if Bvid looks like BV");

        // 2. PlayerWindow 状态权限与重置验证
        var perMedia = (string[])typeof(PlayerWindow).GetField("PerMediaStateFields", AnyPrivate)!.GetValue(null)!;
        check(perMedia.Contains("_offlineOnlineBvid"),
            "PlayerWindow registers _offlineOnlineBvid in PerMediaStateFields");

        var win = new PlayerWindow();
        try
        {
            var isLocalF = typeof(PlayerWindow).GetField("_isLocalPlayback", AnyPrivate)!;
            var offlineBvidF = typeof(PlayerWindow).GetField("_offlineOnlineBvid", AnyPrivate)!;
            var cidF = typeof(PlayerWindow).GetField("_currentCid", AnyPrivate)!;
            var aidF = typeof(PlayerWindow).GetField("_aid", AnyPrivate)!;
            var canDanmakuP = typeof(PlayerWindow).GetProperty("CanUseOnlineDanmaku", AnyPrivate)!;
            var canCommentsP = typeof(PlayerWindow).GetProperty("CanUseOnlineComments", AnyPrivate)!;

            // 离线 B 站视频拿到 cid/aid 后允许使用弹幕与评论，同时保持 _isLocalPlayback=true 不影响本地播放
            isLocalF.SetValue(win, true);
            offlineBvidF.SetValue(win, "BV1xx411c7mD");
            cidF.SetValue(win, 998877L);
            aidF.SetValue(win, 665544L);
            check((bool)canDanmakuP.GetValue(win)! && (bool)canCommentsP.GetValue(win)!,
                "Offline Bilibili cache enables online danmaku and comments once cid/aid are resolved");

            // 纯本地导入文件（无 _offlineOnlineBvid）依然禁用在线弹幕与评论
            offlineBvidF.SetValue(win, "");
            check(!(bool)canDanmakuP.GetValue(win)! && !(bool)canCommentsP.GetValue(win)!,
                "Pure local file keeps online danmaku and comments disabled");

            // ResetForNewMedia 清空 _offlineOnlineBvid
            offlineBvidF.SetValue(win, "BV1xx411c7mD");
            typeof(PlayerWindow).GetMethod("ResetForNewMedia", AnyPrivate)!.Invoke(win, null);
            check(string.IsNullOrEmpty((string)offlineBvidF.GetValue(win)!),
                "ResetForNewMedia clears _offlineOnlineBvid");
        }
        finally
        {
            try { win.Close(); } catch { }
        }
    }

    private static void TestMacroArchitectureAndRuntimeHealth(Action<bool, string> check)
    {
        // 1. 物理收口不变量：ApiClient 全族仅保留唯一 1 处 _http.SendAsync（在 SendThrottledAsync 内）
        var servicesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../BiliTouch/Services"));
        var sendSites = 0;
        foreach (var file in Directory.GetFiles(servicesDir, "ApiClient*.cs"))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.Contains("_http.SendAsync", StringComparison.Ordinal)) sendSites++;
            }
        }
        check(sendSites == 1, $"ApiClient routes every HTTP request through one single SendAsync gateway (sites={sendSites})");

        // 2. 横滑统一物理内核数学不变量
        check(HorizontalSwipePhysics.SnapEase.IsFrozen, "HorizontalSwipePhysics.SnapEase is frozen");
        var r100 = HorizontalSwipePhysics.RubberBand(100, 500);
        var r5000 = HorizontalSwipePhysics.RubberBand(5000, 500);
        var rNeg = HorizontalSwipePhysics.RubberBand(-100, 500);
        check(r100 > 0 && r100 < r5000 && r5000 <= 500 * HorizontalSwipePhysics.EdgeResist + 1e-6,
            $"HorizontalSwipePhysics.RubberBand is monotonic and bounded (r100={r100:F2}, r5000={r5000:F2})");
        check(Math.Abs(r100 + rNeg) < 1e-9, "HorizontalSwipePhysics.RubberBand is symmetric");
        check(!HorizontalSwipePhysics.ShouldCommit(100, 500) && HorizontalSwipePhysics.ShouldCommit(200, 500),
            "HorizontalSwipePhysics.ShouldCommit matches 3/8 threshold");

        // 3. 离屏像素渲染探针：SkinActionIcon（16x16，不显式传 Stretch）渲染 LikeIconGeometry 必须产出可见非零像素
        var likeGeo = (System.Windows.Media.Geometry)Application.Current!.FindResource("LikeIconGeometry");
        var icon = new SkinActionIcon
        {
            Kind = "Like",
            Data = likeGeo,
            Width = 16,
            Height = 16,
            Fill = System.Windows.Media.Brushes.Crimson
        };
        icon.Measure(new Size(16, 16));
        icon.Arrange(new Rect(0, 0, 16, 16));
        icon.UpdateLayout();
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(icon);
        var pixels = new byte[16 * 16 * 4];
        rtb.CopyPixels(pixels, 16 * 4, 0);
        var visiblePixels = 0;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] > 16) visiblePixels++;
        check(visiblePixels >= 30,
            $"Offscreen pixel probe: 16x16 SkinActionIcon renders visible LikeIconGeometry pixels (count={visiblePixels})");

        // 4. WPF DataBindingSource 静默错误监听器：实例化 DynamicFeedCard 与 VideoCard 并布局，必须零 Binding Error
        var bindingErrors = new List<string>();
        var listener = new BindingErrorCaptureListener(bindingErrors);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var oldLevel = System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level;
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
        try
        {
            var feedCard = new BiliTouch.Views.Controls.DynamicFeedCard
            {
                DataContext = new DynamicItem
                {
                    Id = "health-1",
                    AuthorName = "UP主",
                    Text = "健康体检测试动态",
                    LikeCount = 42,
                    CommentCount = 5,
                    ForwardCount = 2,
                    IsLiked = true
                }
            };
            feedCard.Measure(new Size(680, 1000));
            feedCard.Arrange(new Rect(0, 0, 680, feedCard.DesiredSize.Height));
            feedCard.UpdateLayout();

            var videoCard = new BiliTouch.Views.Controls.VideoCard
            {
                DataContext = new VideoItem
                {
                    Bvid = "BV1health000",
                    Title = "健康体检测试视频",
                    Author = "UP主",
                    ViewCount = 12345,
                    Duration = "03:21"
                }
            };
            videoCard.Measure(new Size(260, 300));
            videoCard.Arrange(new Rect(0, 0, 260, videoCard.DesiredSize.Height));
            videoCard.UpdateLayout();
        }
        finally
        {
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = oldLevel;
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
        check(bindingErrors.Count == 0,
            $"WPF DataBindingSource reports zero binding errors on DynamicFeedCard & VideoCard (errors={bindingErrors.Count}{(bindingErrors.Count > 0 ? ": " + bindingErrors[0] : "")})");
    }

    private static void TestAudioQualityBitrateAndFreezeFixes(Action<bool, string> check)
    {
        // 1. BufferPolicy VodDash 双流自动档不低于 1000ms，防高码率/Hi-Res 欠载重采样
        var dashAutoMs = BufferPolicy.Resolve(BufferMediaKind.VodDash, "https://upos.bilivideo.com/v.m4s",
            shortVideo: false, userMs: 500, auto: true);
        check(dashAutoMs >= 1000,
            $"BufferPolicy VodDash auto keeps >=1000ms floor for dual-stream stability (actual={dashAutoMs})");

        // 2. BilibiliClientCacheScanner 在无 expectedBytes 精确匹配时必须把 30280(192k) 排在 30232/30216 之前
        var tempDir = Path.Combine(Path.GetTempPath(), "bt-cache-rank-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(Path.Combine(tempDir, "100_1-1-100026.m4s"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(tempDir, "100_1-1-30216.m4s"), new byte[256]);
            File.WriteAllBytes(Path.Combine(tempDir, "100_1-1-30232.m4s"), new byte[512]);
            File.WriteAllBytes(Path.Combine(tempDir, "100_1-1-30280.m4s"), new byte[384]);
            var resolveStreams = typeof(BilibiliClientCacheScanner).GetMethod("TryLocateMedia", AnyPrivate)!;
            object?[] invokeArgs = { tempDir, 0L, 12, null, null, null, 0L };
            var ok = (bool)resolveStreams.Invoke(null, invokeArgs)!;
            var pickedAudio = invokeArgs[5] as string ?? "";
            check(ok && pickedAudio.EndsWith("30280.m4s", StringComparison.OrdinalIgnoreCase),
                $"BilibiliClientCacheScanner prefers 30280 (192k) over 30232/30216 (picked={Path.GetFileName(pickedAudio)})");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        // 3. 非 Hi-Res / 杜比时角标仍显示当前码率，且有杜比音轨时在菜单内作为可选项列出
        var player = new PlayerWindow();
        try
        {
            var tracksField = typeof(PlayerWindow).GetField("_audioTracks", AnyPrivate)!;
            var actualAudioField = typeof(PlayerWindow).GetField("_actualAudio", AnyPrivate)!;
            var fillMenu = typeof(PlayerWindow).GetMethod("FillQualityMenu", AnyPrivate)!;
            var badge = (Border)player.FindName("BtnAudioBadge");
            var badgeText = (TextBlock)player.FindName("AudioBadgeText");
            var qualityList = (ListBox)player.FindName("QualityList");
            var audioList = (ListBox)player.FindName("AudioQualityList");
            var audioInfo = (TextBlock)player.FindName("AudioQualityInfoText");
            var audioSection = (StackPanel)player.FindName("VolumeAudioSection");

            tracksField.SetValue(player, new List<AudioTrack>
            {
                new() { Quality = AudioQuality.High, TrackId = 30280, Label = "高品质", Bandwidth = 192000, Url = "https://example.invalid/192.m4s" },
                new() { Quality = AudioQuality.Dolby, TrackId = 30250, Label = "杜比全景声", Bandwidth = 448000, Url = "https://example.invalid/dolby.m4s" },
            });
            actualAudioField.SetValue(player, AudioQuality.High);
            fillMenu.Invoke(player, new object[] { 80 });

            check(badge.Visibility == Visibility.Visible && badgeText.Text == "192 kbps",
                $"non-HiRes/Dolby audio displays current bitrate badge under volume (visible={badge.Visibility}, text={badgeText.Text})");
            check(qualityList.Items.OfType<ListBoxItem>().All(i => i.Tag is not AudioTrack),
                "QualityList contains pure video qualities (audio options moved to VolumePopup)");
            var clickableAudioItems = audioList.Items.OfType<ListBoxItem>().Where(i => i.Tag is AudioTrack).ToList();
            check(audioSection.Visibility == Visibility.Visible && audioList.Visibility == Visibility.Visible
                  && clickableAudioItems.Count == 2
                  && clickableAudioItems.Any(i => ((AudioTrack)i.Tag).Quality == AudioQuality.High)
                  && clickableAudioItems.Any(i => ((AudioTrack)i.Tag).Quality == AudioQuality.Dolby),
                $"VolumePopup AudioQualityList exposes optional Dolby alongside stereo AAC (clickable={clickableAudioItems.Count})");

            // 纯普通 AAC（无杜比/无 Hi-Res）同样在音量面板中显示音质信息与码率角标
            tracksField.SetValue(player, new List<AudioTrack>
            {
                new() { Quality = AudioQuality.High, TrackId = 30232, Label = "较高音质", Bandwidth = 132000, Url = "https://example.invalid/132.m4s" },
            });
            actualAudioField.SetValue(player, AudioQuality.High);
            fillMenu.Invoke(player, new object[] { 80 });
            check(badge.Visibility == Visibility.Visible && badgeText.Text == "132 kbps"
                  && audioSection.Visibility == Visibility.Visible && audioInfo.Visibility == Visibility.Visible
                  && audioInfo.Text.Contains("132 kbps"),
                $"pure AAC video displays bitrate badge and VolumePopup audio info (text={badgeText.Text}, info={audioInfo.Text})");

            // 字幕弹层使用非模态 StaysOpen=True 且纳入 IsInAnyPlayerPopup，杜绝点击闪退
            var subPopup = (System.Windows.Controls.Primitives.Popup)player.FindName("SubtitlePopup");
            var isInAny = typeof(PlayerWindow).GetMethod("IsInAnyPlayerPopup", AnyPrivate)!;
            check(subPopup.StaysOpen && (bool)isInAny.Invoke(player, new object[] { subPopup.Child })!,
                "SubtitlePopup uses StaysOpen=True and is protected by IsInAnyPlayerPopup");

            // 评论区预览框：1~2 条已全部展示的回复不再多余显示「共 N 条回复」，存在未展示回复时才显示
            var c1 = new CommentItem { ReplyCount = 2 };
            c1.Replies.Add(new CommentItem { Content = "r1" });
            c1.Replies.Add(new CommentItem { Content = "r2" });
            c1.SyncPreviewReplies();
            check(!c1.HasUnshownPreviewReplies && c1.ReplyBoxText == "",
                "comment with all 2 replies already previewed hides redundant '共 2 条回复'");
            var c2 = new CommentItem { ReplyCount = 5 };
            for (int i = 0; i < 3; i++) c2.Replies.Add(new CommentItem { Content = "r" + i });
            c2.SyncPreviewReplies();
            check(c2.HasUnshownPreviewReplies && c2.ReplyBoxText == "共 5 条回复",
                $"comment with 5 replies (3 previewed) displays '{c2.ReplyBoxText}'");

            // 多图预览双指左右横滑切图契约
            var preview = new BiliTouch.Views.Windows.ImagePreviewWindow { Width = 800, Height = 600 };
            try
            {
                _ = preview.LoadGalleryAsync(new[] { "https://example.invalid/1.jpg", "https://example.invalid/2.jpg", "https://example.invalid/3.jpg" }, 0);
                preview.PreviewGallerySwipe(0.6);
                preview.SettleGallerySwipe(commit: true);
                check(preview.GalleryIndexValue == 1, $"ImagePreviewWindow horizontal swipe advances to next photo (idx={preview.GalleryIndexValue})");
                preview.PreviewGallerySwipe(-0.6);
                preview.SettleGallerySwipe(commit: true);
                check(preview.GalleryIndexValue == 0, $"ImagePreviewWindow horizontal swipe returns to previous photo (idx={preview.GalleryIndexValue})");
            }
            finally
            {
                preview.Close();
            }

            // 4. VmemVideoView 看门狗与 GpuVideoPresenter front-buffer 重绑契约检查
            check(typeof(VmemVideoView).GetField("_gpuWatchdogTimer", AnyPrivate) != null,
                "VmemVideoView installs GPU presentation watchdog timer to prevent permanent freeze");
            var presenterType = typeof(VmemVideoView).Assembly.GetType("BiliTouch.Helpers.GpuVideoPresenter")!;
            check(presenterType.GetMethod("EnsureFrontBufferBound", AnyPrivate) != null
                  && presenterType.GetMethod("CheckDeviceHealth", AnyPrivate) != null,
                "GpuVideoPresenter rebinds D3DImage backbuffer on front-buffer restore and checks D3D9Ex health");
        }
        finally
        {
            player.Close();
        }
    }

    private static void TestSeekAudioSubtitlesAndRightKeyFixes(Action<bool, string> check)
    {
        // 1. VlcCore 不再强制 speex_resampler / quality=10（防止 seek/变速漂移补偿触发无限 flush 断音）
        var opts = new List<string>();
        typeof(VlcCore).GetMethod("AppendAudioQualityOptions", AnyPrivate)!.Invoke(null, new object[] { opts });
        check(!opts.Any(o => o.Contains("speex", StringComparison.OrdinalIgnoreCase)),
            $"VlcCore does not force speex_resampler (opts=[{string.Join(", ", opts)}])");

        // 2. BufferPolicy 在 11Mbps 宽带下给 DASH 1000ms（不再推到 2000~3000ms 放大变速静音空洞）
        var lastMeasuredField = typeof(CdnService).GetField("_lastMeasuredBytesPerSecond", AnyPrivate)!;
        var prevMeasured = (long)(lastMeasuredField.GetValue(CdnService.Instance) ?? 0L);
        try
        {
            lastMeasuredField.SetValue(CdnService.Instance, 1_375_000L); // 11 Mbps
            var dashMs = BufferPolicy.Resolve(BufferMediaKind.VodDash, "https://upos.bilivideo.com/v.m4s",
                shortVideo: false, userMs: 1000, auto: true);
            check(dashMs == 1000, $"BufferPolicy VodDash at 11Mbps resolves to 1000ms instead of 2000-3000ms (actual={dashMs})");
        }
        finally
        {
            lastMeasuredField.SetValue(CdnService.Instance, prevMeasured);
        }

        // 3. SubtitleParser 覆盖 B 站 JSON、SRT/VTT、重叠字幕与轨道优先级
        var biliJson = """
        {
          "body": [
            { "from": 1.0, "to": 3.5, "content": "第一句" },
            { "from": 2.0, "to": 4.0, "content": "重叠句" },
            { "from": 5.0, "to": 7.0, "content": "第二句" }
          ]
        }
        """;
        var biliCues = SubtitleParser.ParseBilibiliJson(biliJson);
        check(biliCues.Count == 3, $"SubtitleParser.ParseBilibiliJson parses 3 cues (actual={biliCues.Count})");
        check(SubtitleParser.FindActiveText(biliCues, 0.5) == "", "SubtitleParser returns empty before first cue");
        check(SubtitleParser.FindActiveText(biliCues, 1.5) == "第一句", "SubtitleParser matches single active cue");
        check(SubtitleParser.FindActiveText(biliCues, 2.5) == "第一句\n重叠句",
            "SubtitleParser joins overlapping active cues in order");
        check(SubtitleParser.FindActiveText(biliCues, 6.0) == "第二句", "SubtitleParser matches later cue");

        var srt = """
        1
        00:00:01,500 --> 00:00:03,200
        <i>Hello</i>
        World

        2
        00:01:02,000 --> 00:01:05,500
        Second line
        """;
        var srtCues = SubtitleParser.ParseSrtOrVtt(srt);
        check(srtCues.Count == 2 && srtCues[0].Content == "Hello\nWorld" && Math.Abs(srtCues[1].FromSec - 62.0) < 1e-6,
            $"SubtitleParser.ParseSrtOrVtt strips tags and preserves multiline/timestamps (count={srtCues.Count})");

        var tracks = new[]
        {
            new SubtitleTrack { Lan = "ai-zh", LanDoc = "中文（自动生成）", IsAi = true, SubtitleUrl = "https://example.invalid/ai.json" },
            new SubtitleTrack { Lan = "en-US", LanDoc = "English", IsAi = false, SubtitleUrl = "https://example.invalid/en.json" },
            new SubtitleTrack { Lan = "zh-CN", LanDoc = "中文（简体）", IsAi = false, SubtitleUrl = "https://example.invalid/zh.json" },
        };
        check(SubtitleParser.PickPreferredTrack(tracks, "")?.Lan == "zh-CN",
            "SubtitleParser.PickPreferredTrack prefers human Chinese over English and AI Chinese");
        check(SubtitleParser.PickPreferredTrack(tracks, "en-US")?.Lan == "en-US",
            "SubtitleParser.PickPreferredTrack honors explicit preferred language");
        check(SubtitleParser.NormalizeSubtitleUrl("//aisubtitle.hdslb.com/a.json") == "https://aisubtitle.hdslb.com/a.json",
            "SubtitleParser.NormalizeSubtitleUrl upgrades protocol-relative URL to https");

        // 4. PlayerWindow 字幕覆盖层、控制栏入口、seek 不再强行静音、以及右方向键长按倍速契约
        var player = new PlayerWindow();
        try
        {
            var subBtn = player.FindName("BtnSubtitle") as Button;
            var subPopup = player.FindName("SubtitlePopup") as System.Windows.Controls.Primitives.Popup;
            var subOverlay = player.FindName("SubtitleOverlay") as Border;
            var subText = player.FindName("SubtitleText") as TextBlock;
            check(subBtn != null && subPopup != null && subOverlay != null && subText != null,
                "PlayerWindow exposes BtnSubtitle, SubtitlePopup, SubtitleOverlay, and SubtitleText");

            // 字幕渲染与重置
            typeof(PlayerWindow).GetField("_subtitleEnabled", AnyPrivate)!.SetValue(player, true);
            typeof(PlayerWindow).GetField("_activeSubtitleTrack", AnyPrivate)!.SetValue(player, tracks[2]);
            typeof(PlayerWindow).GetField("_subtitleCues", AnyPrivate)!.SetValue(player, biliCues);
            player.UpdateSubtitleDisplay(2.5);
            check(subOverlay!.Visibility == Visibility.Visible && subText!.Text == "第一句\n重叠句",
                $"PlayerWindow.UpdateSubtitleDisplay renders active cue text (vis={subOverlay.Visibility}, text={subText!.Text})");

            // MarkSeekIssued 不再把 _seekMuteSavedVolume 置为 >=0（消除 seek 后 200-500ms 人为静音丢音），同时立即刷新字幕
            typeof(PlayerWindow).GetField("_volumeCache", AnyPrivate)!.SetValue(player, 75);
            typeof(PlayerWindow).GetMethod("MarkSeekIssued", AnyPrivate)!.Invoke(player, new object[] { 6000L });
            var savedMute = (int)typeof(PlayerWindow).GetField("_seekMuteSavedVolume", AnyPrivate)!.GetValue(player)!;
            check(savedMute < 0, $"MarkSeekIssued does not artificially mute audio during seek (savedMute={savedMute})");
            check(subText!.Text == "第二句", $"MarkSeekIssued refreshes subtitle text immediately to target time (text={subText!.Text})");

            // 右方向键长按进入倍速，取消/松开后干净复位
            typeof(PlayerWindow).GetField("_rightKeyHeld", AnyPrivate)!.SetValue(player, true);
            typeof(PlayerWindow).GetMethod("BeginRightKeyFastForward", AnyPrivate)!.Invoke(player, null);
            var isFf = (bool)typeof(PlayerWindow).GetField("_isFastForwarding", AnyPrivate)!.GetValue(player)!;
            var rightFf = (bool)typeof(PlayerWindow).GetField("_rightKeyFastForwarding", AnyPrivate)!.GetValue(player)!;
            check(isFf && rightFf, "Right arrow hold activates temporary fast-forward");
            typeof(PlayerWindow).GetMethod("CancelRightKeyFastForward", AnyPrivate)!.Invoke(player, null);
            var isFfAfter = (bool)typeof(PlayerWindow).GetField("_isFastForwarding", AnyPrivate)!.GetValue(player)!;
            var rightHeldAfter = (bool)typeof(PlayerWindow).GetField("_rightKeyHeld", AnyPrivate)!.GetValue(player)!;
            check(!isFfAfter && !rightHeldAfter, "CancelRightKeyFastForward cleanly exits fast-forward and clears held state");

            // ResetForNewMedia 清空字幕覆盖层与轨道
            typeof(PlayerWindow).GetMethod("ResetForNewMedia", AnyPrivate)!.Invoke(player, null);
            check(subOverlay.Visibility == Visibility.Collapsed && string.IsNullOrEmpty(subText.Text),
                "ResetForNewMedia clears active subtitle overlay and text");
        }
        finally
        {
            player.Close();
        }
    }

    private static void TestSeamlessPortraitAndEnhancementInQualityPopup(Action<bool, string> check)
    {
        var prevPortrait = Svc.Settings.PortraitVideoOptimization;
        var prevWeb = Svc.Settings.UseWebPlayerLayout;
        var prevExperimental = Svc.Settings.ExperimentalVideoEnhancement;
        var prevActive = Svc.Settings.VideoEnhancementActive;
        var player = new PlayerWindow { Width = 1400, Height = 860, ShowActivated = false };
        try
        {
            // 1. 画质增强开关已移入清晰度选择面板底部，不再占用控制栏
            var qualityPopup = player.FindName("QualityPopup") as System.Windows.Controls.Primitives.Popup;
            var enhanceSection = player.FindName("QualityEnhancementSection") as FrameworkElement;
            var enhanceBtn = player.FindName("BtnEnhancement") as Button;
            check(qualityPopup != null && enhanceSection != null && enhanceBtn != null,
                "PlayerWindow exposes QualityPopup, QualityEnhancementSection, and BtnEnhancement");

            bool hostedInQualityPopup = false;
            DependencyObject? cur = enhanceBtn;
            while (cur != null)
            {
                if (ReferenceEquals(cur, qualityPopup!.Child)) { hostedInQualityPopup = true; break; }
                cur = LogicalTreeHelper.GetParent(cur) ?? System.Windows.Media.VisualTreeHelper.GetParent(cur);
            }
            check(hostedInQualityPopup,
                "BtnEnhancement is hosted inside QualityPopup bottom instead of the player control bar");

            Svc.Settings.UpdateVideoEnhancement(experimental: true, active: false);
            typeof(PlayerWindow).GetMethod("UpdateEnhancementButton", AnyPrivate)!.Invoke(player, null);
            check(enhanceSection!.Visibility == Visibility.Visible && enhanceBtn!.Visibility == Visibility.Visible,
                "QualityEnhancementSection and BtnEnhancement reveal inside QualityPopup when experimental enhancement is enabled");

            Svc.Settings.UpdateVideoEnhancement(experimental: false, active: false);
            typeof(PlayerWindow).GetMethod("UpdateEnhancementButton", AnyPrivate)!.Invoke(player, null);
            check(enhanceSection!.Visibility == Visibility.Collapsed && enhanceBtn!.Visibility == Visibility.Collapsed,
                "QualityEnhancementSection and BtnEnhancement hide when experimental enhancement is disabled");

            // 2. 一镜到底适配「竖屏优化」：横屏目标宽 > 高，竖屏优化目标高 > 宽且位于左侧竖列
            Svc.Settings.SetWebPlayerLayout(true);
            Svc.Settings.SetPortraitVideoOptimization(true);
            player.Show();
            player.UpdateLayout();

            player.UpdateDetectedVideoSize(1920, 1080, fromMetadata: true);
            player.UpdateLayout();
            var landscapeRect = player.ResolveVideoStageTargetRect();

            player.UpdateDetectedVideoSize(1080, 1920, fromMetadata: true);
            player.UpdateLayout();
            var portraitRect = player.ResolveVideoStageTargetRect();

            check(portraitRect.Height > portraitRect.Width && portraitRect.Width < landscapeRect.Width * 0.75,
                $"Seamless target rect adapts to PortraitVideoOptimization (landscape={landscapeRect.Width:0}x{landscapeRect.Height:0}, portrait={portraitRect.Width:0}x{portraitRect.Height:0})");
            check(portraitRect.Height > landscapeRect.Height * 1.15,
                $"Portrait seamless target rect fills full vertical stage height ({portraitRect.Height:0} > {landscapeRect.Height:0})");

            // 3. 飞行途中元数据到达触发竖屏优化时，平滑改道至竖屏视频列且不产生瞬时跳变
            player.UpdateDetectedVideoSize(1920, 1080, fromMetadata: true);
            player.UpdateLayout();
            var started = player.StartSeamlessMorph(new Rect(200, 320, 320, 180), landscapeRect, 8, 0, reverse: false);
            player.StepSeamlessMorph(0.06);
            var rectBeforeRetarget = player.SeamlessCurrentRect;
            player.UpdateDetectedVideoSize(1080, 1920, fromMetadata: true);
            var rectAfterRetarget = player.SeamlessCurrentRect;
            var seamlessState = typeof(PlayerWindow).GetField("_seamless", AnyPrivate)!.GetValue(player)!;
            var newEndRect = (Rect)seamlessState.GetType().GetField("EndRect", AnyPrivate)!.GetValue(seamlessState)!;
            check(started
                && Math.Abs(rectAfterRetarget.X - rectBeforeRetarget.X) < 1.0
                && Math.Abs(rectAfterRetarget.Width - rectBeforeRetarget.Width) < 1.0
                && newEndRect.Height > newEndRect.Width,
                $"Mid-flight portrait metadata smoothly retargets seamless morph without jumping (before={rectBeforeRetarget.Width:0}x{rectBeforeRetarget.Height:0}, after={rectAfterRetarget.Width:0}x{rectAfterRetarget.Height:0}, end={newEndRect.Width:0}x{newEndRect.Height:0})");
            player.StopSeamlessMorph();

            // 4. 虚拟化封面加载防丢图：
            //    a) 0 字节或损坏的小于 64B 磁盘缓存文件会被自动识别并清理；
            //    b) ViewportImageQueue 在容器换源/回收时取消旧等待项并创建新等待项；
            //    c) Cover.EnsureLoaded 可对未在加载且 Background 为空的卡片触发自愈重填。
            var corruptFile = Path.Combine(Path.GetTempPath(), $"bilitouch-corrupt-{Guid.NewGuid():N}.jpg");
            File.WriteAllBytes(corruptFile, new byte[] { 1, 2, 3 });
            var hasValidMethod = typeof(CoverLoader).GetMethod("HasValidCacheFile", AnyPrivate)!;
            var validResult = (bool)hasValidMethod.Invoke(null, new object[] { corruptFile })!;
            check(!validResult && !File.Exists(corruptFile),
                "CoverLoader.HasValidCacheFile detects and purges truncated/0-byte cache files (<64B)");

            var testBorder = new Border { Width = 240, Height = 135 };
            Cover.SetSource(testBorder, "https://example.invalid/old-recycled-cover.jpg");
            ViewportImageQueue.Cancel(testBorder);
            var genProp = (DependencyProperty)typeof(Cover).GetField("LoadGenerationProperty", AnyPrivate)!.GetValue(null)!;
            var genAfterfirst = (int)testBorder.GetValue(genProp);
            Cover.SetSource(testBorder, "https://example.invalid/new-recycled-cover.jpg");
            var genAfterSecond = (int)testBorder.GetValue(genProp);
            check(genAfterSecond > genAfterfirst,
                $"Cover.OnSourceChanged increments LoadGeneration and cancels stale ViewportImageQueue entry ({genAfterfirst} -> {genAfterSecond})");

            // 5. 更新提示弹窗：「推荐更新」徽标识别与发布说明结构化清洗排版（剔除裸下载链接、截图链接与 Markdown 符号）
            var sampleNotes = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/release-notes/v0.3.12.md"));
            var updateWin = new BiliTouch.Views.Windows.UpdatePromptWindow(new UpdateInfo
            {
                Version = new Version(0, 3, 12),
                Title = "BiliTouch 0.3.12 · 推荐更新",
                Notes = "[下载安装包（x64）](https://example.invalid/a.exe)\n# BiliTouch 0.3.12\n" + sampleNotes + "\n![截图](https://example.invalid/s.png)"
            });
            try
            {
                var badge = (FrameworkElement)updateWin.FindName("RecommendedBadge")!;
                var notesBlock = (TextBlock)updateWin.FindName("NotesText")!;
                var renderedText = string.Concat(notesBlock.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
                check(badge.Visibility == Visibility.Visible,
                    "UpdatePromptWindow displays RecommendedBadge when release title/notes mark 推荐更新");
                check(renderedText.Contains("【推荐更新】")
                    && renderedText.Contains("✦ 澎湃 OS 流体动效")
                    && !renderedText.Contains("https://example.invalid")
                    && !renderedText.Contains("![截图]"),
                    "UpdatePromptWindow formats structured release notes and strips raw Markdown download/image URLs");
            }
            finally
            {
                updateWin.Close();
            }
        }
        finally
        {
            Svc.Settings.SetPortraitVideoOptimization(prevPortrait);
            Svc.Settings.SetWebPlayerLayout(prevWeb);
            Svc.Settings.UpdateVideoEnhancement(experimental: prevExperimental, active: prevActive);
            player.Close();
        }
    }

    private sealed class BindingErrorCaptureListener : System.Diagnostics.TraceListener
    {
        private readonly List<string> _errors;
        public BindingErrorCaptureListener(List<string> errors) => _errors = errors;
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _errors.Add(message.Trim());
        }
    }
}
