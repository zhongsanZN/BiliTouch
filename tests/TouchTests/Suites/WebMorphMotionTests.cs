using System.Windows;
using BiliTouch.Views;
using BiliTouch.Helpers;

/// <summary>
/// 锁死小窗停靠动画的**手感参数**：
/// 1) 曲线零过冲、单调 —— 旧曲线 ζ≈0.54 会冲到 1.089，观感是"弹一下"；
/// 2) 时长在 400ms 量级 —— HyperOS 那种"贵"的手感区间；
/// 3) 反向打断用更短时长，减少迟滞；
/// 4) 遮罩目标是**真实停靠几何**，不是硬编码 0.42。
/// </summary>
internal static class WebMorphMotionTests
{
    public static void Run(Action<bool, string> check, string artifacts)
    {
        // ---- 1. 曲线形状：不过冲 + 单调 ----
        const double dur = PlayerWindow.WebMorphDurationMs;
        check(dur >= 380 && dur <= 520, $"morph duration sits in the 400ms band: {dur}ms");

        var max = 0.0;
        var prev = 0.0;
        var monotonic = true;
        for (var ms = 0.0; ms <= dur; ms += 4)
        {
            var v = SpringMotion.Advance(0, 0, 1, ms / 1000, PlayerWindow.WebMorphOmega).Position;
            if (v > max) max = v;
            if (v < prev - 1e-9) monotonic = false;
            prev = v;
        }
        check(max <= 1.0 + 1e-6, $"morph curve never overshoots (max={max:F4}, old curve hit 1.089)");
        check(monotonic, "morph curve is monotonic (no visible bounce)");
        check(SpringMotion.Advance(0, 0, 1, 0, PlayerWindow.WebMorphOmega).Position == 0, "morph curve starts at 0");
        check(Math.Abs(SpringMotion.Advance(0, 0, 1, dur / 1000, PlayerWindow.WebMorphOmega).Position - 1) < .00075,
            "morph reaches the landing tolerance without a forced endpoint jump");

        // 收敛性：一半时长时应该已经走完大半（不能"最后一下窜进去"）
        var half = SpringMotion.Advance(0, 0, 1, dur / 2000, PlayerWindow.WebMorphOmega).Position;
        check(half > 0.9 && half < 0.98, $"morph curve responds promptly: {half:F3} at half time");

        // ---- 2. 反向打断更短 ----
        check(PlayerWindow.WebMorphReverseDurationMs < dur,
            $"reverse (interrupting) morph is shorter: {PlayerWindow.WebMorphReverseDurationMs}ms");

        // ---- 3. 目标几何来自真实停靠位 ----
        var player = new BiliTouch.Views.PlayerWindow { Width = 1320, Height = 860 };
        try
        {
            var oldLayout = BiliTouch.Svc.Settings.UseWebPlayerLayout;
            var oldScale = BiliTouch.Svc.Settings.UiScalePercent;
            try
            {
                BiliTouch.Svc.Settings.SetWebPlayerLayout(true);
                BiliTouch.Svc.Settings.SetUiScalePercent(110);
                player.Show();
                Pump(500);

                var call = player.GetType().GetMethod("UpdateWebCoverTargetGeometry",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                call.Invoke(player, null);

                var f = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var scale = (double)player.GetType().GetField("_webCoverTargetScale", f)!.GetValue(player)!;
                var nx = (double)player.GetType().GetField("_webCoverTargetNx", f)!.GetValue(player)!;
                var ny = (double)player.GetType().GetField("_webCoverTargetNy", f)!.GetValue(player)!;

                // 停靠位在右栏：目标必须明显靠右，且缩放明显小于 1
                check(scale > 0.15 && scale < 0.95, $"cover target scale derives from dock geometry: {scale:F3}");
                check(nx > 0.62, $"cover shrinks toward the right rail, not the top-right corner: nx={nx:F3}");
                check(nx + scale / 2 <= 1.001 && nx - scale / 2 >= -0.001,
                    $"cover stays fully inside the video area so the landing jump is covered: nx={nx:F3} s={scale:F3}");
                check(ny > 0.05 && ny < 0.95, $"cover target y follows the dock anchor: ny={ny:F3}");
            }
            finally
            {
                player.Close(); Pump(200);
                BiliTouch.Svc.Settings.SetWebPlayerLayout(oldLayout);
                BiliTouch.Svc.Settings.SetUiScalePercent(oldScale);
            }
        }
        catch (Exception ex)
        {
            check(false, "geometry probe failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static void Pump(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
