using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.Views;

/// <summary>
/// 播放器赞助/片头片尾片段自动跳过与进度条色块渲染（SponsorBlock）。
/// </summary>
public partial class PlayerWindow
{
    // --- SponsorBlock(跳过社区标注的赞助 / 片头片尾片段) ---
    // 原始数据(未过滤, 含所有类别)与"按当前设置挑出来的"分开存:
    // 用户在设置页改类别时, 用原始数据就能立刻重算, 不必再问一次第三方服务。
    private IReadOnlyList<SponsorSegment> _sponsorRaw = Array.Empty<SponsorSegment>();
    private IReadOnlyList<SponsorSegment> _sponsorActive = Array.Empty<SponsorSegment>();

    /// <summary>
    /// 已经处理过的片段 UUID(自动跳过过的, 以及被用户「撤回」的)。
    /// 处理过就不再自动跳 —— 否则用户一点撤回就又被跳走, 撤回等于无效。
    /// </summary>
    private readonly HashSet<string> _sponsorHandled = new(StringComparer.Ordinal);

    /// <summary>最近一次自动跳过的片段(「撤回」要用它算回到哪一秒)</summary>
    private SponsorSegment? _lastSkipSeg;
    private DispatcherTimer? _skipNoticeTimer;

    /// <summary>
    /// 本次取片段请求的取消源: 换片 / 关窗要能立刻掐掉, 别让上一个视频的查询继续跑。
    /// </summary>
    private CancellationTokenSource? _sponsorCts;

    /// <summary>片段剩余不足这么多毫秒时不跳: 跳到片段末尾只前进一点点, 反而像画面抽了一下</summary>
    private const long SponsorMinRemainMs = 500;

    // ------------------------------------------------------------ 跳过赞助片段(SponsorBlock)

    /// <summary>
    /// 发起片段查询。**必须在"取播放地址"之前调用** —— 它和后面的网络请求/合流下载并行跑,
    /// 等我们真要用它时结果基本已经在手里了。没有在线语境(直播/本地文件)或功能没开时
    /// 一个请求都不发, 直接给空任务。
    /// </summary>
    private Task<IReadOnlyList<SponsorSegment>> FetchSponsorSegmentsAsync()
    {
        CancelSponsorFetch();
        var none = Task.FromResult<IReadOnlyList<SponsorSegment>>(Array.Empty<SponsorSegment>());
        if (!Svc.Settings.SponsorBlockEnabled) return none;
        if (_isLive || _isLocalPlayback) return none;
        if (_currentCid <= 0 || string.IsNullOrEmpty(_currentBvid)) return none;

        _sponsorCts = new CancellationTokenSource();
        return Svc.SponsorBlock.GetRawAsync(_currentBvid, _currentCid, _sponsorCts.Token);
    }

    /// <summary>换片/关窗时掐掉还没回来的查询(否则上一个视频的结果会写进新视频的状态)</summary>
    private void CancelSponsorFetch()
    {
        var cts = _sponsorCts;
        _sponsorCts = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch { /* 已经取消了也无所谓 */ }
        try { cts.Dispose(); } catch { }
    }

    /// <summary>当前视频的身份串(bvid|cid), 用于判断"这份片段数据还是不是当前这个视频的"</summary>
    private string SponsorKey() => _currentBvid + "|" + _currentCid;

    private static async Task<IReadOnlyList<SponsorSegment>> SafeAwait(
        Task<IReadOnlyList<SponsorSegment>> task)
    {
        try { return await task; }
        catch { return Array.Empty<SponsorSegment>(); }
    }

    /// <summary>
    /// 起播前落地片段数据 —— **绝不等待**。
    /// </summary>
    private async Task BeforePlayApplySponsorAsync(Task<IReadOnlyList<SponsorSegment>> task)
    {
        var key = SponsorKey();
        if (task.IsCompleted)
        {
            ApplySponsorRaw(await SafeAwait(task), applyInitialDrop: true);
            return;
        }

        // 还没回来 ⇒ 先起播, 结果回来后再补(见 ApplySponsorLaterAsync)
        _ = ApplySponsorLaterAsync(task, key);
    }

