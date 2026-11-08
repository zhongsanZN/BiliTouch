using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.Views;

namespace BiliTouch;

/// <summary>
/// 一次性的「退出全屏不闪退」自证探针 —— 给发布前用，不给用户用。
///
/// 用法（在**装好的**程序目录里跑，隔离配置目录，不碰用户数据）：
/// <code>
///   BiliTouch.exe --verify-fullscreen --media=D:\clip.mp4 --result=D:\out.json
/// </code>
///
/// 它验的正是用户报的那条路径（2026-11-08）：
///   **网页布局 + 画质增强（d3d11 processor/super）+ 反复进出全屏**。
///   用户实测"只有网页布局会这样" —— 因为挂/卸网页布局会把原生视频窗口搬进另一个容器、
///   显式改它的 Width/Height/Margin，每一次都是 SetWindowPos ⇒ 一次 vout 重协商；
///   叠在全屏切换的尺寸剧变上，就撞上 libvlccore 那句 0/0（见 VlcPlacePictureGuard）。
///
/// ★ 每一步都往结果文件里写一行进度（<c>steps</c> 数组 + <c>lastStep</c>）。
///   这不是啰嗦：这个探针验的是**进程级闪退**，崩溃时 finally 根本不会执行 ——
///   没有逐步落盘，事后就只能拿到"文件不存在"，等于什么都没验。
/// </summary>
internal static class FullscreenVerification
{
    internal static async Task RunAsync(Application app, string[] args)
    {
        var output = ArgValue(args, "--result=") ?? Path.Combine(Path.GetTempPath(), "bilitouch-fullscreen.json");
        var media = ArgValue(args, "--media=");
        var rounds = int.TryParse(ArgValue(args, "--rounds="), out var parsed) ? Math.Clamp(parsed, 1, 40) : 8;
        output = Path.GetFullPath(output);

        // 隔离配置目录：与 PublishVerification 同一套做法，绝不读写用户的 settings/session。
        var root = Path.Combine(Path.GetDirectoryName(output)!, "fullscreen-profile");
        Directory.CreateDirectory(root);
        typeof(AppPaths).GetProperty(nameof(AppPaths.DataDir))!.SetValue(null, root);
        typeof(AppPaths).GetProperty(nameof(AppPaths.CacheRoot))!.SetValue(null, root);

        var results = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["version"] = App.AppVersion,
            ["baseDirectory"] = AppContext.BaseDirectory,
            ["roundsRequested"] = rounds,
            ["steps"] = new System.Collections.Generic.List<string>(),
            ["vlcLog"] = new System.Collections.Generic.List<string>(),
            ["surfaceSizes"] = new System.Collections.Generic.List<string>()
        };
        var steps = (System.Collections.Generic.List<string>)results["steps"]!;

        void Step(string message)
        {
            steps.Add($"{DateTime.Now:HH:mm:ss.fff} {message}");
            results["lastStep"] = message;
            Flush();
        }

