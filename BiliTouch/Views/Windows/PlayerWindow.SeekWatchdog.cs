using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Views.Controls;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliTouch.Views;

public partial class PlayerWindow
{
    private void UpdateProgressUi()
    {
        if (_closing || _mp == null) return;
        MaybeRefreshPlayUrl();

        // 宽限期(SeekGraceMs): 这段时间内不回写进度条。等待期已由下面的看门狗分支挡住,
        // 这里只作为"看门狗提前放行"时的兜底 —— 见 SeekGraceMs 的说明。
        var inSeekGrace = Environment.TickCount64 < _seekGraceUntil;

        // ---- 跳转看门狗: 目标迟迟没到位就重发 seek, 等待久了给缓冲提示 ----
        if (_pendingSeekMs >= 0 && !_isSeeking && !_ended && _mp.Length > 0)
        {
            var now = Environment.TickCount64;

            // ⚠ 这里踩过一个很隐蔽的坑(2026-09-25 实测):
            // LibVLC 的 Time **在 seek 指令发出的那一刻就跳成目标值**(实测 100ms 内),
            // 而此时的画面还冻在旧位置 —— 真正恢复画面要 0.4s(单文件)到 1.4s(DASH 双流)。
            // 所以"Time 接近目标"根本不能单独作为到位依据: 看门狗会在第一个心跳就判成功,
            // 后果有两个 ——
            //   1. 重发 seek 的分支永远不会执行(丢掉的 seek 没人救);
            //   2. "正在缓冲"提示永远不会弹(因为立刻就 return 了), 用户只能对着冻住的画面干等。
            // 正确的判据是"位置落在目标附近 **并且相对上一次观察真的动过**", 那才代表画面回来了。
            var time = _mp.Time;
            var settled = IsSeekSettled(time, _pendingSeekMs, _seekObservedMs, !_mp.IsPlaying);

            // ★★ 2026-10-17 大修: 原来的判据只要求"远离起点 + 远离目标", 没有**上界**,
            //   结果把"seek 彻底失效、位置回到片子开头"误判成"落地但偏早", 于是收手不重试 ——
            //   这正是用户报的"拉过去又跳回去、画面卡住"(探针 bd-probe-seekstick 实测):
            //     拖到 329s → 落在 5.1s, 拖到 724s → 落在 11.1s, 拖到 988s → 落在 200s。
            //   而 `_seekFromMs` 当时是 3.4s / 8.9s / 198s, 用 `> 1500ms` 判"远离起点"轻而易举
            //   就成立 ⇒ 立刻被当成"落地偏早"认账。**判据缺的是"离目标不能太远"这个上界。**
            //
            //   真实"落地偏早"(DASH 分片边界)的偏差是**秒级**: 实测 60s→55.2s(4.8s)、
            //   150s→145.7s(4.3s); 而这次失效的偏差是 324s / 713s / 788s —— 差两个数量级。
            //   所以判据取"偏早不超过目标的 1/8 且不超过 30s": 正常偏早(几秒)仍被认账,
            //   跳到开头/跳到很远的地方则一律判为"没落地", 交给看门狗重发。
            //
            // ★ 为什么上界要用**相对目标的比例**而不是固定值: 目标可能在 5 分钟处也可能在
            //   20 分钟处, 固定 30s 对短跳转太松(5s 目标偏 25s = 完全跑偏却仍认账)。
            var earlyBy = _pendingSeekMs - time;                 // >0 表示落在目标之前
            var earlyUpper = Math.Min(SeekLandingMaxEarlyMs,
                                      Math.Max(SeekLandingMinEarlyMs, _pendingSeekMs / 8));
            var farFromBoth = _seekFromMs >= 0 &&
                              Math.Abs(time - _seekFromMs) > SeekLandingToleranceMs &&
                              Math.Abs(time - _pendingSeekMs) > SeekConfirmWindowMs;
            // ★ 只接受"落在目标之前、且偏早幅度在合理范围内"; 落在目标**之后**超窗也不算
            //   (那同样是跑偏, 该重试)
            var withinEarlyBand = earlyBy > SeekConfirmWindowMs && earlyBy <= earlyUpper;
            var landedElsewhere = farFromBoth && withinEarlyBand &&
                                  _seekElsewhereSeenMs >= 0 && time != _seekElsewhereSeenMs;
            _seekElsewhereSeenMs = farFromBoth ? time : -1;

            // ★★★ "落点弹回起点"的提前重建(2026-10-31 修"长视频快进回到起点后无限缓冲"):
            //   DASH 双流上远跳偶尔整体失效, 落点直接弹回片子开头(或某个离目标极远的分片)。
            //   这种落点距目标分钟级, settled/landedElsewhere 都判不上, 只能干等
            //   2.5s×3 轮重发 + 一次重建 —— 期间进度条钉在目标、画面停在开头,
            //   重建后 MarkSeekIssued 清零 _seekRetries 再来一整轮, 表现为无限缓冲。
            //   修法: 落点**连续两次心跳**同时远离起点与目标 ⇒ 判定这条 input 的解复用器
            //   又"认死"了, 不再走重发, 立刻走重建(它只给一次预算, 不会循环)。
            //   与 landedElsewhere 的区别: 那个判"偏早幅度合理"(分片边界的正常偏早),
            //   要认账并恢复播放; 这里判"偏到天边去了"(整体失效), 要当机立断换 input。
            if (!settled && !landedElsewhere && farFromBoth && !_seekRebuildTried)
            {
                if (_seekBounceSeenMs < 0)
                {
                    _seekBounceSeenMs = time;
                    _seekBounceAt = now;
                }
                else if (now - _seekBounceAt >= SeekBounceConfirmMs)
                {
                    _seekRebuildTried = true;
                    var bounceTarget = _pendingSeekMs;
                    var bounceFrom = _seekFromMs;
                    ShowLoading("正在跳转…");
                    ClearSeekWatchdog(hideBuffering: false);
                    WriteLibVLCDebug(
                        $"[Seek] 落点弹回别处(time={time}ms target={bounceTarget}ms from={bounceFrom}ms) → 重建 Media\n");
                    RebuildMediaAndSeek(bounceTarget);
                    return;
                }
            }
            else if (!farFromBoth)
            {
                _seekBounceSeenMs = -1;
                _seekBounceAt = -1;
            }

            _seekObservedMs = time;

            if (settled || landedElsewhere)
            {
                ClearSeekWatchdog();
            }
            else if (now >= _seekDeadline)
            {
                if (_seekRetries < SeekRetryMax)
                {
                    // seek 指令被 LibVLC 丢了: 重发一次(重发后位置会被重新 ack, 观察值也要清)
                    _seekRetries++;
                    _seekDeadline = now + SeekConfirmTimeoutMs;
                    _seekObservedMs = -1;
                    try { _mp.Time = _pendingSeekMs; } catch { }
                }
                else if (!_seekRebuildTried && !_isReliableSeekSource &&
                         !string.IsNullOrEmpty(_lastPlayUrl))
                {
                    _seekRebuildTried = true;
                    var target = _pendingSeekMs;
                    ShowLoading("正在跳转…");
                    ClearSeekWatchdog(hideBuffering: false);
                    RebuildMediaAndSeek(target);
                }
                else
                {
                    ClearSeekWatchdog();
                }
            }
            else if (now - (_seekDeadline - SeekConfirmTimeoutMs) > SeekBufferingDelayMs &&
                     LoadingOverlay != null && LoadingText != null &&
                     LoadingOverlay.Visibility == Visibility.Collapsed &&
                     !_isLive && !_isLocalPlayback)
            {
                // 跳转后迟迟不到位: 与其让用户对着冻住的老画面干等, 不如把"视频开头那套"
                // 加载动画(转圈 + 颜文字)搬出来说清楚"在缓冲"。
                ShowLoading(BufferingText);
            }

            // 等待期间不回写进度条(目标位置已由 MarkSeekIssued 钉住), 时间文字同理
            return;
        }

        // ---- 播放中途卡顿(网络缓冲)检测: 与"跳转"无关, 纯播放中卡住 ----
        UpdateStallDetector(inSeekGrace);
        // ★ seek 后音量写回被 aout 就绪窗口吞掉的延迟补写(没排队时零开销)
        RestoreVolumeWhenReady();

        if (_mp.Length > 0 && !inSeekGrace)
        {
            // ★ 进度条与时间文字必须同源(2026-10-01, 用户截图 24:43/24:49 实锤"进度条卡在
            //   最右端但时间还剩 6 秒"): 以前进度条用 `_mp.Position`, 时间文字用 `_mp.Time`。
            //   尾部解复用把较短的那路流(DASH 的音轨)读完时, Position 会先飘到 ~1.0 而
            //   Time 还在走 —— 两源一错位, 进度条就"提前满格卡住"。统一从 Time 派生后
            //   两者的行为永远一致(播完钉满格的规则不变)。
            // Slider 0-1000, 按比例(_updatingSlider 防止回写触发 ValueChanged 造成 seek 循环)
            var ms = _ended ? _mp.Length : GetDisplayedTimeMs(_mp.Length);
            var pos = ms / (double)_mp.Length; // 0.0~1.0
            if (_ended) pos = 1;
            if (!_isSeeking)
            {
                _updatingSlider = true;
                ProgressSlider.Value = pos * 1000;
                _updatingSlider = false;
            }
            TimeText.Text = $"{FormatClock(TimeSpan.FromMilliseconds(ms))} / " +
                            $"{FormatClock(TimeSpan.FromMilliseconds(_mp.Length))}";
        }
    }

