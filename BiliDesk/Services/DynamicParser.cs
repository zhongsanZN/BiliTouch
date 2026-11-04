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
    public static DynamicItem? Parse(JsonElement e) => Parse(e, 0);

    private static DynamicItem? Parse(JsonElement e, int depth)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Object || depth > 2 || Str(e, "type") == "DYNAMIC_TYPE_NONE") return null;

            var item = new DynamicItem
            {
                Id = Scalar(e, "id_str"),
                Kind = KindOf(e),
                RawType = RawTypeOf(e),   // 评论接口要用它决定 type 参数
            };
            if (item.Id.Length == 0) item.Id = Scalar(e, "id");
            if (e.TryGetProperty("basic", out var basic))
            {
                item.CommentOid = Scalar(basic, "comment_id_str");
                item.CommentType = (int)Long(basic, "comment_type");
            }
            if (item.Id.Length > 0) item.DetailUrl = $"https://t.bilibili.com/{item.Id}";

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
                    ReadForward(e, item, depth);
                    break;
            }

            // ---------- 互动数 ----------
            if (e.TryGetProperty("modules", out var m2) && m2.ValueKind == JsonValueKind.Object &&
                m2.TryGetProperty("module_stat", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                if (st.TryGetProperty("like", out var lk))
                {
                    item.LikeCount = Long(lk, "count");
                    item.IsLiked = lk.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.True;
                    item.LikeForbidden = lk.TryGetProperty("forbidden", out var forbidden) && forbidden.ValueKind == JsonValueKind.True;
                }
                if (st.TryGetProperty("comment", out var cm)) item.CommentCount = Long(cm, "count");
                if (st.TryGetProperty("forward", out var fw)) item.ForwardCount = Long(fw, "count");
            }

            if (item.Video is { } video)
            {
                video.OwnerMid = item.AuthorMid;
                video.Author = item.AuthorName;
                video.AuthorFace = item.AuthorFace;
                video.Pubdate = item.PubTs;
            }
            // 一条动态至少要有"某种内容"才有意义。
            // ★ 2026-11-04 放宽: 原来只认 正文/图/视频/专栏标题/直播/转发, 于是
            //   **投票、分享、op(B 站的小内容单元)、UP 主自己的动态**全被判成空而丢弃 ——
            //   用户看到的就是"动态页缺一大块"。现在只要有一条动态的 id 且有作者,
            //   就保留它(最坏情况是一张只有头像+时间的卡片, 也好过整条消失)。
            var hasSomething = item.HasText || item.HasPictures || item.Video != null
                || item.ArticleTitle.Length > 0 || item.LiveRoomId > 0 || item.IsForward
                || item.Vote != null || item.Common != null
                || item.Kind is DynamicKind.Other or DynamicKind.Text;
            // 兜底: 有 id 且能识别出作者(说明这是条真动态, 只是内容形态我们没覆盖)
            if (!hasSomething && item.Id.Length > 0 && item.AuthorName.Length > 0)
                hasSomething = true;
            return hasSomething ? item : null;
        }
        catch
        {
            return null;   // 单条异常只丢这一条, 不影响整页
        }
    }

    // ------------------------------------------------------------ 类型

    /// <summary>接口原文的动态类型数字(评论接口按它分 type)。</summary>
    private static int RawTypeOf(JsonElement e)
    {
        var t = Str(e, "type");
        var mapped = t switch
        {
            "DYNAMIC_TYPE_FORWARD" => 1, "DYNAMIC_TYPE_DRAW" => 2,
            "DYNAMIC_TYPE_WORD" => 4, "DYNAMIC_TYPE_AV" => 8,
            "DYNAMIC_TYPE_ARTICLE" => 64, "DYNAMIC_TYPE_LIVE_RCMD" => 4308,
            "DYNAMIC_TYPE_LIVE" => 4200, _ => 0,
        };
        if (mapped != 0) return mapped;
        if (int.TryParse(t, out var n)) return n;
        if (e.TryGetProperty("type", out var tv) && tv.ValueKind == JsonValueKind.Number)
            return tv.GetInt32();
        return 0;
    }

    private static DynamicKind KindOf(JsonElement e)
    {
        var n = RawTypeOf(e);

        return n switch
        {
            1 => DynamicKind.Forward,
            2 => DynamicKind.ImageText,
            4 => DynamicKind.Text,
            8 => DynamicKind.Video,
            64 => DynamicKind.Article,
            4200 or 4308 => DynamicKind.Live,
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

            // ---- 图片: 两套结构都要认(2026-11-04 重写) ----
            // 现代图文动态(opus)长这样, 这是**当前主流**, 之前完全没读 ⇒ 图片全丢:
            //   modules.module_dynamic.major.opus.pics[] { url, width, height }
            // 老版图文动态才是:
            //   modules.module_dynamic.major.draw.items[] { src, width, height }
            // 两者字段名还不一样(url vs src), 所以必须分开读。
            if (md.TryGetProperty("major", out var major) && major.ValueKind == JsonValueKind.Object)
            {
                // ① opus(新)—— 优先
                if (major.TryGetProperty("opus", out var opus) && opus.ValueKind == JsonValueKind.Object)
                {
                    item.ContentTitle = Str(opus, "title");
                    if (opus.TryGetProperty("pics", out var opics) && opics.ValueKind == JsonValueKind.Array)
                    foreach (var p in opics.EnumerateArray())
                    {
                        var url = Str(p, "url");
                        if (url.Length == 0) url = Str(p, "src");
                        if (url.Length == 0) continue;
                        item.Pictures.Add(new DynamicPicture
                        {
                            Url = UrlUtil.Normalize(url),
                            Width = (int)Long(p, "width"),
                            Height = (int)Long(p, "height"),
                        });
                    }
                    // opus 自带正文摘要, desc 为空时用它
                    // ★★★ 2026-11-04(关键): 新版图文动态的**正文全文就在 summary 里**,
                    //   而 `module_dynamic.desc` 在列表接口下是 null(实测 20 条里 14 条如此)。
                    //   这条路径以前只是"兜底", 实际上是**主路径** —— 没有它, 卡片上就没有正文。
                    //   summary 里同时有 text 与 rich_text_nodes, 两个都要读:
                    //   text 用来显示, rich_text_nodes 用来把话题/@/表情变成可点内容。
                    if (item.Text.Length == 0 &&
                        opus.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.Object)
                    {
                        item.Text = Str(sm, "text");
                        if (item.Text.Length == 0) item.Text = Str(sm, "rich_text");
                        if (item.RichNodes.Count == 0)
                            item.RichNodes = ReadRichNodes(sm);
                    }
                }

                // ② draw(老)—— opus 没给图时退回
                if (item.Pictures.Count == 0 &&
                    major.TryGetProperty("draw", out var draw) && draw.ValueKind == JsonValueKind.Object &&
                    draw.TryGetProperty("items", out var pics) && pics.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in pics.EnumerateArray())
                    {
                        var url = Str(p, "src");
                        if (url.Length == 0) url = Str(p, "url");
                        if (url.Length == 0) continue;
                        item.Pictures.Add(new DynamicPicture
                        {
                            Url = UrlUtil.Normalize(url),
                            Width = (int)Long(p, "width"),
                            Height = (int)Long(p, "height"),
                        });
                    }
                }

                // ③ 投票 —— 与正文/图片并存, 不是互斥的另一种"类型"
                if (major.TryGetProperty("vote", out var vote) && vote.ValueKind == JsonValueKind.Object)
                    item.Vote = ReadVote(vote);

                // ④ 附加卡片(活动 / 商品 / 番剧 / 课程 / 直播预告) —— 之前完全没读
                if (major.TryGetProperty("common", out var common) && common.ValueKind == JsonValueKind.Object)
                    item.Common = ReadCommon(common);
            }

            // ★★★ 2026-11-04 实测纠正: 投票真正的家在 module_dynamic.**additional**.vote,
            //   且 additional.type == "ADDITIONAL_TYPE_VOTE"。扫 160 条动态, major.vote 0 命中。
            //   同时 additional 也是"附加卡片"、UGC 抽奖等所有附加控件的统一入口,
            //   所以这里按 type 分发, 以后加新的附加类型只改这里。
            if (md.TryGetProperty("additional", out var additional) &&
                additional.ValueKind == JsonValueKind.Object)
                ReadAdditional(additional, item);

            // ⑤ 富文本节点(话题 / @ / 表情 / 链接) —— 只读 text 的话这些全是死字
            if (md.TryGetProperty("desc", out var rich) && rich.ValueKind == JsonValueKind.Object)
                item.RichNodes = ReadRichNodes(rich);

            // ⑥ 话题的**权威来源**(2026-11-04 实测补): module_dynamic.topic{ id, name, jump_url }。
            //   为什么不能只靠 rich_text_nodes: 那里的 TOPIC 节点 rid 恒为空串,
            //   拿不到 topic_id, 点话题只能退回"拿名字去搜索"。这里才给真 id。
            if (md.TryGetProperty("topic", out var topic) && topic.ValueKind == JsonValueKind.Object)
            {
                item.TopicId = Scalar(topic, "id");
                item.TopicName = Str(topic, "name");
                item.TopicJumpUrl = Str(topic, "jump_url");
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

    // ------------------------------------------------------------ 投票 / 附加卡片 / 富文本

    /// <summary>
    /// 附加控件分发(2026-11-04, 按只读探针实测结构)。
    ///
    /// <c>module_dynamic.additional</c> 是**所有附加控件的统一入口**, 靠
    /// <c>additional.type</c> 区分:
    ///   · ADDITIONAL_TYPE_VOTE        → 投票
    ///   · ADDITIONAL_TYPE_COMMON      → 活动/商品/番剧 附加卡片
    ///   · ADDITIONAL_TYPE_UGC         → 站内稿件(有时和 major.archive 同时出现)
    ///   · ADDITIONAL_TYPE_RESERVE     → 预约(直播/番剧)
    ///   · ADDITIONAL_TYPE_GOODS       → 带货
    /// 目前只处理前两种; 其余先按"不认识的附加块"跳过 —— 但**不影响正文与图片**,
    /// 因为它们是并存的, 不是互斥的。
    /// </summary>
    private static void ReadAdditional(JsonElement additional, DynamicItem item)
    {
        var type = Str(additional, "type");

        // 投票(实测: 只有 vote_id/title/join_num/status/choice_cnt/end_time, **没有 options**)
        if (additional.TryGetProperty("vote", out var vote) && vote.ValueKind == JsonValueKind.Object)
        {
            item.Vote ??= ReadVote(vote);
            return;
        }
        // 附加卡片(结构与 major.common 一致, 复用同一个读取器)
        if (additional.TryGetProperty("common", out var common) && common.ValueKind == JsonValueKind.Object)
        {
            item.Common ??= ReadCommon(common);
            return;
        }
        // 预约类: 结构与 common 接近(带 title/cover/desc), 也交给 common 渲染,
        // 至少不会让用户看到一条"只有正文、下面空了"的动态
        if (type.EndsWith("_RESERVE", StringComparison.Ordinal) &&
            additional.TryGetProperty("reserve", out var reserve) && reserve.ValueKind == JsonValueKind.Object)
            item.Common ??= ReadCommon(reserve);
    }

    /// <summary>
    /// 投票卡片节点(2026-11-04 按实测重写)。
    ///
    /// ★★★ 实测结论(只读探针, 扫 160 条动态):
    ///   卡片节点位置 = <c>modules.module_dynamic.additional.vote</c>, 不是 major.vote。
    ///   卡片节点**不含 options** —— 只有:
    ///     vote_id / title / desc / join_num / status / choice_cnt / end_time / type / uid /
    ///     jump_url / button / default_share / upower_*
    ///   要拿选项必须再调 <c>GET /x/vote/vote_info?vote_id=</c>(见 ApiClient.GetVoteInfoAsync)。
    ///
    /// ★ 三处与原猜测不同的语义:
    ///   1. <c>desc</c> 是"N人参与"**文案**, 不是投票描述 —— 不能当描述显示;
    ///   2. <c>status</c> 是 '1' 进行中 / '4' 已结束, **不是**"我投过没有";
    ///   3. <c>join_num</c> 直接给参与人数, 不需要把 options[].cnt 加起来
    ///      (况且卡片节点压根没有 options)。
    /// </summary>
    private static DynamicVote ReadVote(JsonElement vote)
    {
        var result = new DynamicVote
        {
            Id = Scalar(vote, "vote_id"),
            Title = Str(vote, "title"),
            Desc = Str(vote, "desc"),
            Status = Str(vote, "status"),
            JumpUrl = Str(vote, "jump_url"),
        };
        if (result.Id.Length == 0) result.Id = Scalar(vote, "id");   // 老结构兜底

        result.TotalCount = Long(vote, "join_num");
        if (result.TotalCount <= 0) result.TotalCount = Long(vote, "join_count");

        var choices = (int)Long(vote, "choice_cnt");
        if (choices <= 0) choices = (int)Long(vote, "can_vote_num");
        result.MaxChoices = choices > 0 ? choices : 1;

        result.EndTime = Long(vote, "end_time");
        if (result.EndTime <= 0) result.EndTime = Long(vote, "vote_end_time");

        // 少数结构里会内联 options —— 有就读, 没有就等界面按需拉 vote_info
        ReadVoteOptions(vote, result);
        result.OptionsLoaded = result.Options.Count > 0;
        return result;
    }

    /// <summary>
    /// 读 options 数组。字段名实测是 <c>opt_desc</c>(不是 desc), <c>opt_idx</c> 从 **1** 开始。
    /// 投票详情接口(/x/vote/vote_info)返回的是同一套字段, 所以两处共用这个方法。
    /// </summary>
    public static void ReadVoteOptions(JsonElement container, DynamicVote vote)
    {
        vote.Options.Clear();
        if (!container.TryGetProperty("options", out var options) ||
            options.ValueKind != JsonValueKind.Array) return;

        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object) continue;
            var text = Str(option, "opt_desc");
            if (text.Length == 0) text = Str(option, "desc");
            vote.Options.Add(new DynamicVoteOption
            {
                Index = (int)Long(option, "opt_idx"),
                Text = text,
                Count = Long(option, "cnt"),
                ImageUrl = UrlUtil.Normalize(Str(option, "img_url")),
            });
        }
        RecomputeVoteOptions(vote);
    }

    /// <summary>
    /// 重算每个选项的百分比。
    /// ★ 分母用**选项票数之和**而不是 join_num: 实测两者可能不一致(有人投了但没算进 join_num,
    ///   或者 join_num 含已删除的票), 用 join_num 当分母会出现"加起来超过 100%"。
    /// </summary>
    public static void RecomputeVoteOptions(DynamicVote vote)
    {
        var sum = vote.Options.Sum(o => o.Count);
        foreach (var option in vote.Options)
        {
            option.Fraction = sum > 0 ? Math.Clamp((double)option.Count / sum, 0, 1) : 0;
            option.PercentText = sum > 0 ? Math.Round(option.Count * 100.0 / sum) + "%" : "";
        }
    }

    /// <summary>
    /// 附加卡片(2026-11-04): 有两套结构, 都要认。
    ///
    /// ★★★ 实测两套(只读探针, 3 样本各):
    ///   ① <c>additional.common</c>(新, 游戏卡等):
    ///        { id_str, cover, title, desc1, desc2, sub_type, head_text, jump_url, button{...} }
    ///      —— 注意描述是 **desc1 / desc2 两段**, 不是 desc; 编号字段是 **id_str**, 不是 id。
    ///   ② <c>major.common</c>(老, DYNAMIC_TYPE_COMMON_SQUARE):
    ///        { id, jump_url, badge, biz_type, sketch_id }
    ///
    /// 之前只按 ② 猜的字段名(desc/cover/title)读, 于是 ① 那种卡片
    /// 标题能显示、但描述永远空着 —— 用户看到一张"只有标题的活动卡"。
    /// 两套都读, 取到哪个算哪个。
    /// </summary>
    private static DynamicCommonCard ReadCommon(JsonElement common)
    {
        var card = new DynamicCommonCard
        {
            Title = Str(common, "title"),
            Cover = UrlUtil.Normalize(Str(common, "cover")),
            JumpUrl = Str(common, "jump_url"),
            SourceName = Str(common, "head_text"),
            SubType = Str(common, "sub_type"),
        };

        // 编号: 新结构是 id_str, 老结构是 id
        card.Id = Scalar(common, "id_str");
        if (card.Id.Length == 0) card.Id = Scalar(common, "id");

        // 描述: 新结构拆成 desc1/desc2, 老结构是单段 desc
        var desc1 = Str(common, "desc1");
        var desc2 = Str(common, "desc2");
        card.Desc = Str(common, "desc");
        if (card.Desc.Length == 0)
            card.Desc = desc1.Length > 0 && desc2.Length > 0 ? desc1 + " · " + desc2
                      : desc1.Length > 0 ? desc1 : desc2;
        card.Desc1 = desc1;
        card.Desc2 = desc2;

        if (card.SourceName.Length == 0) card.SourceName = Str(common, "source_name");

        // button: 两套都有, 但文字在 button.jump_style.text(实测), 不是 button.text
        if (common.TryGetProperty("button", out var button) && button.ValueKind == JsonValueKind.Object)
        {
            if (button.TryGetProperty("jump_style", out var style) && style.ValueKind == JsonValueKind.Object)
                card.ButtonText = Str(style, "text");
            if (card.ButtonText.Length == 0) card.ButtonText = Str(button, "text");
        }
        if (common.TryGetProperty("badge", out var badge) && badge.ValueKind == JsonValueKind.Object)
            card.Badge = Str(badge, "text");

        // 少数活动卡把图片放在 cover_left_* 之类的位置, 拿不到 cover 时兜底一次
        if (card.Cover.Length == 0) card.Cover = UrlUtil.Normalize(Str(common, "cover_left_icon"));
        return card;
    }

    /// <summary>
    /// 富文本节点(2026-11-04): desc.rich_text_nodes[]。
    ///
    /// 为什么必须读: desc.text 是**纯文本**, 话题/ @ / 表情在里面全退化成普通字,
    /// 官方客户端却是蓝色可点 + 表情变图。只有 nodes 带类型:
    ///   { type: "RICH_TEXT_NODE_TYPE_TOPIC" | "_AT" | "_TEXT" | "_WEB" | "_BV" | "_EMOJI",
    ///     text: "带 # 的原样文本", orig_text: "不带装饰", rid: "话题id/被@的mid",
    ///     jump_url: "...", emoji: { icon_url } }
    /// 节点里拿不到 emoji 图时会退回 text 的 [xx] 形式, 那也比丢掉强。
    /// </summary>
    private static List<DynamicRichNode> ReadRichNodes(JsonElement desc)
    {
        var nodes = new List<DynamicRichNode>();
        if (!desc.TryGetProperty("rich_text_nodes", out var array) || array.ValueKind != JsonValueKind.Array)
            return nodes;

        foreach (var node in array.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var text = Str(node, "text");
            var emojiUrl = "";
            if (node.TryGetProperty("emoji", out var emoji) && emoji.ValueKind == JsonValueKind.Object)
            {
                emojiUrl = Str(emoji, "icon_url");
                if (emojiUrl.Length == 0) emojiUrl = Str(emoji, "url");
            }
            if (text.Length == 0 && emojiUrl.Length == 0) continue;
            nodes.Add(new DynamicRichNode
            {
                Type = Str(node, "type"),
                Text = text,
                OrigText = Str(node, "orig_text"),
                Rid = Scalar(node, "rid"),
                JumpUrl = Str(node, "jump_url"),
                Emoji = UrlUtil.Normalize(emojiUrl),
            });
        }
        return nodes;
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
                if (danmaku <= 0) danmaku = ParseCount(Str(st, "danmaku"));
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
            item.ArticleId = Scalar(art, "id");
        }
        if (item.ArticleTitle.Length == 0) item.ArticleTitle = Str(e, "title");
        if (item.ArticleId.Length == 0) item.ArticleId = Scalar(e, "rid");
    }

    // ------------------------------------------------------------ 直播

    private static void ReadLive(JsonElement e, DynamicItem item)
    {
        if (e.TryGetProperty("modules", out var mods) && mods.TryGetProperty("module_dynamic", out var md) &&
            md.TryGetProperty("major", out var major) && major.ValueKind == JsonValueKind.Object)
        {
            if (major.TryGetProperty("live_rcmd", out var recommendation))
            {
                var content = Str(recommendation, "content");
                if (content.Length > 0)
                {
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("live_play_info", out var info))
                    {
                        item.LiveRoomId = Long(info, "room_id");
                        item.ArticleCover = UrlUtil.Normalize(Str(info, "cover"));
                        if (!item.HasText) item.Text = Str(info, "title");
                    }
                }
            }
            else if (major.TryGetProperty("live", out var live))
            {
                item.LiveRoomId = Long(live, "id");
                if (item.LiveRoomId <= 0) item.LiveRoomId = Long(live, "roomid");
                item.ArticleCover = UrlUtil.Normalize(Str(live, "cover"));
                if (!item.HasText) item.Text = Str(live, "title");
            }
        }
        if (item.LiveRoomId <= 0) item.LiveRoomId = Long(e, "rid");
    }

    // ------------------------------------------------------------ 转发

    /// <summary>
    /// 转发: 原动态在 orig 里。★ 递归解析 —— 原动态自己也可能是转发(套娃),
    /// 官方最多显示两层, 这里也限制两层, 免得一条链无限展开。
    /// </summary>
    private static void ReadForward(JsonElement e, DynamicItem item, int depth)
    {
        if (e.TryGetProperty("orig", out var orig) && orig.ValueKind == JsonValueKind.Object)
        {
            var inner = Parse(orig, depth + 1);
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

    private static string Scalar(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";

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
