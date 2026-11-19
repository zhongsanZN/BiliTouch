using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.Utils;
using BiliTouch.Views;

internal static class Upstream176SyncTests
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static;

    private static void Pump(int ms = 40)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    public static void Run(Action<bool, string> check)
    {
        TestStallRecoveryPolicy(check);
        TestBufferPolicyListenAudio(check);
        TestUrlParserExtractQueryLong(check);
        TestDanmakuMotionCalibrate(check);
        TestWindowMaximizeInset(check);
        TestPopupToggle(check);
        TestLinkLauncher(check);
        TestPlayerWindowAndBrowserWindowContracts(check);
    }

    private static void TestStallRecoveryPolicy(Action<bool, string> check)
    {
        var policy = new StallRecoveryPolicy();
        check(policy.Tick(1000, frozen: true, recoverable: true) == StallRecoveryAction.None,
            "StallRecoveryPolicy does nothing on initial freeze");
        check(policy.Tick(1000 + StallRecoveryPolicy.HintAfterMs, frozen: true, recoverable: true) == StallRecoveryAction.ShowHint,
            "StallRecoveryPolicy shows hint at 6s freeze");
        check(policy.Tick(1000 + StallRecoveryPolicy.RebuildAfterMs, frozen: true, recoverable: true) == StallRecoveryAction.Rebuild
              && policy.Rebuilds == 1,
            "StallRecoveryPolicy triggers 1st Rebuild at 10s freeze");

        // OnUnfrozen preserves rebuild count & backoff
        policy.OnUnfrozen();
        check(policy.Rebuilds == 1 && !policy.IsFrozen,
            "StallRecoveryPolicy.OnUnfrozen clears frozen baseline while preserving rebuild budget");

        var t2 = 1000 + StallRecoveryPolicy.RebuildAfterMs;
        check(policy.Tick(t2 + 7_999, frozen: true, recoverable: true) == StallRecoveryAction.None,
            "StallRecoveryPolicy respects 8s backoff after 1st rebuild");
        check(policy.Tick(t2 + 8_000, frozen: true, recoverable: true) == StallRecoveryAction.Rebuild
              && policy.Rebuilds == 2,
            "StallRecoveryPolicy triggers 2nd Rebuild after 8s backoff");
        check(policy.Tick(t2 + 8_000 + 15_000, frozen: true, recoverable: true) == StallRecoveryAction.Rebuild
              && policy.Rebuilds == 3,
            "StallRecoveryPolicy triggers 3rd Rebuild after 15s backoff");
        check(policy.Tick(t2 + 8_000 + 15_000 + 25_000, frozen: true, recoverable: true) == StallRecoveryAction.GiveUp
              && policy.GaveUp,
            "StallRecoveryPolicy gives up after 3 failed rebuilds and 25s backoff");

        policy.OnAdvance();
        check(policy.Rebuilds == 0 && !policy.GaveUp,
            "StallRecoveryPolicy.OnAdvance resets rebuild budget once playback progresses");
    }

    private static void TestBufferPolicyListenAudio(Action<bool, string> check)
    {
        var autoMs = BufferPolicy.Resolve(BufferMediaKind.ListenAudio, "https://upos.bilivideo.com/a.m4s", shortVideo: false, 500, auto: true);
        check(autoMs >= 2000, $"BufferPolicy.ListenAudio auto uses >=2000ms floor (actual={autoMs})");
        var manualMs = BufferPolicy.Resolve(BufferMediaKind.ListenAudio, "https://upos.bilivideo.com/a.m4s", shortVideo: false, 300, auto: false);
        check(manualMs >= 1000, $"BufferPolicy.ListenAudio manual enforces >=1000ms floor (actual={manualMs})");
    }

    private static void TestUrlParserExtractQueryLong(Action<bool, string> check)
    {
        var deadline = UrlParser.ExtractQueryLong("https://upos.bilivideo.com/v.m4s?deadline=1763456789&gen=playurlv2", "deadline");
        check(deadline == 1763456789L, $"UrlParser.ExtractQueryLong extracts deadline (actual={deadline})");
        check(UrlParser.ExtractQueryLong("//upos.bilivideo.com/v.m4s?deadline=42", "deadline") == 42L,
            "UrlParser.ExtractQueryLong handles protocol-relative URLs");
        check(UrlParser.ExtractQueryLong("https://upos.bilivideo.com/v.m4s?deadline=abc", "deadline") == null
              && UrlParser.ExtractQueryLong("not a url", "deadline") == null,
            "UrlParser.ExtractQueryLong returns null on invalid/missing params");
    }

    private static void TestDanmakuMotionCalibrate(Action<bool, string> check)
    {
        check(DanmakuMotion.Calibrate(10.0, 10.015) == 10.0,
            "DanmakuMotion.Calibrate ignores tiny <=20ms phase noise");
        var soft = DanmakuMotion.Calibrate(10.0, 10.8);
        check(Math.Abs(soft - 10.2) < 1e-9,
            $"DanmakuMotion.Calibrate soft-converges moderate drift by 25% (actual={soft})");
        check(DanmakuMotion.Calibrate(10.0, 12.0) == 12.0,
            "DanmakuMotion.Calibrate hard-snaps on large seek jump (>=1.5s)");
        check(DanmakuMotion.Calibrate(10.0, double.NaN) == 10.0,
            "DanmakuMotion.Calibrate ignores NaN measurement");
    }

    private static void TestWindowMaximizeInset(Action<bool, string> check)
    {
        var inset = WindowMaximizeInset.Compute(-9, -9, 1929, 1029, 0, 0, 1920, 1020, 1.25, 1.25);
        check(Math.Abs(inset.Left - 7.2) < 1e-6 && Math.Abs(inset.Right - 7.2) < 1e-6
              && Math.Abs(inset.Top - 7.2) < 1e-6 && Math.Abs(inset.Bottom - 7.2) < 1e-6,
            "WindowMaximizeInset computes 7.2 DIP inset on 125% DPI maximized chrome");
        var zero = WindowMaximizeInset.Compute(0, 0, 1920, 1080, 0, 0, 1920, 1080, 1.0, 1.0);
        check(zero == new WindowInsets(0, 0, 0, 0),
            "WindowMaximizeInset produces 0 insets when window already matches target monitor");
        var clamped = WindowMaximizeInset.Compute(10, 10, 1910, 1070, 0, 0, 1920, 1080, 0, double.NaN);
        check(clamped == new WindowInsets(0, 0, 0, 0),
            "WindowMaximizeInset clamps negative diffs and invalid DPI scale");
    }

    private static void TestPopupToggle(Action<bool, string> check)
    {
        check(PopupToggle.Decide(isOpen: true, closedAtTick: null, nowTick: 1000) == PopupToggle.Action.Close,
            "PopupToggle closes when currently open");
        check(PopupToggle.Decide(isOpen: false, closedAtTick: 900, nowTick: 1100) == PopupToggle.Action.StayClosed,
            "PopupToggle stays closed within suppression window after mouse-down close");
        check(PopupToggle.Decide(isOpen: false, closedAtTick: 500, nowTick: 1100) == PopupToggle.Action.Open,
            "PopupToggle opens normally after suppression window elapses");
    }

    private static void TestLinkLauncher(Action<bool, string> check)
    {
        check(LinkLauncher.IsWebUrl("https://www.bilibili.com/video/BV1xx")
              && LinkLauncher.IsWebUrl("http://live.bilibili.com/1")
              && !LinkLauncher.IsWebUrl("javascript:alert(1)")
              && !LinkLauncher.IsWebUrl("file:///C:/Windows/win.ini"),
            "LinkLauncher.IsWebUrl allows http/https only and rejects dangerous schemes");
    }

    private static void TestPlayerWindowAndBrowserWindowContracts(Action<bool, string> check)
    {
        // Verify ApiClient._buvidGate field exists for serialized fingerprint init
        var buvidGateField = typeof(ApiClient).GetField("_buvidGate", BindingFlags.Instance | BindingFlags.NonPublic);
        check(buvidGateField != null, "ApiClient defines _buvidGate semaphore for EnsureBuvidAsync concurrency safety");

        // Verify BrowserWindow removes redundant UrlText bar and cleans up neatly
        var bw = new BrowserWindow("https://www.bilibili.com", "BiliTouch Web");
        try
        {
            check(bw.FindName("UrlText") == null && bw.FindName("Wv") != null,
                "BrowserWindow removes duplicate 44px UrlText bar while preserving WebView2 host");
        }
        finally
        {
            bw.Close();
        }

        // Verify PlayerWindow instantiates cleanly, ResetForNewMedia passes VerifyResetCoverage, and Ctrl+C is not swallowed
        var win = new PlayerWindow();
        try
        {
            win.Show();
            Pump(50);

            var verify = typeof(PlayerWindow).GetMethod("VerifyResetCoverage", Hidden);
            Exception? verifyErr = null;
            try { verify?.Invoke(win, null); } catch (Exception ex) { verifyErr = ex.InnerException ?? ex; }
            check(verifyErr == null, $"PlayerWindow.VerifyResetCoverage passes all new fields (err={verifyErr?.Message ?? "none"})");

            // Check Ctrl/Alt modifier bypass vs Shift/None shortcuts
            var bypass = typeof(PlayerWindow).GetMethod("ShouldBypassShortcutModifiers", Hidden)!;
            check((bool)bypass.Invoke(null, new object[] { ModifierKeys.Control })!
                  && (bool)bypass.Invoke(null, new object[] { ModifierKeys.Alt })!
                  && !(bool)bypass.Invoke(null, new object[] { ModifierKeys.Shift })!
                  && !(bool)bypass.Invoke(null, new object[] { ModifierKeys.None })!,
                "PlayerWindow.ShouldBypassShortcutModifiers passes Ctrl/Alt while preserving Shift and bare keys");
            var reset = typeof(PlayerWindow).GetMethod("ResetForNewMedia", Hidden)!;
            reset.Invoke(win, null);
            var perMedia = (string[])typeof(PlayerWindow).GetField("PerMediaStateFields", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            check(perMedia.Contains("_playUrlDeadline") && perMedia.Contains("_seekBounceAt") && perMedia.Contains("_errorRecoveryTried"),
                "PerMediaStateFields includes all new seek/recovery tracking fields");
        }
        finally
        {
            win.Close();
            Pump(60);
        }
    }
}
