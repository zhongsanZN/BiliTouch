using System;
using System.Collections.Generic;
using System.Text.Json;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 动态混合流解析(2026-11-04)。
///
/// 背景: 原来的 <c>ParseDynamicVideo</c> 只认"能播的视频动态", 图文/纯文字/专栏/转发
/// 全部返回 null 被丢掉 —— 这就是"动态页只有视频、内容不全"的根因。
///
/// 现在按官方手机端的做法解析**全部类型**, 认不出来的也保留(有正文显示正文),
/// 宁可信息不完整也不要整条消失。
///
/// 接口形态两套(实测都见过, 都要兼容):
///   · 扁平:  { id_str, type, rid, title, cover, jump_url, pub_time, author{...} }
///   · modules 嵌套: { id_str, type, modules: { module_author, module_dynamic{ desc, major } } }
/// 优先 modules(信息最全), 取不到再退回扁平。
/// </summary>
public static class DynamicParser
{
    /// <summary>把一条动态 JSON 解析成 <see cref="DynamicItem"/>。结构异常返回 null(只丢这一条)。</summary>
    public static DynamicItem? Parse(JsonElement e)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Object) return null;

            var item = new DynamicItem
            {
                Id = Str(e, "id_str"),
                Kind = KindOf(e),
            };
            if (item.Id.Length == 0) item.Id = Str(e, "id");

            // ---------- 作者 / 时间 ----------
            if (e.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Object &&
                mods.TryGetProperty("module_author", out var au) && au.ValueKind == JsonValueKind.Object)
            {
                item.AuthorMid = LongLoose(au, "mid");
                item.AuthorName = Str(au, "name");
                item.AuthorFace = UrlUtil.Normalize(Str(au, "face"));
                item.PubTs = Long(au, "pub_ts");
                item.PubTimeText = Str(au, "pub_time");
            }
            if (item.AuthorName.Length == 0 && e.TryGetProperty("author", out var au2) &&
                au2.ValueKind == JsonValueKind.Object)
            {
                item.AuthorMid = LongLoose(au2, "mid");
                item.AuthorName = Str(au2, "name");
                item.AuthorFace = UrlUtil.Normalize(Str(au2, "face"));
            }
            if (item.PubTimeText.Length == 0) item.PubTimeText = Str(e, "pub_time");
            if (item.PubTs <= 0) item.PubTs = Long(e, "pub_ts");
            if (item.AuthorName.Length == 0) item.AuthorName = Str(e, "uname");

            // ---------- 正文 + 图片 ----------
            ReadTextAndPictures(e, item);

            // ---------- 按类型补内容 ----------
            switch (item.Kind)
            {
                case DynamicKind.Video:
                    item.Video = ReadVideo(e);
                    // 视频动态的正文通常是空的, 标题由卡片自己显示
                    break;

                case DynamicKind.Article:
                    ReadArticle(e, item);
                    break;

                case DynamicKind.Live:
                    ReadLive(e, item);
                    break;

                case DynamicKind.Forward:
                    ReadForward(e, item);
                    break;
            }

            // ---------- 互动数 ----------
            if (e.TryGetProperty("modules", out var m2) && m2.ValueKind == JsonValueKind.Object &&
                m2.TryGetProperty("module_stat", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                if (st.TryGetProperty("like", out var lk)) item.LikeCount = Long(lk, "count");
                if (st.TryGetProperty("comment", out var cm)) item.CommentCount = Long(cm, "count");
                if (st.TryGetProperty("forward", out var fw)) item.ForwardCount = Long(fw, "count");
            }

            // 一条动态至少要有"某种内容"才有意义: 有正文/图/视频/专栏/直播/转发之一
            var hasSomething = item.HasText || item.HasPictures || item.Video != null
                || item.ArticleTitle.Length > 0 || item.LiveRoomId > 0 || item.IsForward;
            return hasSomething ? item : null;
        }
        catch
        {
            return null;   // 单条异常只丢这一条, 不影响整页
        }
    }

    // ------------------------------------------------------------ 类型

    private static DynamicKind KindOf(JsonElement e)
    {
        var t = Str(e, "type");
        var n = 0;
        if (int.TryParse(t, out var parsed)) n = parsed;
        else if (e.TryGetProperty("type", out var tv) && tv.ValueKind == JsonValueKind.Number)
            n = tv.GetInt32();

        return n switch
        {
            1 => DynamicKind.Forward,
            2 => DynamicKind.ImageText,
            4 => DynamicKind.Text,
            8 => DynamicKind.Video,
            64 => DynamicKind.Article,
            4308 => DynamicKind.Live,
            _ => DynamicKind.Other,
        };
    }

    // ------------------------------------------------------------ 正文与图片

    /// <summary>
    /// 正文和图片。modules 路径在 module_dynamic.desc.text + major.draw.items;
    /// 扁平路径在 text / description / pictures。
    /// </summary>
    private static void ReadTextAndPictures(JsonElement e, DynamicItem item)
    {
        if (e.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Object &&
            mods.TryGetProperty("module_dynamic", out var md) && md.ValueKind == JsonValueKind.Object)
        {
            if (md.TryGetProperty("desc", out var desc) && desc.ValueKind == JsonValueKind.Object)
            {
                item.Text = Str(desc, "text");
                if (item.Text.Length == 0) item.Text = Str(desc, "rich_text");
            }

            // 图片: major.draw.items[] { src, width, height }
            if (md.TryGetProperty("major", out var major) && major.ValueKind == JsonValueKind.Object &&
                major.TryGetProperty("draw", out var draw) && draw.ValueKind == JsonValueKind.Object &&
                draw.TryGetProperty("items", out var pics) && pics.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pics.EnumerateArray())
                {
                    var url = Str(p, "src");
                    if (url.Length == 0) continue;
                    item.Pictures.Add(new DynamicPicture
                    {
                        Url = UrlUtil.Normalize(url),
                        Width = (int)Long(p, "width"),
                        Height = (int)Long(p, "height"),
                    });
                }
            }
        }

        // 扁平兜底
        if (item.Text.Length == 0)
        {
            item.Text = Str(e, "text");
            if (item.Text.Length == 0) item.Text = Str(e, "description");
            if (item.Text.Length == 0) item.Text = Str(e, "content");
        }
        if (item.Pictures.Count == 0 && e.TryGetProperty("pictures", out var fp) &&
            fp.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in fp.EnumerateArray())
            {
                var url = Str(p, "img_src");
                if (url.Length == 0) url = Str(p, "src");
                if (url.Length == 0) continue;
                item.Pictures.Add(new DynamicPicture
                {
                    Url = UrlUtil.Normalize(url),
                    Width = (int)Long(p, "img_width"),
                    Height = (int)Long(p, "img_height"),
                });
            }
        }
    }

    // ------------------------------------------------------------ 视频

    /// <summary>视频动态: modules.module_dynamic.major.archive 或扁平 jump_url/title/cover。</summary>
    private static VideoItem? ReadVideo(JsonElement e)
    {
        string? bvid = null, title = null, cover = null, duration = null;
        long aid = 0, view = 0, danmaku = 0;

        if (e.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Object &&
            mods.TryGetProperty("module_dynamic", out var md) && md.ValueKind == JsonValueKind.Object &&
            md.TryGetProperty("major", out var major) && major.ValueKind == JsonValueKind.Object &&
            major.TryGetProperty("archive", out var arc) && arc.ValueKind == JsonValueKind.Object)
        {
            bvid = Str(arc, "bvid");
            title = Str(arc, "title");
            cover = Str(arc, "cover");
            duration = Str(arc, "duration_text");
            aid = Long(arc, "aid");
            if (arc.TryGetProperty("stat", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                view = Long(st, "play");
                if (view <= 0) view = ParseCount(Str(st, "play"));
                danmaku = Long(st, "danmaku");
            }
        }

        // 扁平兜底
        if (string.IsNullOrEmpty(bvid)) bvid = ExtractBvid(Str(e, "jump_url"));
        if (string.IsNullOrEmpty(bvid)) bvid = Str(e, "bvid");
        if (string.IsNullOrEmpty(title)) title = Str(e, "title");
        if (string.IsNullOrEmpty(cover)) cover = Str(e, "cover");
        if (aid <= 0 && long.TryParse(Str(e, "rid"), out var rid)) aid = rid;

        if (string.IsNullOrEmpty(bvid) && aid <= 0) return null;

        return new VideoItem
        {
            Bvid = bvid ?? "",
            Aid = aid,
            Title = title ?? "",
            Cover = UrlUtil.Normalize(cover ?? ""),
            Duration = duration ?? "",
            ViewCount = view,
            DanmakuCount = danmaku,
            // 作者信息由外层 DynamicItem 持有, 卡片上不再重复显示(视频卡只显示封面+标题)
        };
    }

    private static string ExtractBvid(string jumpUrl)
    {
        if (string.IsNullOrEmpty(jumpUrl)) return "";
        var i = jumpUrl.IndexOf("BV", StringComparison.Ordinal);
        return i < 0 ? "" : jumpUrl.Substring(i, Math.Min(12, jumpUrl.Length - i));
    }

    // ------------------------------------------------------------ 专栏

    private static void ReadArticle(JsonElement e, DynamicItem item)
    {
        if (e.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Object &&
            mods.TryGetProperty("module_dynamic", out var md) && md.ValueKind == JsonValueKind.Object &&
            md.TryGetProperty("major", out var major) && major.ValueKind == JsonValueKind.Object &&
            major.TryGetProperty("article", out var art) && art.ValueKind == JsonValueKind.Object)
        {
            item.ArticleTitle = Str(art, "title");
            item.ArticleCover = UrlUtil.Normalize(
                art.TryGetProperty("covers", out var cvs) && cvs.ValueKind == JsonValueKind.Array &&
                cvs.GetArrayLength() > 0 ? cvs[0].GetString() ?? "" : Str(art, "cover"));
            item.ArticleId = Str(art, "id");
        }
        if (item.ArticleTitle.Length == 0) item.ArticleTitle = Str(e, "title");
        if (item.ArticleId.Length == 0) item.ArticleId = Str(e, "rid");
    }

    // ------------------------------------------------------------ 直播

    private static void ReadLive(JsonElement e, DynamicItem item)
    {
        if (e.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Object &&
            mods.TryGetProperty("module_dynamic", out var md) && md.ValueKind == JsonValueKind.Object &&
            md.TryGetProperty("major", out var major) && major.ValueKind == JsonValueKind.Object &&
            major.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.Object)
        {
            item.LiveRoomId = Long(live, "id");
            if (item.LiveRoomId <= 0) item.LiveRoomId = Long(live, "roomid");
            if (item.Text.Length == 0) item.Text = Str(live, "title");
        }
        if (item.LiveRoomId <= 0) item.LiveRoomId = Long(e, "rid");
    }

    // ------------------------------------------------------------ 转发

    /// <summary>
    /// 转发: 原动态在 orig 里。★ 递归解析 —— 原动态自己也可能是转发(套娃),
    /// 官方最多显示两层, 这里也限制两层, 免得一条链无限展开。
    /// </summary>
    private static void ReadForward(JsonElement e, DynamicItem item)
    {
        if (e.TryGetProperty("orig", out var orig) && orig.ValueKind == JsonValueKind.Object)
        {
            var inner = Parse(orig);
            if (inner != null)
            {
                // 只保留两层: 原动态若还是转发, 丢掉它的 orig 引用
                if (inner.IsForward) inner.Original = null;
                item.Original = inner;
                return;
            }
        }
        item.OriginalDeleted = true;
    }

    // ------------------------------------------------------------ 小工具

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }

    /// <summary>mid 在动态接口里是**字符串**, 普通 Long 取不到, 所以单独走这个。</summary>
    private static long LongLoose(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }

    /// <summary>"1.4万" 这种展示串 → 数字(统计字段偶尔给文本)</summary>
    private static long ParseCount(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        text = text.Trim();
        var mult = 1.0;
        if (text.EndsWith("亿")) { mult = 100_000_000; text = text[..^1]; }
        else if (text.EndsWith("万")) { mult = 10_000; text = text[..^1]; }
        return double.TryParse(text, out var d) ? (long)(d * mult) : 0;
    }
}