    // --- 尾部进度外推(输入停滞、解码缓冲还在播的那几秒) ---
    // DASH 双流在结尾会出现"Time 停滞几秒"的窗口: 输入线程在等另一路流/最后分片,
    // 而解码缓冲里的内容还在正常播。此时若如实显示, 进度条和时间会冻在最后 5~6 秒
    // (用户报的"进度条最后五秒卡住不动")。尾部(剩余 < TailQuietZoneMs)改用
    // "锚点 + 墙钟 × 倍速"外推显示; 走出尾部或 EOF(钉满格)时自动归位。
    // 中间卡住仍如实显示, 那是真缓冲, 骗用户没有意义。
    //
    // ★★ 外推的触发判据是"真实读数没追上显示值", **不是**"Time 一格不动"(2026-10-01
    //   第二次返工的教训): 第一版只认 Time 完全冻结, 但实测尾部停滞时时钟常常**每跳
    //   挪几十毫秒**—— 每次都算"在走", 外推永远不启动, 进度条就以 ~6% 的速度龟速爬,
    //   看起来照样是卡死。锚点方案对"完全冻结"和"龟速蠕动"一视同仁。
    /// <summary>外推锚点: 最后一次确认"真实读数追上显示"的位置(-1 = 尚未建立)。</summary>
    private long _anchorMs = -1;
    /// <summary>锚点对应的墙钟(毫秒, Environment.TickCount64)。</summary>
    private long _anchorWall;
    /// <summary>
    /// 上一次**显示**的进度。尾部只进不退的钳制基准(见 GetDisplayedTimeMs);
    /// seek / 换片时归零, 否则"从结尾往回跳转"会被钳在旧位置上。
    /// </summary>
    private long _displayedMs = -1;

