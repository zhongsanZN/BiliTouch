using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using BiliTouch.Services;
using BiliTouch.Utils;
using LibVLCSharp.Shared;

namespace BiliTouch.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ 播放中途卡顿自动恢复 & 地址保鲜(借鉴上游 issue #81)

    private const string StallReconnectingText = "网络卡顿，正在重连…";
    private const long UrlRefreshLeadSeconds = 10 * 60;
    private const long UrlRefreshFallbackMs = 90 * 60 * 1000L;
    private const long UrlRefreshRetryMs = 60 * 1000L;

    private readonly StallRecoveryPolicy _stallPolicy = new();
    private readonly List<string> _backups = new();

    private long _playUrlDeadline;
    private long _playUrlFetchedAt;
    private long _playUrlRefreshedAt;
    private long _urlRefreshAttemptAt;
    private bool _urlRefreshInFlight;
    private bool _stallRecoveryInFlight;
    private bool _errorRecoveryTried;

    /// <summary>换片或重置播放上下文时归零所有恢复/保鲜状态(供 ResetForNewMedia 调用)。</summary>
    private void ResetRecoveryState()
    {
        _stallPolicy.Reset();
        _backups.Clear();
        _playUrlDeadline = 0;
        _playUrlFetchedAt = 0;
        _playUrlRefreshedAt = 0;
        _urlRefreshAttemptAt = 0;
        _urlRefreshInFlight = false;
        _stallRecoveryInFlight = false;
        _errorRecoveryTried = false;
    }

    private VLCState SafePlayerState()
    {
        try { return _mp?.State ?? VLCState.Error; }
        catch { return VLCState.Error; }
    }

    private string? CurrentMediaUrl =>
        _isListenMode && !string.IsNullOrEmpty(_listenAudioUrl) ? _listenAudioUrl : _lastPlayUrl;

    private static bool IsStallOverlayText(string? text)
        => text == BufferingText || text == StallReconnectingText;

    private void HandleStallAction(StallRecoveryAction action, long posMs)
    {
        switch (action)
        {
            case StallRecoveryAction.ShowHint:
                if (LoadingOverlay == null || LoadingText == null) return;
                if (LoadingOverlay.Visibility == Visibility.Visible)
                {
                    if (IsShowingBuffering()) SetLoadingText(StallReconnectingText);
                }
                else
                {
                    ShowLoading(StallReconnectingText);
                }
                break;

            case StallRecoveryAction.Rebuild:
                WriteLibVLCDebug(
                    $"[Stall] 卡顿自愈第{_stallPolicy.Rebuilds}次: 重建 input " +
                    $"pos={posMs}ms listen={_isListenMode} " +
                    $"deadline={_playUrlDeadline} url={HostOf(CurrentMediaUrl)}\n");
                _ = RecoverFromStallAsync();
                break;

            case StallRecoveryAction.GiveUp:
                WriteLibVLCDebug(
                    $"[Stall] 自动重连放弃(已重建 {_stallPolicy.Rebuilds} 次) pos={posMs}ms " +
                    $"listen={_isListenMode} url={HostOf(CurrentMediaUrl)}\n");
                ShowFail("网络卡顿，自动重连失败，请检查网络后重试");
                break;
        }
    }

    private async Task RecoverFromStallAsync()
    {
        if (_closing || _mp == null || _stallRecoveryInFlight) return;

        var gen = _loadGen;
        _stallRecoveryInFlight = true;
        try
        {
            ShowLoading(StallReconnectingText);
            ClearSeekWatchdog(hideBuffering: false);

            if (ShouldRefreshUrlBeforeRebuild())
            {
                await RefreshPlayUrlAsync();
                if (_closing || gen != _loadGen) return;
                if (_isSeeking || _pendingSeekMs >= 0) return;
            }

            if (_stallPolicy.Rebuilds >= 2) UseBackupUrlIfAny();

            var resume = _lastKnownMs > 0 ? _lastKnownMs : 0;
            RebuildMediaAndSeek(resume, stallRecovery: true);

            _stallLastMs = -1;
            _stallTicks = 0;
        }
        catch (Exception ex)
        {
            WriteLibVLCDebug($"[Stall] 自愈异常: {ex.Message}\n");
        }
        finally
        {
            _stallRecoveryInFlight = false;
        }
    }

    private void MaybeRefreshPlayUrl()
    {
        if (_closing || _urlRefreshInFlight || _stallRecoveryInFlight) return;
        if (!ShouldRefreshUrlBeforeRebuild()) return;
        if (_urlRefreshAttemptAt > 0 &&
            Environment.TickCount64 - _urlRefreshAttemptAt < UrlRefreshRetryMs) return;
        _ = RefreshPlayUrlAsync();
    }

    private bool ShouldRefreshUrlBeforeRebuild()
    {
        if (_isLive || _isLocalPlayback || _aid <= 0 || _currentCid <= 0) return false;
        var current = CurrentMediaUrl;
        if (string.IsNullOrEmpty(current) ||
            !current.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        if (_switchingQuality) return false;

        if (_playUrlDeadline > 0 &&
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= _playUrlDeadline - UrlRefreshLeadSeconds)
            return true;

        return _playUrlFetchedAt > 0 &&
               Environment.TickCount64 - _playUrlFetchedAt >= UrlRefreshFallbackMs;
    }

    private async Task RefreshPlayUrlAsync()
    {
        if (_closing || _urlRefreshInFlight) return;
        var bvid = _currentBvid;
        var cid = _currentCid;
        if (string.IsNullOrEmpty(bvid) || cid <= 0) return;

        var gen = _loadGen;
        var qn = _currentQn;
        _urlRefreshInFlight = true;
        _urlRefreshAttemptAt = Environment.TickCount64;
        try
        {
            var (ok, err, main, backups, _, _, _, audioUrl, _, _, _) =
                await Svc.Api.GetPlayUrlAsync(bvid, cid, qn, Svc.Settings.PreferredAudioQuality);

            if (_closing || gen != _loadGen || _currentCid != cid) return;
            if (!ok || (string.IsNullOrEmpty(main) && string.IsNullOrEmpty(audioUrl)))
            {
                WriteLibVLCDebug($"[UrlFresh] 重取地址失败: {err}\n");
                return;
            }

            var wasLocalMain = !string.IsNullOrEmpty(_lastPlayUrl) &&
                               !_lastPlayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            var wasDash = !string.IsNullOrEmpty(_lastAudioUrl);
            if (!wasLocalMain && !string.IsNullOrEmpty(main)) _lastPlayUrl = main;
            RememberPlayUrlFreshness(!string.IsNullOrEmpty(main) ? main : audioUrl, backups);
            if (wasDash && !string.IsNullOrEmpty(audioUrl)) _lastAudioUrl = audioUrl;
            if (!string.IsNullOrEmpty(audioUrl)) _listenAudioUrl = audioUrl;
            _playUrlRefreshedAt = Environment.TickCount64;
            WriteLibVLCDebug($"[UrlFresh] 已换新地址 listen={_isListenMode} host={HostOf(CurrentMediaUrl)}\n");
        }
        catch (Exception ex)
        {
            WriteLibVLCDebug($"[UrlFresh] 异常: {ex.Message}\n");
        }
        finally
        {
            _urlRefreshInFlight = false;
        }
    }

    private void UseBackupUrlIfAny()
    {
        if (_isListenMode || _backups.Count == 0) return;
        if (string.IsNullOrEmpty(_lastPlayUrl) ||
            !_lastPlayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;

        var next = _backups[0];
        _backups.RemoveAt(0);
        if (string.IsNullOrEmpty(next) || string.Equals(next, _lastPlayUrl, StringComparison.Ordinal))
            return;
        _lastPlayUrl = next;
        WriteLibVLCDebug($"[Stall] 换候选线路: {HostOf(next)}\n");
    }

    private bool TryAutoRecoverFromError()
    {
        if (_closing || _isLive || _isLocalPlayback || _errorRecoveryTried) return false;
        if (_playUrlRefreshedAt <= 0) return false;
        if (string.IsNullOrEmpty(CurrentMediaUrl)) return false;
        if (Environment.TickCount64 - _playUrlRefreshedAt > 30 * 60 * 1000) return false;

        _errorRecoveryTried = true;
        WriteLibVLCDebug("[Stall] 播放出错 → 用刷新后的地址自动重建一次\n");
        _ = RecoverFromStallAsync();
        return true;
    }

    private void RememberPlayUrlFreshness(string? mainUrl, IEnumerable<string>? backups)
    {
        _backups.Clear();
        if (backups != null) _backups.AddRange(backups);
        var deadlineUrl = !string.IsNullOrEmpty(mainUrl) &&
                          mainUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? mainUrl
            : _listenAudioUrl;
        _playUrlDeadline = UrlParser.ExtractQueryLong(deadlineUrl, "deadline") ?? 0;
        _playUrlFetchedAt = Environment.TickCount64;
        if (_playUrlDeadline > 0)
            WriteLibVLCDebug(
                $"[UrlFresh] deadline={_playUrlDeadline} " +
                $"({DateTimeOffset.FromUnixTimeSeconds(_playUrlDeadline).ToLocalTime():yyyy-MM-dd HH:mm:ss} 本地)\n");
    }

    private static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "?";
        try { return new Uri(url).Host; } catch { return "?"; }
    }
}
