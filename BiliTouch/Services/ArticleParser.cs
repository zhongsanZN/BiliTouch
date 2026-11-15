using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiliTouch.Models;

namespace BiliTouch.Services;

/// <summary>
/// 专栏（classic HTML / x/article/view）与现代图文/专栏（Opus paragraphs[]）统一解析器。
/// 把两种形态都映射成有序的 <see cref="ArticleBlock"/> 列表与图片清单 <see cref="ArticleDetail.AllImages"/>，
/// 让阅读页与动态详情共用一套块渲染模型。
/// </summary>
public static class ArticleParser
{
    private static readonly Regex BvidRegex = new(@"BV[0-9A-Za-z]{10}", RegexOptions.Compiled);
    private static readonly Regex CvRegex = new(@"/read/cv(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OpusRegex = new(@"/opus/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>解析 /x/polymer/web-dynamic/v1/opus/detail 或 detail 响应里的 item 对象</summary>
    public static ArticleDetail? ParseOpusItem(JsonElement item, string? opusIdHint = null, string? cvIdHint = null)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var detail = new ArticleDetail
        {
            IsOpusBacked = true,
            OpusId = opusIdHint ?? "",
            ArticleId = cvIdHint ?? "",
        };

        var idStr = Str(item, "id_str");
        if (detail.OpusId.Length == 0 && idStr.Length > 0) detail.OpusId = idStr;
        detail.DynamicId = idStr;

        if (item.TryGetProperty("basic", out var basic) && basic.ValueKind == JsonValueKind.Object)
        {
            detail.CommentOid = Scalar(basic, "comment_id_str");
            detail.CommentType = (int)Long(basic, "comment_type");
            var ridStr = Scalar(basic, "rid_str");
            if (detail.ArticleId.Length == 0 && detail.CommentType == 12 && ridStr.Length > 0)
                detail.ArticleId = ridStr;
            var jumpUrl = Str(basic, "jump_url");
            TryFillIdsFromUrl(jumpUrl, detail);
        }

        if (item.TryGetProperty("modules", out var mods))
        {
            if (mods.ValueKind == JsonValueKind.Array)
            {
                foreach (var mod in mods.EnumerateArray())
                    ReadOpusModule(mod, detail);
            }
            else if (mods.ValueKind == JsonValueKind.Object)
            {
                ReadDynamicModulesObject(mods, detail);
            }
        }

        if (detail.BannerUrl.Length == 0 && detail.AllImages.Count > 0 && detail.ArticleId.Length > 0)
            detail.BannerUrl = detail.AllImages[0].Url;

        if (detail.WordCount <= 0)
            detail.WordCount = EstimateWordCount(detail.Blocks);

        if (detail.CommentOid.Length == 0)
        {
            if (detail.ArticleId.Length > 0)
            {
                detail.CommentOid = detail.ArticleId;
                if (detail.CommentType <= 0) detail.CommentType = 12;
            }
            else if (detail.DynamicId.Length > 0)
            {
                detail.CommentOid = detail.DynamicId;
                if (detail.CommentType <= 0) detail.CommentType = 17;
            }
        }
        if (detail.CommentType <= 0) detail.CommentType = detail.ArticleId.Length > 0 ? 12 : 11;

        return detail;
    }

    /// <summary>解析 /x/article/view 的 data 对象（兼顾老 HTML 专栏与新 Opus 底层专栏）</summary>
    public static ArticleDetail? ParseArticleViewData(JsonElement data, string cvId)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;

        var detail = new ArticleDetail
        {
            ArticleId = cvId,
            CommentOid = cvId,
            CommentType = 12,
            Title = WebUtility.HtmlDecode(Str(data, "title")),
            BannerUrl = UrlUtil.Normalize(Str(data, "banner_url")),
            DynamicId = Scalar(data, "dyn_id_str"),
            PubTs = Long(data, "publish_time"),
        };
        if (detail.BannerUrl.Length == 0 &&
            data.TryGetProperty("image_urls", out var imgs) && imgs.ValueKind == JsonValueKind.Array &&
            imgs.GetArrayLength() > 0)
            detail.BannerUrl = UrlUtil.Normalize(imgs[0].GetString() ?? "");

        if (detail.PubTs > 0) detail.PubTimeText = FormatPubTime(detail.PubTs);

        if (data.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array && cats.GetArrayLength() > 0)
            detail.CategoryName = Str(cats[0], "name");
        else if (data.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Object)
            detail.CategoryName = Str(cat, "name");