        void Flush()
        {
            try
            {
                File.WriteAllText(output, JsonSerializer.Serialize(results,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 落盘失败不该让探针本身失败 */ }
        }

        // 硬崩溃(原生异常)走不到 finally，这条兜底至少把"崩在哪一步"留下来。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            results["passed"] = false;
            results["nativeCrash"] = e.ExceptionObject?.ToString();
            Flush();
        };

        PlayerWindow? window = null;
        var completed = 0;
        try
        {
            Step("开始");

            // 对照实验开关：--no-guard 时不装守卫，用来复现闪退（证明根因判断正确）。
            if (args.Contains("--no-guard"))
            {
                VlcPlacePictureGuard.DisabledByFlag = true;
                results["guardDisabled"] = true;
            }

            // ★ 网页布局必须**开**：用户实测只有这条路径会崩，关掉它去验等于换了个场景。
            // ★ 画质增强默认**开**：崩溃只在 d3d11 的 processor / super 下发生
            //   （linear/point 根本不调用 D3D11_UpscalerUpdate，也就撞不上那句 idiv）。
            //   --no-enhancement 对照实验用：分离"守卫拦"与"黑屏"是不是同一条路径。
            var enhancement = !args.Contains("--no-enhancement");
            // ★ 网页布局可关：分离"黑屏是网页布局挂/卸造成的"还是"退出全屏本身造成的"。
            var webLayout = !args.Contains("--no-weblayout");
            // ★ 缩放模式可切换：super 与 processor 共用同一个 D3D11_UpscalerUpdate 调用点，
            //   但两者对 pool 的生命周期处理不一定一样 —— 黑屏到底跟哪个模式绑，得实测。
            var mode = ArgValue(args, "--enhancement-mode=") ?? "自动厂商增强";
            File.WriteAllText(Path.Combine(root, "settings.json"),
                $$"""
                {"PlayerVolume":0,"DanmakuEnabled":false,"AutoCheckUpdate":false,
                 "UseWebPlayerLayout":{{(webLayout ? "true" : "false")}},
                 "ExperimentalVideoEnhancement":{{(enhancement ? "true" : "false")}},"VideoEnhancementActive":{{(enhancement ? "true" : "false")}},
                 "VideoEnhancementMode":"{{mode}}"}
                """);
            Svc.Settings.Load();
            Svc.Settings.SetPlayerVolume(0);
            results["webLayout"] = Svc.Settings.UseWebPlayerLayout;
            results["enhancementProfile"] = EnhancementProfile.Current.Key;
            results["enhancementScaler"] = EnhancementProfile.Current.Scaler;
            Step("设置就绪 scaler=" + EnhancementProfile.Current.Scaler);

            // ★ 订阅 libvlc 日志（2026-11-08 排查"退出全屏后黑屏"加的）：
            //   --verbose=2 的日志本来走 stderr, 但 WPF 应用没有控制台句柄, 丢了。
            //   黑屏问题十有八九会在这里留下 d3d11/scaler/pool 的抱怨, 值得随结果落盘。
            var core = VlcCore.Get();
            core.Log += (_, e) =>
            {
                var line = $"[{e.Level}] {e.Message}";
                // 只收 vout/d3d11 相关的, 全量太吵
                if (line.Contains("d3d11", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("scaler", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("vout", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("pool", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("swapchain", StringComparison.OrdinalIgnoreCase))
                    lock (results) ((System.Collections.Generic.List<string>)results["vlcLog"]!).Add(line);
            };

            // ① 守卫必须挂上。这一步失败就没有继续的意义：后面"跑通"也只是运气好。
            if (!VlcPlacePictureGuard.IsInstalled)
            {
                // 走到这里说明 VlcCore.Get() 还没被调用过（守卫是在那里面挂的），
                // 主动初始化一次内核，把守卫装上再继续 —— 与产品启动路径一致。
                Step("预热内核以挂守卫");
                _ = VlcCore.Get();
            }
            results["guardInstalled"] = VlcPlacePictureGuard.IsInstalled;
            results["guardFailure"] = VlcPlacePictureGuard.FailureReason;
            // ★ 对照实验（--no-guard）**故意**不装守卫，那时这一步必须放行 ——
            //   否则永远拿不到"没有守卫会不会崩"的答案。
            if (!VlcPlacePictureGuard.IsInstalled && !VlcPlacePictureGuard.DisabledByFlag)
                throw new InvalidOperationException("除零守卫未挂上: " + VlcPlacePictureGuard.FailureReason);
            Step(VlcPlacePictureGuard.IsInstalled ? "守卫已挂上" : "对照实验: 未装守卫");

            // ② 守卫逻辑的**确定性**自证：拿崩溃现场那种全 0 参数喂进去，必须被拦下。
            //   这一步不依赖时序，所以它才是"根因已被堵住"的硬证据；
            //   后面的多轮全屏只是补充验证"没有引入新的卡死/黑屏"。
            if (VlcPlacePictureGuard.IsInstalled)
            {
                var selfTest = VlcPlacePictureGuard.SelfTest();
                results["guardSelfTest"] = selfTest ?? "ok";
                if (selfTest != null) throw new InvalidOperationException("守卫自证失败: " + selfTest);
                Step("守卫自证通过");
            }

            if (string.IsNullOrEmpty(media) || !File.Exists(media))
                throw new FileNotFoundException("缺少可播放的本地视频（--media=）", media ?? "(null)");

            Step("创建播放器窗口");
            window = new PlayerWindow { Width = 1100, Height = 760 };
            window.Show();
            Step("窗口已显示");
            window.PlayLocalFile(media);
            var player = (LibVLCSharp.Shared.MediaPlayer)typeof(PlayerWindow)
                .GetField("_mp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            player.Volume = 0;
            Step("已请求起播");

            if (!await WaitPlayingAsync(player, TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("播放器没有开始解码: " + player.State);
            Step("首帧已出 time=" + player.Time);

            // 网页布局的挂载是异步排队的（Loaded 优先级），等它真的挂上再开始切全屏，
            // 否则验的是"原生布局"那条路，正好绕开了用户报的场景。
            var mountedField = typeof(PlayerWindow).GetField("_webLayoutMounted", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var i = 0; i < 40 && !(bool)mountedField.GetValue(window)!; i++) await Task.Delay(100);
            results["webLayoutMounted"] = mountedField.GetValue(window);
            Step("网页布局挂载=" + mountedField.GetValue(window));

            var toggle = typeof(PlayerWindow).GetMethod("ToggleFullscreen",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var surface = (FrameworkElement)window.FindName("VideoSurface");

            // ★ 临时诊断：把 VideoSurface 的每一次尺寸变化记下来。
            //   vlc 日志里出现 `VoutDisplayEvent 'resize' 0x0`，说明有人把原生窗口按到 0×0，
            //   而那正是 d3d11 销毁 pool 的条件。这里要看是谁、什么时候按的。
            surface.SizeChanged += (_, e) =>
            {
                lock (results) ((System.Collections.Generic.List<string>)results["surfaceSizes"]!)
                    .Add($"{DateTime.Now:HH:mm:ss.fff} {e.PreviousSize.Width:0.#}x{e.PreviousSize.Height:0.#} → {e.NewSize.Width:0.#}x{e.NewSize.Height:0.#}");
            };
            var videoView = window.FindName("VideoView") as FrameworkElement;
            if (videoView != null)
                videoView.SizeChanged += (_, e) =>
                {
                    lock (results) ((System.Collections.Generic.List<string>)results["surfaceSizes"]!)
                        .Add($"{DateTime.Now:HH:mm:ss.fff} VideoView {e.PreviousSize.Width:0.#}x{e.PreviousSize.Height:0.#} → {e.NewSize.Width:0.#}x{e.NewSize.Height:0.#}");
                };

            // ★★★ 黑屏基线（2026-11-08 用户报"不闪退了但视频变黑"）：
            //   进全屏**之前**先量一次视频区的屏幕亮度，作为"正常画面长什么样"的基准。
            //   ★ 为什么必须抓屏幕而不是 TakeSnapshot：TakeSnapshot 取的是解码器吐的帧，
            //     解码正常、时间推进、快照也有图，但 vout 那一路什么都不画时它照样"通过"。
            //     这正是 0.2.9 探针 14 轮全过却漏掉黑屏的原因。
            await Task.Delay(700);
            var baseline = SampleSurface(surface);
            results["baselineLuminance"] = baseline.Luminance;
            results["baselineBrightRatio"] = baseline.BrightRatio;
            results["baselineSize"] = $"{baseline.Width}x{baseline.Height}";
            ScreenSampler.SavePng(baseline.Image, Path.Combine(Path.GetDirectoryName(output)!, "fullscreen-baseline.png"));
            Step($"基线亮度={baseline.Luminance:0.0} 亮像素={baseline.BrightRatio:P1} 尺寸={baseline.Width}x{baseline.Height}");
            if (!baseline.IsValid)
                throw new InvalidOperationException("取不到视频区的屏幕像素，黑屏判定不可信");
            if (baseline.Luminance < 4)
                throw new InvalidOperationException(
                    $"基线本身就是黑的（{baseline.Luminance:0.0}），换一段有画面的视频再验（--media=）");

            for (var round = 0; round < rounds; round++)
            {
                Step($"第 {round + 1} 轮: 进全屏");
                toggle.Invoke(window, null);
                await Task.Delay(900);

                Step($"第 {round + 1} 轮: 退全屏");
                toggle.Invoke(window, null);
                await Task.Delay(900);

                // 退出全屏之后画面必须还在推进。停住 = 同一个根因的"卡死"表现。
                var before = player.Time;
                await Task.Delay(1000);
                var after = player.Time;
                if (after <= before)
                    throw new InvalidOperationException(
                        $"第 {round + 1} 轮退出全屏后画面停住（{before} → {after}）");

                // ★ 时间推进**不等于**有画面：黑屏时解码器照样在跑。这里必须看屏幕。
                var sample = SampleSurface(surface);
                var ratio = baseline.Luminance > 0.5 ? sample.Luminance / baseline.Luminance : 1;
                results[$"round{round + 1}Luminance"] = sample.Luminance;
                results[$"round{round + 1}Ratio"] = Math.Round(ratio, 3);
                ScreenSampler.SavePng(sample.Image,
                    Path.Combine(Path.GetDirectoryName(output)!, $"fullscreen-round{round + 1}.png"));
                if (!sample.IsValid)
                    throw new InvalidOperationException($"第 {round + 1} 轮取不到视频区像素");
                if (ratio < BlackScreenRatio)
                    throw new InvalidOperationException(
                        $"第 {round + 1} 轮退出全屏后视频区变黑（亮度 {sample.Luminance:0.0} / 基线 {baseline.Luminance:0.0} = {ratio:P0}）");

                completed = round + 1;
                results["roundsCompleted"] = completed;
                Step($"第 {round + 1} 轮通过 time={after} 亮度={sample.Luminance:0.0}（基线 {baseline.Luminance:0.0}）");
            }

            results["roundsCompleted"] = completed;
            results["passed"] = true;
            Step("全部通过");
        }        catch (Exception ex)
        {
            results["roundsCompleted"] = completed;
            results["passed"] = false;
            results["error"] = ex.ToString();
            Step("失败: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            // 守卫的计数是这次验证最有价值的部分：blocked > 0 说明"本来会崩、被挡住了"，
            // blocked == 0 说明这轮压根没走到危险路径（那就要加轮次/换片再验）。
            results["guardBlocked"] = VlcPlacePictureGuard.BlockedCount;
            results["guardForwarded"] = VlcPlacePictureGuard.ForwardedCount;
            if (window != null)
            {
                try { window.Close(); } catch { }
                await Task.Delay(800);
            }
            Flush();
            app.Shutdown(Equals(results["passed"], true) ? 0 : 1);
        }
    }

    private static string? ArgValue(string[] args, string prefix)
        => Array.Find(args, a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    /// <summary>
    /// 判定"变黑"的阈值：退出全屏后的亮度 / 基线亮度。
    ///
    /// ★ 为什么是 0.35 而不是 0.8：画面在停靠/内联两种几何下**尺寸不同**，
    ///   同一帧被缩放到不同大小、加上窗口底色与遮罩边缘，亮度本身就会浮动。
    ///   但"有画面"与"全黑"之间差的是数量级（实测基线 ~40-90，黑屏 &lt; 2），
    ///   取 0.35 足以区分，又不会因为视频恰好切到暗场景就误报。
    /// </summary>
    private const double BlackScreenRatio = 0.35;

    /// <summary>量一次视频区（<c>VideoSurface</c>）在屏幕上的亮度。</summary>
    private static ScreenSampler.Sample SampleSurface(FrameworkElement surface)
    {
        var rect = ScreenSampler.ScreenRect(surface);
        return ScreenSampler.Capture(rect.X, rect.Y, rect.W, rect.H);
    }

    private static async Task<bool> WaitPlayingAsync(LibVLCSharp.Shared.MediaPlayer player, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            if (player.IsPlaying && player.Time > 0) return true;
        }
        return false;
    }
}
