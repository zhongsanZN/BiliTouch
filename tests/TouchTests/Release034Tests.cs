using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.Views;

internal static class Release034Tests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static;

    private static void Pump(int ms = 50)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    public static void Run(Action<bool, string> check)
    {
        TestVersionContracts(check);
        TestPressRingAdorner(check);
        TestTripleLikeResponseParsing(check);
        TestTriplePressSuppressionMath(check);
        TestPlayerWindowTripleLikeIntegration(check);
    }

    private static void TestVersionContracts(Action<bool, string> check)
    {
        var asmVer = typeof(App).Assembly.GetName().Version?.ToString(3);
        check(App.AppVersion == "0.3.4", $"App.AppVersion is updated to 0.3.4 (actual={App.AppVersion})");
        check(asmVer == "0.3.4", $"Assembly version matches 0.3.4 (actual={asmVer})");

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var issPath = Path.Combine(root, "Publish", "installer.iss");
        var psPath = Path.Combine(root, "发布.ps1");
        var issText = File.ReadAllText(issPath);
        var psText = File.ReadAllText(psPath);
        check(issText.Contains("#define MyAppVersion \"0.3.4\"", StringComparison.Ordinal)
              && issText.Contains("#define MyFileVersion \"0.3.4.0\"", StringComparison.Ordinal),
            "installer.iss defines 0.3.4 and 0.3.4.0");
        check(psText.Contains("param([string]$Version = \"0.3.4\"", StringComparison.Ordinal),
            "发布.ps1 defaults to version 0.3.4");
    }

    private static void TestPressRingAdorner(Action<bool, string> check)
    {
        var len = PressRingAdorner.RingLength(100, 40, 8);
        var expected = 2 * (100 - 16) + 2 * (40 - 16) + 2 * Math.PI * 8;
        check(Math.Abs(len - expected) < 1e-6, $"PressRingAdorner.RingLength matches rounded-rect perimeter (len={len:F3})");

        var clamped = PressRingAdorner.RingLength(12, 12, 20);
        check(Math.Abs(clamped - 2 * Math.PI * 6) < 1e-6,
            "PressRingAdorner.RingLength clamps corner radius above half edge");

        var btn = new Button { Content = "Like", Width = 80, Height = 36 };
        var host = new AdornerDecorator { Child = btn };
        host.Measure(new Size(200, 100));
        host.Arrange(new Rect(0, 0, 200, 100));
        var beforeDesired = btn.DesiredSize;

        var ring = new PressRingAdorner(btn) { Progress = 0.65, Stroke = Brushes.Crimson };
        check(!ring.IsHitTestVisible, "PressRingAdorner is non-hit-testable");

        var layer = AdornerLayer.GetAdornerLayer(btn);
        check(layer != null, "AdornerLayer is available around Button");
        layer?.Add(ring);
        host.Measure(new Size(200, 100));
        host.Arrange(new Rect(0, 0, 200, 100));
        check(btn.DesiredSize == beforeDesired, "Attaching PressRingAdorner does not alter Button DesiredSize");

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            ring.Progress = 0;
            typeof(PressRingAdorner).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(ring, new object[] { dc });
            ring.Progress = 0.5;
            typeof(PressRingAdorner).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(ring, new object[] { dc });
            ring.Progress = 1.0;
            typeof(PressRingAdorner).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(ring, new object[] { dc });
        }
        check(true, "PressRingAdorner OnRender handles 0, partial, and full progress without error");
    }

    private static void TestTripleLikeResponseParsing(Action<bool, string> check)
    {
        using var fullDoc = JsonDocument.Parse("{\"like\":true,\"coin\":true,\"fav\":true,\"multiply\":2}");
        var full = ApiClient.ParseTripleLikeResponse(0, "0", fullDoc.RootElement);
        check(full.Ok && full.Like && full.Coin && full.Fav && full.Multiply == 2,
            "ParseTripleLikeResponse parses full triple-like payload");

        using var partialDoc = JsonDocument.Parse("{\"like\":true,\"coin\":false,\"fav\":true,\"multiply\":0}");
        var partial = ApiClient.ParseTripleLikeResponse(0, "0", partialDoc.RootElement);
        check(partial.Ok && partial.Like && !partial.Coin && partial.Fav && partial.Multiply == 0,
            "ParseTripleLikeResponse preserves partial coin=false status");

        var risk = ApiClient.ParseTripleLikeResponse(-352, "风控校验失败", null);
        check(!risk.Ok && !string.IsNullOrWhiteSpace(risk.Error) && risk.Error.StartsWith("B 站风控", StringComparison.Ordinal),
            $"ParseTripleLikeResponse maps -352 through FriendlyRiskMessage (actual={risk.Error})");

        check(PlayerWindow.FormatTripleToast(true, true, true) == "一键三连成功",
            "FormatTripleToast returns success message when all three are active");
        var missingToast = PlayerWindow.FormatTripleToast(true, false, true);
        check(missingToast.Contains("投币", StringComparison.Ordinal),
            $"FormatTripleToast reports missing coin state honestly (actual={missingToast})");
    }

    private static void TestTriplePressSuppressionMath(Action<bool, string> check)
    {
        check(!PlayerWindow.IsTripleComboSuppressActive(0, 5000),
            "Combo suppression is inactive when never triggered");
        check(PlayerWindow.IsTripleComboSuppressActive(4000, 4800),
            "Combo suppression blocks immediate post-combo BtnLike Click");
        check(!PlayerWindow.IsTripleComboSuppressActive(4000, 5500),
            "Combo suppression expires after 1500ms");

        long cancelAt = 10_000;
        check(PlayerWindow.TryConsumeCancelTimestamp(ref cancelAt, 10_400) && cancelAt == 0,
            "TryConsumeCancelTimestamp consumes active cancellation within 1000ms window");
        check(!PlayerWindow.TryConsumeCancelTimestamp(ref cancelAt, 10_500),
            "TryConsumeCancelTimestamp only suppresses one Click");

        long staleCancel = 10_000;
        check(!PlayerWindow.TryConsumeCancelTimestamp(ref staleCancel, 11_100) && staleCancel == 0,
            "TryConsumeCancelTimestamp expires stale cancellation after 1000ms (e.g. pointer left button)");
    }

    private static void TestPlayerWindowTripleLikeIntegration(Action<bool, string> check)
    {
        var perMedia = (string[])typeof(PlayerWindow).GetField("PerMediaStateFields", Hidden)!.GetValue(null)!;
        var requiredPerMedia = new[]
        {
            "_favoriteHeld",
            "_tripleComboAt", "_tripleComboBusy",
            "_likeDownTick", "_likePressCancelAt", "_likePressStartTick",
            "_likePressCommitted", "_likeConfirmed", "_activeLikeTouchId"
        };
        var missingPerMedia = requiredPerMedia.Where(n => !perMedia.Contains(n, StringComparer.Ordinal)).ToArray();
        check(missingPerMedia.Length == 0,
            $"PerMediaStateFields includes all triple-like and favorite-hold state fields (missing={string.Join(", ", missingPerMedia)})");

        var win = new PlayerWindow();
        try
        {
            win.Show();
            Pump(60);

            var btnLike = (Button)win.FindName("BtnLike");
            var badge = (Border)win.FindName("TripleComboBadge");
            var themedCoin = (SkinActionIcon)win.FindName("ThemedCoinIcon");
            check(btnLike != null && badge != null && themedCoin != null,
                "PlayerWindow contains BtnLike, TripleComboBadge, and ThemedCoinIcon");
            check(badge != null && badge.Opacity == 0 && !badge.IsHitTestVisible,
                "TripleComboBadge starts invisible and non-hit-testable");
            check(btnLike != null && !Stylus.GetIsPressAndHoldEnabled(btnLike),
                "BtnLike disables Windows Stylus press-and-hold right-click delay for touch triple-like");
            check((string)(btnLike?.ToolTip ?? "") == "点赞 (L)；长按一键三连",
                "BtnLike tooltip advertises long-press triple-like");

            // Simulate applying full triple-like API result onto fresh counts
            SetField(win, "_isLiked", false);
            SetField(win, "_isCoined", false);
            SetField(win, "_isFavorited", false);
            SetField(win, "_likeCount", 10L);
            SetField(win, "_coinCount", 5L);
            SetField(win, "_favCount", 3L);
            themedCoin!.Count = 0;

            win.ApplyTripleLikeSuccess(new ApiClient.TripleLikeResult(true, null, true, true, true, 2));
            check((bool)GetField(win, "_isLiked")!
                  && (bool)GetField(win, "_isCoined")!
                  && (bool)GetField(win, "_isFavorited")!
                  && (long)GetField(win, "_likeCount")! == 11L
                  && (long)GetField(win, "_coinCount")! == 7L
                  && (long)GetField(win, "_favCount")! == 4L
                  && themedCoin.Count == 2,
                "ApplyTripleLikeSuccess updates liked/coined/favorited flags, counts, and ThemedCoinIcon.Count");

            // Already liked video must not double-increment _likeCount on triple combo
            win.ApplyTripleLikeSuccess(new ApiClient.TripleLikeResult(true, null, true, false, false, 0));
            check((long)GetField(win, "_likeCount")! == 11L,
                "ApplyTripleLikeSuccess avoids double-counting already liked video");

            // Verify ShouldSuppressLikeClick only blocks BtnLike within window and does NOT block Key.L (sender=win)
            SetField(win, "_likePressCancelAt", Environment.TickCount64);
            var suppressFn = typeof(PlayerWindow).GetMethod("ShouldSuppressLikeClick", Hidden)!;
            check(!(bool)suppressFn.Invoke(win, new object[] { win })!,
                "ShouldSuppressLikeClick does not swallow keyboard shortcut Key.L (sender=PlayerWindow)");
            check((bool)suppressFn.Invoke(win, new object[] { btnLike! })!,
                "ShouldSuppressLikeClick suppresses BtnLike Click after long-press cancel");
            check(!(bool)suppressFn.Invoke(win, new object[] { btnLike! })!,
                "ShouldSuppressLikeClick consumes cancel timestamp once");

            // Verify ResetForNewMedia resets triple-like & favorite-hold transient state
            SetField(win, "_tripleComboAt", Environment.TickCount64);
            SetField(win, "_tripleComboBusy", true);
            SetField(win, "_likePressCancelAt", Environment.TickCount64);
            SetField(win, "_likePressCommitted", true);
            SetField(win, "_likeConfirmed", true);
            SetField(win, "_activeLikeTouchId", 7);
            SetField(win, "_favoriteHeld", true);

            typeof(PlayerWindow).GetMethod("ResetForNewMedia", Hidden)!.Invoke(win, null);
            check((long)GetField(win, "_tripleComboAt")! == 0L
                  && !(bool)GetField(win, "_tripleComboBusy")!
                  && (long)GetField(win, "_likePressCancelAt")! == 0L
                  && !(bool)GetField(win, "_likePressCommitted")!
                  && !(bool)GetField(win, "_likeConfirmed")!
                  && (int)GetField(win, "_activeLikeTouchId")! == -1
                  && !(bool)GetField(win, "_favoriteHeld")!,
                "ResetForNewMedia clears all triple-like press/suppression and favorite-hold state");
        }
        finally
        {
            win.Close();
            Pump(60);
        }
    }

    private static object? GetField(object target, string name)
        => target.GetType().GetField(name, Hidden)!.GetValue(target);

    private static void SetField(object target, string name, object? value)
        => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
}