    /// <summary>取"应该显示的播放位置": 正常时就是 _mp.Time; 尾部按"锚点 + 墙钟×倍速"外推。</summary>
    private long GetDisplayedTimeMs(long lengthMs)
    {
        var t = _mp.Time;
        var wall = Environment.TickCount64;

        // 非尾部 / 暂停: 如实显示, 锚点对齐当下(暂停时不积累外推量, 恢复后从暂停点继续)
        if (!_mp.IsPlaying || lengthMs - t >= TailQuietZoneMs)
        {
            _anchorMs = t;
            _anchorWall = wall;
            _displayedMs = t;
            return t;
        }

        long shown;
        if (t > _displayedMs)
        {
            // 真实读数追上/超过显示值: 以真实为准, 抬锚
            _anchorMs = t;
            _anchorWall = wall;
            shown = t;
        }
        else
        {
            // 真实读数落后(停滞/龟速/时钟回跳): 从锚点按墙钟外推, 封顶在片尾
            if (_anchorMs < 0) { _anchorMs = t; _anchorWall = wall; }
            var rate = _mp.Rate;
            if (rate <= 0) rate = 1.0f;
            shown = Math.Min(_anchorMs + (long)((wall - _anchorWall) * rate), lengthMs);
        }

        // ★ 只进不退: 时钟重锚/回跳不让显示跟着倒。真正允许倒退的路径
        //   (用户拖进度条、点重播、换片)都会先归零 _displayedMs / _anchorMs。
        if (_displayedMs >= 0 && shown < _displayedMs)
            shown = _displayedMs;
        _displayedMs = shown;
        return shown;
    }

