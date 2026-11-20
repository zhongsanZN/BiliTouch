using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiliTouch.Models;

namespace BiliTouch.Helpers;

/// <summary>
/// 字幕解析与时间轴匹配工具（纯逻辑，零 UI/网络依赖）：
/// 支持 B 站 CC/AI 字幕 JSON、外挂 SRT / WebVTT，并提供 O(log N) 级当前字幕检索与轨道偏好挑选。
/// </summary>
public static class SubtitleParser
{
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex SrtTimeRangeRegex = new(
        @"(?<sH>\d{1,2}:)?(?<sM>\d{1,2}):(?<sS>\d{1,2})[.,](?<sMs>\d{1,3})\s*-->\s*(?<eH>\d{1,2}:)?(?<eM>\d{1,2}):(?<eS>\d{1,2})[.,](?<eMs>\d{1,3})",
        RegexOptions.Compiled);

    /// <summary>把协议相对地址补全为 https://。</summary>
    public static string NormalizeSubtitleUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        if (s.StartsWith("//", StringComparison.Ordinal)) return "https:" + s;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "https://" + s[7..];
        return s;
    }

    /// <summary>解析 B 站字幕 JSON（{ body: [{ from, to, content }] }）。</summary>
    public static IReadOnlyList<SubtitleCue> ParseBilibiliJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SubtitleCue>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
                return Array.Empty<SubtitleCue>();

            var list = new List<SubtitleCue>(body.GetArrayLength());
            foreach (var item in body.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var from = ReadDouble(item, "from");
                var to = ReadDouble(item, "to");
                var content = item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()?.Trim() ?? ""
                    : "";
                if (content.Length == 0 || !double.IsFinite(from) || !double.IsFinite(to)) continue;
                if (from < 0) from = 0;
                if (to <= from) to = from + 0.5;
                list.Add(new SubtitleCue(from, to, content));
            }
            if (list.Count > 1)
                list.Sort((a, b) => a.FromSec.CompareTo(b.FromSec));
            return list;
        }
        catch
        {
            return Array.Empty<SubtitleCue>();
        }
    }

    /// <summary>解析标准 .srt / .vtt 外挂字幕文本。</summary>
    public static IReadOnlyList<SubtitleCue> ParseSrtOrVtt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<SubtitleCue>();
        var list = new List<SubtitleCue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            var m = SrtTimeRangeRegex.Match(line);
            if (!m.Success)
            {
                i++;
                continue;
            }
            var from = ParseTimestamp(m.Groups["sH"].Value, m.Groups["sM"].Value, m.Groups["sS"].Value, m.Groups["sMs"].Value);
            var to = ParseTimestamp(m.Groups["eH"].Value, m.Groups["eM"].Value, m.Groups["eS"].Value, m.Groups["eMs"].Value);
            i++;
            var sb = new StringBuilder();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                var cleaned = HtmlTagRegex.Replace(lines[i].Trim(), "").Trim();
                if (cleaned.Length > 0)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(cleaned);
                }
                i++;
            }
            if (sb.Length > 0 && to > from)
                list.Add(new SubtitleCue(from, to, sb.ToString()));
        }
        if (list.Count > 1)
            list.Sort((a, b) => a.FromSec.CompareTo(b.FromSec));
        return list;
    }

    /// <summary>
    /// 在按 <see cref="SubtitleCue.FromSec"/> 升序排列的字幕列表中查找当前时刻应显示的全部字幕文本。
    /// 支持同屏多条重叠字幕（按出现顺序换行拼接）。
    /// </summary>
    public static string FindActiveText(IReadOnlyList<SubtitleCue> cues, double timeSec)
    {
        if (cues == null || cues.Count == 0 || !double.IsFinite(timeSec) || timeSec < 0)
            return "";

        // 二分定位最后一个 FromSec <= timeSec 的下标
        int lo = 0, hi = cues.Count - 1, right = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (cues[mid].FromSec <= timeSec)
            {
                right = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (right < 0) return "";

        // 向前扫过可能重叠的字幕（最多覆盖前 16 条或 30 秒窗，避免超长轨道退化）
        var minFrom = timeSec - 30.0;
        List<string>? active = null;
        string? single = null;
        int checkedCount = 0;
        for (int idx = right; idx >= 0 && checkedCount < 24; idx--, checkedCount++)
        {
            var cue = cues[idx];
            if (cue.FromSec < minFrom && cue.ToSec < timeSec) break;
            if (cue.FromSec <= timeSec && timeSec < cue.ToSec)
            {
                if (single == null)
                {
                    single = cue.Content;
                }
                else
                {
                    active ??= new List<string> { single };
                    active.Add(cue.Content);
                }
            }
        }

        if (active == null) return single ?? "";
        active.Reverse();
        return string.Join("\n", active);
    }

    /// <summary>按用户语言偏好及「人工中文 > 人工其他 > AI 中文 > 任意首轨」优先级选择默认字幕轨。</summary>
    public static SubtitleTrack? PickPreferredTrack(IReadOnlyList<SubtitleTrack> tracks, string? preferredLan)
    {
        if (tracks == null || tracks.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(preferredLan))
        {
            var exact = tracks.FirstOrDefault(t =>
                string.Equals(t.Lan, preferredLan, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        static int Rank(SubtitleTrack t)
        {
            var lan = t.Lan ?? "";
            var isZh = lan.Contains("zh", StringComparison.OrdinalIgnoreCase)
                    || (t.LanDoc?.Contains("中文", StringComparison.OrdinalIgnoreCase) ?? false);
            if (!t.IsAi && isZh) return 0;
            if (!t.IsAi) return 1;
            if (isZh) return 2;
            return 3;
        }

        return tracks.OrderBy(Rank).FirstOrDefault();
    }

    private static double ParseTimestamp(string hRaw, string mRaw, string sRaw, string msRaw)
    {
        var h = string.IsNullOrEmpty(hRaw) ? 0 : int.Parse(hRaw.TrimEnd(':'), CultureInfo.InvariantCulture);
        var m = int.Parse(mRaw, CultureInfo.InvariantCulture);
        var s = int.Parse(sRaw, CultureInfo.InvariantCulture);
        var msStr = msRaw.PadRight(3, '0');
        var ms = int.Parse(msStr[..3], CultureInfo.InvariantCulture);
        return h * 3600.0 + m * 60.0 + s + ms / 1000.0;
    }

    private static double ReadDouble(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return double.NaN;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds))
            return ds;
        return double.NaN;
    }
}
