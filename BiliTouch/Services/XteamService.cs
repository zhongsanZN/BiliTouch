using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BiliTouch.Helpers;
using BiliTouch.Utils;

namespace BiliTouch.Services;

public sealed class XteamStatItem
{
    public string Value { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed class XteamActionLink
{
    public string Label { get; init; } = "";
    public string Url { get; init; } = "";
    public bool IsPrimary { get; init; }
    public bool IsInternalDetail => Url.StartsWith("https://xteamparty.com/works/", StringComparison.OrdinalIgnoreCase)
        || Url.StartsWith("https://xteamparty.com/picks/", StringComparison.OrdinalIgnoreCase);
}

public sealed class XteamProjectItem
{
    public string Category { get; init; } = ""; // works | picks
    public string SubCategory { get; init; } = ""; // mod | game | ""
    public string StatusText { get; init; } = "";
    public bool IsPinned { get; init; }
    public string IpTag { get; init; } = "";
    public string Title { get; init; } = "";
    public string SubTitle { get; init; } = "";
    public string Tagline { get; init; } = "";
    public string Description { get; init; } = "";
    public string AuthorName { get; init; } = "";
    public string AuthorUrl { get; init; } = "";
    public string AuthorUid { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public string DetailUrl { get; init; } = "";
    public string? PrimaryActionLabel { get; init; }
    public string? PrimaryActionUrl { get; init; }
    public List<string> Screenshots { get; } = new();
    public List<string> Tags { get; } = new();
    public bool HasAuthor => !string.IsNullOrEmpty(AuthorName);
    public bool HasBiliAuthor => !string.IsNullOrEmpty(AuthorUid);
    public bool HasPrimaryAction => !string.IsNullOrEmpty(PrimaryActionUrl);
    public bool HasScreenshots => Screenshots.Count > 0;
}

public sealed class XteamLinkItem
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Url { get; init; } = "";
    public string Hint { get; init; } = "";
    public string BiliUid { get; init; } = "";
}

public sealed class XteamAudioTrack
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string File { get; init; } = "";
    public string Pack { get; init; } = "";
    public string PackCategory { get; init; } = "";
    public string Url { get; init; } = "";
}

public sealed class XteamAudioPack
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public List<XteamAudioTrack> Tracks { get; } = new();
}

public sealed class XteamAboutSection
{
    public string Heading { get; init; } = "";
    public List<string> Paragraphs { get; } = new();
    public List<string> Bullets { get; } = new();
}

public sealed class XteamSiteData
{
    public string HeroBadge { get; init; } = "XTEAM · 吉星派对非官方同人站";
    public string HeroTitle { get; init; } = "让热爱相遇，让派对继续。";
    public string HeroSubtitle { get; init; } = "为热爱而创作 · 免费分享 · 非商业交流";
    public List<XteamStatItem> Stats { get; } = new();
    public List<XteamProjectItem> Works { get; } = new();
    public List<XteamProjectItem> Picks { get; } = new();
    public List<XteamLinkItem> Links { get; } = new();
    public List<XteamAudioPack> AudioPacks { get; } = new();
    public List<XteamAboutSection> AboutSections { get; } = new();
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class XteamProjectDetail
{
    public string Url { get; init; } = "";
    public string BreadcrumbCategory { get; init; } = "";
    public string StatusText { get; init; } = "";
    public bool IsPinned { get; init; }
    public string DateText { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public string AuthorName { get; init; } = "";
    public string AuthorUrl { get; init; } = "";
    public string AuthorUid { get; init; } = "";
    public List<(string Label, string Value)> Meta { get; } = new();
    public List<string> Paragraphs { get; } = new();
    public List<string> Screenshots { get; } = new();
    public List<XteamActionLink> Actions { get; } = new();
    public bool HasBiliAuthor => !string.IsNullOrEmpty(AuthorUid);
}

/// <summary>
/// Native client and parser for https://xteamparty.com/ (星创组 · 吉星派对同人站).
/// Fetches and structures works, community fan projects, partner links, about info, and OST tracks
/// so the desktop UI renders completely natively without embedding a full browser window.
/// </summary>
public sealed partial class XteamService
{
    public const string BaseUrl = "https://xteamparty.com";
    private static readonly HttpClient Http = CreateClient();

    private static XteamSiteData? _cachedSite;
    private static readonly Dictionary<string, XteamProjectDetail> DetailCache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", HttpDefaults.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");
        return client;
    }

    public static XteamService Instance { get; } = new();

