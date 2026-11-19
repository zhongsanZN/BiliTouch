using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Controls;
using BiliTouch.Views.Pages;

internal static class Release033Tests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static;

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Task task)
    {
        var start = Environment.TickCount64;
        while (!task.IsCompleted && Environment.TickCount64 - start < 5000) Pump(15);
        if (!task.IsCompleted) throw new TimeoutException("Task did not complete in test dispatcher");
        task.GetAwaiter().GetResult();
    }

    public static void Run(Action<bool, string> check, string artifacts)
    {
        TestDanmakuOutlinePopupIsolation(check);
        TestPullToRefreshTouchAndWheelRebound(check);
        TestSeamlessMorphDeferralAndCache(check);
        TestXteamNativeParsingAndPage(check);
        TestEmotesAndCommentPostingFeedback(check);
        TestOfflineEntryPlaybackStaysLocal(check);
    }

    private static void TestDanmakuOutlinePopupIsolation(Action<bool, string> check)
    {
        var oldOutline = Svc.Settings.DanmakuOutline;
        var win = new PlayerWindow();
        try
        {
            win.Show();
            Pump(150);
            var popup = (Popup)win.FindName("DanmakuAreaPopup")!;
            var checkBox = (CheckBox)win.FindName("DanmakuOutlineCheck")!;
            var stateText = (TextBlock)win.FindName("DanmakuOutlineStateText")!;
            popup.IsOpen = true;
            Pump(40);

            var before = Svc.Settings.DanmakuOutline;
            var isInPopupMethod = typeof(PlayerWindow).GetMethod("IsInAnyPlayerPopup", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var inside = (bool)isInPopupMethod.Invoke(win, new object?[] { checkBox })!;
            check(inside, "IsInAnyPlayerPopup identifies DanmakuOutlineCheck inside DanmakuAreaPopup");

            // 1. Route real tunneling/bubbling Mouse.PreviewMouseDownEvent -> Mouse.MouseDownEvent ->
            //    Mouse.PreviewMouseUpEvent -> Mouse.MouseUpEvent on stateText through VideoAreaRoot
            var downPreviewRow = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = stateText
            };
            stateText.RaiseEvent(downPreviewRow);
            var downBubbleRow = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseDownEvent,
                Source = stateText
            };
            stateText.RaiseEvent(downBubbleRow);
            var upPreviewRow = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseUpEvent,
                Source = stateText
            };
            stateText.RaiseEvent(upPreviewRow);
            check(!upPreviewRow.Handled, "OnVideoAreaMouseLeftUp does not swallow PreviewMouseUp inside DanmakuAreaPopup");

            var upBubbleRow = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseUpEvent,
                Source = stateText
            };
            stateText.RaiseEvent(upBubbleRow);
            Pump(40);

            check(Svc.Settings.DanmakuOutline == !before && popup.IsOpen,
                "Clicking 弹幕描边 row toggles setting cleanly without closing DanmakuAreaPopup");

            // 2. Route tunneling PreviewMouseUp on DanmakuOutlineCheck directly and verify Mouse.Captured is never stuck
            var upPreviewCheck = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseUpEvent,
                Source = checkBox
            };
            checkBox.RaiseEvent(upPreviewCheck);
            check(!upPreviewCheck.Handled && Mouse.Captured == null,
                "PreviewMouseUp on DanmakuOutlineCheck is not swallowed and never leaves Mouse.Captured stuck");

            var isFastForward = (bool)typeof(PlayerWindow).GetField("_isFastForwarding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(win)!;
            check(!isFastForward, "Clicking 弹幕描边 inside popup never starts video fast-forward");
        }
        finally
        {
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
                outline: oldOutline);
            win.Close();
            Pump(60);
        }
    }

    private static void TestPullToRefreshTouchAndWheelRebound(Action<bool, string> check)
    {
        var stack = new StackPanel();
        stack.Children.Add(new Border { Height = 900, Background = Brushes.Transparent });
        var scroll = new ScrollViewer { Content = stack };
        var root = new Grid();
        root.Children.Add(scroll);
        var page = new UserControl { Content = root };
        var refreshes = 0;
        var gestures = ModuleGestures.Install(page, () => { refreshes++; return Task.CompletedTask; });
        var win = new Window { Width = 500, Height = 400, Content = page, ShowActivated = false };
        try
        {
            win.Show();
            Pump(80);

            var begin = typeof(ModuleGestures).GetMethod("Begin", Hidden)!;
            var move = typeof(ModuleGestures).GetMethod("Move", Hidden)!;
            var end = typeof(ModuleGestures).GetMethod("End", Hidden)!;
            var pullField = typeof(ModuleGestures).GetField("_pull", Hidden)!;
            var offset = (TranslateTransform)typeof(ModuleGestures).GetField("_contentOffset", Hidden)!.GetValue(gestures)!;
            var hintText = (TextBlock)typeof(ModuleGestures).GetField("_hintText", Hidden)!.GetValue(gestures)!;

            // Downward touch drag engages at 8px (before WPF ScrollViewer ~10px capture threshold)
            begin.Invoke(gestures, new object?[] { new Point(120, 60), stack });
            var engagedEarly = (bool)move.Invoke(gestures, new object?[] { new Point(120, 68) })!;
            check(engagedEarly && (bool)pullField.GetValue(gestures)! && offset.Y > 0,
                $"Touch downward pull engages early at 8px before ScrollViewer panning (offset={offset.Y:0.0}px)");
            check(hintText.Visibility == Visibility.Visible,
                "Pull-to-refresh hint text stays visible alongside spinner");
            end.Invoke(gestures, null);
            var touchReboundDeadline = Environment.TickCount64 + 800;
            while (Math.Abs(offset.Y) >= 1.0 && Environment.TickCount64 < touchReboundDeadline) Pump(20);
            check(Math.Abs(offset.Y) < 1.0,
                $"Short touch pull rebounds cleanly back to 0 (offset={offset.Y:0.0}px)");

            // Precision trackpad / wheel downward pull at scroll top
            var wheelField = typeof(ModuleGestures).GetField("_wheel", Hidden)!;
            var finishWheel = typeof(ModuleGestures).GetMethod("FinishWheelAsync", Hidden)!;
            var attachTarget = typeof(ModuleGestures).GetMethod("AttachPullTarget", Hidden)!;
            var applyImm = typeof(ModuleGestures).GetMethod("ApplyPullOffsetImmediate", Hidden)!;
            // Drive wheel pull state below threshold then finish -> elastic rebound without triggering
            attachTarget.Invoke(gestures, new object?[] { scroll });
            wheelField.SetValue(gestures, 110.0);
            applyImm.Invoke(gestures, new object?[] { 40.0 });
            check(offset.Y > 20, $"Wheel/trackpad pull translates scroll content elastically ({offset.Y:0.0}px)");
            PumpUntil((Task)finishWheel.Invoke(gestures, null)!);
            var deadline = Environment.TickCount64 + 800;
            while (Math.Abs(offset.Y) >= 1.0 && Environment.TickCount64 < deadline) Pump(20);
            check(Math.Abs(offset.Y) < 1.0 && refreshes == 0,
                $"Wheel/trackpad pull below threshold rebounds elastically without triggering refresh (offset={offset.Y:0.0}px)");

            // Wheel/trackpad pull above threshold (240) triggers refresh
            wheelField.SetValue(gestures, 260.0);
            PumpUntil((Task)finishWheel.Invoke(gestures, null)!);
            Pump(80);
            check(refreshes == 1, $"Wheel/trackpad pull >= 240 triggers refresh cleanly (count={refreshes})");
        }
        finally
        {
            win.Close();
        }
    }

    private static void TestSeamlessMorphDeferralAndCache(Action<bool, string> check)
    {
        var win = new PlayerWindow();
        try
        {
            win.Show();
            Pump(100);
            var ensureMethod = typeof(PlayerWindow).GetMethod("EnsureSeamlessTransformsMounted", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ensureMethod.Invoke(win, null);
            var heroCard = (Border)win.FindName("SeamlessHeroCard")!;
            var overlay = (Grid)win.FindName("SeamlessMorphOverlay")!;
            check(heroCard.CacheMode is BitmapCache && !heroCard.UseLayoutRounding && !overlay.UseLayoutRounding,
                "SeamlessMorph pre-caches hero card bitmap and disables layout rounding during flight");

            var seamless = typeof(PlayerWindow).GetField("_seamless", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(win)!;
            var entryField = seamless.GetType().GetField("EntryWindow")!;
            var runningField = seamless.GetType().GetField("IsRunning")!;
            var reverseField = seamless.GetType().GetField("IsReverse")!;
            var deferMethod = typeof(PlayerWindow).GetMethod("RunOrDeferAfterSeamlessEntry", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var stopMethod = typeof(PlayerWindow).GetMethod("StopSeamlessMorph", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var stageWin = new Window { Width = 200, Height = 120, ShowActivated = false };
            entryField.SetValue(seamless, stageWin);
            runningField.SetValue(seamless, true);
            reverseField.SetValue(seamless, false);
            try
            {
                var ran = false;
                deferMethod.Invoke(win, new object[] { new Action(() => ran = true) });
                check(!ran, "Heavy post-detail UI updates defer while SeamlessMorph is actively flying");
                entryField.SetValue(seamless, null);
                stopMethod.Invoke(win, null);
                check(ran, "Deferred post-detail UI updates execute immediately upon StopSeamlessMorph()");
            }
            finally
            {
                stageWin.Close();
            }
        }
        finally
        {
            win.Close();
            Pump(60);
        }
    }

    private static void TestXteamNativeParsingAndPage(Action<bool, string> check)
    {
        var sampleCardHtml = """
        <section id="works">
          <div class="showcase-grid">
            <article class="xcard xcard--pinned" data-pick-category="game" data-card-href="/works/star-rougelike/">
              <div class="xcard-badges-bar">
                <span class="status-name">抢先体验中</span>
                <span class="xcard-pinned-pill">置顶</span>
                <span class="ip-tag">吉星派对</span>
              </div>
              <div class="xcard-cover-wrapper"><img src="/covers/game1.png" /></div>
              <h3 class="xcard-title"><span class="main-title">星引擎小游戏</span><span class="sub-title">Demo</span></h3>
              <p class="xcard-subtitle">一句话简介</p>
              <p class="xcard-description">原生桌面端完整支持解析并展示。</p>
              <a class="author-link" href="https://space.bilibili.com/3493123456"><span class="author-name-text">星创组测试UP</span></a>
              <div class="xcard-actions">
                <a class="btn-primary" href="/works/star-rougelike/">进入体验 →</a>
              </div>
            </article>
          </div>
          <script type="application/json" id="home-ost-data">{"packs":[{"id":"pack1","name":"核心曲包","category":"BGM","tracks":[{"id":"t1","title":"星之派对","file":"party.ogg"}]}]}</script>
        </section>
        """;

        var data = XteamService.ParseSite(sampleCardHtml, sampleCardHtml, "", "", "");
        check(data.Works.Count == 1
            && data.Works[0].Title == "星引擎小游戏"
            && data.Works[0].SubTitle == "Demo"
            && data.Works[0].IsPinned
            && data.Works[0].AuthorUid == "3493123456"
            && data.Works[0].CoverUrl == "https://xteamparty.com/covers/game1.png",
            "XteamService.ParseSite extracts native work cards, pinned state, cover URL, and Bilibili UID");
        check(data.AudioPacks.Count == 1 && data.AudioPacks[0].Tracks[0].Url == "https://xteamparty.com/audio/party.ogg",
            "XteamService.ParseSite extracts embedded OST packs and track URLs");

        var sampleDetailHtml = """
        <nav class="breadcrumb-bar"><a class="crumb-link" href="/works/">自研作品</a></nav>
        <div class="detail-cover-box"><img src="/covers/detail.png" /></div>
        <span class="status-pill"><span>正式版</span></span>
        <span class="date-pill">2026-10-01</span>
        <h1 class="detail-title">星创组测试作</h1>
        <p class="detail-subtitle">原生详情页档案</p>
        <div class="detail-meta-tags"><a href="https://space.bilibili.com/123456"><span class="meta-value author-name-highlight"><a href="https://space.bilibili.com/123456">作者A ↗</a></span></a></div>
        <section><div class="panel-body rich-desc"><p>第一段详细说明。</p><p>第二段详细说明。</p></div></section>
        <div class="gallery-grid"><img data-lightbox-src="/shots/1.jpg" /><img data-lightbox-src="/shots/2.jpg" /></div>
        """;
        var detail = XteamService.ParseDetail(sampleDetailHtml, "https://xteamparty.com/works/star-rougelike/");
        check(detail.Title == "星创组测试作"
            && detail.AuthorUid == "123456"
            && detail.Paragraphs.Count == 2
            && detail.Screenshots.Count == 2,
            "XteamService.ParseDetail extracts native detail metadata, paragraphs, and screenshots");

        var cachedField = typeof(XteamService).GetField("_cachedSite", BindingFlags.Static | BindingFlags.NonPublic);
        var prevCached = cachedField?.GetValue(null);
        cachedField?.SetValue(null, data);
        var page = new StarPartyPage();
        var win = new Window { Width = 900, Height = 640, Content = page, ShowActivated = false };
        try
        {
            win.Show();
            Pump(40);
            var isPlayableMethod = typeof(StarPartyPage).GetMethod("IsPlayableGameUrl", BindingFlags.Static | BindingFlags.NonPublic)!;
            var playUrlDetected = (bool)isPlayableMethod.Invoke(null, new object[] { "https://xteamparty.com/play/crazy-united-city/" })!;
            var worksUrlNotGame = !(bool)isPlayableMethod.Invoke(null, new object[] { "https://xteamparty.com/works/crazy-united-city/" })!;
            check(playUrlDetected && worksUrlNotGame,
                "StarPartyPage.IsPlayableGameUrl identifies /play/... web games for inline runner instead of external BrowserWindow");

            var gameOverlay = (FrameworkElement)page.FindName("GameRunnerOverlay")!;
            gameOverlay.Visibility = Visibility.Visible;
            var steppedBack = page.TryStepBack();
            check(steppedBack && gameOverlay.Visibility == Visibility.Collapsed,
                "StarPartyPage.TryStepBack closes inline GameRunnerOverlay before exiting StarPartyPage");

            win.Close();
            check(true, "StarPartyPage mounts and unloads cleanly without initializing WebView2");
        }
        finally
        {
            cachedField?.SetValue(null, prevCached);
        }
    }

    private static void TestEmotesAndCommentPostingFeedback(Action<bool, string> check)
    {
        var panelJson = """
        {
          "packages": [
            {
              "id": 1,
              "text": "小黄脸",
              "type": 1,
              "url": "//i0.hdslb.com/bfs/emote/sm.png",
              "emote": [
                { "id": 10, "text": "[微笑]", "url": "//i0.hdslb.com/bfs/emote/smile.png", "meta": { "size": 1 } }
              ]
            },
            {
              "id": 999,
              "text": "吉星派对收藏集",
              "type": 3,
              "url": "//i0.hdslb.com/bfs/garb/pack.png",
              "flags": { "unlocked": true },
              "emote": [
                { "id": 99901, "text": "[吉星派对_干杯]", "url": "//i0.hdslb.com/bfs/garb/cheer.png", "meta": { "size": 2 } }
              ]
            },
            {
              "id": 4,
              "text": "颜文字",
              "type": 4,
              "emote": [
                { "id": 401, "text": "(⌒▽⌒)", "url": "" }
              ]
            }
          ],
          "all_packages": [
            {
              "id": 1000,
              "text": "未解锁装扮",
              "type": 3,
              "flags": { "no_access": true },
              "emote": [
                { "id": 10001, "text": "[未解锁_哭]", "url": "//i0.hdslb.com/bfs/garb/locked.png", "meta": { "size": 2 } }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(panelJson);
        var (pkgs, lookup) = ApiClient.ParseEmotePanelJson(doc.RootElement);
        check(pkgs.Count == 4
            && pkgs[1].IsCollection && pkgs[1].Unlocked && pkgs[1].Emotes[0].Size == 2
            && !pkgs[3].Unlocked
            && pkgs[2].Emotes[0].IsTextOnly,
            "ApiClient.ParseEmotePanelJson extracts unlocked 收藏集 packs, locked suits, and text-only kaomoji");
        check(lookup.ContainsKey("[吉星派对_干杯]") && lookup["[吉星派对_干杯]"].Url == "https://i0.hdslb.com/bfs/garb/cheer.png",
            "ApiClient.ParseEmotePanelJson builds shortcode lookup for collectible emotes");

        var addReplyJson = """
        {
          "rpid": 12345678901,
          "rpid_str": "12345678901",
          "success_toast": "发送成功",
          "need_captcha": false,
          "reply": {
            "rpid": 12345678901,
            "oid": 999,
            "type": 1,
            "mid": 42,
            "ctime": 1760000000,
            "like": 0,
            "member": { "mid": "42", "uname": "测试号", "avatar": "//i0.hdslb.com/face.jpg", "level_info": { "current_level": 6 } },
            "content": {
              "message": "太好看了[吉星派对_干杯]",
              "emote": {
                "[吉星派对_干杯]": { "url": "//i0.hdslb.com/bfs/garb/cheer.png", "meta": { "size": 2 } }
              }
            }
          }
        }
        """;
        using var replyDoc = JsonDocument.Parse(addReplyJson);
        var okRes = ApiClient.ParsePostCommentResponse(0, "0", replyDoc.RootElement);
        check(okRes.Ok && okRes.Rpid == 12345678901 && okRes.SuccessToast == "发送成功"
            && okRes.Reply != null
            && okRes.Reply.UserName == "测试号"
            && okRes.Reply.Emotes.ContainsKey("[吉星派对_干杯]"),
            "ApiClient.ParsePostCommentResponse extracts server-echoed CommentItem with 收藏集 emotes & success_toast");

        var captchaJson = """{ "need_captcha": true, "url": "//www.bilibili.com/h5/comment/captcha?id=1" }""";
        using var capDoc = JsonDocument.Parse(captchaJson);
        var capRes = ApiClient.ParsePostCommentResponse(0, "0", capDoc.RootElement);
        check(!capRes.Ok && capRes.NeedCaptcha && capRes.CaptchaUrl.StartsWith("https://www.bilibili.com/"),
            "ApiClient.ParsePostCommentResponse surfaces comment captcha challenges instead of silent success");

        var picker = new EmotePickerPanel();
        var win = new Window { Width = 420, Height = 380, Content = picker, ShowActivated = false };
        win.Show();
        Pump(40);
        win.Close();
        check(true, "EmotePickerPanel initializes cleanly in WPF host");
    }

    private static void TestOfflineEntryPlaybackStaysLocal(Action<bool, string> check)
    {
        var sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/touch-sample-silent.mp4"));
        var root = Path.Combine(Path.GetTempPath(), "BiliTouch-offline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "videos"));
        var id = Guid.NewGuid().ToString("N");
        var localMp4 = Path.Combine(root, "videos", id + ".mp4");
        File.Copy(sample, localMp4, overwrite: true);

        var entry = new OfflineEntry
        {
            Id = id,
            Bvid = "BV1xx411c7mD",
            Cid = 123456,
            Title = "离线缓存测试视频",
            PartTitle = "P1",
            Author = "测试UP",
            RootDirectory = root,
            State = OfflineState.Completed,
            Done = new FileInfo(localMp4).Length,
            Total = new FileInfo(localMp4).Length,
        };

        var win = new PlayerWindow { ShowActivated = false };
        try
        {
            // Simulate WarmUp -> PlayOfflineEntry -> Show -> OnLoaded sequence
            win.PlayOfflineEntry(entry);
            win.Show();

            var deadline = Environment.TickCount64 + 3500;
            var lastPlayUrlField = typeof(PlayerWindow).GetField("_lastPlayUrl", Hidden)!;
            var isLocalField = typeof(PlayerWindow).GetField("_isLocalPlayback", Hidden)!;
            var loadRunningField = typeof(PlayerWindow).GetField("_loadRunning", Hidden)!;
            var currentBvidField = typeof(PlayerWindow).GetField("_currentBvid", Hidden)!;
            var mp = (LibVLCSharp.Shared.MediaPlayer)typeof(PlayerWindow).GetField("_mp", Hidden)!.GetValue(win)!;
            mp.Volume = 0;

            while (Environment.TickCount64 < deadline)
            {
                var url = (string)(lastPlayUrlField.GetValue(win) ?? "");
                if (string.Equals(url, localMp4, StringComparison.OrdinalIgnoreCase) && mp.IsPlaying)
                    break;
                Pump(40);
            }
            Pump(200);

            var finalUrl = (string)(lastPlayUrlField.GetValue(win) ?? "");
            var isLocal = (bool)isLocalField.GetValue(win)!;
            var loadRunning = (bool)loadRunningField.GetValue(win)!;
            var currentBvid = (string)(currentBvidField.GetValue(win) ?? "");
            var failOverlay = (FrameworkElement)win.FindName("FailOverlay")!;

            check(isLocal && !loadRunning && string.IsNullOrEmpty(currentBvid),
                $"PlayOfflineEntry before Show/OnLoaded stays in local playback without triggering LoadVideoAsync (isLocal={isLocal}, loadRunning={loadRunning}, bvid='{currentBvid}')");
            check(string.Equals(finalUrl, localMp4, StringComparison.OrdinalIgnoreCase) && failOverlay.Visibility != Visibility.Visible,
                $"PlayOfflineEntry plays local cached file instead of online stream (url='{finalUrl}')");

            // Also ensure OnRetryClick stays in local/offline playback
            var onRetry = typeof(PlayerWindow).GetMethod("OnRetryClick", Hidden)!;
            onRetry.Invoke(win, new object?[] { win, new RoutedEventArgs() });
            Pump(150);
            check((bool)isLocalField.GetValue(win)! && !(bool)loadRunningField.GetValue(win)!,
                "OnRetryClick during offline playback retries locally instead of calling LoadVideoAsync");
        }
        finally
        {
            win.Close();
            Pump(80);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
