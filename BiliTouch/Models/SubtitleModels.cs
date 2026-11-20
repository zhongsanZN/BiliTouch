using System;

namespace BiliTouch.Models;

/// <summary>视频字幕轨道（B 站 CC / AI 字幕、外挂本地字幕或本地内嵌 SPU 轨道）。</summary>
public sealed class SubtitleTrack
{
    public long Id { get; init; }
    public string Lan { get; init; } = "";
    public string LanDoc { get; init; } = "";
    public string SubtitleUrl { get; init; } = "";
    public bool IsAi { get; init; }
    public bool IsLocalFile { get; init; }
    /// <summary>LibVLC 本地内嵌字幕轨道 ID（-1 表示非内嵌 SPU，走 WPF 原生字幕层渲染）。</summary>
    public int EmbeddedSpuId { get; init; } = -1;

    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(LanDoc)
            ? LanDoc
            : !string.IsNullOrWhiteSpace(Lan) ? Lan : "字幕";
}

/// <summary>单条字幕时间片段（按秒计，与播放器媒体时钟同单位）。</summary>
public readonly record struct SubtitleCue(double FromSec, double ToSec, string Content);
