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
    /// <summary>
    /// 关注 UP 的动态时间线(「动态」页的"全部动态")。
    ///
    /// 接口: <c>GET /x/polymer/web-dynamic/v1/feed/nav</c>(需 WBI 签名 + 登录态)。
    /// 动态的种类很多(视频/图文/纯文字/转发/直播开播...), 这个页面只关心**视频投稿**:
    ///   · type = DYNAMIC_TYPE_AV 且 major 里带 archive —— 直接映射成 VideoItem 走通用卡片;
    ///   · 转发(DYNAMIC_TYPE_FORWARD)里嵌的视频(orig)也算 —— 用户想看的是"UP 发了什么视频";
    ///   · 图文/纯文字/投票等没有可播对象, 跳过。
    /// 分页是游标式的: 响应里的 offset 原样带给下一页, has_more=false 就是到底了。
    ///
    /// ★ 实测(2026-10-17, 6 页真实样本):
    ///   · has_more 是 **bool**(true/false), 不是 0/1 数字 —— 按 Number 读会恒得 false;
    ///   · 扁平条目的字段只有 id_str/author/pub_time/type/rid/title/cover/jump_url/visible/fold,
    ///     **没有** stat / play / duration / length / pub_ts ⇒ 卡片上没有计数与时长可显示;
    ///   · author.mid 是**字符串**("1255510247") ⇒ 必须走 GetLongLoose;
    ///   · 6 页共 104 条里 91 条 type=8(视频)、2 条 type=64(专栏, 无可播对象, 跳过是对的),
    ///     其余为图文/转发等。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items, string? nextOffset, bool hasMore)>
        GetDynamicFeedAsync(string? offset = null)
    {
        var full = await GetDynamicFeedFullAsync(offset);
        var videos = full.items?.Where(d => d.Video != null).Select(d => d.Video!).ToList();
        return (full.ok, full.err, videos, full.nextOffset, full.hasMore);
    }

    /// <summary>
    /// 单个 UP 主的动态流(`feed/space`, 2026-11-04)。
    ///
    /// ★ 为什么必须换成这个接口: 原来单 UP 视图走的是 `GetSpaceVideosAsync`
    ///   (空间的**投稿视频**列表), 那条路**只有视频** —— 所以点开任何一个 UP,
    ///   看到的全是视频卡, 图文/转发/专栏一条都没有。用户截图里就是这个现象。
    ///   官方 PC 客户端点 UP 主看的是 TA 的**动态时间线**, 不是投稿列表。
    ///
    /// 与全部动态(feed/nav)的差别: 用 host_mid 指定对象, 分页靠 offset。
    /// 返回结构与 feed/nav 完全一致, 所以复用 DynamicParser。
    /// </summary>
    public async Task<(bool ok, string? err, List<DynamicItem>? items, string? nextOffset, bool hasMore)>
        GetSpaceDynamicsAsync(long mid, string? offset = null)
    {
        var items = new List<DynamicItem>();
        try
        {
            if (mid <= 0) return (false, "缺少 UP 主 mid", null, null, false);
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string> { ["host_mid"] = mid.ToString() };
            if (!string.IsNullOrEmpty(offset)) ps["offset"] = offset;
            // ★ 同上(feed/all 那处的说明): 不带 itemOpusStyle 的话, 新版图文动态只下发
            //   major.draw(纯图), 正文的 major.opus.summary 整块缺失 ⇒ 卡片上没有文字。
            //   单 UP 动态流走的是同一个 polymer 体系, 能力位同样适用。
            ps["features"] = "itemOpusStyle";

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space", ps,
                sign: false,
                referer: $"https://space.bilibili.com/{mid}/dynamic");
            if (code != 0 || data == null)
                return (false, code == -101 ? "请先登录后查看动态" : (msg ?? "动态加载失败"), null, null, false);

            var d = data.Value;
            var hasMore = d.TryGetProperty("has_more", out var hm) &&
                          (hm.ValueKind == JsonValueKind.True ||
                           (hm.ValueKind == JsonValueKind.Number && hm.GetInt32() == 1));
            var next = d.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.String
                ? off.GetString() : null;

            if (d.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    var item = DynamicParser.Parse(e);
                    if (item != null) items.Add(item);
                }
            }
            return (true, null, items, next, hasMore);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null, null, false);
        }
    }

    /// <summary>
    /// 拉一条动态下的热门评论(2026-11-04)。
    ///
    /// B 站 PC 客户端的动态卡下面直接挂 1~2 条热评, 不用点进去就能看到别人怎么说 ——
    /// 这是"动态流"和"视频列表"观感差异的关键。这里只取前几条, 且**失败一律静默**
    /// (评论是锦上添花, 拉不到不该让整条动态报错)。
    ///
    /// 接口: `/x/v2/reply?type=&oid=&sort=2`(sort=2 按热度), type 由动态原始类型决定:
    ///   1(转发)/ 11(视频) → 11;  2(图文)/ 4(文字)/ 17 → 17;  其它默认 11。
    /// ★ oid 用 `dynamic_id`(不是 id_str 的字符串形式), 接口要的是数字。
    /// </summary>
    internal static HttpRequestMessage BuildDynamicLikeRequest(string id,bool liked,string csrf,string cookie)
    {
        var request=new HttpRequestMessage(HttpMethod.Post,"https://api.bilibili.com/x/dynamic/feed/dyn/thumb?csrf="+Uri.EscapeDataString(csrf));
        request.Headers.TryAddWithoutValidation("Cookie",cookie);
        request.Headers.TryAddWithoutValidation("Referer","https://t.bilibili.com/");
        request.Headers.TryAddWithoutValidation("Origin","https://t.bilibili.com");
        request.Content=new StringContent(JsonSerializer.Serialize(new{dyn_id_str=id,up=liked?1:2,spmid="333.1369.0.0",from_spmid="333.999.0.0"}),Encoding.UTF8,"application/json");
        return request;
    }
    public async Task<(bool ok,string? err)> LikeDynamicAsync(string id,bool liked)
    {
        if(!long.TryParse(id,out var numeric) || numeric<=0)return(false,"动态编号无效");
        var csrf=SessionManager.Instance.Current.BiliJct;
        if(string.IsNullOrEmpty(csrf))return(false,"请先登录后点赞");
        try
        {
            var account = SessionManager.Instance.CaptureAccount();
            await EnsureWriteReadyAsync();
            using var request=BuildDynamicLikeRequest(id,liked,csrf,AppendFingerprint(SessionManager.Instance.BuildCookieHeader()));
            using var response=await SendThrottledAsync(request, expectedAccount: account);
            if (response == null) return (false, "账号已切换，操作已停止");
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var code=json.RootElement.TryGetProperty("code",out var codeNode)?codeNode.GetInt32():-999;
            return code==0?(true,null):(false,GetStr(json.RootElement,"message"));
        }
        catch(Exception ex){return(false,ex.Message);}
    }

    public async Task<List<Models.DynamicComment>?> GetDynamicCommentsAsync(string commentOid, int replyType, int count = 2)
    {
        try
        {
            if (!long.TryParse(commentOid, out var oid) || oid <= 0 || replyType <= 0) return null;
            count = Math.Clamp(count, 1, 20);
            await EnsureBuvidAsync();

            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/reply",
                new Dictionary<string, string>
                {
                    ["type"] = replyType.ToString(),
                    ["oid"] = commentOid,
                    ["sort"] = "2",            // 2 = 按热度
                    ["ps"] = count.ToString(),
                    ["pn"] = "1",
                },
                sign: false,
                referer: "https://t.bilibili.com/");

            if (code != 0 || data == null) return null;
            if (!data.Value.TryGetProperty("replies", out var list) || list.ValueKind != JsonValueKind.Array)
                return new List<Models.DynamicComment>();   // 没评论 ≠ 失败

            var result = new List<Models.DynamicComment>();
            foreach (var r in list.EnumerateArray())
            {
                if (result.Count >= count) break;
                var text = "";
                if (r.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object)
                {
                    text = c.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? "" : "";
                }
                if (text.Length == 0) continue;

                var user = r.TryGetProperty("member", out var mb) && mb.ValueKind == JsonValueKind.Object
                    ? mb : default;
                result.Add(new Models.DynamicComment
                {
                    UserName = user.ValueKind == JsonValueKind.Object ? GetStr(user, "uname") : "",
                    UserFace = user.ValueKind == JsonValueKind.Object
                        ? UrlUtil.Normalize(GetStr(user, "avatar")) : "",
                    UserMid = user.ValueKind == JsonValueKind.Object ? GetLongLoose(user, "mid") : 0,
                    Text = text,
                    LikeCount = GetLongLoose(r, "like"),
                });
            }
            return result;
        }
        catch
        {
            return null;   // 评论失败不打扰用户
        }
    }

    /// <summary>
    /// 动态混合流(2026-11-04): 返回**全部类型**的动态, 不再只挑视频。
    ///
    /// 这是"动态页内容不全"的根治点: 原实现走 ParseDynamicVideo, 图文/纯文字/专栏/转发
    /// 全部返回 null 被丢弃, 于是动态页看起来"只有视频"。现在改用 DynamicParser 解析全部类型。
    ///
    /// ★ 保留旧接口: 消息页/单 UP 视图只要视频列表, 它们仍走 GetDynamicFeedAsync。
    /// </summary>
    public async Task<(bool ok, string? err, List<DynamicItem>? items, string? nextOffset, bool hasMore)>
        GetDynamicFeedFullAsync(string? offset = null, long hostMid = 0)
    {
        var items = new List<DynamicItem>();
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>
            {
                ["type"] = "all",
                ["timezone_offset"] = "-480",
                ["platform"] = "web",
                ["web_location"] = "333.1365",
                ["features"] = DynamicFeatures
            };
            if (!string.IsNullOrEmpty(offset)) ps["offset"] = offset;
            // The official dynamic-home UP selector uses feed/all + host_mid, not
            // feed/space. This path also lets Bilibili update that UP's read state.
            if (hostMid > 0)
            {
                ps["host_mid"] = hostMid.ToString();
                if (string.IsNullOrEmpty(offset)) ps["page"] = "1";
            }

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all", ps,
                sign: false, referer: "https://t.bilibili.com/");
            if (code == -352)
            {
                (code, msg, data) = await GetJsonAsync(
                    "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all", ps,
                    sign: true, referer: "https://t.bilibili.com/");
            }
            if ((code != 0 || data == null) && hostMid > 0)
            {
                var spacePs = new Dictionary<string, string>
                {
                    ["host_mid"] = hostMid.ToString(),
                    ["timezone_offset"] = "-480",
                    ["platform"] = "web",
                    ["web_location"] = "333.999",
                    ["features"] = DynamicFeatures
                };
                if (!string.IsNullOrEmpty(offset)) spacePs["offset"] = offset;
                var spaceRes = await GetJsonAsync(
                    "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space", spacePs,
                    sign: true, referer: $"https://space.bilibili.com/{hostMid}/dynamic");
                if (spaceRes.code == 0 && spaceRes.data != null)
                {
                    code = spaceRes.code;
                    msg = spaceRes.msg;
                    data = spaceRes.data;
                }
            }
            if (code != 0 || data == null)
                return (false, code == -101 ? "请先登录后查看动态" : (msg ?? "动态加载失败"), null, null, false);

            var d = data.Value;
            // ★ has_more 实测是 bool; 但早期/其它形态见过 0/1, 两种都认(与消息页/收藏页的
            //   `ValueKind == True` 写法对齐, 那边只认 bool)。
            var hasMore = d.TryGetProperty("has_more", out var hm) &&
                          (hm.ValueKind == JsonValueKind.True ||
                           (hm.ValueKind == JsonValueKind.Number && hm.GetInt32() == 1));
            var next = d.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.String
                ? off.GetString() : null;

            if (d.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    var item = DynamicParser.Parse(e);
                    if (item != null) items.Add(item);
                }
            }
            return (true, null, items, next, hasMore);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null, null, false);
        }
    }

}
