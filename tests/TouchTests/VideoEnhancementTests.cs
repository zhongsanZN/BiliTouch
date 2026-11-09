using BiliTouch.Services;

/// <summary>
/// 锁死「画质增强开着时 VSR 每秒级 ON/OFF 把 UI 抖一遍」这个真机问题。
/// 用户 video-enhancement.log 最后 40 行里有 33 次 turning VSR ON/OFF。
/// 旧实现把 OFF 也解析成一个新状态，于是每次切换都 Changed?.Invoke()
/// ⇒ 播放器按钮 / ToolTip 每 1~2 秒重刷一次。
/// </summary>
internal static class VideoEnhancementTests
{
    public static void Run(Action<bool, string> check)
    {
        var on = VideoEnhancement.ParseState("direct3d11: turning VSR ON");
        check(on != null && on.Contains("已启用"), "VSR ON confirms vendor super resolution");

        check(VideoEnhancement.ParseState("direct3d11: turning VSR OFF") == null,
            "VSR OFF is a driver transient and does not overwrite the confirmed state");

        // 模拟真机切换风暴：20 次 ON/OFF 交替，状态最多只该变 2 次
        // （scaler 创建 → 驱动确认），不该变成 20 次。
        string? state = null;
        var changes = 0;
        var messages = new List<string> { "direct3d11: Using Super Resolution scaler with B8G8R8A8 output" };
        for (var i = 0; i < 10; i++)
        {
            messages.Add("direct3d11: turning VSR ON");
            messages.Add("direct3d11: turning VSR OFF");
        }
        foreach (var msg in messages)
        {
            var parsed = VideoEnhancement.ParseState(msg);
            if (parsed != null && parsed != state) { state = parsed; changes++; }
        }
        check(changes <= 2,
            $"VSR toggle storm does not churn UI state: {changes} state changes for 20 toggles");
        check(state != null && state.Contains("已启用"),
            "final enhancement state stays confirmed instead of flipping to standby");

        check(VideoEnhancement.ParseState("direct3d11: Using VideoProcessor scaler") == "视频处理器缩放已启用",
            "video processor scaler state still parsed");
        check(VideoEnhancement.ParseState("direct3d11: Super Resolution filter not supported") != null,
            "unsupported-driver fallback state still parsed");
        check(VideoEnhancement.ParseState("main: some unrelated debug line") == null,
            "unrelated libvlc lines are ignored");

        check(VideoEnhancement.ShouldLogVsrToggle(), "first VSR toggle is logged");
        var extraLogs = 0;
        for (var i = 0; i < 20; i++) if (VideoEnhancement.ShouldLogVsrToggle()) extraLogs++;
        check(extraLogs == 0, "VSR toggle storm is throttled to one log line per 2 seconds");
    }
}