    public async Task<(bool ok, string? err, XteamSiteData? data)> LoadSiteAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedSite != null) return (true, null, _cachedSite);
        try
        {
            var homeTask = FetchHtmlAsync("/");
            var worksTask = FetchHtmlAsync("/works/");
            var picksTask = FetchHtmlAsync("/picks/");
            var linksTask = FetchHtmlAsync("/links/");
            var aboutTask = FetchHtmlAsync("/about/");
            await Task.WhenAll(homeTask, worksTask, picksTask, linksTask, aboutTask).ConfigureAwait(false);

            var data = ParseSite(
                homeTask.Result,
                worksTask.Result,
                picksTask.Result,
                linksTask.Result,
                aboutTask.Result);
            _cachedSite = data;
            return (true, null, data);
        }
        catch (Exception ex)
        {
            return (false, "加载星创组站点数据失败：" + ex.Message, _cachedSite);
        }
    }

    public async Task<(bool ok, string? err, XteamProjectDetail? detail)> LoadDetailAsync(string url, bool forceRefresh = false)
    {
        var abs = ResolveUrl(url);
        if (string.IsNullOrEmpty(abs)) return (false, "无效的作品详情地址", null);
        if (!forceRefresh && DetailCache.TryGetValue(abs, out var cached))
            return (true, null, cached);
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, abs);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var detail = ParseDetail(html, abs);
            DetailCache[abs] = detail;
            return (true, null, detail);
        }
        catch (Exception ex)
        {
            return (false, "加载作品档案失败：" + ex.Message, null);
        }
    }

    private static async Task<string> FetchHtmlAsync(string path)
    {
        using var resp = await Http.GetAsync(ResolveUrl(path)).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    public async Task<string?> ResolveShortLinkAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            return resp.RequestMessage?.RequestUri?.ToString();
        }
        catch
        {
            return null;
        }
    }

    internal static string ResolveUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = WebUtility.HtmlDecode(raw).Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (trimmed.StartsWith("//")) return "https:" + trimmed;
        if (!trimmed.StartsWith("/")) trimmed = "/" + trimmed;
        return BaseUrl + trimmed;
    }

    internal static string ExtractBiliUid(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals("space.bilibili.com", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(uri.AbsolutePath, @"^/(\d+)");
            if (m.Success) return m.Groups[1].Value;
        }
        return UrlParser.ExtractUid(url) ?? "";
    }

    internal static XteamSiteData ParseSite(string homeHtml, string worksHtml, string picksHtml, string linksHtml, string aboutHtml)
    {
        var site = new XteamSiteData();

        var badge = ExtractText(homeHtml, @"class=""hero-eyebrow-badge[^>]*>.*?<span class=""badge-text"">(.*?)</span>");
        if (badge.Length > 0) site = new XteamSiteData
        {
            HeroBadge = badge,
            HeroTitle = CleanText(MatchGroup(homeHtml, @"<h1 class=""hero-main-title[^>]*>(.*?)</h1>")).Replace("， ", "，"),
            HeroSubtitle = "为热爱而创作 · 免费分享 · 非商业交流"
        };

        foreach (Match sm in StatRegex().Matches(homeHtml))
        {
            var val = CleanText(sm.Groups["val"].Value);
            var unit = CleanText(sm.Groups["unit"].Value);
            var label = CleanText(sm.Groups["label"].Value);
            if (val.Length > 0 || label.Length > 0)
                site.Stats.Add(new XteamStatItem { Value = val, Unit = unit, Label = label });
        }

        foreach (var pack in ParseHomeOstTracks(homeHtml))
            site.AudioPacks.Add(pack);

        foreach (var w in ParseCards(worksHtml, "works"))
            site.Works.Add(w);
        foreach (var p in ParseCards(picksHtml, "picks"))
            site.Picks.Add(p);
        foreach (var l in ParseLinks(linksHtml))
            site.Links.Add(l);
        foreach (var a in ParseAbout(aboutHtml))
            site.AboutSections.Add(a);

        return site;
    }

    internal static List<XteamProjectItem> ParseCards(string html, string category)
    {
        var list = new List<XteamProjectItem>();
        if (string.IsNullOrEmpty(html)) return list;

        foreach (Match cm in ArticleCardRegex().Matches(html))
        {
            var block = cm.Value;
            var cardHref = cm.Groups["href"].Value;
            var subCat = MatchGroup(block, @"data-pick-category=""([^""]+)""");
            var status = CleanText(MatchGroup(block, @"<span class=""status-name"">(.*?)</span>"));
            var pinned = block.Contains("xcard-pinned-pill", StringComparison.Ordinal) || block.Contains("xcard--pinned", StringComparison.Ordinal);
            var ipTag = CleanText(MatchGroup(block, @"<span class=""ip-tag"">(.*?)</span>"));
            var mainTitle = CleanText(MatchGroup(block, @"<span class=""main-title"">(.*?)</span>"));
            var subTitle = CleanText(MatchGroup(block, @"<span class=""sub-title"">(.*?)</span>"));
            var title = string.IsNullOrEmpty(subTitle) ? mainTitle : $"{mainTitle} {subTitle}".Trim();
            if (string.IsNullOrEmpty(title))
                title = CleanText(MatchGroup(block, @"<h3 class=""xcard-title"">\s*<a[^>]*>(.*?)</a>"));
            var tagline = CleanText(MatchGroup(block, @"<p class=""xcard-subtitle"">(.*?)</p>"));
            var desc = CleanText(MatchGroup(block, @"<p class=""xcard-description"">(.*?)</p>"));
            var authorUrl = ResolveUrl(MatchGroup(block, @"<a class=""author-link""[^>]*href=""([^""]+)"""));
            var authorName = CleanText(MatchGroup(block, @"<span class=""author-name-text"">(.*?)</span>"));
            var authorUid = ExtractBiliUid(authorUrl);
            var coverSrc = ResolveUrl(MatchGroup(block, @"<div class=""xcard-cover-wrapper"">.*?<img[^>]*src=""([^""]+)"""));
            var detailHref = ResolveUrl(string.IsNullOrEmpty(cardHref)
                ? MatchGroup(block, @"<a href=""([^""]+)"" class=""xcard-cover""")
                : cardHref);

            var primaryHref = "";
            var primaryLabel = "";
            var actionsHtml = MatchGroup(block, @"<div class=""xcard-actions"">(.*?)</div>\s*(?:</div>|<!--)");
            foreach (Match am in AnchorRegex().Matches(actionsHtml))
            {
                var href = am.Groups["href"].Value;
                var cls = am.Groups["cls"].Value;
                var text = CleanText(am.Groups["text"].Value).Replace("→", "").Replace("↗", "").Trim();
                if (cls.Contains("btn-primary", StringComparison.Ordinal))
                {
                    primaryHref = ResolveUrl(href);
                    primaryLabel = text;
                    break;
                }
            }

            var item = new XteamProjectItem
            {
                Category = category,
                SubCategory = subCat,
                StatusText = status,
                IsPinned = pinned,
                IpTag = ipTag,
                Title = mainTitle.Length > 0 ? mainTitle : title,
                SubTitle = subTitle,
                Tagline = tagline,
                Description = desc,
                AuthorName = authorName,
                AuthorUrl = authorUrl,
                AuthorUid = authorUid,
                CoverUrl = coverSrc,
                DetailUrl = detailHref,
                PrimaryActionLabel = primaryLabel.Length > 0 ? primaryLabel : null,
                PrimaryActionUrl = primaryHref.Length > 0 ? primaryHref : null
            };

            foreach (Match tm in ThumbImgRegex().Matches(block))
            {
                var src = ResolveUrl(tm.Groups["src"].Value);
                if (src.Length > 0 && !item.Screenshots.Contains(src))
                    item.Screenshots.Add(src);
            }

            foreach (Match tagm in CardTagRegex().Matches(block))
            {
                var t = CleanText(tagm.Groups["tag"].Value);
                if (t.Length > 0) item.Tags.Add(t);
            }

            if (!string.IsNullOrEmpty(item.Title)) list.Add(item);
        }

        return list;
    }

    internal static List<XteamAudioPack> ParseHomeOstTracks(string homeHtml)
    {
        var packs = new List<XteamAudioPack>();
        if (string.IsNullOrEmpty(homeHtml)) return packs;

        foreach (Match sm in ScriptRegex().Matches(homeHtml))
        {
            var content = sm.Groups["body"].Value;
            var idx = content.IndexOf("{\"packs\":[", StringComparison.Ordinal);
            if (idx < 0) continue;

            // Extract balanced JSON object starting at idx
            var json = ExtractBalancedJsonObject(content, idx);
            if (string.IsNullOrEmpty(json)) continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("packs", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var p in arr.EnumerateArray())
                {
                    var pack = new XteamAudioPack
                    {
                        Id = p.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                        Name = p.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? "" : "",
                        Category = p.TryGetProperty("category", out var catEl) ? catEl.GetString() ?? "" : ""
                    };
                    if (p.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tr in tracks.EnumerateArray())
                        {
                            var file = tr.TryGetProperty("file", out var fEl) ? fEl.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(file)) continue;
                            pack.Tracks.Add(new XteamAudioTrack
                            {
                                Id = tr.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "",
                                Title = tr.TryGetProperty("title", out var tt) ? tt.GetString() ?? file : file,
                                File = file,
                                Pack = tr.TryGetProperty("pack", out var pk) ? pk.GetString() ?? pack.Name : pack.Name,
                                PackCategory = pack.Category,
                                Url = $"{BaseUrl}/audio/{Uri.EscapeDataString(file)}"
                            });
                        }
                    }
                    if (pack.Tracks.Count > 0) packs.Add(pack);
                }
                if (packs.Count > 0) break;
            }
            catch { /* keep scanning if script fragment wasn't clean JSON */ }
        }
        return packs;
    }

    internal static List<XteamLinkItem> ParseLinks(string html)
    {
        var list = new List<XteamLinkItem>();
        if (string.IsNullOrEmpty(html)) return list;
        foreach (Match m in LinkCardRegex().Matches(html))
        {
            var block = m.Value;
            var href = ResolveUrl(m.Groups["href"].Value);
            var name = CleanText(MatchGroup(block, @"<span class=""link-name"">(.*?)</span>"));
            var desc = CleanText(MatchGroup(block, @"<p class=""link-desc"">(.*?)</p>"));
            var hint = CleanText(MatchGroup(block, @"<span class=""link-url-hint"">(.*?)</span>"));
            var uid = ExtractBiliUid(href);
            if (!string.IsNullOrEmpty(name))
                list.Add(new XteamLinkItem { Name = name, Description = desc, Url = href, Hint = hint, BiliUid = uid });
        }
        return list;
    }

    internal static List<XteamAboutSection> ParseAbout(string html)
    {
        var sections = new List<XteamAboutSection>();
        if (string.IsNullOrEmpty(html)) return sections;
        foreach (Match m in GlassPanelRegex().Matches(html))
        {
            var block = m.Value;
            var heading = CleanText(MatchGroup(block, @"<h3>(.*?)</h3>"));
            if (heading.Length == 0) continue;
            var sec = new XteamAboutSection { Heading = heading };
            foreach (Match pm in ParagraphRegex().Matches(block))
            {
                var text = CleanText(pm.Groups["text"].Value);
                if (text.Length > 0 && !text.Contains("__cf_email__", StringComparison.Ordinal))
                    sec.Paragraphs.Add(text);
            }
            foreach (Match lm in ListItemRegex().Matches(block))
            {
                var text = CleanText(lm.Groups["text"].Value);
                if (text.Length > 0) sec.Bullets.Add(text);
            }
            if (sec.Paragraphs.Count > 0 || sec.Bullets.Count > 0)
                sections.Add(sec);
        }
        return sections;
    }

    internal static XteamProjectDetail ParseDetail(string html, string url)
    {
        var cover = ResolveUrl(MatchGroup(html, @"<div class=""detail-cover-box""[^>]*>.*?<img[^>]*src=""([^""]+)"""));
        var title = CleanText(MatchGroup(html, @"<h1 class=""detail-title"">(.*?)</h1>"));
        var subtitle = CleanText(MatchGroup(html, @"<p class=""detail-subtitle"">(.*?)</p>"));
        var status = CleanText(MatchGroup(html, @"<span class=""status-pill[^>]*>.*?<span>([^<]+)</span>\s*</span>"));
        var date = CleanText(MatchGroup(html, @"<span class=""date-pill"">(.*?)</span>"));
        var pinned = html.Contains("xcard-pinned-pill", StringComparison.Ordinal);
        var breadcrumb = CleanText(MatchGroup(html, @"<nav class=""breadcrumb-bar""[^>]*>.*?<a class=""crumb-link"" href=""/(?:works|picks)/"">(.*?)</a>"));
        var authorUrl = ResolveUrl(MatchGroup(html, @"<div class=""detail-meta-tags"">.*?<a href=""([^""]+)"""));
        var authorName = CleanText(MatchGroup(html, @"<span class=""meta-value author-name-highlight"">.*?<a[^>]*>(.*?)</a>")).Replace("↗", "").Trim();
        var authorUid = ExtractBiliUid(authorUrl);

        var detail = new XteamProjectDetail
        {
            Url = url,
            BreadcrumbCategory = breadcrumb,
            StatusText = status,
            IsPinned = pinned,
            DateText = date,
            Title = title,
            Subtitle = subtitle,
            CoverUrl = cover,
            AuthorName = authorName,
            AuthorUrl = authorUrl,
            AuthorUid = authorUid
        };

        foreach (Match mm in MetaItemRegex().Matches(html))
        {
            var label = CleanText(mm.Groups["label"].Value).TrimEnd('：', ':');
            var val = CleanText(mm.Groups["val"].Value).Replace("↗", "").Trim();
            if (label.Length > 0 && val.Length > 0) detail.Meta.Add((label, val));
        }

        var actionsBlock = MatchGroup(html, @"<div class=""detail-actions-cluster"">(.*?)</div>");
        foreach (Match am in AnchorRegex().Matches(actionsBlock))
        {
            var href = am.Groups["href"].Value;
            if (href.StartsWith("#", StringComparison.Ordinal)) continue;
            var label = CleanText(am.Groups["text"].Value).Replace("→", "").Replace("↗", "").Trim();
            var cls = am.Groups["cls"].Value;
            if (label.Length > 0 && href.Length > 0)
                detail.Actions.Add(new XteamActionLink
                {
                    Label = label,
                    Url = ResolveUrl(href),
                    IsPrimary = cls.Contains("btn-primary", StringComparison.Ordinal)
                });
        }

        var descBlock = MatchGroup(html, @"<div class=""panel-body rich-desc"">(.*?)</div>\s*</section>");
        foreach (Match pm in ParagraphRegex().Matches(descBlock))
        {
            var p = CleanText(pm.Groups["text"].Value);
            if (p.Length > 0) detail.Paragraphs.Add(p);
        }

        foreach (Match gm in GalleryImgRegex().Matches(html))
        {
            var raw = gm.Groups["src"].Value;
            var abs = ResolveUrl(raw);
            if (abs.Length > 0 && !detail.Screenshots.Contains(abs)) detail.Screenshots.Add(abs);
        }

        return detail;
    }

    private static string ExtractBalancedJsonObject(string text, int start)
    {
        var depth = 0;
        var inStr = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inStr)
            {
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return "";
    }

    private static string ExtractText(string html, string pattern) => CleanText(MatchGroup(html, pattern));

    private static string MatchGroup(string input, string pattern)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var m = Regex.Match(input, pattern, RegexOptions.Singleline);
        return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : "";
    }

    private static string CleanText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var stripped = HtmlTagRegex().Replace(raw, " ");
        var decoded = WebUtility.HtmlDecode(stripped);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    [GeneratedRegex(@"<article class=""xcard[^""]*""[^>]*?(?:data-card-href=""(?<href>[^""]+)"")?[^>]*>.*?</article>", RegexOptions.Singleline)]
    private static partial Regex ArticleCardRegex();

    [GeneratedRegex(@"<a\s+(?:[^>]*?\s)?class=""(?<cls>[^""]*)""[^>]*?href=""(?<href>[^""]+)""[^>]*>(?<text>.*?)</a>|<a\s+(?:[^>]*?\s)?href=""(?<href>[^""]+)""[^>]*?class=""(?<cls>[^""]*)""[^>]*>(?<text>.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"data-lightbox-src=""(?<src>[^""]+)""", RegexOptions.Singleline)]
    private static partial Regex GalleryImgRegex();

    [GeneratedRegex(@"<div class=""thumb-item""[^>]*>.*?<img[^>]*(?:data-lightbox-src|src)=""(?<src>[^""]+)""", RegexOptions.Singleline)]
    private static partial Regex ThumbImgRegex();

    [GeneratedRegex(@"<span class=""card-tag"">(?<tag>.*?)</span>", RegexOptions.Singleline)]
    private static partial Regex CardTagRegex();

    [GeneratedRegex(@"<div class=""x-stat""[^>]*>.*?<strong>(?<val>.*?)</strong>\s*(?:<span class=""stat-unit"">(?<unit>.*?)</span>)?.*?<span class=""stat-label"">(?<label>.*?)</span>", RegexOptions.Singleline)]
    private static partial Regex StatRegex();

    [GeneratedRegex(@"<script[^>]*>(?<body>.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<a class=""link-card[^""]*""[^>]*href=""(?<href>[^""]+)""[^>]*>.*?</a>", RegexOptions.Singleline)]
    private static partial Regex LinkCardRegex();

    [GeneratedRegex(@"<section class=""glass-panel[^""]*"">.*?</section>", RegexOptions.Singleline)]
    private static partial Regex GlassPanelRegex();

    [GeneratedRegex(@"<div class=""meta-item"">\s*<span class=""meta-label"">(?<label>.*?)</span>\s*<span[^>]*>(?<val>.*?)</span>", RegexOptions.Singleline)]
    private static partial Regex MetaItemRegex();

    [GeneratedRegex(@"<p[^>]*>(?<text>.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex(@"<li[^>]*>(?<text>.*?)</li>", RegexOptions.Singleline)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
