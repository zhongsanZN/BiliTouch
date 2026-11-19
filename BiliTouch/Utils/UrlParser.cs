using System;
using System.Text.RegularExpressions;

namespace BiliTouch.Utils;

/// <summary>B 站 URL 解析器: 提取 bvid / uid / keyword 等参数</summary>
public static class UrlParser
{
    private static readonly Regex BvidRegex = new(@"/(BV[0-9A-Za-z]{10})", RegexOptions.Compiled);
    private static readonly Regex UidRegex = new(@"/space/(\d+)", RegexOptions.Compiled);
    private static readonly Regex SpaceHostUidRegex = new(@"^/(\d+)(?:/|$)", RegexOptions.Compiled);
    private static readonly Regex CvRegex = new(@"/read/cv(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OpusRegex = new(@"/opus/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 把"可能是完整 URL、也可能是不带协议的裸路径"的串解析成 <see cref="Uri"/>。
    ///
    /// 三个提取方法(视频/UP/关键词)面对的是同一批输入 —— 剪贴板里粘来的、网页版复制的、
    /// 只带了 `//` 的 —— 所以容错规则只该有一份, 否则迟早出现"BV 号能提取、关键词却提取不到"。
    /// 解析不出来就让调用方返回 null(降级), 不抛异常。
    /// </summary>
    private static bool TryParseUri(string urlOrUri, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(urlOrUri)) return false;
        var trimmed = urlOrUri.Trim();
        try
        {
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                uri = new Uri(trimmed);
            else if (trimmed.StartsWith("//", StringComparison.Ordinal))
                uri = new Uri("https:" + trimmed);
            else if (trimmed.StartsWith("/", StringComparison.Ordinal))
                uri = new Uri("https://www.bilibili.com" + trimmed);
            else
                uri = new Uri("https://" + trimmed);
            return true;
        }
        catch
        {
            // 空串 / 只给了相对路径 / 协议名非法 —— 都算"不是链接", 交给调用方降级
            return false;
        }
    }

    /// <summary>提取视频 BV 号</summary>
    public static string? ExtractBvid(string urlOrUri)
    {
        if (!TryParseUri(urlOrUri, out var uri)) return null;
        var m = BvidRegex.Match(uri.AbsolutePath);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>提取 UP 主 uid(space.bilibili.com/{mid} 或 /space/{mid})</summary>
    public static string? ExtractUid(string urlOrUri)
    {
        if (!TryParseUri(urlOrUri, out var uri)) return null;
        if (uri.Host.Equals("space.bilibili.com", StringComparison.OrdinalIgnoreCase))
        {
            var hostMatch = SpaceHostUidRegex.Match(uri.AbsolutePath);
            if (hostMatch.Success) return hostMatch.Groups[1].Value;
        }
        var m = UidRegex.Match(uri.AbsolutePath);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>提取专栏 cv 号(www.bilibili.com/read/cv123456)</summary>
    public static string? ExtractArticleId(string urlOrUri)
    {
        if (!TryParseUri(urlOrUri, out var uri)) return null;
        var m = CvRegex.Match(uri.AbsolutePath);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>提取图文动态 Opus 号(www.bilibili.com/opus/123456)</summary>
    public static string? ExtractOpusId(string urlOrUri)
    {
        if (!TryParseUri(urlOrUri, out var uri)) return null;
        var m = OpusRegex.Match(uri.AbsolutePath);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>从 /search?... 中提取 keyword(手动解析, 不依赖 System.Web)</summary>
    public static string? ExtractKeyword(string urlOrUri)
    {
        if (!TryParseUri(urlOrUri, out var uri)) return null;
        try
        {
            var q = uri.Query.TrimStart('?');
            foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = Uri.UnescapeDataString(pair[..eq]);
                if (key != "keyword") continue;
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return null;
        }
        catch
        {
            // 查询串里的转义是坏的(非法百分号编码): 当作"没提取到关键词", 让调用方走普通搜索
            return null;
        }
    }

    /// <summary>
    /// 从完整 URL(或带 <c>//</c> 的 URL)的查询串中读取一个整数参数(例如 playurl 的 <c>deadline</c>)。
    /// 找不到、格式无效或 URL 解析失败一律返回 null。
    /// </summary>
    public static long? ExtractQueryLong(string? urlOrUri, string paramName)
    {
        if (string.IsNullOrEmpty(urlOrUri) || string.IsNullOrEmpty(paramName)) return null;
        if (!TryParseUri(urlOrUri, out var uri)) return null;
        try
        {
            var q = uri.Query.TrimStart('?');
            foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = Uri.UnescapeDataString(pair[..eq]);
                if (!string.Equals(key, paramName, StringComparison.Ordinal)) continue;
                var raw = Uri.UnescapeDataString(pair[(eq + 1)..]);
                return long.TryParse(raw, out var value) ? value : null;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}