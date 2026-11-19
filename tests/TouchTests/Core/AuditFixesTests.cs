using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        TestDashRemuxerCttsLeadingZeros(check);
        TestDynamicLikeIconStability(check);
        TestVideoTailEndingAndElstPatch(check);
        TestOfflineOnlineCommentsAndDanmakuContract(check);
        TestAudioQualityBitrateAndFreezeFixes(check);
        TestMacroArchitectureAndRuntimeHealth(check);
    }

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

            tracksField.SetValue(player, new List<AudioTrack>
            {
                new() { Quality = AudioQuality.High, TrackId = 30280, Label = "高品质", Bandwidth = 192000, Url = "https://example.invalid/192.m4s" },
                new() { Quality = AudioQuality.Dolby, TrackId = 30250, Label = "杜比全景声", Bandwidth = 448000, Url = "https://example.invalid/dolby.m4s" },
            });
            actualAudioField.SetValue(player, AudioQuality.High);
            fillMenu.Invoke(player, new object[] { 80 });

            check(badge.Visibility == Visibility.Visible && badgeText.Text == "192 kbps",
                $"non-HiRes/Dolby audio displays current bitrate badge (visible={badge.Visibility}, text={badgeText.Text})");
            var clickableAudioItems = qualityList.Items.OfType<ListBoxItem>().Where(i => i.Tag is AudioTrack).ToList();
            check(clickableAudioItems.Count == 2
                  && clickableAudioItems.Any(i => ((AudioTrack)i.Tag).Quality == AudioQuality.High)
                  && clickableAudioItems.Any(i => ((AudioTrack)i.Tag).Quality == AudioQuality.Dolby),
                $"QualityList exposes optional Dolby alongside stereo AAC (clickable={clickableAudioItems.Count})");

            // 纯普通 AAC（无杜比/无 Hi-Res）同样显示码率角标与菜单信息
            tracksField.SetValue(player, new List<AudioTrack>
            {
                new() { Quality = AudioQuality.High, TrackId = 30232, Label = "较高音质", Bandwidth = 132000, Url = "https://example.invalid/132.m4s" },
            });
            actualAudioField.SetValue(player, AudioQuality.High);
            fillMenu.Invoke(player, new object[] { 80 });
            check(badge.Visibility == Visibility.Visible && badgeText.Text == "132 kbps",
                $"pure AAC video still displays bitrate badge (text={badgeText.Text})");

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