        if (data.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object)
        {
            detail.AuthorMid = Long(au, "mid");
            detail.AuthorName = Str(au, "name");
            detail.AuthorFace = UrlUtil.Normalize(Str(au, "face"));
        }
        else
        {
            detail.AuthorMid = Long(data, "mid");
            detail.AuthorName = Str(data, "author_name");
        }

        if (data.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            detail.ViewCount = Long(st, "view");
            detail.LikeCount = Long(st, "like");
            detail.FavoriteCount = Long(st, "favorite");
            detail.CoinCount = Long(st, "coin");
            detail.ShareCount = Long(st, "share");
            detail.ReplyCount = Long(st, "reply");
        }
        detail.IsLiked = Long(data, "like") == 1;
        detail.IsFavorited = Bool(data, "favorite");

        // 新版专栏编辑器的正文在 data.opus.content.paragraphs[] 或 data.opus.paragraphs[]
        if (data.TryGetProperty("opus", out var opus) && opus.ValueKind == JsonValueKind.Object)
        {
            var opusId = Scalar(opus, "opus_id");
            if (opusId.Length == 0) opusId = Scalar(opus, "id_str");
            if (opusId.Length > 0) detail.OpusId = opusId;

            if (detail.Title.Length == 0) detail.Title = Str(opus, "title");
            if (opus.TryGetProperty("content", out var contentObj) && contentObj.ValueKind == JsonValueKind.Object &&
                contentObj.TryGetProperty("paragraphs", out var p1) && p1.ValueKind == JsonValueKind.Array)
            {
                detail.IsOpusBacked = true;
                AppendOpusParagraphs(p1, detail);
            }
            else if (opus.TryGetProperty("paragraphs", out var p2) && p2.ValueKind == JsonValueKind.Array)
            {
                detail.IsOpusBacked = true;
                AppendOpusParagraphs(p2, detail);
            }
        }

        // 老版 HTML 专栏正文在 data.content
        if (detail.Blocks.Count == 0)
        {
            var html = Str(data, "content");
            if (html.Length > 0)
                ParseHtmlIntoBlocks(html, detail);
        }

        detail.WordCount = Long(data, "words");
        if (detail.WordCount <= 0)
            detail.WordCount = EstimateWordCount(detail.Blocks);

