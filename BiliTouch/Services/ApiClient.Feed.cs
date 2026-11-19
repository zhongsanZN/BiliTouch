using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliTouch.Helpers;
using BiliTouch.Models;

namespace BiliTouch.Services;

public partial class ApiClient
{
    // ---------------------------------------------------------------- 业务接口

    /// <summary>热门视频</summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetPopularAsync(int pn)
    {
        return await GetVideoListAsync(
            "https://api.bilibili.com/x/web-interface/popular",
            new Dictionary<string, string> { ["ps"] = "20", ["pn"] = pn.ToString() });
    }

    /// <summary>
    /// 分区内容(**B站现行分区页的数据源**)。原生分区页走这个。
    ///
    /// ★★★ 2026-10-17 整体重做(用户报"分区视频数据有问题, 重新做该功能")。
    ///
    ///   根因: 旧实现走 <c>ranking/v2?rid=旧tid</c>(美食=211)。实测那个组合返回的是
    ///   **约 564 天前**的陈旧榜单 —— 20 个分区里中位年龄普遍 560~580 天, 这就是
    ///   "数据有问题" 的直接来源。而 B站网页版现行分区页(/c/food/)用的是
    ///   <c>region/feed/rcmd</c> + **v2 tid**(美食=1020), 实测中位年龄只有 3~9 天,
    ///   与用户截图里的内容完全一致。
    ///
    ///   接口: <c>GET /x/web-interface/region/feed/rcmd?display_id={n}&amp;request_cnt={20}
    ///   &amp;from_region={v2tid}&amp;device=web&amp;plat=30</c>, 数据在 <c>data.archives[]</c>。
    ///
    ///   实测(2026-10-17, 带 buvid 设备指纹):
    ///     · <c>from_region</c> 只认 **v2 tid**(1001~1031); 传旧 tid(211/1/3…)一律 -400。
    ///     · <c>request_cnt</c> 上限 20: 21 起返回 62013「暂时没有更多内容了」。
    ///     · <c>display_id</c> 是**游标**, 从 1 递增即可持续翻页: 美食 25 页 = 500 条不重复,
    ///       页间重复率随深度上升(第 20 页仍有 2 条新的), 说明服务端在滚动推荐池。
    ///     · 条目字段(aid/bvid/cid/title/cover/duration/pubdate/stat{view,like,danmaku}/author{mid,name})
    ///       与 popular 的**语义**同构, 但**字段名有两处不同**: 封面是 <c>cover</c> 而不是 <c>pic</c>,
    ///       UP 主是 <c>author{}</c> 而不是 <c>owner{}</c>。
    ///       <see cref="ParseVideoItem"/> 两种都认 —— 只认前者的话分区页会"标题正常、封面全灰、
    ///       UP 主名空白"(探针 bd-probe-zone 抓着这个 bug)。
    ///
    ///   ★ 为什么要设备指纹(buvid): 不带 cookie 时, 鬼畜/vlog/小剧场 会返回
    ///     62013「暂时没有更多内容了」(空列表), 带上就正常 —— 又是一处"看起来像接口没数据、
    ///     其实是风控认不出设备"的坑。所以 <see cref="GetJsonAsync"/> 默认就带指纹。
    ///
    ///   ★ 为什么不做"热门+最新混合"了(旧实现的思路): 既然这个接口本身就是
    ///     "按分区 + 按质量 + 近期" 的推荐流, 混入就不需要了 —— 旧的混合是为了绕开
    ///     ranking 全是陈年榜单的问题, 换源之后那个问题的前提不存在了。
    /// </summary>
    /// <param name="tid">**v2 分区 id**(见 <see cref="PartitionCatalog"/>), 不是旧 tid。</param>
    /// <param name="page">游标, 从 1 开始。同一个分区连续翻页时才递增。</param>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetRegionFeedAsync(int tid, int page = 1)
    {
        try
        {
            await EnsureBuvidAsync();
            // ★ 这里**不用** GetVideoListAsync: 它把非 0 码压成一句 message, 拿不到**码**本身,
            //   而本接口恰恰需要区分 62013「暂时没有更多内容了」与真正的失败(见下)。
            //   字符串匹配 message 也能凑合, 但服务端改文案就会悄悄失效 —— 直接看码才可靠。
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/region/feed/rcmd",
                new Dictionary<string, string>
                {
                    // 接口硬上限: request_cnt >20 直接 62013。写死 20, 调用方不必关心。
                    ["display_id"] = Math.Max(1, page).ToString(),
                    ["request_cnt"] = "20",
                    ["from_region"] = tid.ToString(),
                    ["device"] = "web",
                    ["plat"] = "30"
                });

            // ★ 62013 = **正常的翻到底**, 不是错误: 翻译成"成功但空列表", 让页面能区分
            //   "到头了"与"请求失败", 而不是在列表底部弹一句吓人的报错(旧实现就是这么错的,
            //   探针 bd-probe-zone 抓着这个 bug)。
            if (code == 62013) return (true, null, new List<VideoItem>());
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            if (!data.Value.TryGetProperty("archives", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return (true, null, new List<VideoItem>());

            var items = new List<VideoItem>();
            foreach (var e in list.EnumerateArray())
            {
                var item = ParseVideoItem(e);
                if (item != null) items.Add(item);
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>分区流每页条数(接口硬上限 20, 见 <see cref="GetRegionFeedAsync"/>)。</summary>
    public const int RegionPageSize = 20;

    /// <summary>
    /// 全站排行榜(rid=0 = 全站, 与分区榜同一个接口)。
    /// </summary>
    public Task<(bool ok, string? err, List<VideoItem>? items)> GetRankingAsync() => GetRegionRankAsync(0);

    /// <summary>
    /// 排行榜。ranking/v2 的 rid 参数决定范围: 0 = 全站, 其余是**old tid**。
    ///
    /// ★ 首页「排行榜」tab 用它。注意 rid=0(全站)与分区是两套表现, 别拿分区 rid 来套:
    ///   实测(2026-10-17) 这个接口对**v2 tid**(1005 动画 / 1020 美食 / 1009 资讯) 一律 -400,
    ///   只有全站(0)与**旧 tid**(1/3/4/36/188…) 可用; 而旧 tid 那批返回的是约 564 天前的
    ///   陈年榜单 —— 分区页**不要**再走这里, 用 <see cref="GetRegionFeedAsync"/>。
    /// </summary>
    public Task<(bool ok, string? err, List<VideoItem>? items)> GetRegionRankAsync(int rid)
    {
        return GetVideoListAsync(
            "https://api.bilibili.com/x/web-interface/ranking/v2",
            new Dictionary<string, string> { ["rid"] = rid.ToString(), ["type"] = "all" },
            jsonProp: "list");
    }

    /// <summary>
    /// 首页个性化推荐(浏览器网页版「为你推荐」, wbi/index/top/feed/rcmd)。
    ///
    /// ★ 为什么只剩网页这一条路(2026-10-03 定):
    ///   以前还有个"B 站官方 App 算法"选项(app.bilibili.com/x/v2/feed/index)。
    ///   实测它对第三方客户端**不提供个性化** —— 即便拿到了有效的 access_key
    ///   (电视端扫码, myinfo 能返回真实昵称), 该接口返回的仍是全站通用热门池:
    ///   100 条里命中用户关注的 UP 只有 1~2 个、不重复 UP 达 90/100、平均播放量 66~103 万;
    ///   而网页 rcmd 的平均播放量只有 17 万、内容明显更垂直。参数层面穷举过 20 多种组合
    ///   (UA / buvid / session_id / recsys_mode / idx 游标接力 / fnval 变体)都无改善 ——
    ///   服务端按"手机端登录身份 + 手机端设备指纹"才给个性化, 第三方拿不到那个身份组合。
    ///   所以那个选项与 access_key 机制已整体删除, 只保留这条真正个性化的网页路。
    ///
    /// 失败时自动重试一次(刷新 WBI 密钥), 仍未成功则降级返回热门, 保证首页不会是空列表。
    /// </summary>
    /// <param name="reset">保留下拉刷新语义(此接口每次请求都返回新一批, 参数不影响)</param>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetRecommendAsync(bool reset = false)
    {
        _ = reset;
        try
        {
            await EnsureBuvidAsync();
            var (ok, err, items) = await GetWebRecommendCoreAsync();
            if (!ok || items == null || items.Count == 0)
            {
                // 重试一次。
                // ★ 这里**故意不**清 _buvid3/_buvid4: 设备指纹是推荐系统认人的凭据,
                //   清掉它们等于"失败一次就换一台新设备重来", 会让推荐模型重新冷启动
                //   —— 与"让推荐更准"正相反。真正需要刷新的是 WBI 密钥(会过期)。
                _wbiKeys = null;
                await EnsureBuvidAsync();
                (ok, err, items) = await GetWebRecommendCoreAsync();
            }
            if (ok && items != null && items.Count > 0) return (true, null, items);
            // 失败 -> 降级热门
            return await GetPopularAsync(1);
        }
        catch
        {
            return await GetPopularAsync(1);
        }
    }

    private async Task<(bool ok, string? err, List<VideoItem>? items)> GetWebRecommendCoreAsync()
    {
        try
        {
            // 现行 web 首页推荐接口(top feed), 需要 WBI 签名
            var ps = new Dictionary<string, string>
            {
                ["fresh_type"] = "4",
                ["ps"] = "30",
                ["fresh_idx"] = "1",
                ["fresh_idx_1h"] = "1",
                ["brush"] = "1",
                ["web_location"] = "1430650"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/wbi/index/top/feed/rcmd",
                ps, sign: true);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("item", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    // 过滤直播/边栏等非视频项
                    if (GetStr(e, "goto") != "av") continue;
                    var item = ParseRecommendItem(e);
                    if (item != null) items.Add(item);
                }
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }


    private static VideoItem? ParseRecommendItem(JsonElement e)
    {
        try
        {
            var item = new VideoItem
            {
                Bvid = GetStr(e, "bvid"),
                Title = GetStr(e, "title").StripHtml(),
                Cover = UrlUtil.Normalize(GetStr(e, "pic")),
                Author = e.TryGetProperty("owner", out var o) ? GetStr(o, "name") : "",
                OwnerMid = e.TryGetProperty("owner", out var om) ? GetLong(om, "mid") : 0,
                Duration = VideoItem.FormatSeconds(GetInt(e, "duration")),
                Pubdate = GetLong(e, "pubdate"),
            };
            if (string.IsNullOrEmpty(item.Bvid)) return null;
            if (e.TryGetProperty("stat", out var st))
            {
                if (st.TryGetProperty("view", out var v) && v.ValueKind == JsonValueKind.Number)
                    item.ViewCount = v.GetInt64();
                if (st.TryGetProperty("danmaku", out var d) && d.ValueKind == JsonValueKind.Number)
                    item.DanmakuCount = d.GetInt64();
                if (st.TryGetProperty("like", out var lk) && lk.ValueKind == JsonValueKind.Number)
                    item.LikeCount = lk.GetInt64();
            }
            return item;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool ok, string? err, List<VideoItem>? items)> GetVideoListAsync(
        string url, Dictionary<string, string> ps, string jsonProp = "list")
    {
        try
        {
            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(url, ps);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            if (!data.Value.TryGetProperty(jsonProp, out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return (true, null, new List<VideoItem>());

            var items = new List<VideoItem>();
            foreach (var e in list.EnumerateArray())
            {
                var item = ParseVideoItem(e);
                if (item != null) items.Add(item);
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    private static VideoItem? ParseVideoItem(JsonElement e)
    {
        try
        {
            var item = new VideoItem();
            item.Bvid = GetStr(e, "bvid");
            if (string.IsNullOrEmpty(item.Bvid)) return null;
            item.Title = GetStr(e, "title").StripHtml();
            // ★★ 封面字段名两个接口不一致(2026-10-17 修 "分区页卡片全灰、UP 主名空白"):
            //   popular/ranking/newlist/related 用 `pic` + `owner{mid,name}`,
            //   而 region/feed/rcmd(分区流) 用 `cover` + `author{mid,name}`。
            //   以前只认前者, 于是分区页拿到了标题和时间、封面却是空的 —— 卡片一片灰。
            //   两种都认, 谁在就用谁(实测没有哪个接口两个字段同时给且含义不同)。
            item.Cover = UrlUtil.Normalize(GetStr(e, "pic"));
            if (item.Cover.Length == 0) item.Cover = UrlUtil.Normalize(GetStr(e, "cover"));

            // UP 主名 + mid: 卡片下方的 UP 主名要能点进他的主页。
            // ★ 三种下发写法都要认(实测 2026-10-15):
            //   · 热门/排行/收藏: owner{ name, mid } 嵌一层;
            //   · 分区流(region/feed/rcmd): author{ name, mid } 嵌一层;
            //   · 空间投稿(arc/search 的 vlist): author + mid **平铺**, 没有 owner 这一层 ——
            //     只读 owner 的话这里的 Author 会一直是空串, 卡片上就只剩一个孤零零的"·"。
            // ★ mid 一律走 GetLongLoose: 动态接口实测把 mid 当字符串下发("1255510247"),
            //   这里用 GetLong 会静默读成 0(见 GetLongLoose 的说明)。
            var owner = e.TryGetProperty("owner", out var o) ? o
                      : e.TryGetProperty("author", out var a) ? a
                      : default;
            if (owner.ValueKind == JsonValueKind.Object)
            {
                item.Author = GetStr(owner, "name");
                item.OwnerMid = GetLongLoose(owner, "mid");
            }
            if (string.IsNullOrEmpty(item.Author)) item.Author = GetStr(e, "author");
            if (item.OwnerMid <= 0) item.OwnerMid = GetLongLoose(e, "mid");
            // 风控降级接口(recArchivesByKeywords)用的是 upMid, 两者都认
            if (item.OwnerMid <= 0) item.OwnerMid = GetLongLoose(e, "upMid");

            // 时长: `duration` 的单位是**秒**; 空间投稿给的是 "03:57" 这种成品文本(`length`)。
            // ★ 读不到时**留空串**而不是 FormatSeconds(0) 的 "--:--": 空串由界面隐藏角标,
            //   "--:--" 是一个看起来像数据的假值(空间页每张卡都挂这个)。
            item.Duration = GetInt(e, "duration") > 0
                ? VideoItem.FormatSeconds(GetInt(e, "duration"))
                : GetStr(e, "length");

            if (e.TryGetProperty("stat", out var st))
            {
                if (st.TryGetProperty("view", out var v) && v.ValueKind == JsonValueKind.Number)
                    item.ViewCount = v.GetInt64();
                // 弹幕数/点赞数: 封面左下角的角标与文字区的"N点赞"都靠它们
                if (st.TryGetProperty("danmaku", out var dm) && dm.ValueKind == JsonValueKind.Number)
                    item.DanmakuCount = dm.GetInt64();
                if (st.TryGetProperty("like", out var lk) && lk.ValueKind == JsonValueKind.Number)
                    item.LikeCount = lk.GetInt64();
            }
            // 空间投稿把计数平铺在条目上(play / video_review), 不套 stat 那一层
            if (item.ViewCount <= 0) item.ViewCount = GetLong(e, "play");
            if (item.DanmakuCount <= 0) item.DanmakuCount = GetLong(e, "video_review");

            // 发布时间: 热门/收藏用 pubdate, 空间投稿用 created
            item.Pubdate = GetLong(e, "pubdate");
            if (item.Pubdate <= 0) item.Pubdate = GetLong(e, "created");
            return item;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 视频详情。
    ///
    /// ★ 2026-10-01 由裸 <c>Task&lt;VideoDetail?&gt;</c> 改为 (ok, err, data):
    ///   原来只有"拿到"和"没拿到"两种结果, 调用方分不清"接口报错"与"这个 BV 不存在",
    ///   失败时只能默默给一个通用提示。现在把原因带出来。
    /// </summary>
    public async Task<(bool ok, string? err, VideoDetail? data)> GetVideoAsync(string bvid)
    {
        try
        {
            // 和其它业务接口一样先拿到设备指纹: 实测这个接口在**完全没有 cookie** 时会被
            // 风控拦下(返回非 0 码), 表现是"详情拿不到 → 播放器空转"。
            // 已登录时 cookie 里本来就有 buvid3, 这一步也是无副作用的空操作。
            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/view",
                new Dictionary<string, string> { ["bvid"] = bvid });
            if (code != 0 || data == null)
                return (false, msg ?? $"获取视频信息失败 (code {code})", null);

            var d = data.Value;
            var detail = new VideoDetail
            {
                Bvid = bvid,
                Title = GetStr(d, "title"),
                Pic = UrlUtil.Normalize(GetStr(d, "pic")),
                Desc = GetStr(d, "desc"),
                DurationSec = GetInt(d, "duration")
            };
            if (d.TryGetProperty("owner", out var o))
            {
                detail.Owner = GetStr(o, "name");
                detail.OwnerMid = GetLong(o, "mid");
                detail.OwnerFace = UrlUtil.Normalize(GetStr(o, "face"));
            }
            if (d.TryGetProperty("stat", out var st))
            {
                detail.ViewCount = GetLong(st, "view");
                detail.LikeCount = GetLong(st, "like");
                detail.DanmakuCount = GetLong(st, "danmaku");
                // 操作栏要显示计数(2026-09-26 改成"图标 + 数量"那套)
                detail.CoinCount = GetLong(st, "coin");
                detail.FavoriteCount = GetLong(st, "favorite");
                detail.ShareCount = GetLong(st, "share");
            }
            // ★★ 这里以前写的是 `GetInt(d, "coin") == 1`, 而 view 接口**没有**这个字段
            //   (data.stat.coin 是全站投币总数, 不是本人的状态) ⇒ 恒为 false,
            //   于是"投过币、重开视频后投币键还是灰的"(2026-10-13)。
            //   真实状态改由 <see cref="GetCoinCountAsync"/> 单独查询, 调用方在拿到 aid 后拉一次。
            detail.Aid = GetLong(d, "aid");
            detail.Pubdate = GetLong(d, "pubdate");

            // 多分P: 全部解析, 不只取第一页。
            //
            // ★ 以前这里只取 pages[0].cid 用于播放, 于是"克拉克森的农场 1-5季"
            //   (BV1JMbw62E72, 单稿件 40 个 P) 只能看第 1 P, 界面上又没有合集入口可点,
            //   用户那边表现为"这个视频无法选集"(2026-10-11)。分P 列表跟合集一样
            //   是 view 接口**顺带**给的, 解析它零额外请求 —— 没有理由不收。
            //
            // ★ duration 的单位是**秒**(实测 P1 = 5395 ≈ 90 分钟正片)。这跟番剧分集的
            //   duration(毫秒)正好相反, 见 ParseBangumiDetail 的对照说明。
            if (d.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array
                && pages.GetArrayLength() > 0)
            {
                foreach (var p in pages.EnumerateArray())
                {
                    var cid = GetLong(p, "cid");
                    if (cid == 0) continue;   // 脏数据跳过, 别造出一个换不过去的空 P
                    detail.Pages.Add(new VideoPage
                    {
                        Cid = cid,
                        // page 字段可能缺失(极少), 这时用数组下标 +1 兜底 —— 序号从 1 起是硬约定
                        Page = GetInt(p, "page") > 0 ? GetInt(p, "page") : detail.Pages.Count + 1,
                        Part = p.TryGetProperty("part", out var pt) && pt.ValueKind == JsonValueKind.String
                            ? pt.GetString() ?? "" : "",
                        DurationSec = GetInt(p, "duration"),
                    });
                }
                detail.Cid = detail.Pages[0].Cid;
            }
            if (detail.Cid == 0) detail.Cid = GetLong(d, "cid");

            // 合集: view 接口顺带就给了 ugc_season, 解析它**零额外请求** ——
            // 播放器要在"简介"旁边常驻一个合集入口, 不能为它再挂一次 network round-trip。
            // ★ 不在合集里的视频**根本没有 ugc_season 这个键**(不是 null), 所以判存在性。
            if (d.TryGetProperty("ugc_season", out var ugc) &&
                ugc.ValueKind == JsonValueKind.Object)
            {
                detail.Season = ParseSeason(ugc);
            }

            return (true, null, detail);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取视频信息失败: " + ex.Message, null);
        }
    }

    // ---------------------------------------------------------------- 视频合集

    /// <summary>
    /// 视频合集(ugc_season)。单独取一次合集信息时用它; 播放器的主路径是直接吃
    /// <see cref="GetVideoAsync"/> 顺带解析出来的 <see cref="VideoDetail.Season"/>, 不走这里。
    ///
    /// ★ 为什么要单独留一个方法: 超大合集可能被 view 接口截断(自报 ep_count 大于实际给的条数),
    ///   这时才需要走 seasons_archives_list 翻页补齐 —— 补页逻辑收在这里, 调用方只感知成败。
    /// </summary>
    public async Task<(bool ok, string? err, SeasonInfo? data)> GetSeasonAsync(string bvid)
    {
        try
        {
            var (detailOk, detailErr, detail) = await GetVideoAsync(bvid);
            if (!detailOk || detail == null) return (false, detailErr, null);
            if (detail.Season == null) return (false, "这个视频不属于任何合集", null);

            var season = detail.Season;
            // 截断才补页: 正常情况下(实测 118 集 / 254 集都是全量)这里不会触发。
            if (season.EpCount > 0 && season.EpCount > season.Episodes.Count)
            {
                await CompleteSeasonFromArchivesAsync(season);
                // 补进来的条目还没有序号/分组标题标记: 整个展平序重编一遍
                // (newly added 的落在尾部, 重编不会打乱已有条目的相对顺序)
                NumberEpisodes(season);
            }

            return (true, null, season);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取合集失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 把 ugc_season 解析成 <see cref="SeasonInfo"/>。
    ///
    /// ★ 顺序即官方顺序: sections 依次拼接展平后的序列, 与官方网页播放器默认的
    ///   `sort_reverse=false` **逐 aid 完全一致**(2026-10-02 用 118 集合集实测 118/118),
    ///   所以列表直接用这个展平序, 不需要再按发布时间排 —— 它本来也不按时间排。
    /// </summary>
    private static SeasonInfo ParseSeason(JsonElement ugc)
    {
        var season = new SeasonInfo
        {
            SeasonId = GetLong(ugc, "id"),
            Title = GetStr(ugc, "title").StripHtml(),
            // 合集封面本身就是 https, 但也过一道 Normalize —— 下一行 episodes 的 arc.pic
            // 实测是 http://, 统一在这里规范化省得 UI 层再各写一遍。
            Cover = UrlUtil.Normalize(GetStr(ugc, "cover")),
            Mid = GetLong(ugc, "mid"),
            Intro = GetStr(ugc, "intro"),
            EpCount = GetInt(ugc, "ep_count")
        };

        if (!ugc.TryGetProperty("sections", out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
            return season;

        foreach (var sec in sections.EnumerateArray())
        {
            if (sec.ValueKind != JsonValueKind.Object) continue;
            var section = new SeasonSection
            {
                Id = GetLong(sec, "id"),
                Title = GetStr(sec, "title").StripHtml()
            };

            if (sec.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in eps.EnumerateArray())
                {
                    if (ep.ValueKind != JsonValueKind.Object) continue;
                    var parsed = ParseSeasonEpisode(ep, section.Id, section.Title);
                    if (parsed == null) continue;
                    section.Episodes.Add(parsed);
                    season.Episodes.Add(parsed);
                }
            }

            // 空分组不放进 Sections: 界面上一个只有标题、底下什么都没有的分组是纯噪音
            if (section.Episodes.Count > 0) season.Sections.Add(section);
        }

        NumberEpisodes(season);
        return season;
    }

    private static SeasonEpisode? ParseSeasonEpisode(JsonElement ep, long sectionId, string sectionTitle)
    {
        // episode 自己的 bvid 是权威播放键; 拿不到就整条丢掉(点不动的一行不如不显示)
        var bvid = GetStr(ep, "bvid");
        if (string.IsNullOrEmpty(bvid)) return null;

        var item = new SeasonEpisode
        {
            Bvid = bvid,
            // ★ aid 必须 long: 实测 117367564666453 已远超 int 上限
            Aid = GetLong(ep, "aid"),
            Cid = GetLong(ep, "cid"),
            Title = GetStr(ep, "title").StripHtml(),
            SectionId = sectionId,
            SectionTitle = sectionTitle
        };

        // arc 是"这条稿件"的摘要: 封面/时长/播放量/发布时间全在它里面
        if (ep.TryGetProperty("arc", out var arc) && arc.ValueKind == JsonValueKind.Object)
        {
            // 封面/时长以 arc 为准; title 优先用 episode 自己的(它是合集语境下的标题)
            item.Cover = UrlUtil.Normalize(GetStr(arc, "pic"));
            item.DurationSec = GetInt(arc, "duration");
            item.Pubdate = GetLong(arc, "pubdate");
            if (item.Title.Length == 0) item.Title = GetStr(arc, "title").StripHtml();
            if (item.Aid == 0) item.Aid = GetLong(arc, "aid");
            if (arc.TryGetProperty("stat", out var st) && st.ValueKind == JsonValueKind.Object)
                item.ViewCount = GetLong(st, "view");
        }
        // arc 缺了就从 page 兜底(实测两者 cid/duration 一致, 只是字段可能各自缺)
        if (ep.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object)
        {
            if (item.Cid == 0) item.Cid = GetLong(page, "cid");
            if (item.DurationSec == 0) item.DurationSec = GetInt(page, "duration");
        }

        return item;
    }

    /// <summary>
    /// 相关视频推荐(播放器信息栏简介下方那一列, 2026-10-12 新增)。
    ///
    /// 接口: <c>/x/web-interface/archive/related</c>(B 站播放器右侧「相关推荐」同源)。
    /// 已由 .probes/bd-probe-related 实测核对过响应形状, 有两点与直觉相反:
    ///
    /// ★ `data` **直接就是数组**, 不是 `{ list: [...] }`。
    ///   照着热门那套去取 `data.list` 会静默拿到"0 条"—— 请求成功、页面却永远空着,
    ///   是一类极难往接口头上想的失败。所以两种形状都认。
    /// ★ 条目字段与 popular **完全同构**(owner{mid,name,face} / stat{view,danmaku,...} /
    ///   top-level bvid/pic/title/duration/pubdate), 因此直接复用
    ///   <see cref="ParseVideoItem"/>, 不必再写一份解析器 —— 写两份必然漂移。
    ///   duration 单位是秒(实测 133 / 134), 与 popular 一致; 番剧 PGC 那套是毫秒, 别混。
    ///
    /// ★★ 2026-10-13 用户报"相关视频只有 8 个, 添加更多视频, 往下滑刷新":
    ///   实测(探针 bd-probe-coin, 逐组 ps/pn 对照):
    ///     · ps=12 / 40 / 50 **返回的条数完全一样, 都是 40** —— 服务端忽略了 ps,
    ///       一次就把 40 条全给;
    ///     · pn=1 与 pn=2 拿到的也是同一批 40 条 ⇒ **翻页没有意义**,
    ///       拉第二页只会得到重复内容(自己跟自己重复, 比不拉更难查)。
    ///   所以"更多"的正解是: 一次拿全 40 条, 由界面**分批展示**(先 8 条,
    ///   滚到附近再补下一批), 而不是"滚到底再请求一次"。
    ///
    /// 需要设备指纹: 与 view 一样, 无 cookie 时会被风控拦下(返回非 0 码)。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetRelatedAsync(string bvid)
    {
        try
        {
            if (string.IsNullOrEmpty(bvid)) return (false, "缺少视频 BV 号", null);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/archive/related",
                // ps 写 40 只是"表明意图": 实测服务端恒返 40 条, 这个值不影响结果(见上面那段)
                new Dictionary<string, string> { ["bvid"] = bvid, ["pn"] = "1", ["ps"] = "40" });
            if (code != 0 || data == null)
                return (false, msg ?? $"获取相关视频失败 (code {code})", null);

            // data 可能直接是数组, 也可能是 { list: [...] }; 两种都认, 见上面的说明
            JsonElement arr;
            var root = data.Value;
            if (root.ValueKind == JsonValueKind.Array)
            {
                arr = root;
            }
            else if (root.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                arr = l;
            }
            else
            {
                return (true, null, new List<VideoItem>());   // 形状不认识: 空列表而不是崩
            }

            var items = new List<VideoItem>();
            foreach (var e in arr.EnumerateArray())
            {
                var item = ParseVideoItem(e);
                if (item == null) continue;
                // ★ 自身可能混在结果里(实测会), 点它等于原地重播 —— 先挡一道,
                //   免得列表末尾莫名出现"当前视频"且点了没反应。
                if (item.Bvid == bvid) continue;
                items.Add(item);
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取相关视频失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 给展平后的每一集补上"第几集"与"是不是分组第一集"。
    /// 单独一步是因为这两件事依赖**跨分组的累计**, 逐条解析时算不出来。
    ///
    /// 按 <see cref="SeasonInfo.Episodes"/> 的展平顺序编号(而不是按 Sections 再遍历一遍):
    /// 兜底补页追加进来的条目只落在展平序列的尾部, 两个序列的顺序在那种情况下并不一致,
    /// 以展平序为准才不会出现"第 119 集排在第 100 集前面"。
    /// </summary>
    private static void NumberEpisodes(SeasonInfo season)
    {
        var started = new HashSet<long>();
        var index = 0;
        // 单分组时不画分组标题(官方在只有"正片"一组时也不显示组名)。
        // 在这里就定下初值: 界面灌数据之前必须先有确定值(条目是普通 POCO, 没有变更通知)。
        var showHeader = season.Sections.Count > 1;
        foreach (var ep in season.Episodes)
        {
            ep.Index = ++index;
            ep.IsSectionStart = started.Add(ep.SectionId);
            ep.ShowSectionHeader = showHeader && ep.IsSectionStart;
        }
    }

    /// <summary>
    /// 超大合集的兜底补齐: 走 <c>polymer/web-space/seasons_archives_list</c> 按页取。
    ///
    /// 只有 view 接口给不全(ep_count > 实际条数)时才会被调用, 正常路径永远不触发。
    /// 补进来的条目**排在原有条目之后**: 原有那批来自 view, 顺序已与官方一致, 不能打乱;
    /// 缺的那些按接口返回的页序追加即可。
    ///
    /// ★★ 终止条件必须是"这一页没有 archives", **不能靠错误码** ——
    ///   实测越界页照样回 code=0 + 空数组, 靠错误码判断会死循环。
    /// ★ mid 会被服务端忽略(给错的 mid 也照样返回全部), 所以只用它做参数, 不做校验。
    /// ★ page_size 上限: 30/50/100 可用, 200 回 -400。这里保守取 30。
    /// </summary>
    private async Task CompleteSeasonFromArchivesAsync(SeasonInfo season)
    {
        // 卫语句: 没有 season_id/mid 就没法翻页; 已经是全量也不用翻
        if (season.SeasonId <= 0 || season.Mid <= 0) return;

        var seen = new HashSet<string>(season.Episodes.Select(e => e.Bvid), StringComparer.Ordinal);
        // 上限只是防"服务端永远不返回空页"这种病态情况, 30 页 × 30 条 = 900 集, 远超实际合集规模
        const int maxPages = 30;
        const int pageSize = 30;

        for (var pageNum = 1; pageNum <= maxPages; pageNum++)
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-space/seasons_archives_list",
                new Dictionary<string, string>
                {
                    ["mid"] = season.Mid.ToString(),
                    ["season_id"] = season.SeasonId.ToString(),
                    // false = 官方网页播放器的默认顺序(与 view 的展平序一致)
                    ["sort_reverse"] = "false",
                    ["page_num"] = pageNum.ToString(),
                    ["page_size"] = pageSize.ToString()
                },
                referer: "https://space.bilibili.com/" + season.Mid);

            if (code != 0 || data == null)
                throw new InvalidOperationException(
                    $"合集分页补齐失败 (code {code}{(string.IsNullOrEmpty(msg) ? "" : ": " + msg)})");
            if (!data.Value.TryGetProperty("archives", out var archives) ||
                archives.ValueKind != JsonValueKind.Array)
                return;

            // ★ 越界页就是"空数组 + code 0", 靠它收口
            var count = archives.GetArrayLength();
            if (count == 0) return;

            foreach (var a in archives.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var bvid = GetStr(a, "bvid");
                if (string.IsNullOrEmpty(bvid)) continue;
                if (!seen.Add(bvid)) continue;   // 原有条目已经带过, 不重复追加

                var ep = new SeasonEpisode
                {
                    Bvid = bvid,
                    Aid = GetLong(a, "aid"),
                    Title = GetStr(a, "title").StripHtml(),
                    Cover = UrlUtil.Normalize(GetStr(a, "pic")),
                    DurationSec = GetInt(a, "duration"),
                    Pubdate = GetLong(a, "pubdate")
                };
                if (a.TryGetProperty("stat", out var st) && st.ValueKind == JsonValueKind.Object)
                    ep.ViewCount = GetLong(st, "view");

                // 补进来的条目没有分组归属(接口不给): 挂到第一个分组名下, 免得界面上
                // 它们落在所有分组标题之外、看起来像"无家可归"的一堆。
                var host = season.Sections.Count > 0 ? season.Sections[0] : null;
                if (host != null)
                {
                    ep.SectionId = host.Id;
                    ep.SectionTitle = host.Title;
                    host.Episodes.Add(ep);
                }
                season.Episodes.Add(ep);
            }
        }
    }

    // ---------------------------------------------------------------- 番剧 / 追番(PGC)
    //
    // ★ 这三个接口与项目里其余接口有一处关键差别: **必须是 www.bilibili.com 的 Referer**。
    //   PGC 是网页版播放器的后端, 对来源校验比 UGC 那套严; Referer 给成别的域会被拒。
    //   (GetJsonAsync 的默认 Referer 正好就是 www.bilibili.com, 但这里显式写出来 ——
    //    免得以后有人改全局默认值时顺手把这一处带崩。)

    /// <summary>
    /// 某个用户关注的番剧("我的追番")。
    ///
    /// 接口: /x/space/bangumi/follow/list —— type: 1 追番 / 2 追剧(电视剧、纪录片)。
    ///
    /// ★ 为什么不是网上常见的 /x/v2/follow/seasons 或 /pgc/season/follow/list:
    ///   那两条 2026-10-11 实测**已经 404**(带登录 Cookie 也一样), 这个才是活的。
    ///   候选是逐个探测出来的, 别再改回去。
    ///
    /// ★ 需要登录: 匿名访问私有设置的用户会拿到 53013。调用方应先判 <c>SessionManager.HasLogin</c>。
    /// </summary>
    public async Task<(bool ok, string? err, List<BangumiItem>? items)>
        GetMyBangumiAsync(long vmid, int pn = 1, int ps = 30)
    {
        if (vmid <= 0) return (false, "用户 ID 无效", null);
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/space/bangumi/follow/list",
                new Dictionary<string, string>
                {
                    ["vmid"] = vmid.ToString(),
                    ["pn"] = Math.Max(1, pn).ToString(),
                    ["ps"] = Math.Clamp(ps, 1, 100).ToString(),
                    ["type"] = "1" // 只要番剧; 追剧(影视)不在追番页里
                },
                referer: "https://www.bilibili.com/");

            if (code != 0 || data == null) return (false, msg ?? $"加载追番失败({code})", null);
            if (!data.Value.TryGetProperty("list", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return (true, null, new List<BangumiItem>());

            var items = new List<BangumiItem>();
            foreach (var el in list.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var item = ParseBangumiItem(el);
                if (item != null) items.Add(item);
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "加载追番失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 番剧推荐 / 国创推荐(首页追番页那两栏)。
    ///
    /// 接口: /pgc/season/index/result —— 返回热门番剧列表, 不需要登录。
    ///
    /// ★★ 接口的 <c>season_type</c> 参数**实测不生效**: season_type=1 与 =4 返回的是同一批
    ///   (2026-10-11 实测两边 list 完全一致)。真正的分类在**返回项自己的 season_type 字段**里。
    ///   所以这里只请求一次, 由调用方按 <see cref="BangumiItem.SeasonType"/> 分栏 ——
    ///   分两次请求不但没效果, 还会让两栏内容重复。
    /// </summary>
    public async Task<(bool ok, string? err, List<BangumiItem>? items, bool hasNext)>
        GetBangumiRecommendAsync(int pn = 1, int ps = 25)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/pgc/season/index/result",
                new Dictionary<string, string>
                {
                    ["season_type"] = "1",
                    ["type"] = "0", // 0 = 综合热门(按 type 细分的那套分类不要)
                    ["page"] = Math.Max(1, pn).ToString(),
                    ["ps"] = Math.Clamp(ps, 1, 50).ToString()
                },
                referer: "https://www.bilibili.com/");

            if (code != 0 || data == null) return (false, msg ?? $"加载番剧推荐失败({code})", null, false);
            if (!data.Value.TryGetProperty("list", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return (true, null, new List<BangumiItem>(), false);

            var items = new List<BangumiItem>();
            foreach (var el in list.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var item = ParseBangumiItem(el);
                if (item != null) items.Add(item);
            }

            // has_next 缺省时按"这一页是不是读满了"兜底 —— 两个信号都拿不到就只能认为到底了,
            // 那只会少翻一页, 比无限翻下去安全。
            var hasNext = data.Value.TryGetProperty("has_next", out var hn)
                && hn.ValueKind == JsonValueKind.Number
                ? hn.GetInt32() == 1
                : items.Count >= Math.Clamp(ps, 1, 50);

            return (true, null, items, hasNext);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "加载番剧推荐失败: " + ex.Message, null, false);
        }
    }

    /// <summary>
    /// 番剧详情 + **选集列表**。
    ///
    /// 接口: /pgc/view/web/season —— 不需要登录(能拿到番剧的基本信息与分集表)。
    ///
    /// ★ 选集取的是**顶层 episodes**, 不是 section[].episodes:
    ///   顶层是"正片"列表, 顺序就是官方播放顺序; 而 section[] 里混着预告 / 特别花絮 / UP 杂谈,
    ///   凡人修仙传实测顶层 307 集、section 各项加起来却是 1130 集 ——
    ///   把它们串起来当"选集"会让用户从第 307 话一路翻到花絮区去。
    /// </summary>
    public async Task<(bool ok, string? err, BangumiDetail? data)>
        GetBangumiDetailAsync(long seasonId)
    {
        if (seasonId <= 0) return (false, "番剧 ID 无效", null);
        try
        {
            // ★ payloadKey: "result" —— 这个接口把内容放在根级 result 下, 没有 data。
            //   照常取 data 会拿到 null, 而 code 是 0、message 是 success(接口完全正常)。
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/pgc/view/web/season",
                new Dictionary<string, string> { ["season_id"] = seasonId.ToString() },
                referer: "https://www.bilibili.com/",
                payloadKey: "result");

            if (code != 0 || data == null) return (false, msg ?? $"加载番剧详情失败({code})", null);

            var res = data.Value;

            var detail = new BangumiDetail
            {
                SeasonId = seasonId,
                Title = GetStr(res, "title").StripHtml(),
                Cover = UrlUtil.Normalize(GetStr(res, "cover")),
                Subtitle = GetStr(res, "subtitle").StripHtml(),
                Evaluate = GetStr(res, "evaluate").StripHtml(),
                Renewal = GetStr(res, "renewal_time")
            };

            // 分类名在 show_season_type / type 上, 优先取能拿到名字的那个
            detail.SeasonTypeName = GetStr(res, "season_type_name").StripHtml();
            if (string.IsNullOrEmpty(detail.SeasonTypeName))
            {
                var st = TryObject(res, "show_season_type");
                detail.SeasonTypeName = st is { } s ? GetStr(s, "name").StripHtml() : "";
            }

            if (TryObject(res, "rating") is { } rating)
            {
                detail.Score = GetStr(rating, "score");
                detail.ScoreCount = GetLong(rating, "count");
            }
            if (TryObject(res, "stat") is { } stat)
                detail.FollowCount = GetLong(stat, "follow");

            if (res.TryGetProperty("areas", out var areas) && areas.ValueKind == JsonValueKind.Array)
            {
                var names = areas.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.Object)
                    .Select(a => GetStr(a, "name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();
                detail.Area = string.Join(" / ", names);
            }

            if (res.TryGetProperty("styles", out var styles) && styles.ValueKind == JsonValueKind.Array)
            {
                var names = styles.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.Object)
                    .Select(a => GetStr(a, "name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();
                detail.Styles = string.Join(" / ", names);
            }

            if (TryObject(res, "publish") is { } pub)
                detail.PublishDate = GetStr(pub, "pub_time_show");

            if (res.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in eps.EnumerateArray())
                {
                    if (ep.ValueKind != JsonValueKind.Object) continue;
                    var parsed = ParseBangumiEpisode(ep);
                    // 没有 bvid 的条目播不了(预告条目常见), 直接丢掉而不是留一个点不动的行
                    if (parsed == null || string.IsNullOrEmpty(parsed.Bvid)) continue;
                    detail.Episodes.Add(parsed);
                }
            }

            return (true, null, detail);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "加载番剧详情失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 解析一个番剧条目。
    ///
    /// 两个接口(追番 / 推荐)字段基本同名但**都不完整**, 各缺一块, 所以逐个兜底:
    ///   追番接口有 progress / subtitle / season_type_name, 但没有 score、index_show 藏在 new_ep;
    ///   推荐接口有 score / index_show, 但没有 progress。
    /// </summary>
    private static BangumiItem? ParseBangumiItem(JsonElement el)
    {
        var seasonId = GetLong(el, "season_id");
        if (seasonId <= 0) return null;

        var item = new BangumiItem
        {
            SeasonId = seasonId,
            MediaId = GetLong(el, "media_id"),
            Title = GetStr(el, "title").StripHtml(),
            Cover = UrlUtil.Normalize(GetStr(el, "cover")),
            Subtitle = GetStr(el, "subtitle").StripHtml(),
            SeasonType = GetInt(el, "season_type"),
            SeasonTypeName = GetStr(el, "season_type_name").StripHtml(),
            Badge = GetStr(el, "badge").StripHtml(),
            IsFinish = GetInt(el, "is_finish") == 1,
            Progress = GetStr(el, "progress").StripHtml(),
            Url = GetStr(el, "url")
        };

        // total_count 缺失时退回 new_ep 的集号(番外/剧场版给的是 0)
        item.TotalCount = GetInt(el, "total_count");
        if (item.TotalCount <= 0) item.TotalCount = GetInt(el, "formal_ep_count");
        // ★ 搜索接口(media_bangumi)把总集数叫 ep_size, 三个名字轮着来, 都试一遍
        if (item.TotalCount <= 0) item.TotalCount = GetInt(el, "ep_size");

        // 更新进度: 推荐接口直接给 index_show; 追番接口把它放在 new_ep 里
        item.IndexShow = GetStr(el, "index_show").StripHtml();
        if (string.IsNullOrEmpty(item.IndexShow) && TryObject(el, "new_ep") is { } nep)
            item.IndexShow = GetStr(nep, "index_show").StripHtml();

        if (string.IsNullOrEmpty(item.IndexShow) && item.TotalCount > 0)
            item.IndexShow = "全" + item.TotalCount + "话";

        // 角标底色: badge_info.bg_color(追番)/ badge_info.bg_color_night
        if (TryObject(el, "badge_info") is { } bi)
        {
            item.BadgeColor = GetStr(bi, "bg_color");
            if (string.IsNullOrEmpty(item.BadgeColor))
                item.BadgeColor = GetStr(bi, "bg_color_night");
            // 有些条目 badge 为空但 badge_info 里带着文字
            if (string.IsNullOrEmpty(item.Badge))
                item.Badge = GetStr(bi, "text").StripHtml();
        }

        if (TryObject(el, "stat") is { } st)
            item.FollowCount = GetLong(st, "follow");

        if (string.IsNullOrEmpty(item.Url))
            item.Url = $"https://www.bilibili.com/bangumi/play/ss{seasonId}";

        return item;
    }

    /// <summary>解析一集。注意 duration 是**毫秒**, 与 UGC 那边(秒)不一样。</summary>
    private static BangumiEpisode? ParseBangumiEpisode(JsonElement ep)
    {
        var epId = GetLong(ep, "ep_id");
        if (epId <= 0) epId = GetLong(ep, "id");
        if (epId <= 0) return null;

        var durationMs = GetLong(ep, "duration");
        return new BangumiEpisode
        {
            EpId = epId,
            Aid = GetLong(ep, "aid"),
            Cid = GetLong(ep, "cid"),
            Bvid = GetStr(ep, "bvid"),
            Title = GetStr(ep, "title"),
            LongTitle = GetStr(ep, "long_title").StripHtml(),
            Cover = UrlUtil.Normalize(GetStr(ep, "cover")),
            // 毫秒 → 秒, 四舍五入到整秒(播放器与卡片都按整秒显示)
            DurationSec = (int)Math.Round(durationMs / 1000.0),
            PubTime = GetLong(ep, "pub_time"),
            // status: 2/13 = 已播; 0 = 预告未播
            IsAired = GetInt(ep, "status") is 2 or 13
        };
    }

}