    /// <summary>超时后补落地。只认"还是同一个视频"的那份数据 —— 用户可能已经换片了</summary>
    private async Task ApplySponsorLaterAsync(Task<IReadOnlyList<SponsorSegment>> task, string key)
    {
        var raw = await SafeAwait(task);
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || key != SponsorKey()) return;
            ApplySponsorRaw(raw, applyInitialDrop: false);
        }));
    }

    /// <summary>
    /// 落地片段数据。
    /// </summary>
    private void ApplySponsorRaw(IReadOnlyList<SponsorSegment> raw, bool applyInitialDrop)
    {
        _sponsorRaw = raw ?? Array.Empty<SponsorSegment>();
        RecalcSponsorActive();
        if (!applyInitialDrop || _sponsorActive.Count == 0) return;

        SponsorSegment? coverStart = null;
        foreach (var s in _sponsorActive)
        {
            if (s.StartSec > 0.6) continue;
            if (coverStart == null || s.EndMs > coverStart.EndMs) coverStart = s;
        }
        if (coverStart == null) return;

        _sponsorHandled.Add(coverStart.Uuid);
        _resumeAfterSwitchMs = coverStart.EndMs;
        _lastSkipSeg = coverStart;
        ShowSkipNotice(
            $"已跳过{coverStart.Label}  {VideoItem.FormatSeconds((int)coverStart.EndSec)}");
        WriteLibVLCDebug(
            $"[Sponsor] 空降 {coverStart.Category} {coverStart.StartSec:0.##}~{coverStart.EndSec:0.##}s\n");
    }

    /// <summary>
    /// 按当前设置从**原始数据**里重算"该跳哪些", 并同步刷新进度条上的色块。
    /// </summary>
    private void RecalcSponsorActive()
    {
        _sponsorActive = Svc.Settings.SponsorBlockEnabled
            ? SponsorBlockService.Select(_sponsorRaw, _currentCid, Svc.Settings.SponsorBlockCategories)
            : Array.Empty<SponsorSegment>();
        RenderSponsorMarks();
    }

    private void OnSponsorSettingsChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closing) return;
        RecalcSponsorActive();
        if (!Svc.Settings.SponsorBlockEnabled) HideSkipNotice();
    }));

    private void MaybeSkipSponsorSegment()
    {
        if (_closing || _mp == null) return;
        if (_sponsorActive.Count == 0) return;
        if (_isLive || _isLocalPlayback) return;
        if (!Svc.Settings.SponsorBlockEnabled) return;
        if (_ended || _isSeeking) return;
        if (_pendingSeekMs >= 0) return;
        if (_mp.Length <= 0) return;

        var t = _mp.Time;
        foreach (var seg in _sponsorActive)
        {
            if (t < seg.StartMs || t >= seg.EndMs) continue;
            if (seg.EndMs - t <= SponsorMinRemainMs) return;
            if (!_sponsorHandled.Add(seg.Uuid)) return;
            SkipSponsorSegment(seg, t);
            return;
        }
    }

    private void SkipSponsorSegment(SponsorSegment seg, long fromMs)
    {
        var target = seg.EndMs;
        _lastSkipSeg = seg;
        MarkSeekIssued(target);
        try { _mp.Time = target; } catch { /* 播放器可能刚好失效 */ }
        ShowSkipNotice($"已跳过{seg.Label}  {VideoItem.FormatSeconds((int)((target - fromMs) / 1000.0))}");
        WriteLibVLCDebug(
            $"[Sponsor] skip {seg.Category} {seg.StartSec:0.##}~{seg.EndSec:0.##}s (from {fromMs / 1000.0:0.##}s)\n");
    }

    private void ShowSkipNotice(string text)
    {
        if (SkipNoticeBorder == null || SkipNoticeText == null) return;
        SkipNoticeText.Text = text;
        SkipNoticeBorder.BeginAnimation(OpacityProperty, null);
        SkipNoticeBorder.Opacity = 0;
        SkipNoticeBorder.Visibility = Visibility.Visible;
        SkipNoticeBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        if (_skipNoticeTimer == null)
        {
            _skipNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _skipNoticeTimer.Tick += (_, _) =>
            {
                _skipNoticeTimer?.Stop();
                HideSkipNotice();
            };
        }
        else
        {
            _skipNoticeTimer.Stop();
        }
        _skipNoticeTimer.Start();
    }

    private void HideSkipNotice()
    {
        _skipNoticeTimer?.Stop();
        if (SkipNoticeBorder == null || SkipNoticeBorder.Visibility != Visibility.Visible) return;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            SkipNoticeBorder.BeginAnimation(OpacityProperty, null);
            SkipNoticeBorder.Opacity = 0;
            SkipNoticeBorder.Visibility = Visibility.Collapsed;
        };
        SkipNoticeBorder.BeginAnimation(OpacityProperty, fade);
    }

    private void OnSkipUndoClick(object sender, RoutedEventArgs e)
    {
        var seg = _lastSkipSeg;
        HideSkipNotice();
        if (_closing || _mp == null || seg == null) return;

        var target = seg.StartMs;
        MarkSeekIssued(target);
        try { _mp.Time = target; } catch { }
        WriteLibVLCDebug($"[Sponsor] undo -> {seg.StartSec:0.##}s\n");
    }

    // ------------------------------------------------------------ 进度条上的片段色块

    private static readonly Dictionary<string, Brush> SponsorMarkBrushes = new(StringComparer.Ordinal);

    private static Brush SponsorMarkBrush(string category)
    {
        lock (SponsorMarkBrushes)
        {
            if (SponsorMarkBrushes.TryGetValue(category, out var cached)) return cached;

            var brush = new SolidColorBrush(Colors.Gray);
            try
            {
                if (ColorConverter.ConvertFromString(SponsorCategories.MarkColorHex(category)) is Color c)
                    brush = new SolidColorBrush(c);
            }
            catch
            {
            }
            brush.Freeze();
            SponsorMarkBrushes[category] = brush;
            return brush;
        }
    }

    private (double offset, double usable) TrackMetrics()
    {
        try
        {
            if (ProgressSlider?.Template?.FindName("PART_Track", ProgressSlider) is Track track &&
                track.ActualWidth > 0)
            {
                var thumb = track.Thumb?.ActualWidth ?? 0;
                return (thumb / 2, Math.Max(1, track.ActualWidth - thumb));
            }
        }
        catch
        {
        }
        return (0, Math.Max(1, ProgressSlider?.ActualWidth ?? 0));
    }

    private void RenderSponsorMarks()
    {
        if (SponsorMarks == null) return;
        SponsorMarks.Children.Clear();
        if (_sponsorActive.Count == 0) return;

        var total = _totalMs > 0 ? _totalMs : (_mp?.Length ?? 0);
        if (total <= 0) return;

        var (offset, usable) = TrackMetrics();
        if (usable <= 1) return;

        const double markHeight = 5;
        var height = SponsorMarks.ActualHeight;
        var top = height > markHeight ? (height - markHeight) / 2 : 6.5;

        foreach (var seg in _sponsorActive)
        {
            var ratio1 = Math.Clamp(seg.StartMs / (double)total, 0, 1);
            var ratio2 = Math.Clamp(seg.EndMs / (double)total, 0, 1);
            var x1 = offset + usable * ratio1;
            var x2 = offset + usable * ratio2;
            if (x1 >= usable + offset) continue;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, x2 - x1),
                Height = markHeight,
                RadiusX = markHeight / 2,
                RadiusY = markHeight / 2,
                Fill = SponsorMarkBrush(seg.Category)
            };
            Canvas.SetLeft(rect, x1);
            Canvas.SetTop(rect, top);
            SponsorMarks.Children.Add(rect);
        }
    }
}
