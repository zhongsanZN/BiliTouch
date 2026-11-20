using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliTouch.Helpers;
using BiliTouch.Models;

namespace BiliTouch.Services;

public partial class ApiClient
{
    /// <summary>
    /// 获取视频的在线字幕轨道清单（B 站 CC / AI 字幕，接口 /x/player/wbi/v2）。
    /// 失败或无字幕时安全返回空列表，不影响主播放流程。
    /// </summary>
    public async Task<List<SubtitleTrack>> GetSubtitlesAsync(string bvid, long cid, long aid = 0)
    {
        var result = new List<SubtitleTrack>();
        if (cid <= 0 || (string.IsNullOrWhiteSpace(bvid) && aid <= 0)) return result;

        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string> { ["cid"] = cid.ToString() };
            if (!string.IsNullOrWhiteSpace(bvid)) ps["bvid"] = bvid;
            if (aid > 0) ps["aid"] = aid.ToString();

            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/v2", ps, sign: true);
            if (code != 0 || data == null)
            {
                (code, _, data) = await GetJsonAsync(
                    "https://api.bilibili.com/x/player/v2", ps, sign: false);
            }
            if (code != 0 || data == null) return result;

            if (!data.Value.TryGetProperty("subtitle", out var subObj) || subObj.ValueKind != JsonValueKind.Object)
                return result;
            if (!subObj.TryGetProperty("subtitles", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            var seenUrls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var rawUrl = GetStr(item, "subtitle_url");
                var normalized = SubtitleParser.NormalizeSubtitleUrl(rawUrl);
                if (string.IsNullOrEmpty(normalized) || !seenUrls.Add(normalized)) continue;

                var lan = GetStr(item, "lan");
                var lanDoc = GetStr(item, "lan_doc");
                var type = GetInt(item, "type");
                var aiType = GetInt(item, "ai_type");
                var isAi = type == 1 || aiType != 0 || lan.StartsWith("ai-", StringComparison.OrdinalIgnoreCase);

                result.Add(new SubtitleTrack
                {
                    Id = GetLong(item, "id"),
                    Lan = lan,
                    LanDoc = string.IsNullOrWhiteSpace(lanDoc) ? lan : lanDoc,
                    SubtitleUrl = normalized,
                    IsAi = isAi,
                });
            }
        }
        catch
        {
            // 字幕属于可选增强信息，失败静默返回空
        }
        return result;
    }

    /// <summary>下载并解析单条 B 站字幕轨道 JSON。</summary>
    public async Task<IReadOnlyList<SubtitleCue>> DownloadSubtitleCuesAsync(string subtitleUrl, CancellationToken ct = default)
    {
        var url = SubtitleParser.NormalizeSubtitleUrl(subtitleUrl);
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<SubtitleCue>();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            using var resp = await SendThrottledAsync(req, ct).ConfigureAwait(false);
            if (resp == null || !resp.IsSuccessStatusCode) return Array.Empty<SubtitleCue>();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return SubtitleParser.ParseBilibiliJson(json);
        }
        catch
        {
            return Array.Empty<SubtitleCue>();
        }
    }
}
