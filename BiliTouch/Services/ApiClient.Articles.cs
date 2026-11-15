using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using BiliTouch.Models;

namespace BiliTouch.Services;

public partial class ApiClient
{
    /// <summary>
    /// 加载一篇专栏（cv 号）或 Opus 图文动态详情。
    /// 支持经典 HTML 专栏、新版 Opus 专栏（x/article/view 返回 opus.paragraphs 或空 content+dyn_id_str）、以及纯 Opus 链接。
    /// </summary>
    public async Task<(bool ok, string? err, ArticleDetail? detail)> GetArticleDetailAsync(
        string? cvId, string? opusId = null)
    {
        var cleanCv = StripCvPrefix(cvId);
        var cleanOpus = string.IsNullOrWhiteSpace(opusId) ? "" : opusId.Trim();

        if (cleanCv.Length == 0 && cleanOpus.Length == 0)
            return (false, "缺少专栏或文章编号", null);

        await EnsureBuvidAsync();

        ArticleDetail? detail = null;
        string? lastErr = null;

        if (cleanCv.Length > 0 && long.TryParse(cleanCv, out var cvNum) && cvNum > 0)
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/article/view",
                new() { ["id"] = cleanCv },
                referer: $"https://www.bilibili.com/read/cv{cleanCv}");