    /// <summary>
    /// 播放器时间标签的时钟格式: 不足一小时用 mm:ss, **满一小时用 h:mm:ss**。
    /// ★ 之前恒用 mm:ss, 一个多小时的视频总时长"1:13:25"会被截成"01:13"(用户截图实锤:
    ///   播到 46:52 时右侧显示 01:13) —— TimeSpan 的 "mm" 格式符**不会进位到小时**,
    ///   它只是"分钟位的两位数字"。小时位要显式给, B 站的样式是"1:13:25"(小时不补零)。
    /// </summary>
    private static string FormatClock(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";

    /// <summary>
    /// 判断一次跳转是不是**真的**到位了(画面回来了), 而不是只收到了 LibVLC 的"已受理"回执。
    ///
    /// 为什么需要这么拧巴的判据(2026-09-25 真机实测得出来):
    ///   LibVLC 的 Time 在 seek 指令发出的那一刻就跳成目标值 —— 实测发出后 100ms 内返回
    ///   60000ms(正好等于目标), 而画面要到 380ms(单文件源)~1410ms(DASH 双流)后才动。
    ///   换句话说"Time 接近目标"只是一张回执, 完全没有画面信息。
    ///   老代码只看这个, 于是看门狗每个跳转都在第一跳就判成功, 连带两个后果:
    ///     ① 重发 seek 的分支永远走不到 —— 被丢掉的 seek 没人救;
    ///     ② "正在缓冲"提示永远弹不出来 —— 用户只能对着冻住的画面干等(这正是"拖进度条卡"
    ///        最难受的那部分: 不是慢, 是没有任何反馈)。
    ///
    /// 所以判据改成"接近目标 **并且** 相对上一次观察真的动过"。
    /// 唯一的例外是暂停态: 暂停时位置本来就不前进, 而 VLC 已经按目标解出了那一帧,
    /// 这时"接近目标"就是完整的到位信号(不加这条, 暂停中拖进度条会白弹一个缓冲提示)。
    /// </summary>
    /// <param name="timeMs">本次观察到的播放位置(毫秒)</param>
    /// <param name="targetMs">跳转目标(毫秒)</param>
    /// <param name="observedMs">上一次观察到的位置, -1 = 还没观察过</param>
    /// <param name="paused">播放器当前是否处于暂停/停止态</param>
    private static bool IsSeekSettled(long timeMs, long targetMs, long observedMs, bool paused)
    {
        if (Math.Abs(timeMs - targetMs) > SeekSettledToleranceMs) return false;
        if (paused) return true;
        return observedMs >= 0 && timeMs != observedMs;
    }

    /// <summary>跳转确认到位(或放弃重试)后收尾: 关掉"正在缓冲"提示, 清掉待确认目标</summary>
    private void ClearSeekWatchdog(bool hideBuffering = true)
    {
        _pendingSeekMs = -1;
        _seekObservedMs = -1;
        _seekFromMs = -1;
        _seekElsewhereSeenMs = -1;
        _seekBounceSeenMs = -1;
        _seekBounceAt = -1;
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
        RestoreVolumeAfterSeek();
        if (hideBuffering) HideBufferingIfShown();
        // ★ 2026-10-31: 到位这一刻正是"音轨偶发没跟上"的暴露点, 顺手做一次自愈检查
        //   (见 HealAudioIfDead —— 命中才动作, 不命中零开销)。
        HealAudioIfDead("seek-settled");
    }

    // ------------------------------------------------------------ seek 后音频自愈

    /// <summary>音频自愈检查的宽限期(毫秒): 重建 Media / 长按快进恢复 1x 之后,
    /// aout 重建与音轨枚举都需要一点时间, 立刻检查必然误报。</summary>
    private const int AudioHealGraceMs = 1500;

    /// <summary>上次音频自愈检查的时刻(TickCount64), 用于实现"每个检查点只查一次"。</summary>
    private long _audioHealCheckAt = -1;

    /// <summary>
    /// seek / 长按快进之后音轨偶发静默的兜底自愈。
    ///
    /// ★★★ 症状(2026-10-31 用户报"长视频快进后有时没声音"): 画面正常、时间在走,
    ///   但就是没声。实测机理是 libvlc 在 seek/变速时重建音频输出, 偶发地**把选中的
    ///   音轨丢回"无轨道"**(AudioTrack 变 -1, 音轨数明明 >0) —— 这是库的内部竞态。
    ///
    /// 检查判据(三个条件同时成立才动手, 宁可漏报不可误伤):
    ///   ① 在播放且不在拖动/等待 seek —— 排除过渡期;
    ///   ② 距上次检查超过宽限期 —— 避免对同一次过渡反复操作;
    ///   ③ 音轨数 >0 而 AudioTrack == -1 —— "有轨道却没选"就是那个竞态的指纹。
    ///
    /// ★★★ 2026-11-06 修「自愈本身是一条死路」(探针 artifacts/probes/rateaudio/run9.log 实测):
    ///
    ///   原来修复动作是 `SetAudioTrack(0)` —— **在 3.0.24 上完全救不回来**:
    ///     制造掉线后 AudioTrack=-1, 调 SetAudioTrack(0) → AudioTrack 仍是 -1,
    ///     之后 1.5 秒内 play 回调 **0 次**(音频永久静默, 但 state 仍是 Playing)。
    ///     对照组: 按**真实 track id** 重选 → AudioTrack 立刻变回 1, 1.5 秒内 play 71 次, 音频恢复。
    ///
    ///   原因: `0` 不是"第一条音轨", 而是"无轨道"以外的某个**绝对索引**; 掉线之后
    ///   libvlc 内部的轨道表已经重排, 写 0 落在一个不存在的槽位上, 被静默丢弃
    ///   (setter 不看返回值, 所以我们连失败都看不到)。必须从 **AudioTrackDescription
    ///   里取真实的 id** 才能选中。
    ///
    ///   ⇒ 这解释了为什么"快进后没声音"偶发且**自愈无效**: 用户听到的静默不是
    ///     那零点几秒的变速过渡, 而是这条自愈根本没修好, 一直静默到下一次重建 input。
    ///
    /// ★ 顺带把"每次检查都读三个原生 getter"收成一次: 三个 getter 各 ~0.005ms,
    ///   在 UI 线程上不构成卡顿(v8 实测松手整段中位 0.02ms), 保留原判据不变。
    /// </summary>
    /// <param name="why">调用点标签, 进诊断日志</param>
    private void HealAudioIfDead(string why)
    {
        if (_closing || _mp == null) return;
        var now = Environment.TickCount64;
        if (_audioHealCheckAt >= 0 && now - _audioHealCheckAt < AudioHealGraceMs) return;
        _audioHealCheckAt = now;
        try
        {
            // 拖动中/等待 seek 确认期间音轨本来就处于切换途中, 不检查
            if (!_mp.IsPlaying || _isSeeking || _pendingSeekMs >= 0) return;
            var trackCount = _mp.AudioTrackCount;
            if (trackCount <= 0) return;                   // 本来就没有音轨(纯视频/直播占位), 无事可做
            if (_mp.AudioTrack != -1) return;              // 音轨选着呢, 一切正常

            // ★★★ 必须按**真实 id** 重选, 不能写 0(见方法说明里的实测)。
            var desc = _mp.AudioTrackDescription;
            var picked = -1;
            if (desc != null)
            {
                foreach (var d in desc)
                {
                    if (d.Id >= 0) { picked = d.Id; break; }
                }
            }
            if (picked < 0)
            {
                WriteLibVLCDebug(
                    $"[Audio] 自愈: 检出音轨掉线({why}, tracks={trackCount}), 但拿不到可用 track id, 跳过\n");
                return;
            }

            var ok = _mp.SetAudioTrack(picked);
            WriteLibVLCDebug(
                $"[Audio] 自愈: 检出音轨掉线({why}, tracks={trackCount}) → SetAudioTrack({picked})={ok}" +
                $" → AudioTrack={_mp.AudioTrack}\n");
        }
        catch { /* 播放器已失效就算了 */ }
    }

    // ------------------------------------------------------------ seek 静音

    /// <summary>跳转发出: 静音(记住原音量)。连续拖动时已静音则只更新心跳, 不覆盖原值。</summary>
    private void MuteForSeek()
    {
        if (_closing || _mp == null || _seekMuteSavedVolume >= 0) return;
        try
        {
            // 读权威值而不是原生 _mp.Volume: aout 未就绪时原生读回 -1, 会被 "v<=0" 误判成
            // "本来就静音" 而跳过, 恢复点也就不会把音量补写回 aout(见 _volumeCache 的说明)
            var v = _volumeCache;
            if (v <= 0) return;   // 本来就静音, 无事可做
            _seekMuteSavedVolume = v;
            _mp.Volume = 0;
        }
        catch { /* 播放器已失效就算了 */ }
    }

    /// <summary>画面确认到位(或放弃/换片/播完): 恢复跳转前的音量。
    ///
    /// ★★ 2026-10-31 补"写回被吞"的兜底: aout 未就绪时 libvlc_audio_set_volume 是
    ///   **静默丢弃**的(反编译 3.8.0 确认 setter 不看返回值), 而 seek 到位的那一刻
    ///   新 input 的 aout 可能刚好还没建好 —— 写回丢了, aout 就绪后音量停在 0,
    ///   表现就是"快进过去画面正常但一直没声"。所以写回后要读一次原生值核对,
    ///   读不回(-1)或没写上(≠v)就交给 <see cref="RestoreVolumeWhenReady"/> 延迟补写。</summary>
    private void RestoreVolumeAfterSeek()
    {
        if (_seekMuteSavedVolume < 0) return;
        var v = _seekMuteSavedVolume;
        _seekMuteSavedVolume = -1;
        _volumeCache = v;   // 静音期间滚轮/滑块一直在改权威值, 恢复时对齐一次, 防止两条账本漂移
        try
        {
            if (_mp == null) return;
            _mp.Volume = v;
            var applied = _mp.Volume;   // aout 未就绪时读回 -1
            if (applied != v)
            {
                _volumeRestoreDeadline = Environment.TickCount64 + VolumeRestoreTimeoutMs;
                WriteLibVLCDebug($"[Audio] 音量写回未生效(applied={applied}, want={v}) → 稍后补写\n");
            }
        }
        catch { }
    }

    /// <summary>音量补写的最长等待(毫秒): 超过就放弃(避免无限期挂着)。</summary>
    private const int VolumeRestoreTimeoutMs = 8000;

    /// <summary>音量补写的截止时刻(>0 表示有补写在排队, 见 RestoreVolumeWhenReady)。</summary>
    private long _volumeRestoreDeadline;

    /// <summary>补写已重试的次数(每次心跳 +1, 夹在超时内; 日志防刷)。</summary>
    private int _volumeRestoreTries;

    /// <summary>
    /// 心跳里调: seek 后音量写回被 aout 就绪窗口"吞掉"时的延迟补写。
    /// 只在 <see cref="_volumeRestoreDeadline"/> 排了队时才干活, 平时零开销。
    /// 超时放弃时按权威值再硬写一次 —— 哪怕这次仍被丢弃, 下一次 Playing/调音量也会对齐。
    /// </summary>
    private void RestoreVolumeWhenReady()
    {
        if (_volumeRestoreDeadline <= 0 || _closing || _mp == null) return;
        var now = Environment.TickCount64;
        try
        {
            var v = _volumeCache;
            if (v <= 0) { _volumeRestoreDeadline = 0; return; }   // 用户自己静音了, 收队
            if (_seekMuteSavedVolume >= 0) return;                // 新一轮 seek 静音接管了, 让位
            _mp.Volume = v;
            var applied = _mp.Volume;
            if (applied == v)
            {
                WriteLibVLCDebug($"[Audio] 音量补写成功(第{_volumeRestoreTries + 1}次尝试, v={v})\n");
                _volumeRestoreDeadline = 0;
                _volumeRestoreTries = 0;
                return;
            }
        }
        catch { _volumeRestoreDeadline = 0; _volumeRestoreTries = 0; return; }

        if (now >= _volumeRestoreDeadline)
        {
            // 超时: 别再耗着, 直接按权威值硬写一次然后收队(playing 事件还会再补一轮)
            try { _mp.Volume = _volumeCache; } catch { }
            WriteLibVLCDebug($"[Audio] 音量补写超时放弃(tried={_volumeRestoreTries + 1})\n");
            _volumeRestoreDeadline = 0;
            _volumeRestoreTries = 0;
            return;
        }
        _volumeRestoreTries++;
    }

    // ------------------------------------------------------------ 倍速切换与"松手后静音"
    //
    // ★★★ 2026-10-16 定稿(实测见 .probes/bd-probe-rateguard2, 前面两次结论都有偏差, 以本条为准):
    //
    //  现象: 长按 2x 松手后, 会听到一小段"没有声音"。
    //
    //  根因: **libvlc 在 SetRate 生效时会丢弃整个音频输出缓冲**, 原生日志:
    //          playback way too late (~529-655ms): flushing buffers
    //        官方 flush 定义 = "discard all pending buffers and stop playback as soon as
    //        possible" ⇒ 待播音频被整体丢掉, 要重填才恢复出声。
    //        ★ 这是 **VLC 自身行为**, 不是本项目的代码 bug(探针裸调 libvlc 也复现)。
    //
    //  ★★ 但**这个代价比之前估计的小得多**: 真正的停顿只有 **0.25~0.5s**
    //     (base 变体两次实测: 停顿 0.25s / 0.50s, 松手后吞吐比 1.12 / 0.79)。
    //     曾经记成"约 2 秒"是**误读**: 松手瞬间 OS 缓冲里还排着已解码音频, 它仍以
    //     **2 倍速**播完(实测按住期间吞吐 = 正常 1x 的 3.4 倍)⇒ 听感像"还在快进",
    //     等它播空才轮到那一小段静音。**静音本身只有零点几秒。**
    //
    //  ★★★ 三个"缓解"手段全都**实测更糟**, 已被否掉(别再想):
    //    · 松手前先压音量再改 rate  → 停顿拉长到 2.25s, 吞吐比 0.01
    //    · 按住期间全程静音          → 停顿 2.00s, 吞吐比 0.27
    //    · 松手不改 rate 只补一次 seek → 停顿 1.50s
    //    ⇒ 什么都不做(base)就是最优。**不要去"治"那 0.3 秒。**
    //
    //  ★ 早期逐一实测过、确认**无效**的参数(别再重复): `--network-caching` 1000/3000/5000/6000、
    //    `--no-audio-time-stretch`(libvlc 级与 media 级; 该选项不生效, scaletempo 照旧加载)、
    //    `--aout=mmdevice / directsound / waveout / afile / amem`、`--clock-jitter=0`、
    //    `--clock-synchro=0`、Pause/Play 包住 SetRate、分两步换速率(flush 翻倍)、
    //    SetRate 后补 seek(flush 暴增)、Stop + 重建 Media(更差)、先压音量再 SetRate(同前)。
    //  ★ 也扫过 libvlccore/libvlc 的选项表: **没有**能调 aout 缓冲深度的开关
    //    (--audio-buffer / --aout-buffer / --mmdevice-buffer 等全部不存在)。
    //
    //  ⇒ 结论: 长按快进就用 SetRate。顺滑度是它天生就有的(与倍速按钮同款),
    //    代价只有松手后零点几秒静音 —— 换 seek 式反而会把顺滑度整个丢掉(实测用户反馈"画面和音频均卡顿")。


    /// <summary>当前遮罩是不是我们自己弹的"正在缓冲/重连"(据此判断能不能收掉)</summary>
    private bool IsShowingBuffering()
        => LoadingOverlay != null && LoadingText != null &&
           LoadingOverlay.Visibility == Visibility.Visible &&
           IsStallOverlayText(LoadingText.Text);

    /// <summary>
    /// 收起"正在缓冲"遮罩 —— **只收这一种**。
    /// 不能直接 HideLoading(): 此刻遮罩上可能写着"正在切换清晰度…""正在获取播放地址…",
    /// 那些有自己的生命周期, 被这里顺手收掉就会露出后面的黑屏。
    /// </summary>
    private void HideBufferingIfShown()
    {
        if (_closing) return;
        if (IsShowingBuffering()) HideLoading();
    }

    // --- 播放中途卡顿检测 ---
    // 在每 500ms 一次的进度刷新里比较 _mp.Time 有没有推进: 连续几个心跳原地不动就是卡住了。
    //
    // 为什么不用 LibVLC 的 Buffering 事件: 它在正常播放时也会频繁给到 100, 在部分片源上
    // 又长期给不满 100 —— 拿它当"该不该提示"的依据很容易把提示卡在屏上收不掉。
    // 用"播放位置有没有推进"这个客观事实判断: 最多漏报(极少数片源 Time 会假性前进),
    // 不会误报, 而且位置一动就自动恢复。
    //
    // 这个检测同时覆盖了"快进后卡住"和"看着看着网络卡住"两种情况 —— 用户感知都是同一件事。
    private long _stallLastMs = -1;
    private int _stallTicks;

    /// <summary>连续几次心跳没推进才算卡顿(2 * 500ms ≈ 1 秒, 正常的缓冲抖动不会被判成卡顿)</summary>
    private const int StallTicksToReport = 2;

    /// <summary>判定"没推进"的阈值(毫秒): 500ms 的心跳间隔下正常播放至少推进几百毫秒</summary>
    private const long StallIdleToleranceMs = 60;

    private void UpdateStallDetector(bool inSeekGrace)
    {
        // 直播/本地文件没有"缓冲卡顿"这回事; 播完了也不算
        if (_isLive || _isLocalPlayback || _ended || _mp == null || _mp.Length <= 0)
        {
            _stallTicks = 0;
            _stallPolicy.OnUnfrozen();
            return;
        }

        // 暂停/停止时位置本来就不动; 拖动/跳转宽限期与刚发完 seek 的 2s 冷却期同理
        var state = SafePlayerState();
        var nowTick = Environment.TickCount64;
        var inPostSeekCooldown = _seekIssuedAt > 0 && nowTick - _seekIssuedAt < 2_000;
        if (state is VLCState.Paused or VLCState.Stopped or VLCState.Ended ||
            _isSeeking || inSeekGrace || inPostSeekCooldown || _pendingSeekMs >= 0)
        {
            _stallTicks = 0;
            _stallPolicy.OnUnfrozen();
            if (state == VLCState.Paused) HideBufferingIfShown();
            return;
        }

        // 结尾静默区豁免
        var now = _mp.Time;
        var remainingMs = _mp.Length - now;
        if (remainingMs < TailQuietZoneMs)
        {
            _stallTicks = 0;
            _stallLastMs = now;
            _stallPolicy.OnAdvance();
            HideBufferingIfShown();
            return;
        }

        if (_stallLastMs >= 0 && Math.Abs(now - _stallLastMs) < StallIdleToleranceMs)
        {
            _stallTicks++;
            if (_stallTicks >= StallTicksToReport)
            {
                if (!_stallPolicy.GaveUp && LoadingOverlay != null &&
                    LoadingOverlay.Visibility != Visibility.Visible)
                {
                    ShowLoading(BufferingText);
                    BufferPolicy.ReportStall(CurrentMediaUrl);
                }

                var recoverable = !_stallRecoveryInFlight &&
                                  state is VLCState.Playing or VLCState.Buffering or VLCState.Opening;
                var action = _stallPolicy.Tick(nowTick, frozen: true, recoverable);
                if (action != StallRecoveryAction.None) HandleStallAction(action, now);
            }
            return;
        }

        // 位置推进了 -> 卡顿结束, 归零自愈状态并收起自己的缓冲/重连遮罩
        _stallLastMs = now;
        if (_stallTicks >= StallTicksToReport || _stallPolicy.IsFrozen || _stallPolicy.Rebuilds > 0)
            HideBufferingIfShown();
        _stallTicks = 0;
        _stallPolicy.OnAdvance();
    }

    /// <summary>把一次跳转记下来: 打上宽限期, 并立刻把进度条/时间钉到目标位置</summary>
    private void MarkSeekIssued(long targetMs)
    {
        var now = Environment.TickCount64;
        _seekGraceUntil = now + SeekGraceMs;
        MuteForSeek();
        // 启动/重置跳转看门狗
        _pendingSeekMs = targetMs;
        // 记下"跳转前的位置"给看门狗判"是不是真的丢了"
        _seekFromMs = _lastKnownMs > 0 ? _lastKnownMs : -1;
        _seekRetries = 0;
        _seekDeadline = now + SeekConfirmTimeoutMs;
        _seekObservedMs = -1;
        _seekElsewhereSeenMs = -1;
        _seekBounceSeenMs = -1;
        _seekBounceAt = -1;
        _seekIssuedAt = now;
        _lastSeekTargetMs = targetMs;
        _ended = false;
        _stallPolicy.OnUnfrozen();
        // 进度外推的锚点与显示钳制基线作废: 不作废的话, 跳转落地后可能外推出凭空进度,
        // 往回拖进度条也无法倒着显示
        _anchorMs = -1;
        _displayedMs = -1;
        EndOverlay.Visibility = Visibility.Collapsed;
        // 等待期把心跳调密, 好让缓冲提示与到位判定都及时(见 ProgressTickSeeking 的说明)
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickSeeking;

        if (_totalMs <= 0) return;
        _updatingSlider = true;
        ProgressSlider.Value = Math.Clamp(targetMs * 1000.0 / _totalMs, 0, 1000);
        _updatingSlider = false;
        TimeText.Text = $"{FormatClock(TimeSpan.FromMilliseconds(targetMs))} / " +
                        $"{FormatClock(TimeSpan.FromMilliseconds(_totalMs))}";
    }

    /// <summary>
    /// 统一的 seek 发起入口 —— **所有跳转都必须走这里**(拖进度条 / 方向键 / 快进快退 /
    /// 点击轨道 / 跳过推广片段)。
    ///
    /// ★★★ 曾经为什么需要这么多补偿(2026-10-17 已全部删除, 这里是结论留档):
    ///   当年以为根因是"libvlc 对 frag-MP4 的 seek 有单次跳跃距离上限", 于是叠了
    ///   分段跳(每段 ≤150s)、重建补跳(最多 20 次、要求连续 3 次稳住)、看门狗重发。
    ///   探针 bd-probe-muxed5 实测否掉了这个前提 —— DASH 双流连发三次同一个 Time,
    ///   落点**完全一样**(偏差 -1180s/-590s/-910s 稳定复现), 说明不是"距离太大"而是
    ///   这条 input 的解复用器**认死了**, 补偿只能把一次跳换成多次跳, 中途照样丢。
    ///
    ///   真正的根因是 **`input-slave` 音轨**这个组合: 去掉它(改用 durl 合流单流)之后,
    ///   seek 一次到位, 于是整套补偿就没有存在意义了(见 BuildMedia 与 ApiClient)。
    /// </summary>
    private void SeekTo(long targetMs)
    {
        if (_closing || _mp == null) return;
        var length = _mp.Length;
        if (length <= 0) return;
        targetMs = Math.Clamp(targetMs, 0, Math.Max(0, length - 500));

        if (_ended || _mp.State == VLCState.Ended)
        {
            _ended = false;
            EndOverlay.Visibility = Visibility.Collapsed;
            MarkSeekIssued(targetMs);
            RebuildMediaAndSeek(targetMs);
            return;
        }

        var cur = _lastKnownMs > 0 ? _lastKnownMs : 0;
        var delta = targetMs - cur;

        // 挡掉同一次点击经 ValueChanged + DragCompleted 重复进来的相同目标
        if (_pendingSeekMs >= 0 && Math.Abs(_pendingSeekMs - targetMs) <= SeekConfirmWindowMs) return;
        if (_pendingSeekMs < 0 && Math.Abs(cur - targetMs) < 250) return;

        // ★ 用户**主动**发起了一次新跳转 ⇒ 重建预算复位(新的一次该再给一次机会)。
        //   只在这里复位, 不在 MarkSeekIssued 里 —— 见 _seekRebuildTried 的说明。
        _seekRebuildTried = false;
        _eofSeekRebuildTried = false;

        // ★★★ 2026-10-17 播放器手术第 2 步: 两条路, 由**片源类型**决定, 不由距离决定。
        //
        //   【可靠路】单文件合流(durl + :demux=avformat): seek 一次到位, 实测来回 7 跳
        //           偏差 0.9~2.2s 且画面持续推进(探针 bd-probe-muxed2)。
        //           ⇒ 只发一次 `_mp.Time = target`, 剩下的交给那套**通用**看门狗。
        //
        //   【旧路】DASH 双流(input-slave): libvlc 对 frag-MP4 的远跳会 `cannot seek`,
        //           位置掉回开头(实测偏差 -1180s/-910s/-590s, 探针 bd-probe-muxed5)。
        //           这条路救不回来 —— 分段跳/重建补跳实测只是把"一次跳"换成
        //           "多次跳", 中途照样丢, 用户体感就是"卡在半路、无限缓冲"。
        //           既然主路已换成可靠源(见 ApiClient.GetPlayUrlAsync), 这里
        //           **不再叠补偿**: 发一次 + 看门狗超时后重建一次, 重建也失败就如实
        //           停在原地(让用户看见真实位置), 好过让进度条说谎。
        //           ★ 2026-10-31: 曾试过"长跳一律直接重建"以修"跳 1 小时没声",
        //             结果引入"打开黑屏 / 拖进度条卡死"两个更严重的回归(见 RebuildMediaAndSeek),
        //             已回退。真正的无声根因是音量写回被 aout 就绪窗口吞掉, 由
        //             RestoreVolumeWhenReady 那套补写修复 —— 与跳转距离无关。
        if (_isReliableSeekSource)
        {
            try { _mp.Time = targetMs; } catch { }
            MarkSeekIssued(targetMs);
            return;
        }

        // DASH: 短跳(≤150s)实测可靠, 一步到位
        if (Math.Abs(delta) <= SeekSingleMaxMs)
        {
            try { _mp.Time = targetMs; } catch { }
            MarkSeekIssued(targetMs);
            return;
        }

        // 长跳: 发一次, 由看门狗确认; 确认不到就重建一次(见 UpdateProgressUi 的看门狗分支)
        try { _mp.Time = targetMs; } catch { }
        MarkSeekIssued(targetMs);
    }

    /// <summary>
    /// 当前片源的 seek 是否"一发就准"(单文件合流, 无 input-slave)。
    ///
    /// 由 <see cref="BuildMedia"/> 在建 Media 时顺带置位 —— 判定依据是"远端分片流":
    /// 挂了 input-slave 的 DASH 双流、以及「听视频」的音轨单流都是**分片 fMP4**
    /// (远跳不可靠), 只有 durl 合流单流与本地文件是普通 MP4(可靠)。
    /// </summary>
    private bool _isReliableSeekSource = true;

    /// <summary>单次 seek 能可靠完成的最大距离(毫秒)。超过它 libvlc 会把位置丢回原处。</summary>
    private const long SeekSingleMaxMs = 150_000;

    /// <summary>
    /// 重建 Media 后跳到目标 —— **看门狗放弃时的兜底**(不是长跳的常规路径)。
    ///
    /// ★ 这就是"换片/切清晰度"那条路: 丢一条全新的 Media 进去, 起播后立刻设目标。
    ///   实测落点偏差 416ms(见探针 I 段)。音轨 slave 与主输入**一起新建**,
    ///   不存在"slave 远跳掉线"的状态。
    ///
    /// ★★★ 2026-10-31 补 `Stop()`(修"打开长视频黑屏 / 拖进度条程序卡死"):
    ///   这条路径原先**漏了 Stop**。它此前只在看门狗彻底放弃时才走到, 而那种时刻旧
    ///   input 往往已经死了(seek 失败 → 状态回落), 所以 `play()` 还能被接受, 一直没暴露。
    ///   一旦让它承接更常见的情形, 就会踩中 LibVLC 3 的硬规则:
    ///   **已有 input 在播时 `play()` 被直接忽略**(返回值 false, 见 SwitchQualityAsync 的同款
    ///   注释) ⇒ 新 Media 根本没起播, 画面被 ShowLoading 遮着 ⇒ **黑屏**;
    ///   而 `_resumeAfterSwitchMs` 永远等不到 Playing, 那次跳转的恢复也一并丢失。
    ///   ⇒ 必须先 Stop 再 Play(与换片/切清晰度/重播那几条路完全一致)。
    ///   ★ 代价: `Stop()` 会阻塞约 1 秒(见 TeardownAndCloseAsync 的实测)。这条路径本来就
    ///     已经压着"正在跳转…"遮罩, 用户感知是"缓冲了一下", 可接受; 但**绝不能让它在
    ///     常规操作里反复触发** —— 那才会变成"卡死"。这正是长跳不再一律走这里的理由。
    /// </summary>
    private void RestoreEndedOverlay()
    {
        if (_closing) return;
        _ended = true;
        HideLoading();
        if (VideoAreaRoot != null)
        {
            VideoAreaRoot.Background = Brushes.Black;
            _fullscreenCoverArmed = true;
        }
        if (EndOverlay != null) EndOverlay.Visibility = Visibility.Visible;
        UpdatePlayButton();
    }

    private void RebuildMediaAndSeek(long targetMs, bool stallRecovery = false)
    {
        if (_closing) return;
        if (_isLocalPlayback)
        {
            var path = _lastPlayUrl;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            ShowLoading("正在跳转…");
            _resumeAfterSwitchMs = targetMs;
            _resumeByStartTime = false;
            _ = ReplaceNativeInputAsync(() =>
            {
                if (!PlayMedia(new Media(_libVLC, path, FromType.FromPath)))
                {
                    _resumeAfterSwitchMs = 0;
                    WriteLibVLCDebug("[Seek] 重建后 play() 未被接受(本地文件)\n");
                    if (stallRecovery) ShowFail("播放重建失败，请重试");
                    else RestoreEndedOverlay();
                }
            });
            return;
        }

        var url = _lastPlayUrl;
        if (string.IsNullOrEmpty(url)) return;
        // 聆听态下重建必须切回音轨单流, 否则会退成无声音视频(issue #81)
        var audioForRebuild = _isListenMode ? null : _lastAudioUrl;
        var urlForRebuild = _isListenMode && !string.IsNullOrEmpty(_listenAudioUrl)
            ? _listenAudioUrl!
            : url;
        ShowLoading(stallRecovery ? StallReconnectingText : "正在跳转…");
        _lastKnownMs = 0;
        _resumeAfterSwitchMs = targetMs;
        _resumeByStartTime = true;
        WriteLibVLCDebug(
            $"[Seek] 重建 Media url={(urlForRebuild == url ? "video" : "listen-audio")} " +
            $"audio={(!string.IsNullOrEmpty(audioForRebuild) ? "slave" : "none")} " +
            $"stall={stallRecovery} start-time={targetMs}ms\n");
        _ = ReplaceNativeInputAsync(() =>
        {
            if (!PlayMedia(BuildMedia(urlForRebuild, audioForRebuild, targetMs)))
            {
                _resumeAfterSwitchMs = 0;
                _resumeByStartTime = false;
                WriteLibVLCDebug("[Seek] 重建后 play() 未被接受(远端流)\n");
                if (stallRecovery) ShowFail("网络卡顿，自动重连失败，请检查网络后重试");
                else RestoreEndedOverlay();
            }
        });
    }

}