        return detail;
    }

    /// <summary>只从经典 HTML 提取段落列表（也供单元测试调用）</summary>
    public static void ParseHtmlIntoBlocks(string html, ArticleDetail detail)
    {
        if (string.IsNullOrWhiteSpace(html)) return;

        // 清理脚本、样式与注释
        var clean = Regex.Replace(html, @"<(script|style)\b[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"<!--[\s\S]*?-->", "");

        // 用占位换行分隔顶级常见块级标签，按出现顺序逐个扫描
        // 先处理代码块 <pre>...</pre> 与 <hr> 与 <figure>...</figure> 与 <h1>..<h6> 与 <blockquote> 与 <li> 与 <p>
        var blockPattern = new Regex(
            @"<(?<tag>h[1-6]|p|blockquote|pre|figure|li)\b[^>]*>(?<inner>[\s\S]*?)</\k<tag>>|<(?<self>hr)\b[^>]*?/?>|<(?<imgOnly>img)\b(?<attrs>[^>]*)/?>",
            RegexOptions.IgnoreCase);

        int lastEnd = 0;
        foreach (Match m in blockPattern.Matches(clean))
        {
            if (m.Index > lastEnd)
            {
                var gap = clean.Substring(lastEnd, m.Index - lastEnd);
                AddPlainTextFragment(gap, detail);
            }
            lastEnd = m.Index + m.Length;

            if (m.Groups["self"].Success)
            {
                detail.Blocks.Add(new ArticleBlock { Kind = ArticleBlockKind.Divider });
                continue;
            }
            if (m.Groups["imgOnly"].Success)
            {
                TryAddImageTag("<img" + m.Groups["attrs"].Value + ">", "", detail);
                continue;
            }

            var tag = m.Groups["tag"].Value.ToLowerInvariant();
            var inner = m.Groups["inner"].Value;

            if (tag == "pre")
            {
                var code = WebUtility.HtmlDecode(StripTagsPreserveBreaks(inner)).Trim('\r', '\n');
                if (code.Length > 0)
                    detail.Blocks.Add(new ArticleBlock { Kind = ArticleBlockKind.Code, Text = code });
                continue;
            }

            if (tag == "figure")
            {
                var capMatch = Regex.Match(inner, @"<figcaption\b[^>]*>([\s\S]*?)</figcaption>", RegexOptions.IgnoreCase);
                var caption = capMatch.Success ? WebUtility.HtmlDecode(StripTagsPreserveBreaks(capMatch.Groups[1].Value)).Trim() : "";
                var addedImg = false;
                foreach (Match im in Regex.Matches(inner, @"<img\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    if (TryAddImageTag(im.Value, caption, detail))
                    {
                        addedImg = true;
                        caption = "";
                    }
                }
                if (!addedImg && caption.Length > 0)
                    detail.Blocks.Add(new ArticleBlock { Kind = ArticleBlockKind.Quote, Text = caption });
                continue;
            }

            if (tag.StartsWith('h') && tag.Length == 2 && char.IsDigit(tag[1]))
            {
                var level = Math.Clamp(tag[1] - '0', 1, 3);
                var text = WebUtility.HtmlDecode(StripTagsPreserveBreaks(inner)).Trim();
                if (text.Length > 0)
                    detail.Blocks.Add(new ArticleBlock { Kind = ArticleBlockKind.Heading, HeadingLevel = level, Text = text });
                continue;
            }

            if (tag == "blockquote")
            {
                EmitRichParagraphOrImages(inner, ArticleBlockKind.Quote, detail);
                continue;
            }

            if (tag == "li")
            {
                EmitRichParagraphOrImages(inner, ArticleBlockKind.ListItem, detail);
                continue;
            }

            // <p>
            EmitRichParagraphOrImages(inner, ArticleBlockKind.Paragraph, detail);
        }

        if (lastEnd < clean.Length)
            AddPlainTextFragment(clean.Substring(lastEnd), detail);
    }

    // ------------------------------------------------------------ Opus 内部解析

    private static void ReadOpusModule(JsonElement mod, ArticleDetail detail)
    {
        if (mod.ValueKind != JsonValueKind.Object) return;
        var mtype = Str(mod, "module_type");

        switch (mtype)
        {
            case "MODULE_TYPE_TITLE":
                if (mod.TryGetProperty("module_title", out var mt) && mt.ValueKind == JsonValueKind.Object)
                {
                    var title = Str(mt, "text");
                    if (title.Length > 0) detail.Title = title;
                }
                break;

            case "MODULE_TYPE_TOP":
                if (mod.TryGetProperty("module_top", out var top) && top.ValueKind == JsonValueKind.Object &&
                    top.TryGetProperty("display", out var disp) && disp.ValueKind == JsonValueKind.Object &&
                    disp.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object &&
                    album.TryGetProperty("pics", out var topPics) && topPics.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in topPics.EnumerateArray())
                    {
                        var url = UrlUtil.Normalize(Str(p, "url"));
                        if (url.Length == 0) continue;
                        if (detail.BannerUrl.Length == 0) detail.BannerUrl = url;
                        var idx = RegisterImage(detail, url, (int)Long(p, "width"), (int)Long(p, "height"));
                        detail.Blocks.Add(new ArticleBlock
                        {
                            Kind = ArticleBlockKind.Image,
                            ImageUrl = url,
                            ImageWidth = (int)Long(p, "width"),
                            ImageHeight = (int)Long(p, "height"),
                            ImageIndex = idx,
                        });
                    }
                }
                break;

            case "MODULE_TYPE_AUTHOR":
                if (mod.TryGetProperty("module_author", out var au) && au.ValueKind == JsonValueKind.Object)
                {
                    detail.AuthorMid = Long(au, "mid");
                    detail.AuthorName = Str(au, "name");
                    detail.AuthorFace = UrlUtil.Normalize(Str(au, "face"));
                    detail.PubTs = Long(au, "pub_ts");
                    detail.PubTimeText = Str(au, "pub_time");
                    if (detail.PubTimeText.Length == 0 && detail.PubTs > 0)
                        detail.PubTimeText = FormatPubTime(detail.PubTs);
                }
                break;

            case "MODULE_TYPE_CONTENT":
                if (mod.TryGetProperty("module_content", out var mc) && mc.ValueKind == JsonValueKind.Object &&
                    mc.TryGetProperty("paragraphs", out var paras) && paras.ValueKind == JsonValueKind.Array)
                {
                    AppendOpusParagraphs(paras, detail);
                }
                break;

            case "MODULE_TYPE_STAT":
                if (mod.TryGetProperty("module_stat", out var st) && st.ValueKind == JsonValueKind.Object)
                    ReadModuleStat(st, detail);
                break;
        }
    }

    private static void ReadDynamicModulesObject(JsonElement mods, ArticleDetail detail)
    {
        if (mods.TryGetProperty("module_author", out var au) && au.ValueKind == JsonValueKind.Object)
        {
            detail.AuthorMid = Long(au, "mid");
            detail.AuthorName = Str(au, "name");
            detail.AuthorFace = UrlUtil.Normalize(Str(au, "face"));
            detail.PubTs = Long(au, "pub_ts");
            detail.PubTimeText = Str(au, "pub_time");
            if (detail.PubTimeText.Length == 0 && detail.PubTs > 0)
                detail.PubTimeText = FormatPubTime(detail.PubTs);
        }

        if (mods.TryGetProperty("module_stat", out var st) && st.ValueKind == JsonValueKind.Object)
            ReadModuleStat(st, detail);

        if (mods.TryGetProperty("module_dynamic", out var md) && md.ValueKind == JsonValueKind.Object)
        {
            if (md.TryGetProperty("major", out var major) && major.ValueKind == JsonValueKind.Object &&
                major.TryGetProperty("opus", out var opus) && opus.ValueKind == JsonValueKind.Object)
            {
                var title = Str(opus, "title");
                if (title.Length > 0) detail.Title = title;
                TryFillIdsFromUrl(Str(opus, "jump_url"), detail);

                if (opus.TryGetProperty("content", out var cnt) && cnt.ValueKind == JsonValueKind.Object &&
                    cnt.TryGetProperty("paragraphs", out var paras) && paras.ValueKind == JsonValueKind.Array)
                {
                    AppendOpusParagraphs(paras, detail);
                }
                else if (opus.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.Object)
                {
                    var text = Str(sm, "text");
                    if (text.Length > 0)
                        detail.Blocks.Add(new ArticleBlock { Kind = ArticleBlockKind.Paragraph, Text = text });
                }

                if (detail.AllImages.Count == 0 &&
                    opus.TryGetProperty("pics", out var pics) && pics.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in pics.EnumerateArray())
                    {
                        var url = UrlUtil.Normalize(Str(p, "url"));
                        if (url.Length == 0) continue;
                        var idx = RegisterImage(detail, url, (int)Long(p, "width"), (int)Long(p, "height"));
                        detail.Blocks.Add(new ArticleBlock
                        {
                            Kind = ArticleBlockKind.Image,
                            ImageUrl = url,
                            ImageWidth = (int)Long(p, "width"),
                            ImageHeight = (int)Long(p, "height"),
                            ImageIndex = idx,
                        });
                    }
                }
            }
            else if (md.TryGetProperty("desc", out var desc) && desc.ValueKind == JsonValueKind.Object)
            {
                var text = Str(desc, "text");
                if (text.Length > 0)
                    detail.Blocks.Add(new ArticleBlock { Kind = ArticleBlockKind.Paragraph, Text = text });
            }
        }
    }

    private static void ReadModuleStat(JsonElement st, ArticleDetail detail)
    {
        if (st.TryGetProperty("like", out var lk) && lk.ValueKind == JsonValueKind.Object)
        {
            detail.LikeCount = Long(lk, "count");
            detail.IsLiked = Bool(lk, "status");
        }
        if (st.TryGetProperty("comment", out var cm) && cm.ValueKind == JsonValueKind.Object)
            detail.ReplyCount = Long(cm, "count");
        if (st.TryGetProperty("forward", out var fw) && fw.ValueKind == JsonValueKind.Object)
            detail.ShareCount = Long(fw, "count");
        if (st.TryGetProperty("favorite", out var fav) && fav.ValueKind == JsonValueKind.Object)
        {
            detail.FavoriteCount = Long(fav, "count");
            detail.IsFavorited = Bool(fav, "status");
        }
        if (st.TryGetProperty("coin", out var cn) && cn.ValueKind == JsonValueKind.Object)
            detail.CoinCount = Long(cn, "count");
    }

    /// <summary>将 Opus paragraphs[] 按段类型逐条转成 ArticleBlock</summary>
    public static void AppendOpusParagraphs(JsonElement paragraphs, ArticleDetail detail)
    {
        if (paragraphs.ValueKind != JsonValueKind.Array) return;
        foreach (var para in paragraphs.EnumerateArray())
        {
            if (para.ValueKind != JsonValueKind.Object) continue;
            var ptype = (int)Long(para, "para_type");

            switch (ptype)
            {
                case 1: // 普通文本 / 标题段落
                    if (para.TryGetProperty("text", out var txtObj) && txtObj.ValueKind == JsonValueKind.Object)
                        AppendOpusTextBlock(txtObj, ArticleBlockKind.Paragraph, detail);
                    break;

                case 2: // 图片（单图或多图）
                    if (para.TryGetProperty("pic", out var picObj) && picObj.ValueKind == JsonValueKind.Object &&
                        picObj.TryGetProperty("pics", out var pics) && pics.ValueKind == JsonValueKind.Array)
                    {
                        var caption = Str(picObj, "caption");
                        foreach (var p in pics.EnumerateArray())
                        {
                            var url = UrlUtil.Normalize(Str(p, "url"));
                            if (url.Length == 0) url = UrlUtil.Normalize(Str(p, "src"));
                            if (url.Length == 0) continue;
                            var w = (int)Long(p, "width");
                            var h = (int)Long(p, "height");
                            var idx = RegisterImage(detail, url, w, h);
                            detail.Blocks.Add(new ArticleBlock
                            {
                                Kind = ArticleBlockKind.Image,
                                ImageUrl = url,
                                ImageWidth = w,
                                ImageHeight = h,
                                ImageCaption = caption,
                                ImageIndex = idx,
                            });
                            caption = "";
                        }
                    }
                    break;

                case 3: // 分割线
                    detail.Blocks.Add(new ArticleBlock { Kind = ArticleBlockKind.Divider });
                    break;

                case 4: // 引用块
                    if (para.TryGetProperty("blockquote", out var bq) && bq.ValueKind == JsonValueKind.Object &&
                        bq.TryGetProperty("children", out var bqChildren) && bqChildren.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var child in bqChildren.EnumerateArray())
                        {
                            if (child.TryGetProperty("text", out var ctxt) && ctxt.ValueKind == JsonValueKind.Object)
                                AppendOpusTextBlock(ctxt, ArticleBlockKind.Quote, detail);
                        }
                    }
                    else if (para.TryGetProperty("text", out var bqTxt) && bqTxt.ValueKind == JsonValueKind.Object)
                    {
                        AppendOpusTextBlock(bqTxt, ArticleBlockKind.Quote, detail);
                    }
                    break;

                case 5: // 有序/无序列表
                    if (para.TryGetProperty("list", out var listObj) && listObj.ValueKind == JsonValueKind.Object &&
                        listObj.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        var ordered = Long(listObj, "style") == 1;
                        int idx = 1;
                        foreach (var li in items.EnumerateArray())
                        {
                            var (text, runs) = ParseTextNodes(li);
                            if (text.Length == 0 && runs.Count == 0) continue;
                            detail.Blocks.Add(new ArticleBlock
                            {
                                Kind = ArticleBlockKind.ListItem,
                                Text = text,
                                Runs = runs,
                                OrderedList = ordered,
                                ListIndex = idx++,
                            });
                        }
                    }
                    break;

                case 6: // 链接卡片（B站视频 / 站内外链接 / 投票）
                    if (para.TryGetProperty("link_card", out var lc) && lc.ValueKind == JsonValueKind.Object &&
                        lc.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object)
                    {
                        AppendOpusLinkCard(card, detail);
                    }
                    break;

                case 7: // 代码块
                    if (para.TryGetProperty("code", out var codeObj) && codeObj.ValueKind == JsonValueKind.Object)
                    {
                        var content = Str(codeObj, "content");
                        if (content.Length > 0)
                        {
                            detail.Blocks.Add(new ArticleBlock
                            {
                                Kind = ArticleBlockKind.Code,
                                CodeLanguage = Str(codeObj, "lang"),
                                Text = content,
                            });
                        }
                    }
                    break;

                case 8: // 标题（部分客户端版本单独拆为 para_type=8）
                    if (para.TryGetProperty("heading", out var hdObj) && hdObj.ValueKind == JsonValueKind.Object)
                    {
                        var (text, runs) = ParseTextNodes(hdObj);
                        if (text.Length > 0)
                        {
                            detail.Blocks.Add(new ArticleBlock
                            {
                                Kind = ArticleBlockKind.Heading,
                                HeadingLevel = Math.Clamp((int)Long(hdObj, "level"), 1, 3),
                                Text = text,
                                Runs = runs,
                            });
                        }
                    }
                    else if (para.TryGetProperty("text", out var hdTxt) && hdTxt.ValueKind == JsonValueKind.Object)
                    {
                        AppendOpusTextBlock(hdTxt, ArticleBlockKind.Heading, detail);
                    }
                    break;

                default:
                    if (para.TryGetProperty("text", out var fallbackTxt) && fallbackTxt.ValueKind == JsonValueKind.Object)
                        AppendOpusTextBlock(fallbackTxt, ArticleBlockKind.Paragraph, detail);
                    break;
            }
        }
    }

    private static void AppendOpusTextBlock(JsonElement txtObj, ArticleBlockKind preferredKind, ArticleDetail detail)
    {
        var (text, runs, maxFontSize, allBold) = ParseTextNodesDetailed(txtObj);
        if (string.IsNullOrWhiteSpace(text) && runs.Count == 0) return;

        var kind = preferredKind;
        if (kind == ArticleBlockKind.Paragraph && maxFontSize >= 22 && allBold && text.Length <= 80 && !text.Contains('\n'))
            kind = ArticleBlockKind.Heading;

        detail.Blocks.Add(new ArticleBlock
        {
            Kind = kind,
            HeadingLevel = maxFontSize >= 26 ? 1 : maxFontSize >= 22 ? 2 : 3,
            Text = text,
            Runs = runs,
        });
    }

    private static void AppendOpusLinkCard(JsonElement card, ArticleDetail detail)
    {
        if (card.TryGetProperty("ugc", out var ugc) && ugc.ValueKind == JsonValueKind.Object)
        {
            var jumpUrl = UrlUtil.Normalize(Str(ugc, "jump_url"));
            var bvidMatch = BvidRegex.Match(jumpUrl);
            if (!bvidMatch.Success) bvidMatch = BvidRegex.Match(Str(ugc, "oid_str"));
            if (bvidMatch.Success)
            {
                detail.Blocks.Add(new ArticleBlock
                {
                    Kind = ArticleBlockKind.VideoCard,
                    LinkUrl = jumpUrl,
                    Video = new VideoItem
                    {
                        Bvid = bvidMatch.Value,
                        Title = Str(ugc, "title"),
                        Cover = UrlUtil.Normalize(Str(ugc, "cover")),
                        Duration = Str(ugc, "duration"),
                        Author = Str(ugc, "head_text"),
                    },
                });
                return;
            }
        }

        if (card.TryGetProperty("common", out var common) && common.ValueKind == JsonValueKind.Object)
        {
            var title = Str(common, "title");
            var jumpUrl = UrlUtil.Normalize(Str(common, "jump_url"));
            if (title.Length > 0 || jumpUrl.Length > 0)
            {
                detail.Blocks.Add(new ArticleBlock
                {
                    Kind = ArticleBlockKind.Paragraph,
                    Text = title.Length > 0 ? title : jumpUrl,
                    LinkUrl = jumpUrl,
                    Runs = new()
                    {
                        new ArticleTextRun
                        {
                            Text = title.Length > 0 ? $"🔗 {title}" : jumpUrl,
                            Url = jumpUrl,
                            ReferencedBvid = ExtractBvid(jumpUrl),
                        },
                    },
                });
            }
        }
    }

    private static (string text, List<ArticleTextRun> runs) ParseTextNodes(JsonElement container)
    {
        var (t, r, _, _) = ParseTextNodesDetailed(container);
        return (t, r);
    }

    private static (string text, List<ArticleTextRun> runs, double maxFont, bool allBold) ParseTextNodesDetailed(JsonElement container)
    {
        var sb = new StringBuilder();
        var runs = new List<ArticleTextRun>();
        double maxFont = 0;
        bool hasNonEmptyRun = false;
        bool allBold = true;

        if (!container.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return ("", runs, 0, false);

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var ntype = Str(node, "type");

            switch (ntype)
            {
                case "TEXT_NODE_TYPE_WORD":
                    if (node.TryGetProperty("word", out var w) && w.ValueKind == JsonValueKind.Object)
                    {
                        var words = Str(w, "words");
                        var fontSize = Long(w, "font_size");
                        if (fontSize > maxFont) maxFont = fontSize;
                        bool bold = false, italic = false;
                        if (w.TryGetProperty("style", out var style) && style.ValueKind == JsonValueKind.Object)
                        {
                            bold = Bool(style, "bold");
                            italic = Bool(style, "italic");
                        }
                        if (!string.IsNullOrWhiteSpace(words))
                        {
                            hasNonEmptyRun = true;
                            if (!bold) allBold = false;
                        }
                        sb.Append(words);
                        runs.Add(new ArticleTextRun
                        {
                            Text = words,
                            Bold = bold,
                            Italic = italic,
                            Color = Str(w, "color"),
                        });
                    }
                    break;

                case "TEXT_NODE_TYPE_RICH":
                    if (node.TryGetProperty("rich", out var rich) && rich.ValueKind == JsonValueKind.Object)
                    {
                        var text = Str(rich, "text");
                        if (text.Length == 0) text = Str(rich, "orig_text");
                        var jump = UrlUtil.Normalize(Str(rich, "jump_url"));
                        var bvid = ExtractBvid(jump);
                        if (bvid.Length == 0) bvid = ExtractBvid(Str(rich, "rid"));
                        if (bvid.Length == 0) bvid = ExtractBvid(text);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            hasNonEmptyRun = true;
                            allBold = false;
                        }
                        sb.Append(text);
                        runs.Add(new ArticleTextRun
                        {
                            Text = text,
                            Url = jump,
                            ReferencedBvid = bvid,
                        });
                    }
                    break;

                case "TEXT_NODE_TYPE_EMOJI":
                    if (node.TryGetProperty("emoji", out var em) && em.ValueKind == JsonValueKind.Object)
                    {
                        var text = Str(em, "text");
                        sb.Append(text);
                        if (text.Length > 0) runs.Add(new ArticleTextRun { Text = text });
                    }
                    break;

                case "TEXT_NODE_TYPE_LINK":
                    if (node.TryGetProperty("link", out var lk) && lk.ValueKind == JsonValueKind.Object)
                    {
                        var text = Str(lk, "show_text");
                        var url = UrlUtil.Normalize(Str(lk, "link"));
                        if (text.Length == 0) text = url;
                        sb.Append(text);
                        runs.Add(new ArticleTextRun
                        {
                            Text = text,
                            Url = url,
                            ReferencedBvid = ExtractBvid(url),
                        });
                    }
                    break;

                default:
                    if (node.TryGetProperty("word", out var fw) && fw.ValueKind == JsonValueKind.Object)
                    {
                        var words = Str(fw, "words");
                        sb.Append(words);
                        if (words.Length > 0) runs.Add(new ArticleTextRun { Text = words });
                    }
                    break;
            }
        }

        return (sb.ToString(), runs, maxFont, hasNonEmptyRun && allBold);
    }

    // ------------------------------------------------------------ HTML 内部辅助

    private static void EmitRichParagraphOrImages(string innerHtml, ArticleBlockKind kind, ArticleDetail detail)
    {
        var imgMatches = Regex.Matches(innerHtml, @"<img\b[^>]*>", RegexOptions.IgnoreCase);
        if (imgMatches.Count == 0)
        {
            var (text, runs) = ExtractHtmlTextAndRuns(innerHtml);
            if (text.Length > 0)
                detail.Blocks.Add(new ArticleBlock { Kind = kind, Text = text, Runs = runs });
            return;
        }

        int pos = 0;
        foreach (Match im in imgMatches)
        {
            if (im.Index > pos)
            {
                var sub = innerHtml.Substring(pos, im.Index - pos);
                var (text, runs) = ExtractHtmlTextAndRuns(sub);
                if (text.Length > 0)
                    detail.Blocks.Add(new ArticleBlock { Kind = kind, Text = text, Runs = runs });
            }
            TryAddImageTag(im.Value, "", detail);
            pos = im.Index + im.Length;
        }
        if (pos < innerHtml.Length)
        {
            var tail = innerHtml.Substring(pos);
            var (text, runs) = ExtractHtmlTextAndRuns(tail);
            if (text.Length > 0)
                detail.Blocks.Add(new ArticleBlock { Kind = kind, Text = text, Runs = runs });
        }
    }

    private static void AddPlainTextFragment(string fragment, ArticleDetail detail)
    {
        var text = WebUtility.HtmlDecode(StripTagsPreserveBreaks(fragment)).Trim();
        if (text.Length > 0)
            detail.Blocks.Add(new ArticleBlock { Kind = ArticleBlockKind.Paragraph, Text = text });
    }

    private static (string text, List<ArticleTextRun> runs) ExtractHtmlTextAndRuns(string html)
    {
        var runs = new List<ArticleTextRun>();
        var sb = new StringBuilder();
        var inlinePattern = new Regex(
            @"<(?<tag>a|strong|b|em|i)\b(?<attrs>[^>]*)>(?<inner>[\s\S]*?)</\k<tag>>",
            RegexOptions.IgnoreCase);

        int pos = 0;
        foreach (Match m in inlinePattern.Matches(html))
        {
            if (m.Index > pos)
            {
                var plain = WebUtility.HtmlDecode(StripTagsPreserveBreaks(html.Substring(pos, m.Index - pos)));
                if (plain.Length > 0)
                {
                    sb.Append(plain);
                    runs.Add(new ArticleTextRun { Text = plain });
                }
            }
            pos = m.Index + m.Length;

            var tag = m.Groups["tag"].Value.ToLowerInvariant();
            var inner = WebUtility.HtmlDecode(StripTagsPreserveBreaks(m.Groups["inner"].Value));
            if (inner.Length == 0) continue;

            if (tag == "a")
            {
                var href = ExtractAttr(m.Groups["attrs"].Value, "href");
                var normUrl = UrlUtil.Normalize(WebUtility.HtmlDecode(href));
                sb.Append(inner);
                runs.Add(new ArticleTextRun
                {
                    Text = inner,
                    Url = normUrl,
                    ReferencedBvid = ExtractBvid(normUrl),
                });
            }
            else
            {
                bool bold = tag is "strong" or "b";
                bool italic = tag is "em" or "i";
                sb.Append(inner);
                runs.Add(new ArticleTextRun { Text = inner, Bold = bold, Italic = italic });
            }
        }

        if (pos < html.Length)
        {
            var tail = WebUtility.HtmlDecode(StripTagsPreserveBreaks(html.Substring(pos)));
            if (tail.Length > 0)
            {
                sb.Append(tail);
                runs.Add(new ArticleTextRun { Text = tail });
            }
        }

        var full = sb.ToString().Trim();
        if (full.Length == 0) runs.Clear();
        return (full, runs);
    }

    private static bool TryAddImageTag(string imgTag, string caption, ArticleDetail detail)
    {
        var rawUrl = ExtractAttr(imgTag, "data-src");
        if (rawUrl.Length == 0) rawUrl = ExtractAttr(imgTag, "src");
        if (rawUrl.Length == 0) return false;

        var url = UrlUtil.Normalize(WebUtility.HtmlDecode(rawUrl));
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return false;

        int.TryParse(ExtractAttr(imgTag, "width"), out var w);
        int.TryParse(ExtractAttr(imgTag, "height"), out var h);
        var idx = RegisterImage(detail, url, w, h);
        detail.Blocks.Add(new ArticleBlock
        {
            Kind = ArticleBlockKind.Image,
            ImageUrl = url,
            ImageWidth = w,
            ImageHeight = h,
            ImageCaption = caption,
            ImageIndex = idx,
        });
        return true;
    }

    private static string ExtractAttr(string attrs, string name)
    {
        var m = Regex.Match(attrs, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        if (m.Groups[1].Success) return m.Groups[1].Value;
        if (m.Groups[2].Success) return m.Groups[2].Value;
        return m.Groups[3].Value;
    }

    private static string StripTagsPreserveBreaks(string html)
    {
        var s = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        return s;
    }

    // ------------------------------------------------------------ 共用工具

    private static int RegisterImage(ArticleDetail detail, string url, int w, int h)
    {
        var existing = detail.AllImages.FindIndex(p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) return existing;
        detail.AllImages.Add(new DynamicPicture { Url = url, Width = w, Height = h });
        return detail.AllImages.Count - 1;
    }

    private static void TryFillIdsFromUrl(string url, ArticleDetail detail)
    {
        if (string.IsNullOrEmpty(url)) return;
        var cv = CvRegex.Match(url);
        if (cv.Success && detail.ArticleId.Length == 0) detail.ArticleId = cv.Groups[1].Value;
        var op = OpusRegex.Match(url);
        if (op.Success && detail.OpusId.Length == 0) detail.OpusId = op.Groups[1].Value;
    }

    private static string ExtractBvid(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var m = BvidRegex.Match(s);
        return m.Success ? m.Value : "";
    }

    private static long EstimateWordCount(List<ArticleBlock> blocks)
    {
        long n = 0;
        foreach (var b in blocks) n += b.Text.Length;
        return n;
    }

    internal static string FormatPubTime(long ts)
    {
        if (ts <= 0) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch { return ""; }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Scalar(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";

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

    private static bool Bool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetInt64() == 1);
    }
}