            if (code == 0 && data != null)
            {
                detail = ArticleParser.ParseArticleViewData(data.Value, cleanCv);
                // 补齐浏览/点赞状态等元信息（部分字段 viewinfo 给得更全）
                await EnrichArticleViewInfoAsync(cleanCv, detail);

                // 若 x/article/view 正文为空但带了 dyn_id_str 或 opus_id，继续拉 Opus 详情补齐正文块
                if (detail != null && detail.Blocks.Count == 0)
                {
                    var fallbackOpusId = detail.OpusId.Length > 0 ? detail.OpusId : detail.DynamicId;
                    if (fallbackOpusId.Length > 0)
                    {
                        var opusRes = await GetOpusDetailInternalAsync(fallbackOpusId, cleanCv);
                        if (opusRes.ok && opusRes.detail != null)
                        {
                            if (opusRes.detail.Blocks.Count > 0)
                            {
                                detail.Blocks = opusRes.detail.Blocks;
                                detail.AllImages = opusRes.detail.AllImages;
                                detail.IsOpusBacked = true;
                            }
                            if (detail.Title.Length == 0) detail.Title = opusRes.detail.Title;
                            if (detail.AuthorName.Length == 0)
                            {
                                detail.AuthorMid = opusRes.detail.AuthorMid;
                                detail.AuthorName = opusRes.detail.AuthorName;
                                detail.AuthorFace = opusRes.detail.AuthorFace;
                            }
                            if (detail.WordCount <= 0) detail.WordCount = opusRes.detail.WordCount;
                        }
                    }
                }

                if (detail != null && (detail.Blocks.Count > 0 || detail.Title.Length > 0))
                    return (true, null, detail);
            }
            else
            {
                lastErr = msg;
            }
        }

        // 若有 opusId（或动态 ID），直接走 Opus 详情端点
        if (cleanOpus.Length > 0 && long.TryParse(cleanOpus, out var opusNum) && opusNum > 0)
        {
            var opusRes = await GetOpusDetailInternalAsync(cleanOpus, cleanCv);
            if (opusRes.ok && opusRes.detail != null)
            {
                // 如果 Opus 反推出了 cv 号且还没拉过专栏正文，再试拉一次 classic cv 补统计数据/正文
                if (cleanCv.Length == 0 && opusRes.detail.ArticleId.Length > 0 &&
                    opusRes.detail.Blocks.Count == 0)
                {
                    var cvRes = await GetArticleDetailAsync(opusRes.detail.ArticleId, null);
                    if (cvRes.ok && cvRes.detail != null)
                        return cvRes;
                }
                return opusRes;
            }
            if (!string.IsNullOrEmpty(opusRes.err)) lastErr = opusRes.err;
        }

        return (false, lastErr ?? "专栏内容加载失败，请重试", detail);
    }

    private async Task<(bool ok, string? err, ArticleDetail? detail)> GetOpusDetailInternalAsync(
        string opusId, string? cvIdHint = null)
    {
        var (code, msg, data) = await GetJsonAsync(
            "https://api.bilibili.com/x/polymer/web-dynamic/v1/opus/detail",
            new() { ["id"] = opusId, ["features"] = "itemOpusStyle" },
            referer: $"https://www.bilibili.com/opus/{opusId}");

        if (code == 0 && data != null && data.Value.TryGetProperty("item", out var opusItem))
        {
            var parsed = ArticleParser.ParseOpusItem(opusItem, opusId, cvIdHint);
            if (parsed != null) return (true, null, parsed);
        }

        // 兜底：部分旧格式动态或未迁移条目只能走 /web-dynamic/v1/detail
        var (c2, m2, d2) = await GetJsonAsync(
            "https://api.bilibili.com/x/polymer/web-dynamic/v1/detail",
            new() { ["id"] = opusId, ["features"] = "itemOpusStyle" },
            referer: $"https://t.bilibili.com/{opusId}");

        if (c2 == 0 && d2 != null && d2.Value.TryGetProperty("item", out var dynItem))
        {
            var parsed = ArticleParser.ParseOpusItem(dynItem, opusId, cvIdHint);
            if (parsed != null) return (true, null, parsed);
        }

        return (false, m2 ?? msg ?? "未找到对应文章或动态内容", null);
    }

    private async Task EnrichArticleViewInfoAsync(string cvId, ArticleDetail? detail)
    {
        if (detail == null) return;
        try
        {
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/article/viewinfo",
                new() { ["id"] = cvId },
                referer: $"https://www.bilibili.com/read/cv{cvId}");
            if (code != 0 || data == null || data.Value.ValueKind != JsonValueKind.Object) return;

            var d = data.Value;
            detail.IsLiked = GetLong(d, "like") == 1;
            detail.IsFavorited = d.TryGetProperty("favorite", out var fav) &&
                                 (fav.ValueKind == JsonValueKind.True || GetLong(d, "favorite") == 1);
            if (detail.DynamicId.Length == 0) detail.DynamicId = GetStr(d, "dyn_id_str");
            if (detail.AuthorMid <= 0) detail.AuthorMid = GetLong(d, "mid");
            if (detail.AuthorName.Length == 0) detail.AuthorName = GetStr(d, "author_name");
            if (detail.Title.Length == 0) detail.Title = WebUtility.HtmlDecode(GetStr(d, "title"));
            if (detail.BannerUrl.Length == 0) detail.BannerUrl = UrlUtil.Normalize(GetStr(d, "banner_url"));

            if (d.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                if (detail.ViewCount <= 0) detail.ViewCount = GetLong(st, "view");
                if (detail.LikeCount <= 0) detail.LikeCount = GetLong(st, "like");
                if (detail.FavoriteCount <= 0) detail.FavoriteCount = GetLong(st, "favorite");
                if (detail.CoinCount <= 0) detail.CoinCount = GetLong(st, "coin");
                if (detail.ShareCount <= 0) detail.ShareCount = GetLong(st, "share");
                if (detail.ReplyCount <= 0) detail.ReplyCount = GetLong(st, "reply");
            }
        }
        catch { /* 辅助统计接口失败静默处理，不影响正文阅读 */ }
    }

    /// <summary>点赞 / 取消点赞专栏（cv 号）</summary>
    public async Task<(bool ok, string? err)> LikeArticleAsync(string cvId, bool like)
    {
        var clean = StripCvPrefix(cvId);
        if (!long.TryParse(clean, out var n) || n <= 0) return (false, "专栏编号无效");
        return await PostFormAsync(
            "https://api.bilibili.com/x/article/like",
            new() { ["id"] = clean, ["type"] = like ? "1" : "2" },
            referer: $"https://www.bilibili.com/read/cv{clean}");
    }

    /// <summary>收藏 / 取消收藏专栏（cv 号）</summary>
    public async Task<(bool ok, string? err)> FavoriteArticleAsync(string cvId, bool favorite)
    {
        var clean = StripCvPrefix(cvId);
        if (!long.TryParse(clean, out var n) || n <= 0) return (false, "专栏编号无效");
        var url = favorite
            ? "https://api.bilibili.com/x/article/favorites/add"
            : "https://api.bilibili.com/x/article/favorites/del";
        return await PostFormAsync(
            url,
            new() { ["id"] = clean },
            referer: $"https://www.bilibili.com/read/cv{clean}");
    }

    /// <summary>获取 UP 主个人空间专栏列表</summary>
    public async Task<(bool ok, string? err, List<ArticleSummary> items, bool hasMore, int total)>
        GetSpaceArticlesAsync(long mid, int page = 1, int pageSize = 20, string sort = "publish_time")
    {
        var items = new List<ArticleSummary>();
        if (mid <= 0) return (false, "缺少 UP 主 mid", items, false, 0);

        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>
            {
                ["mid"] = mid.ToString(),
                ["pn"] = page.ToString(),
                ["ps"] = Math.Clamp(pageSize, 1, 30).ToString(),
                ["sort"] = sort,
            };

            var referer = $"https://space.bilibili.com/{mid}/article";
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/space/wbi/article", ps, sign: true, referer: referer);
            if (code != 0 || data == null)
            {
                var psBare = new Dictionary<string, string>(ps);
                psBare.Remove("wts");
                psBare.Remove("w_rid");
                (code, msg, data) = await GetJsonAsync(
                    "https://api.bilibili.com/x/space/article", psBare, sign: false, referer: referer);
            }

            if (code != 0 || data == null)
                return (false, msg ?? "专栏列表加载失败", items, false, 0);

            var d = data.Value;
            var count = (int)GetLong(d, "count");
            if (d.TryGetProperty("articles", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var art = ParseSpaceArticleItem(e);
                    if (art != null) items.Add(art);
                }
            }
            var hasMore = items.Count > 0 && (count > 0 ? page * pageSize < count : items.Count >= pageSize);
            return (true, null, items, hasMore, count);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, items, false, 0);
        }
    }

    internal static ArticleSummary? ParseSpaceArticleItem(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = GetLong(e, "id");
        if (id <= 0) return null;

        var covers = new List<string>();
        if (e.TryGetProperty("image_urls", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in imgs.EnumerateArray())
            {
                var url = UrlUtil.Normalize(u.GetString() ?? "");
                if (url.Length > 0) covers.Add(url);
            }
        }
        var banner = UrlUtil.Normalize(GetStr(e, "banner_url"));
        var cover = covers.Count > 0 ? covers[0] : banner;

        long authorMid = 0;
        string authorName = "", authorFace = "";
        if (e.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object)
        {
            authorMid = GetLong(au, "mid");
            authorName = GetStr(au, "name");
            authorFace = UrlUtil.Normalize(GetStr(au, "face"));
        }

        var cat = "";
        if (e.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.Object)
            cat = GetStr(c, "name");
        else if (e.TryGetProperty("categories", out var cs) && cs.ValueKind == JsonValueKind.Array && cs.GetArrayLength() > 0)
            cat = GetStr(cs[0], "name");

        long views = 0, likes = 0, replies = 0, favs = 0;
        if (e.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            views = GetLong(st, "view");
            likes = GetLong(st, "like");
            replies = GetLong(st, "reply");
            favs = GetLong(st, "favorite");
        }

        var pubTs = GetLong(e, "publish_time");
        return new ArticleSummary
        {
            ArticleId = id.ToString(),
            DynamicId = GetStr(e, "dyn_id_str"),
            Title = WebUtility.HtmlDecode(GetStr(e, "title")),
            Summary = WebUtility.HtmlDecode(GetStr(e, "summary")),
            Cover = cover,
            Covers = covers,
            AuthorMid = authorMid,
            AuthorName = authorName,
            AuthorFace = authorFace,
            CategoryName = cat,
            PubTs = pubTs,
            PubTimeText = ArticleParser.FormatPubTime(pubTs),
            ViewCount = views,
            LikeCount = likes,
            ReplyCount = replies,
            FavoriteCount = favs,
            WordCount = GetLong(e, "words"),
        };
    }

    internal static ArticleSummary? ParseArticleSearchItem(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = GetLong(e, "id");
        if (id <= 0) return null;

        var covers = new List<string>();
        if (e.TryGetProperty("image_urls", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in imgs.EnumerateArray())
            {
                var url = UrlUtil.Normalize(u.GetString() ?? "");
                if (url.Length > 0) covers.Add(url);
            }
        }

        var pubTs = GetLong(e, "pub_time");
        var pubText = pubTs > 0 ? ArticleParser.FormatPubTime(pubTs) : GetStr(e, "pub_time");

        return new ArticleSummary
        {
            ArticleId = id.ToString(),
            Title = WebUtility.HtmlDecode(GetStr(e, "title").StripHtml()),
            Summary = WebUtility.HtmlDecode(GetStr(e, "desc").StripHtml()),
            Cover = covers.Count > 0 ? covers[0] : "",
            Covers = covers,
            AuthorMid = GetLong(e, "mid"),
            AuthorName = GetStr(e, "author").Length > 0 ? GetStr(e, "author") : GetStr(e, "uname"),
            CategoryName = GetStr(e, "category_name"),
            PubTs = pubTs,
            PubTimeText = pubText,
            ViewCount = GetLong(e, "view"),
            LikeCount = GetLong(e, "like"),
            ReplyCount = GetLong(e, "reply"),
        };
    }

    private static string StripCvPrefix(string? cvId)
    {
        if (string.IsNullOrWhiteSpace(cvId)) return "";
        var s = cvId.Trim();
        if (s.StartsWith("cv", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s;
    }
}
