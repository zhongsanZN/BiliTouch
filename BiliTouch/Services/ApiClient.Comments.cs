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
    /// 对**评论**点赞 / 取消点赞。
    ///
    /// 接口: `POST /x/v2/reply/action` —— `oid` 是视频 aid、`type=1`(稿件)、`rpid` 是评论 id、
    /// `action` 1 = 赞 / 0 = 取消赞。要带 csrf(bili_jct), 所以必须登录。
    /// 注意它和视频点赞是**两套接口**, 别混: 视频走 `/x/web-interface/archive/like`(like=1/2)。
    ///
    /// ★ **点踩是另一个接口**(`/x/v2/reply/hate`), 见 <see cref="CommentDislikeAsync"/>。
    ///   2026-09-27 修: 之前把踩也当成 action=2 发给这个接口, 服务端直接回
    ///   `12011 不合法的赞或踩`, 表现就是"点踩点了报错"。
    /// </summary>
    public Task<(bool ok, string? err)> CommentLikeAsync(long aid, long rpid, bool like, int replyType = 1)
        => CommentReactionAsync("https://api.bilibili.com/x/v2/reply/action", aid, rpid, like, replyType);

    /// <summary>
    /// 对**评论**点踩 / 取消点踩。
    ///
    /// 接口: `POST /x/v2/reply/hate`, 参数与点赞完全一样, 只是 `action` 的含义变成
    /// 1 = 踩 / 0 = 取消踩。同样要带 csrf, 同样必须登录。
    ///
    /// 赞与踩的服务端行为是**互相覆盖**: 点赞成功会自动消掉踩、点踩成功会自动消掉赞
    /// (这是接口文档写明的副作用, 不是我们本地凑出来的规矩) —— 所以界面上的两个按钮状态
    /// 也必须互斥, 否则会出现"两个都高亮但服务端只认后一个"的假象。
    /// 另外踩**没有计数**: 接口不下发 dislike 总数, 所以踩只有状态、没有数字。
    /// </summary>
    public Task<(bool ok, string? err)> CommentDislikeAsync(long aid, long rpid, bool dislike, int replyType = 1)
        => CommentReactionAsync("https://api.bilibili.com/x/v2/reply/hate", aid, rpid, dislike, replyType);

    /// <summary>赞与踩的公共实现(两个接口的参数、返回值、错误码完全一致, 只有地址不同)</summary>
    private async Task<(bool ok, string? err)> CommentReactionAsync(
        string url, long aid, long rpid, bool on, int replyType)
    {
        if (aid <= 0 || rpid <= 0) return (false, "参数不完整");
        var (code, msg) = await PostFormCodeAsync(
            url,
            new Dictionary<string, string>
            {
                ["oid"] = aid.ToString(),
                ["type"] = replyType.ToString(),
                ["rpid"] = rpid.ToString(),
                ["action"] = on ? "1" : "0"
            },
            referer: "https://www.bilibili.com/");
        if (code == 0) return (true, null);

        // 这串错误码里有一半是"用户能看懂并且知道该怎么办"的, 直接说人话;
        // 剩下的原样透出服务端 message(带上 code 更好排查)。
        var friendly = code switch
        {
            -101 => "请先登录后再操作",
            -111 => "登录状态已失效, 请重新登录",
            -400 => "评论可能已被删除",
            -404 => "评论不存在或已被删除",
            -509 => "操作太频繁, 稍后再试",
            12002 => "该视频的评论区已关闭",
            12004 => "该评论禁止赞或踩(可能被折叠或已被删除)",
            12006 => "评论不存在或已被删除",
            12009 => "评论区类型不合法(这是客户端 bug, 请反馈)",
            12011 => "服务端不认这个操作(这是客户端 bug, 请反馈)",
            _ => msg
        };
        return (false, friendly);
    }

    /// <summary>
    /// 发表评论, 或**回复楼中楼**。
    ///
    /// 接口: `POST /x/v2/reply/add` —— `oid` 是**视频 aid**(不是 bvid)、`type=1`(稿件)、
    /// `message` 是正文(带换行也行, 表单编码会处理)。要带 csrf(bili_jct), 所以必须登录。
    /// `plat=1` 声明"来自 web 端", 少了它评论会以"未知来源"落库(网页版看不到来源标识)。
    ///
    /// 回复楼中楼时必须**同时**带 `root`(顶层评论 id) 与 `parent`(被回复的那条 id) ——
    /// 实测(2026-09-25 抓包): 只回复楼主时两者相等; 回复楼中楼里的某个人时 root 仍是顶层、
    /// parent 指向那个人。少了 root 会被服务端当成普通评论挂到根上(表现是"回错位置")。
    /// </summary>
    /// <param name="rootRpid">要回复的**顶层评论** id; 0 = 发普通评论(挂在新楼层上)</param>
    /// <param name="parentRpid">被回复的那条 id; 传 0 表示"回复楼主"(自动用 rootRpid)</param>
    public async Task<(bool ok, string? err)> PostCommentAsync(
        long aid, string message, long rootRpid = 0, long parentRpid = 0, int replyType = 1)
    {
        var result = await PostCommentDetailedAsync(aid, message, rootRpid, parentRpid, replyType);
        return (result.Ok, result.Err);
    }

    /// <summary>
    /// 发表/回复评论并返回完整结果（含 rpid、success_toast、need_captcha 与 data.reply 实回条目），
    /// 让调用方可以直接把新发评论插入列表顶部并渲染收藏集表情，避免按热度排序下“发完看不到反馈”。
    /// </summary>
    public async Task<PostCommentResult> PostCommentDetailedAsync(
        long aid, string message, long rootRpid = 0, long parentRpid = 0, int replyType = 1)
    {
        if (aid <= 0) return new PostCommentResult { Err = "没拿到视频 aid, 暂时不能评论" };
        if (string.IsNullOrWhiteSpace(message)) return new PostCommentResult { Err = "评论内容不能为空" };
        if (message.Length > 1000) message = message[..1000];

        var form = new Dictionary<string, string>
        {
            ["oid"] = aid.ToString(),
            ["type"] = replyType.ToString(),
            ["message"] = message,
            ["plat"] = "1",
            ["ordering"] = "heat"
        };
        if (rootRpid > 0)
        {
            form["root"] = rootRpid.ToString();
            form["parent"] = (parentRpid > 0 ? parentRpid : rootRpid).ToString();
        }

        var referer = replyType == 1 ? "https://www.bilibili.com/" : "https://t.bilibili.com/";
        var (code, msg, data) = await PostFormJsonAsync(
            "https://api.bilibili.com/x/v2/reply/add", form, referer: referer);

        var res = ParsePostCommentResponse(code, msg, data, message, rootRpid, parentRpid);
        if (res.Ok)
        {
            if (res.Reply == null && res.Rpid > 0)
            {
                var cur = SessionManager.Instance.Current;
                long.TryParse(cur.DedeUserID, out var myMid);
                res.Reply = new CommentItem
                {
                    Rpid = res.Rpid,
                    ParentRpid = parentRpid > 0 ? parentRpid : rootRpid,
                    Mid = myMid,
                    UserName = "我",
                    UserFace = "",
                    Content = message,
                    Ctime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    RcTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                };
            }
            if (res.Reply != null)
                PopulateKnownEmotes(res.Reply);
        }
        return res;
    }

    internal static PostCommentResult ParsePostCommentResponse(
        int code, string? msg, JsonElement? data, string fallbackMessage = "",
        long rootRpid = 0, long parentRpid = 0)
    {
        if (code == 0)
        {
            long rpid = 0;
            string toast = "";
            bool needCaptcha = false;
            string captchaUrl = "";
            CommentItem? reply = null;

            if (data.HasValue && data.Value.ValueKind == JsonValueKind.Object)
            {
                var d = data.Value;
                rpid = GetLongLoose(d, "rpid");
                if (rpid <= 0) rpid = GetLongLoose(d, "rpid_str");
                toast = GetStr(d, "success_toast");
                needCaptcha = d.TryGetProperty("need_captcha", out var nc) &&
                              (nc.ValueKind == JsonValueKind.True || nc.ToString() == "1");
                captchaUrl = UrlUtil.Normalize(GetStr(d, "url"));

                if (d.TryGetProperty("reply", out var replyEl) && replyEl.ValueKind == JsonValueKind.Object)
                {
                    reply = ParseComment(replyEl, withReplies: false);
                    if (reply.Rpid <= 0 && rpid > 0) reply.Rpid = rpid;
                    if (string.IsNullOrEmpty(reply.Content) && !string.IsNullOrEmpty(fallbackMessage))
                        reply.Content = fallbackMessage;
                    if (parentRpid > 0 && reply.ParentRpid <= 0) reply.ParentRpid = parentRpid;
                }
            }

            if (needCaptcha && !string.IsNullOrEmpty(captchaUrl))
            {
                return new PostCommentResult
                {
                    Ok = false,
                    NeedCaptcha = true,
                    CaptchaUrl = captchaUrl,
                    Rpid = rpid,
                    Err = "评论触发人机验证，请完成验证后重新发表"
                };
            }

            return new PostCommentResult
            {
                Ok = true,
                Rpid = rpid,
                SuccessToast = toast,
                Reply = reply
            };
        }

        var friendly = code switch
        {
            -101 => "请先登录后再发表评论",
            -111 => "登录状态已失效, 请重新登录",
            -352 => "评论触发风控拦截, 请稍后再试或检查账号状态",
            -400 => "评论内容不合规, 已拒绝发布",
            -403 => "当前账号没有评论权限",
            -412 => "操作太频繁, 请稍后再试",
            -509 => "操作太频繁, 请稍后再试",
            12002 => "该内容评论区已关闭",
            12025 => "评论字数超出限制",
            12035 => "当前已被对方拉黑, 无法发表评论",
            12051 => "请勿重复发送相同评论",
            12066 => "评论内容不能为空",
            _ => msg ?? $"评论发送失败 (code {code})"
        };
        return new PostCommentResult { Ok = false, Err = friendly };
    }

    /// <summary>
    /// 获取当前账号可用的评论表情包（含普通小黄脸、会员表情、已解锁的收藏集/装扮表情、颜文字）。
    /// 优先调用 `/x/emote/user/panel/web?business=reply`（携带登录态才会返回用户已拥有的收藏集表情），
    /// 未登录或失败时自动回退 `/x/emote/setting/panel`。
    /// </summary>
    public async Task<(bool ok, string? err, List<EmotePackage> packages)> GetReplyEmotePanelAsync(
        string business = "reply", bool forceRefresh = false)
    {
        var accountKey = SessionManager.Instance.Current.DedeUserID ?? "";
        lock (_emoteCacheGate)
        {
            if (!forceRefresh && _cachedEmotePackages != null && _emoteCacheAccountKey == accountKey)
                return (true, null, _cachedEmotePackages);
        }

        var ps = new Dictionary<string, string> { ["business"] = business };
        var (code, msg, data) = await GetJsonAsync(
            "https://api.bilibili.com/x/emote/user/panel/web", ps,
            sign: false, referer: "https://www.bilibili.com/");

        if (code != 0 || data == null)
        {
            (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/emote/setting/panel", ps,
                sign: false, referer: "https://www.bilibili.com/");
        }

        if (code != 0 || data == null)
            return (false, msg ?? "获取表情面板失败", new List<EmotePackage>());

        var (packages, lookup) = ParseEmotePanelJson(data.Value);
        lock (_emoteCacheGate)
        {
            _cachedEmotePackages = packages;
            _cachedEmoteLookup = lookup;
            _emoteCacheAccountKey = accountKey;
        }
        return (true, null, packages);
    }

    internal static (List<EmotePackage> packages, Dictionary<string, CommentEmote> lookup) ParseEmotePanelJson(JsonElement data)
    {
        var packages = new List<EmotePackage>();
        var lookup = new Dictionary<string, CommentEmote>(StringComparer.Ordinal);
        var seenPkgIds = new HashSet<long>();

        void ReadPackageArray(JsonElement arr, bool forceLocked)
        {
            if (arr.ValueKind != JsonValueKind.Array) return;
            foreach (var p in arr.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var id = GetLongLoose(p, "id");
                if (id > 0 && !seenPkgIds.Add(id)) continue;

                var type = GetIntSafe(p, "type");
                var unlocked = !forceLocked;
                if (p.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Object)
                {
                    if (flags.TryGetProperty("no_access", out var na) &&
                        (na.ValueKind == JsonValueKind.True || na.ToString() == "1"))
                        unlocked = false;
                    if (flags.TryGetProperty("unlocked", out var ul))
                        unlocked = ul.ValueKind == JsonValueKind.True || ul.ToString() == "1";
                }

                var pkg = new EmotePackage
                {
                    Id = id,
                    Text = GetStr(p, "text"),
                    Url = UrlUtil.Normalize(GetStr(p, "url")),
                    Type = type,
                    Unlocked = unlocked
                };

                if (p.TryGetProperty("emote", out var emotes) && emotes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var em in emotes.EnumerateArray())
                    {
                        if (em.ValueKind != JsonValueKind.Object) continue;
                        var text = GetStr(em, "text");
                        if (string.IsNullOrEmpty(text)) continue;

                        var url = UrlUtil.Normalize(GetStr(em, "url"));
                        var gifUrl = UrlUtil.Normalize(GetStr(em, "gif_url"));
                        var webpUrl = UrlUtil.Normalize(GetStr(em, "webp_url"));
                        if (url.Length == 0) url = gifUrl.Length > 0 ? gifUrl : webpUrl;
                        var size = 1;
                        if (em.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                        {
                            var s = GetIntSafe(meta, "size");
                            if (s > 1) size = s;
                        }
                        if (type == 3 && size < 2) size = 2;

                        var entry = new EmoteEntry
                        {
                            Id = GetLongLoose(em, "id"),
                            PackageId = id,
                            Text = text,
                            Url = url,
                            GifUrl = gifUrl,
                            Size = size,
                            Type = GetIntSafe(em, "type") > 0 ? GetIntSafe(em, "type") : type,
                            Unlocked = unlocked
                        };
                        pkg.Emotes.Add(entry);

                        if ((url.Length > 0 || gifUrl.Length > 0) && !lookup.ContainsKey(text))
                            lookup[text] = new CommentEmote { Url = url, GifUrl = gifUrl, Size = size };
                    }
                }

                if (pkg.Emotes.Count > 0)
                    packages.Add(pkg);
            }
        }

        if (data.TryGetProperty("packages", out var pkgs)) ReadPackageArray(pkgs, forceLocked: false);
        if (data.TryGetProperty("setting", out var setting) && setting.ValueKind == JsonValueKind.Object
            && setting.TryGetProperty("packages", out var settingPkgs))
            ReadPackageArray(settingPkgs, forceLocked: false);
        if (data.TryGetProperty("all_packages", out var allPkgs)) ReadPackageArray(allPkgs, forceLocked: true);

        return (packages, lookup);
    }

    /// <summary>用已缓存的表情包字典补全评论实体里缺失的表情图片映射（例如 /x/v2/reply/add 回显未下发 emote 时）</summary>
    public void PopulateKnownEmotes(CommentItem? item)
    {
        if (item == null || string.IsNullOrEmpty(item.Content)) return;
        Dictionary<string, CommentEmote> snapshot;
        lock (_emoteCacheGate)
        {
            if (_cachedEmoteLookup.Count == 0) return;
            snapshot = new Dictionary<string, CommentEmote>(_cachedEmoteLookup, StringComparer.Ordinal);
        }

        var idx = 0;
        while (idx < item.Content.Length)
        {
            var open = item.Content.IndexOf('[', idx);
            if (open < 0) break;
            var close = item.Content.IndexOf(']', open + 1);
            if (close < 0) break;
            var token = item.Content[open..(close + 1)];
            if (!item.Emotes.ContainsKey(token) && snapshot.TryGetValue(token, out var emote))
                item.Emotes[token] = emote;
            idx = close + 1;
        }
    }

    /// <summary>
    /// 查询当前用户是否已点赞该视频(aid)。
    ///
    /// ★ 2026-10-01 从 <c>Task&lt;bool?&gt;</c> 改为 (ok, err, value):
    ///   原来的三态约定是"null = 查询失败", 但**失败原因被丢掉了**, 调用方只能默默
    ///   保持原状, 用户看到的就是"点了没反应"。现在 ok=false 时 err 会说明原因。
    ///   value 仍然只在 ok=true 时有意义。
    /// </summary>
    public async Task<(bool ok, string? err, bool value)> HasLikedAsync(long aid)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/archive/has/like",
                new Dictionary<string, string> { ["aid"] = aid.ToString() });
            if (code != 0 || data == null) return (false, msg ?? $"查询点赞状态失败 (code {code})", false);
            // data: 1 已赞 / 0 未赞
            return (true, null, data.Value.ValueKind == JsonValueKind.Number && data.Value.GetInt32() == 1);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "查询点赞状态失败: " + ex.Message, false);
        }
    }

    /// <summary>
    /// 查询当前用户对该视频**已投了几枚币**(0 = 没投过)。
    ///
    /// ★★ 2026-10-13 用户报"投过币、关掉重开, 投币键还是灰的"——真根因是
    ///   <c>GetVideoAsync</c> 里那句 <c>GetInt(d, "coin") == 1</c> 读的字段**根本不存在**。
    ///   实测(探针 bd-probe-coin, 带真实登录 Cookie):
    ///     · <c>/x/web-interface/view</c> 的 data 里**没有** coin / req_user 这类用户态字段,
    ///       只有 <c>data.stat.coin</c> —— 那是**全站投币总数**(实测 128 万), 与本账号无关;
    ///     · 真正管用的是 <c>/x/web-interface/archive/coins</c>, 返回
    ///       <c>{"code":0,"data":{"multiply":N}}</c>, N 就是本人投的枚数(0/1/2)。
    ///   决定性验证: 真投 1 枚后, 同一个 aid 的 multiply 由 0 变成 1(探针已跑通)。
    ///   ⇒ 判断"曾经投过币"必须走这里, 不能靠详情接口。
    ///
    /// ★ 返回**枚数**而不是 bool: 单稿件上限 2 枚, 界面上"已投 1 枚还想再投 1 枚"
    ///   是有意义的场景, bool 会把这个信息丢掉。
    /// </summary>
    public async Task<(bool ok, string? err, int coins)> GetCoinCountAsync(long aid)
    {
        try
        {
            if (aid <= 0) return (false, "缺少视频 aid", 0);
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/archive/coins",
                new Dictionary<string, string> { ["aid"] = aid.ToString() });
            if (code != 0 || data == null) return (false, msg ?? $"查询投币状态失败 (code {code})", 0);
            // data.multiply: 本人已投枚数(实测 0/1)
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("multiply", out var m) && m.ValueKind == JsonValueKind.Number)
                return (true, null, m.GetInt32());
            // 字段缺失时按"没投过"处理, 不能让按钮卡在"已投"上 —— 那会让用户再也投不了
            return (true, null, 0);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "查询投币状态失败: " + ex.Message, 0);
        }
    }

    /// <summary>
    /// 把服务端的风控文案翻译成**用户能照着做**的提示。
    ///
    /// ★ 为什么需要它(2026-10-06 用户报"点赞投币依然报错"):
    ///   账号被风控时服务端回的是 `-403 账号异常,操作失败` / `-401 非法访问` ——
    ///   这两句对用户毫无信息量, 他只会反复点、然后以为程序坏了。
    ///   实测把完整响应打出来后, 真因写在 data.ga_data 里:
    ///       decisions = ["verify_captcha_level2"]   ← 要求二级人机验证
    ///   这是 B 站的**风控门**, 客户端绕不过(实测: 换 Referer/Origin/参数/端点/稿件,
    ///   以及补 b_nut/_uuid/bili_ticket 全部无效)。所以只能把话说清楚, 指引用户去官方
    ///   渠道完成一次验证 —— 而不是把服务端原文丢给用户。
    ///
    /// ★ 只替换**明确是风控**的这几种文案, 其它错误原样透出 —— 不吃掉真实报错。
    /// </summary>
    internal static string FriendlyRiskMessage(string? raw)
    {
        var msg = raw ?? "";
        // 这三句是同一道人机验证门的不同说法(见 v_voucher 文档的 -352 与实测的 -401/-403)
        if (msg.Contains("账号异常", StringComparison.Ordinal) ||
            msg.Contains("非法访问", StringComparison.Ordinal) ||
            msg.Contains("风控", StringComparison.Ordinal))
        {
            return "B 站风控拦截: 请在手机 App 或网页版用同一账号完成一次验证后重试";
        }
        return string.IsNullOrEmpty(msg) ? "未知错误" : msg;
    }

    public async Task<(bool ok, string? err)> SendDanmakuAsync(long cid, string bvid, string message, long progressMs)
    {
        if (!SessionManager.Instance.HasLogin) return (false, "请先登录后再发送弹幕");
        message = message.Trim();
        if (cid <= 0 || string.IsNullOrWhiteSpace(bvid)) return (false, "视频信息尚未就绪");
        if (message.Length == 0 || message.Length > 100) return (false, "弹幕内容须为 1–100 字");
        var mixin = await GetMixinKeyAsync();
        if (mixin == null) return (false, "弹幕签名获取失败，请稍后重试");
        var query = new Dictionary<string, string> { ["web_location"] = "1315873", ["csrf"] = SessionManager.Instance.Current.BiliJct ?? "" };
        SignParams(query, mixin);
        var url = "https://api.bilibili.com/x/v2/dm/post?" + string.Join("&", query.Select(kv => $"{kv.Key}={Enc(kv.Value)}"));
        var result = await PostFormAsync(url, new Dictionary<string, string>
        {
            ["type"] = "1", ["oid"] = cid.ToString(), ["bvid"] = bvid, ["msg"] = message,
            ["progress"] = Math.Max(0, progressMs).ToString(), ["color"] = "16777215", ["fontsize"] = "25",
            ["pool"] = "0", ["mode"] = "1", ["rnd"] = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000).ToString()
        }, "https://www.bilibili.com/video/" + bvid);
        return result.ok ? result : (false, FriendlyRiskMessage(result.err));
    }

    /// <summary>点赞/取消点赞视频(现行 web 接口)。like=1 点赞, like=2 取消</summary>
    public async Task<(bool ok, string? err)> LikeVideoAsync(string bvid, bool liked)
    {
        var (ok, err) = await PostFormAsync(
            "https://api.bilibili.com/x/web-interface/archive/like",
            new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["like"] = liked ? "1" : "2"
            },
            referer: "https://www.bilibili.com/video/" + bvid);
        return (ok, ok ? err : FriendlyRiskMessage(err));
    }

    /// <summary>
    /// 投币(现行 web 接口, 需要 aid)。count=1 或 2。
    /// alsoLike=true 时带 select_like=1 —— 服务端会在同一次请求里把赞也点上
    /// (对应投币弹窗里的"同时点赞内容"; 拆成两次请求的话, 赞失败就该不该退币说不清了)。
    /// </summary>
    public async Task<(bool ok, string? err)> CoinVideoAsync(long aid, int count, string bvid, bool alsoLike = false)
    {
        var (ok, err) = await PostFormAsync(
            "https://api.bilibili.com/x/web-interface/coin/add",
            new Dictionary<string, string>
            {
                ["aid"] = aid.ToString(),
                ["multiply"] = count.ToString(),
                ["select_like"] = alsoLike ? "1" : "0"
            },
            referer: "https://www.bilibili.com/video/" + bvid);
        return (ok, ok ? err : FriendlyRiskMessage(err));
    }

    public sealed record TripleLikeResult(bool Ok, string? Error, bool Like, bool Coin, bool Fav, int Multiply);

    /// <summary>一键三连结果解析（无网络单测入口）。</summary>
    internal static TripleLikeResult ParseTripleLikeResponse(int code, string? msg, JsonElement? data)
    {
        if (code != 0)
        {
            var err = FriendlyRiskMessage(string.IsNullOrWhiteSpace(msg) ? $"code {code}" : msg);
            return new TripleLikeResult(false, err, false, false, false, 0);
        }

        if (data is { ValueKind: JsonValueKind.Object } d)
        {
            var like = GetBool(d, "like");
            var coin = GetBool(d, "coin");
            var fav = GetBool(d, "fav");
            var multiply = Math.Max(0, GetInt(d, "multiply"));
            return new TripleLikeResult(true, null, like, coin, fav, multiply);
        }

        return new TripleLikeResult(true, null, false, false, false, 0);
    }

    /// <summary>
    /// 官方 Web 端一键三连接口：单次请求完成点赞 + 投币(最多 2 枚) + 收藏(默认收藏夹)。
    /// 相比拆成多次 POST，服务端按实际状态决定每一项是否变更并统一过风控。
    /// </summary>
    public async Task<TripleLikeResult> TripleLikeAsync(string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid))
            return new TripleLikeResult(false, "缺少视频 bvid", false, false, false, 0);

        var (code, msg, data) = await PostFormJsonAsync(
            "https://api.bilibili.com/x/web-interface/archive/like/triple",
            new Dictionary<string, string> { ["bvid"] = bvid },
            referer: "https://www.bilibili.com/video/" + bvid);
        return ParseTripleLikeResponse(code, msg, data);
    }

    private static bool GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// 查询与指定用户的关系(是否已关注)。同 <see cref="HasLikedAsync"/>: 2026-10-01 由
    /// <c>Task&lt;bool?&gt;</c> 改为 (ok, err, value), 失败时给出原因而不是静默返回 null。
    /// </summary>
    public async Task<(bool ok, string? err, bool value)> GetRelationAsync(long mid)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation",
                new Dictionary<string, string> { ["fid"] = mid.ToString() },
                referer: "https://space.bilibili.com/" + mid);
            if (code != 0 || data == null) return (false, msg ?? $"查询关注状态失败 (code {code})", false);
            // data.attribute: 0 未关注, 2 已关注, 3 互关, 6 悄悄关注
            return (true, null,
                data.Value.TryGetProperty("attribute", out var a)
                && a.ValueKind == JsonValueKind.Number && a.GetInt32() > 0);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "查询关注状态失败: " + ex.Message, false);
        }
    }

    /// <summary>关注/取消关注 UP 主。act=1 关注, act=2 取消关注</summary>
    public Task<(bool ok, string? err)> FollowUpAsync(long mid, bool follow)
    {
        return PostFormAsync(
            "https://api.bilibili.com/x/relation/modify",
            new Dictionary<string, string>
            {
                ["fid"] = mid.ToString(),
                ["act"] = follow ? "1" : "2",
                ["re_src"] = "11"
            },
            referer: "https://space.bilibili.com/" + mid);
    }

    /// <summary>
    /// 视频评论(返回前 N 条评论, 并带上每条的内联前几条回复)。
    ///
    /// ★ 2026-10-01 由裸 <c>Task&lt;List&lt;CommentItem&gt;&gt;</c> 改为 (ok, err, items):
    ///   原来"接口报错"与"这个视频一条评论都没有"都返回**空列表**, 调用方无法区分,
    ///   于是评论区会显示成"还没有人评论" —— 网络失败被伪装成了正常状态。
    ///
    /// ★ 2026-10-06 增加 <paramref name="mode"/>(评论区「热门 / 时间」切换)与返回的
    ///   <c>total</c>(根评论总数)。两个都来自同一个接口, 不需要额外请求:
    ///     · <c>mode=3</c> 热门 / <c>mode=2</c> 时间 —— 与 B 站网页版同一组参数;
    ///     · 总数在 <c>data.cursor.all_count</c>。
    ///   ★ 为什么要把 total 透出来: 以前调用方拿 <c>list.Count</c> 当总数显示"共 N 条评论",
    ///     而一页只拉 30 条 —— 视频有几千条评论时那句话是**错的**。排序入口就挂在这行文字右边,
    ///     数字不对会让整个入口看着像坏的。
    /// </summary>
    public async Task<(bool ok, string? err, List<CommentItem> items, long total)> GetCommentsAsync(
        long aid, int count = 20, int mode = 3)
    {
        var page = await GetVideoCommentsPageAsync(aid, count, mode, null);
        return (page.ok, page.err, page.items, page.total);
    }

    internal sealed record VideoCommentCursor(long Next, string Offset, int Page);

    internal async Task<(bool ok, string? err, List<CommentItem> items, long total, VideoCommentCursor? next)> GetVideoCommentsPageAsync(
        long aid, int count, int mode, VideoCommentCursor? after)
    {
        VideoCommentCursor? next = null;
        var result = new List<CommentItem>();
        var total = 0L;
        try
        {
            var ps = new Dictionary<string, string>
            {
                ["oid"] = aid.ToString(),
                ["type"] = "1",
                ["mode"] = mode.ToString(),
                ["ps"] = count.ToString(),
                ["pn"] = (after?.Page ?? 1).ToString()
            };
            if (after != null)
            {
                ps["next"] = after.Next.ToString();
                if (after.Offset.Length > 0)
                    ps["pagination_str"] = JsonSerializer.Serialize(new { offset = after.Offset });
            }
            // ★ fingerprint: 按**登录态**决定带不带 Cookie。
            //
            // 实测(2026-09-25, 冷启动单请求, 同一个 aid):
            //   未登录 + 不带任何 Cookie            -> 20 条, 但 reply_control 里**没有 location**
            //                                          (所以评论区一条 IP 属地都看不到)
            //   已登录 + 完整 Cookie(buvid3+buvid4) -> 20 条, 内联楼中楼 34 条,
            //                                          location = "IP属地：广东"
            // 即: IP 属地是**登录态才下发**的字段; 而 2026-09-24 那条"带设备指纹会被降级成
            // 3 条"的结论只对**未登录**成立 —— 登录态下带指纹一样是满 20 条。
            // 于是: 登录了就带(拿得到 IP 属地), 没登录就不带(保住 20 条不被降级)。
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/reply/main", ps,
                fingerprint: SessionManager.Instance.HasLogin);
            if (code != 0 || data == null)
                return (false, msg ?? $"获取评论失败 (code {code})", result, 0, null);

            // 根评论总数(不受 ps 分页影响)。缺失时保持 0, 由调用方回落到"本页条数"。
            if (data.Value.TryGetProperty("cursor", out var cursor) &&
                cursor.ValueKind == JsonValueKind.Object)
            {
                total = GetLongSafe(cursor, "all_count");
                var position = GetLongLoose(cursor, "next");
                var pagination = TryObject(cursor, "pagination_reply");
                var offset = pagination.HasValue ? GetStr(pagination.Value, "next_offset") : "";
                if ((!cursor.TryGetProperty("is_end", out var end) || end.ValueKind != JsonValueKind.True)
                    && (position > 0 || offset.Length > 0)
                    && (after == null || position != after.Next || offset != after.Offset))
                    next = new(position, offset, (after?.Page ?? 1) + 1);
            }

            // 本稿件的 UP 主 mid: 用来给"UP 主自己的评论"打 [UP] 标
            var upperMid = data.Value.TryGetProperty("upper", out var up) &&
                           up.ValueKind == JsonValueKind.Object
                ? GetLongSafe(up, "mid") : 0L;

            // ★★★ 置顶评论(2026-10-03 修"置顶评论不可见"): 置顶那条**不在 replies 里**,
            //   而是在 `data.top.upper`(UP 主置顶)或 `data.top.admin`(管理员置顶)。
            //   只读 replies 就会把它整条丢掉 —— 用户看到的就是"明明有置顶, 却一条都看不到"。
            //   实测(data.top.upper.rpid=315658667873 在 replies 里搜不到)。
            //   ★ 放在 replies **之前**: 置顶本来就该排在最前面。
            //   ★ 两个都可能有值, 但语义上只应展示一条置顶, 所以 upper 优先、admin 兜底。
            if (after == null && data.Value.TryGetProperty("top", out var top) && top.ValueKind == JsonValueKind.Object)
            {
                var topEl = TryObject(top, "upper") ?? TryObject(top, "admin");
                if (topEl != null)
                {
                    var pinned = ParseComment(topEl.Value, withReplies: true, upperMid: upperMid);
                    pinned.IsPinned = true;
                    result.Add(pinned);
                }
            }

            if (data.Value.TryGetProperty("replies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    result.Add(ParseComment(e, withReplies: true, upperMid: upperMid));
                }
            }
        }
        catch (Exception ex)
        {
            // 不能真的"静默": 解析里任何一处异常都会让后面的评论**整批丢掉**,
            // 表现是"评论只出来前几条", 而排查时一点线索都没有。记到日志里。
            App.ReportError(ex);
            // 堆栈首行一起带出去: "invalid format"这类错误只有堆栈才能定位到具体字段
            var stackTop = (ex.StackTrace ?? "").Split('\n')
                .FirstOrDefault(l => l.Contains("BiliTouch") && !l.Contains("ThrowHelper")) ?? "";
            return (false, "解析评论失败: " + ex.Message + (stackTop.Length > 0 ? " @ " + stackTop.Trim() : ""), result, 0, null);
        }
        return (true, null, result, total, next);
    }

    /// <summary>取对象里的某个子对象; 不存在或是 null 时返回 null(置顶评论可能只有 upper 或只有 admin)</summary>
    private static JsonElement? TryObject(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>
    /// 取某条评论下的**全部**回复(楼中楼)。
    /// 接口: /x/v2/reply/reply —— root 传顶层评论的 rpid, oid 是视频 aid, type=1。
    /// 首页接口 /x/v2/reply/main 只内联前 3 条回复, 要看全就只能走这个接口,
    /// 否则用户会觉得"别人回复的内容看不到"。
    /// </summary>
    public async Task<(bool ok, string? err, List<CommentItem> items)> GetReplyRepliesAsync(long aid, long rootRpid, int pn = 1, int ps = 20, int replyType = 1)
    {
        var result = new List<CommentItem>();
        try
        {
            if (aid <= 0 || rootRpid <= 0) return (false, "参数不完整", result);
            var ps2 = new Dictionary<string, string>
            {
                ["oid"] = aid.ToString(),
                ["type"] = replyType.ToString(),
                ["root"] = rootRpid.ToString(),
                ["ps"] = ps.ToString(),
                ["pn"] = pn.ToString()
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/reply/reply", ps2);
            if (code != 0 || data == null)
                return (false, msg ?? $"获取楼中楼失败 (code {code})", result);

            // 楼中楼里 UP 主自己的回复同样要打 [UP] 标 —— reply/reply 的 data 里也带 upper
            var upperMid = data.Value.TryGetProperty("upper", out var up) &&
                           up.ValueKind == JsonValueKind.Object
                ? GetLongSafe(up, "mid") : 0L;

            if (data.Value.TryGetProperty("replies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    result.Add(ParseComment(e, withReplies: false, upperMid: upperMid));
                }
            }
        }
        catch (Exception ex)
        {
            // 不能真的"静默": 解析里任何一处异常都会让后面的评论**整批丢掉**,
            // 表现是"评论只出来前几条", 而排查时一点线索都没有。记到日志里。
            App.ReportError(ex);
            return (false, "解析评论失败: " + ex.Message, result);
        }
        return (true, null, result);
    }

    /// <summary>
    /// 解析一条评论 JSON。顶层评论与楼中楼回复的字段结构完全一致, 所以共用同一份解析,
    /// 免得两边各写一遍之后字段理解跑偏。
    /// </summary>
    /// <param name="withReplies">是否解析 rcount 与内联的 replies(只有顶层评论才有)</param>
    /// <param name="upperMid">本稿件 UP 主的 mid; 评论者 mid 与它相同就打 [UP] 标。
    /// 0 = 调用方拿不到(或不是从"稿件维度"取的), 此时谁都不打标。</param>
    private static CommentItem ParseComment(JsonElement e, bool withReplies, long upperMid = 0)
    {
        // 当前登录用户对这条评论的操作: 1 = 已赞(0 = 无)。未登录时恒为 0。
        // 2 = 已踩: 只在服务端确实会下发这个值时才成立, 见 CommentItem.IsDisliked 的说明。
        var myAction = GetIntSafe(e, "action");

        var item = new CommentItem
        {
            // ★ 数值字段一律 GetLongSafe/GetIntSafe: 实测服务端会把 like 等字段下发成
            //   小数(如 12.0), 普通 GetLong 对浮点调 GetInt64() 直接抛
            //   "One of the identified items was in an invalid format",
            //   而 ParseComment 外层只有一个大 try —— 一条评论抛异常整页全丢。
            Rpid = GetLongSafe(e, "rpid"),
            // 评论者 mid: 顶层字段就有, 取不到再从 member.mid 兜一次
            Mid = GetLongLoose(e, "mid"),
            // 被回复的那条评论 id(详情页里"回复 @"指向谁)
            ParentRpid = GetLongLoose(e, "parent"),
            LikeCount = GetIntSafe(e, "like"),
            IsLiked = myAction == 1,
            IsDisliked = myAction == 2
        };

        if (e.TryGetProperty("member", out var m))
        {
            item.UserName = GetStr(m, "uname");
            item.UserFace = UrlUtil.Normalize(GetStr(m, "avatar"));
            if (item.Mid <= 0) item.Mid = GetLongLoose(m, "mid");
            // 等级徽章。member.level_info.current_level(0~6), 缺了就不显示徽章
            if (m.TryGetProperty("level_info", out var lv) && lv.ValueKind == JsonValueKind.Object)
                item.Level = GetIntSafe(lv, "current_level");
        }

        // UP 标: 评论者就是本稿件 UP 主本人
        item.IsUp = upperMid > 0 && item.Mid == upperMid;

        // IP 属地。接口给的是 `reply_control.location`, 形如 "IP属地：上海";
        // 它**本来就是带前缀的展示串**, 所以直接取用、不再自己拼"IP属地"。
        // 值可能缺失(老接口 / 用户设置里关了展示), 缺了就不显示。
        if (e.TryGetProperty("reply_control", out var rc) && rc.ValueKind == JsonValueKind.Object)
            item.Location = GetStr(rc, "location").Trim();

        // 评论内容在 content.message 嵌套里
        item.Content = e.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object
            ? GetStr(c, "message")
            : "";

        // 表情: content.emote 是 { "[短代码]": { url, meta:{size} } } 的字典,
        // key 就是正文里原样出现的短代码(实测 "[打call]"、"鸣潮·共鸣与群星_逼近" 都在 message 里)。
        // 只收正文里真的用到的, 免得白存一堆用不上的地址。
        if (c.ValueKind == JsonValueKind.Object &&
            c.TryGetProperty("emote", out var emote) && emote.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in emote.EnumerateObject())
            {
                if (kv.Value.ValueKind != JsonValueKind.Object) continue;
                if (!item.Content.Contains(kv.Name, StringComparison.Ordinal)) continue;
                var url = UrlUtil.Normalize(GetStr(kv.Value, "url"));
                var gifUrl = UrlUtil.Normalize(GetStr(kv.Value, "gif_url"));
                var webpUrl = UrlUtil.Normalize(GetStr(kv.Value, "webp_url"));
                if (url.Length == 0) url = gifUrl.Length > 0 ? gifUrl : webpUrl;
                if (url.Length == 0) continue;
                var size = 1;
                if (kv.Value.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    var s = GetIntSafe(meta, "size");
                    if (s > 1) size = s;
                }
                item.Emotes[kv.Name] = new CommentEmote { Url = url, GifUrl = gifUrl, Size = size };
            }
        }

        item.Ctime = e.TryGetProperty("ctime", out var ctRaw) && ctRaw.ValueKind == JsonValueKind.Number
            ? GetLongSafe(e, "ctime") : 0L;
        item.RcTime = item.Ctime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(item.Ctime).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : "";

        // @提及(2026-10-30 新增): 扫出正文里 @ 到的昵称, 界面据此把它们染成强调色。
        // ★ 接口给的 content.message 是**纯文本**, 没有现成的结构化引用字段可用,
        //   所以只能按 B 站网页版的规则自己扫(见 CommentItem.Mentions 的说明)。
        foreach (var name in MentionParser.Parse(item.Content))
            item.Mentions.Add(name);

        // 评论图片(2026-10-31 新增): content.pictures 数组, 带图评论才有。
        // ★ 字段名带 img_ 前缀(img_src/img_width/img_height/img_size), 与 emote 那套不同名。
        if (c.ValueKind == JsonValueKind.Object &&
            c.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pics.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var url = UrlUtil.Normalize(GetStr(p, "img_src"));
                if (url.Length == 0) continue;
                item.Pictures.Add(new CommentPicture
                {
                    Url = url,
                    Width = GetIntSafe(p, "img_width"),
                    Height = GetIntSafe(p, "img_height"),
                    // img_size 实测是 KB 数(可能带小数), 用 LongSafe 兜住
                    FileSize = GetLongSafe(p, "img_size")
                });
            }
        }

        if (withReplies)
        {
            item.ReplyCount = GetIntSafe(e, "rcount");
            // /x/v2/reply/main 会把前 3 条回复直接内联在 replies 里 —— 先渲染出来,
            // 用户不点任何东西就能看见别人的回复内容
            if (e.TryGetProperty("replies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in arr.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    item.Replies.Add(ParseComment(r, withReplies: false, upperMid: upperMid));
                }
            }
            // 服务端有时返回的 rcount 比实际内联的还少, 兜一下, 否则"展开"按钮会一直挂着
            if (item.ReplyCount < item.Replies.Count) item.ReplyCount = item.Replies.Count;
            // 列表页方框只展示短预览; 详情页仍然用 Replies 看全部(见 PreviewReplies 的说明)
            item.SyncPreviewReplies();
        }

        return item;
    }
}
