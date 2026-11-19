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
    // ---------------------------------------------------------------- 评论


    /// <summary>
    /// 私信接口统一的来源页。
    /// 这几个接口在 message.bilibili.com 域名下被调用, 不带对应 Referer 很容易吃 -403,
    /// 所以每处都显式传它。
    /// </summary>
    private const string MsgReferer = "https://message.bilibili.com/";

    private static long ParseMid()
        => long.TryParse(SessionManager.Instance.Current.DedeUserID, out var v) ? v : 0;

    /// <summary>获取私信会话列表(含昵称/头像, 一次请求批量补齐)</summary>
    public async Task<(bool ok, string? err, List<MsgSession>? sessions)> GetMsgSessionsAsync(
        int sessionType = 1, int size = 50)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "私信需要登录后查看", null);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.vc.bilibili.com/session_svr/v1/session_svr/get_sessions",
                new Dictionary<string, string>
                {
                    ["session_type"] = sessionType.ToString(), // 1 = 私聊 + 系统消息
                    ["sort_rule"] = "2",                       // 按活跃时间倒序
                    ["size"] = size.ToString(),
                    ["build"] = "0",
                    ["mobi_app"] = "web"
                },
                referer: MsgReferer);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<MsgSession>();
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("session_list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in arr.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    var talker = GetLong(s, "talker_id");
                    if (talker <= 0) continue; // 系统会话可能没有 talker_id, 跳过

                    var atTime = GetLong(s, "session_ts");
                    if(atTime>1000000000000)atTime/=1000000;
                    if(atTime<=0)atTime=GetLong(s,"at_time");
                    var profile=MessageParser.Profile(s);
                    if(profile.name.Length==0 && s.TryGetProperty("last_msg",out var last))profile=MessageParser.Profile(last);
                    list.Add(new MsgSession
                    {
                        TalkerId = talker,Name=profile.name,Face=profile.face,IsOfficial=profile.name.Length>0,Timestamp=atTime,
                        UnreadCount = GetInt(s, "unread_count"),
                        AckSeqno = GetLong(s, "ack_seqno"),
                        MaxSeqno = GetLong(s, "max_seqno"),
                        TimeText = atTime > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(atTime).LocalDateTime
                                .ToString("MM-dd HH:mm")
                            : "",
                        Preview = s.TryGetProperty("last_msg", out var lm) ? MessageParser.Preview(lm) : ""
                    });
                }
            }

            await FillSessionProfilesAsync(list);
            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 批量补会话的昵称与头像。
    /// 会话列表接口本身不返回用户资料, 得用 /account/v1/user/cards 单独查, 一次最多 50 个 UID。
    /// 失败只是没有名字(会退化成 "UID xxxx"), 不影响会话列表本身可用。
    /// </summary>
    private async Task FillSessionProfilesAsync(List<MsgSession> sessions)
    {
        var ids = sessions.Where(s=>s.Name.Length==0).Select(s => s.TalkerId).Distinct().ToList();
        for (var i = 0; i < ids.Count; i += 50)
        {
            var batch = ids.Skip(i).Take(50).ToList();
            try
            {
                var (code, _, data) = await GetJsonAsync(
                    "https://api.vc.bilibili.com/account/v1/user/cards",
                    new Dictionary<string, string> { ["uids"] = string.Join(",", batch) },
                    referer: MsgReferer);
                if (code != 0 || data == null) continue;
                if (data.Value.ValueKind != JsonValueKind.Array) continue;

                var cards = new Dictionary<long, (string name, string face)>();
                foreach (var c in data.Value.EnumerateArray())
                {
                    var mid = GetLong(c, "mid");
                    if (mid > 0)
                        cards[mid] = (GetStr(c, "name"), UrlUtil.Normalize(GetStr(c, "face")));
                }

                foreach (var s in sessions)
                    if (cards.TryGetValue(s.TalkerId, out var v))
                    {
                        s.Name = v.name;
                        s.Face = v.face;
                    }
            }
            catch { /* 单批失败不影响其它批次 */ }
        }
    }

    /// <summary>
    /// 拉取与某个会话的私信记录。
    /// 接口: /svr_sync/v1/svr_sync/fetch_session_msgs (GET, Cookie 认证)。
    /// 返回结果按 msg_seqno 升序排好(旧 → 新), 直接可以正序渲染。
    /// </summary>
    public async Task<(bool ok, string? err, List<MsgItem>? items, bool hasMore)> GetMsgHistoryAsync(
        long talkerId, int size = 30, long endSeqno = 0)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "私信需要登录后查看", null, false);
            if (talkerId <= 0) return (false, "会话无效", null, false);
            var me = ParseMid();
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["sender_device_id"] = "1",
                ["talker_id"] = talkerId.ToString(),
                ["session_type"] = "1",
                ["size"] = size.ToString(),
                ["build"] = "0",
                ["mobi_app"] = "web"
            };
            if (endSeqno > 0) ps["end_seqno"] = endSeqno.ToString();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.vc.bilibili.com/svr_sync/v1/svr_sync/fetch_session_msgs",
                ps,
                referer: MsgReferer);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null, false);

            var list = new List<MsgItem>();
            var hasMore = false;
            if (data.Value.ValueKind == JsonValueKind.Object)
            {
                hasMore = data.Value.TryGetProperty("has_more", out var hm) &&
                          (hm.ValueKind==JsonValueKind.True || hm.ToString()=="1");

                if (data.Value.TryGetProperty("messages", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;
                        list.Add(MessageParser.Parse(m,me));
                    }
                }
            }

            // 按序号升序(旧 → 新)。不依赖接口返回顺序 —— 那个在新旧方向上没有稳定保证。
            // 序号缺失(0)的消息保持原有相对次序。
            list.Sort((a, b) => a.Seqno.CompareTo(b.Seqno));
            return (true, null, list, hasMore);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, false);
        }
    }

    /// <summary>发送文字私信</summary>
    public async Task<(bool ok, string? err)> SendMsgAsync(long receiverId, string text)
    {
        if (receiverId <= 0) return (false, "缺少接收者");
        if (string.IsNullOrWhiteSpace(text)) return (false, "消息不能为空");
        if (!SessionManager.Instance.HasLogin) return (false, "私信需要登录");

        var me = ParseMid();
        if (me <= 0) return (false, "无法获取自己的 UID");

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        return await PostFormAsync(
            "https://api.vc.bilibili.com/web_im/v1/web_im/send_msg",
            new Dictionary<string, string>
            {
                ["msg[sender_uid]"] = me.ToString(),
                ["msg[receiver_id]"] = receiverId.ToString(),
                ["msg[receiver_type]"] = "1",   // 1 = 用户
                ["msg[msg_type]"] = "1",        // 1 = 文字
                ["msg[msg_status]"] = "0",
                ["msg[content]"] = JsonSerializer.Serialize(new { content = text }),
                ["msg[timestamp]"] = ts,
                ["msg[dev_id]"] = Guid.NewGuid().ToString("N").ToUpperInvariant(),
                // 官方文档写的是 csrf_token, 但别的实现里也用 csrf; 两个都带上最稳
                ["csrf_token"] = SessionManager.Instance.Current.BiliJct ?? "",
                ["build"] = "0",
                ["mobi_app"] = "web"
            },
            referer: MsgReferer);
    }

    /// <summary>把某个会话标记为已读(失败无所谓, 只是未读数不准)</summary>
    public async Task<bool> MarkMsgReadAsync(long talkerId, long ackSeqno)
    {
        if (talkerId <= 0 || ackSeqno <= 0) return false;
        try
        {
            var result = await PostFormAsync(
                "https://api.vc.bilibili.com/session_svr/v1/session_svr/update_ack",
                new Dictionary<string, string>
                {
                    ["talker_id"] = talkerId.ToString(),
                    ["session_type"] = "1",
                    ["ack_seqno"] = ackSeqno.ToString(),
                    ["build"] = "0",
                    ["mobi_app"] = "web"
                },
                referer: MsgReferer);
            return result.ok;
        }
        catch { return false; }
    }



    private async Task<string?> RegisterVoucherAsync(string voucherId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.bilibili.com/x/gaia-vgate/v1/register")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { voucher_id = voucherId }),
                    Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            // ★ 先补指纹再拼 cookie(2026-10-31): 未登录时 BuildCookieHeader() 可能一个 cookie
            //   都不剩 —— 而 gaia-vgate 是风控网关, 裸游客请求它大概率不放行, voucher 验证
            //   流程等于白走。EnsureBuvidAsync 有内存/磁盘两级缓存, 已有指纹时零网络开销。
            await EnsureBuvidAsync();
            var cookie = AppendFingerprint(SessionManager.Instance.BuildCookieHeader());
            if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var resp = await SendThrottledAsync(req);
            if (resp == null) return null;
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var c) && c.GetInt32() == 0 &&
                root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "token", "v_token", "f_token" })
                {
                    if (d.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(t.GetString()))
                        return t.GetString();
                }
            }
        }
        catch
        {
            // 忽略
        }
        return null;
    }

}
