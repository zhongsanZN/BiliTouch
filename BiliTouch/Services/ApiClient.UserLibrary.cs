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
    /// <summary>关注列表(关注的 UP 主)。vmid=自己 mid, pn=页码, ps=每页数量(<=50)</summary>
    public async Task<(bool ok, string? err, List<FollowUser>? list)> GetFollowingsAsync(long vmid, int pn = 1, int ps = 30)
    {
        try
        {
            await EnsureBuvidAsync();
            var ps2 = new Dictionary<string, string>
            {
                ["vmid"] = vmid.ToString(),
                ["pn"] = pn.ToString(),
                ["ps"] = Math.Min(ps, 50).ToString(),
                ["order"] = "desc"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation/followings", ps2);
            if (code != 0 || data == null)
            {
                var errMsg = msg ?? $"请求失败 (code {code})";
                if (code == -101) errMsg = "请先登录";
                return (false, errMsg, null);
            }

            var users = new List<FollowUser>();
            if (data.Value.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var u = new FollowUser
                    {
                        Mid = GetLong(e, "mid"),
                        Name = GetStr(e, "uname"),
                        Face = UrlUtil.Normalize(GetStr(e, "face")),
                        Sign = GetStr(e, "sign")
                    };
                    if (e.TryGetProperty("official_verify", out var ov) && ov.ValueKind == JsonValueKind.Object)
                    {
                        u.VerifyType = GetInt(ov, "type");
                        u.VerifyDesc = GetStr(ov, "desc");
                    }
                    if (u.Mid > 0 && !string.IsNullOrEmpty(u.Name)) users.Add(u);
                }
            }
            return (true, null, users);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    public async Task<(bool ok, string? err, List<FollowUser>? list)> GetFollowersAsync(long vmid, int pn = 1, int ps = 30)
    {
        try
        {
            await EnsureBuvidAsync();
            var ps2 = new Dictionary<string, string>
            {
                ["vmid"] = vmid.ToString(),
                ["pn"] = pn.ToString(),
                ["ps"] = Math.Min(ps, 50).ToString(),
                ["order"] = "desc"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation/followers", ps2);
            if (code != 0 || data == null)
            {
                var errMsg = msg ?? $"请求失败 (code {code})";
                if (code == -101) errMsg = "请先登录";
                return (false, errMsg, null);
            }

            var users = new List<FollowUser>();
            if (data.Value.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var u = new FollowUser
                    {
                        Mid = GetLong(e, "mid"),
                        Name = GetStr(e, "uname"),
                        Face = UrlUtil.Normalize(GetStr(e, "face")),
                        Sign = GetStr(e, "sign")
                    };
                    if (e.TryGetProperty("official_verify", out var ov) && ov.ValueKind == JsonValueKind.Object)
                    {
                        u.VerifyType = GetInt(ov, "type");
                        u.VerifyDesc = GetStr(ov, "desc");
                    }
                    if (u.Mid > 0 && !string.IsNullOrEmpty(u.Name)) users.Add(u);
                }
            }
            return (true, null, users);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }


    /// <summary>获取 UP 主空间信息。
    /// acc/info 系列接口风控严格(未带完整设备指纹时返回 -403「访问权限不足」),
    /// 因此采用多接口降级策略:
    ///   1. WBI 签名的 /x/space/wbi/acc/info (信息最全)
    ///   2. 公开的 /x/web-interface/card (网页卡片悬浮预览接口, 风控宽松, 附带粉丝/投稿数)
    ///   3. 旧版 /x/space/acc/info
    /// 全部失败才返回 ok=false。★ 2026-10-01 由裸 <c>Task&lt;SpaceInfo?&gt;</c> 改为 (ok, err, data):
    /// 三个降级接口的 code 原本全被丢掉了, 调用方只知道"没取到", 排查风控时无从下手。</summary>
    public async Task<(bool ok, string? err, SpaceInfo? data)> GetSpaceInfoAsync(long mid)
    {
        try
        {
            await EnsureBuvidAsync();
            var args = new Dictionary<string,string> { ["mid"] = mid.ToString() };
            var referer = $"https://space.bilibili.com/{mid}/";
            var cardTask = GetJsonAsync("https://api.bilibili.com/x/web-interface/card",args,referer:referer);
            var statTask = GetJsonAsync("https://api.bilibili.com/x/relation/stat",new Dictionary<string,string> { ["vmid"] = mid.ToString() },referer:referer);
            var (code,_,data) = await GetJsonAsync("https://api.bilibili.com/x/space/wbi/acc/info",args,sign:true,referer:referer);
            var (cardCode,_,cardData) = await cardTask;
            var (statCode,_,statData) = await statTask;
            SpaceInfo? info = null;
            if(code==0 && data is {} d)info=ReadSpaceProfile(d,mid);
            if(cardCode==0 && cardData is {} cd && cd.TryGetProperty("card",out var card) && card.ValueKind==JsonValueKind.Object)
            {
                info ??= ReadSpaceProfile(card,mid);
                if(card.TryGetProperty("level_info",out var level))info.Level=GetInt(level,"current_level");
                info.Follower=cd.TryGetProperty("follower",out _)?GetLong(cd,"follower"):GetLong(card,"fans");
                info.Following=card.TryGetProperty("friend",out _)?GetLong(card,"friend"):GetLong(card,"attention");
                info.RelationStatsKnown=card.TryGetProperty("friend",out _) || card.TryGetProperty("attention",out _);
                info.VideoCount=GetLong(cd,"archive_count");info.VideoCountKnown=cd.TryGetProperty("archive_count",out _);
            }
            if(info==null)
            {
                (code,_,data)=await GetJsonAsync("https://api.bilibili.com/x/space/acc/info",args,referer:referer);
                if(code==0 && data is {} legacy)info=ReadSpaceProfile(legacy,mid);
            }
            if(info==null)return(false,"个人资料暂时无法加载，请稍后重试",null);
            if(statCode==0 && statData is {} stat && stat.TryGetProperty("following",out _) && stat.TryGetProperty("follower",out _))
            {info.Following=GetLong(stat,"following");info.Follower=GetLong(stat,"follower");info.RelationStatsKnown=true;}
            return(true,null,info);
        }
        catch(Exception ex){return(false,"获取个人资料失败："+ex.Message,null);}
    }
    private static SpaceInfo ReadSpaceProfile(JsonElement d,long mid)=>new()
    {Mid=mid,Name=GetStr(d,"name"),Face=UrlUtil.Normalize(GetStr(d,"face")),Sign=GetStr(d,"sign"),Level=GetInt(d,"level")};

    /// <summary>获取 UP 主空间视频列表。
    /// 优先 arc/search(WBI 签名); 被风控(-403/-352)时降级到公开的 recArchivesByKeywords</summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)>
        GetSpaceVideosAsync(long mid, int pn = 1, int ps = 30)
    {
        try
        {
            await EnsureBuvidAsync();
            var ps2 = new Dictionary<string, string>
            {
                ["mid"] = mid.ToString(),
                ["pn"] = pn.ToString(),
                ["ps"] = Math.Min(ps, 50).ToString(),
                ["order"] = "pubdate",
                ["platform"] = "web",
                ["web_location"] = "1550101"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/space/wbi/arc/search", ps2, sign: true,
                referer: $"https://space.bilibili.com/{mid}/video");
            if (code != 0 || data == null)
            {
                // 风控降级: 公开的合集推荐接口, 不需要签名, 返回完整 archive 对象
                return await GetSpaceVideosFallbackAsync(mid, pn, ps);
            }

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Object
                && arr.TryGetProperty("vlist", out var vl) && vl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in vl.EnumerateArray())
                {
                    var item = ParseVideoItem(e);
                    if (item != null) items.Add(item);
                }
            }
            if (items.Count == 0) return await GetSpaceVideosFallbackAsync(mid, pn, ps);
            return (true, null, items);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    /// <summary>空间视频列表降级接口(series/recArchivesByKeywords, 公开无签名)。
    /// 返回 data.archives 数组, 字段与 arc/search 的 vlist 略有不同</summary>
    private async Task<(bool ok, string? err, List<VideoItem>? items)>
        GetSpaceVideosFallbackAsync(long mid, int pn, int ps)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/series/recArchivesByKeywords",
                new Dictionary<string, string>
                {
                    ["mid"] = mid.ToString(),
                    ["keywords"] = "",
                    ["pn"] = pn.ToString(),
                    ["ps"] = Math.Min(ps, 50).ToString()
                },
                referer: $"https://space.bilibili.com/{mid}/video");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("archives", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    // ★ 走通用解析器(2026-10-15): 这里原先是手抄的一份, 只认 owner/pubdate,
                    //   而这个接口的 archives 给的是 **upMid + pubdate + duration(秒)** ——
                    //   手抄版把 OwnerMid 读成 0、头像/名字全丢。通用解析器两种写法都认, 不必两份。
                    var item = ParseVideoItem(e);
                    if (item == null) continue;
                    // 空间投稿: 没给 mid 时就用当前空间主(这条列表本来就是他的)
                    if (item.OwnerMid <= 0) item.OwnerMid = mid;
                    items.Add(item);
                }
            }
            return (true, null, items);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }


    /// <summary>
    /// 登录用户信息(nav)。
    ///
    /// ★ 2026-10-01 由裸 <c>Task&lt;UserState?&gt;</c> 改为 (ok, err, data):
    ///   原来"未登录"和"请求失败"都塌成同一个 null, 而这个方法有 5 个调用点,
    ///   其中 MainViewModel.RefreshUserAsync 靠它判断登录态 —— 分不清两者时,
    ///   网络抖一下也会被当成"登录过期", 界面上直接显示"登录状态已过期"。
    /// </summary>
    public async Task<(bool ok, string? err, UserState? data)> GetUserStateAsync()
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/nav", null);
            if (code != 0 || data == null) return (false, msg ?? $"nav 请求失败 (code {code})", null);

            var d = data.Value;
            // 注意: 未登录时 nav 仍返回 code=0, 只是 isLogin=false —— 这是**正常状态**不是错误,
            // 所以 ok=true(查询本身成功), 由调用方看 data.IsLogin 决定怎么显示。
            if (!d.TryGetProperty("isLogin", out var l) || !l.GetBoolean())
                return (true, null, new UserState { IsLogin = false });

            var u = new UserState
            {
                IsLogin = true,
                Name = GetStr(d, "uname"),
                Face = UrlUtil.Normalize(GetStr(d, "face")),
                Mid = GetLong(d, "mid"),
                Level = d.TryGetProperty("level_info", out var li) ? GetInt(li, "current_level") : 0,
                Coins = d.TryGetProperty("money", out var mo) && mo.ValueKind == JsonValueKind.Number
                    ? mo.GetDouble() : 0
            };
            // 经验值(「我的」页显示"经验 x/y"): level_info.current_exp / next_exp
            if (d.TryGetProperty("level_info", out var lev))
            {
                u.ExperienceCurrent = GetLong(lev, "current_exp");
                u.ExperienceNext = GetLong(lev, "next_exp");
            }
            return (true, null, u);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取登录信息失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 用户关系数(关注/粉丝)。接口 /x/relation/stat, 匿名即可用(2026-09-26 实测, 与登录态无关)。
    ///
    /// ★ 2026-10-01 去掉 (-1,-1) 哨兵, 改为 (ok, err, following, follower):
    ///   哨兵值的坏处是"真的 0 个粉丝"和"取不到"都是 -1, 调用方必须再自己判一次,
    ///   而且失败原因(B 站风控? 未登录? 网络?)一点都传不出来。现在 ok=false 时 err 说明原因,
    ///   界面再决定显示 "--"。
    /// </summary>
    public async Task<(bool ok, string? err, long following, long follower)> GetRelationStatAsync(long mid)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation/stat",
                new Dictionary<string, string> { ["vmid"] = mid.ToString() },
                referer: $"https://space.bilibili.com/{mid}/");
            if (code != 0 || data == null) return (false, msg ?? $"relation/stat 请求失败 (code {code})", 0, 0);
            return (true, null, GetLong(data.Value, "following"), GetLong(data.Value, "follower"));
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取关注/粉丝数失败: " + ex.Message, 0, 0);
        }
    }

    /// <summary>
    /// 当前登录用户的计数三件套: 关注 / 粉丝 / 动态数。
    /// 接口: /x/web-interface/nav/stat(需要登录 Cookie, api.bilibili.com 域)。
    ///
    /// ★ 为什么不再用 api.vc.bilibili.com 的 dynamic_svr/num: 实测(2026-09-26)匿名请求
    /// 被风控页拦(返 HTML), 应用内带 Cookie 也拿不到 → 界面显示 "--"。
    /// nav/stat 一次给齐三个数, 动态数字段是 dynamic_count。
    /// ★ 2026-10-01 去掉 (-1,-1,-1) 哨兵, 改为 (ok, err, ...) —— 同 GetRelationStatAsync。
    /// </summary>
    public async Task<(bool ok, string? err, long following, long follower, long dynamicCount)> GetUserNavStatAsync()
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/nav/stat", null);
            if (code != 0 || data == null) return (false, msg ?? $"nav/stat 请求失败 (code {code})", 0, 0, 0);
            return (true, null,
                GetLong(data.Value, "following"),
                GetLong(data.Value, "follower"),
                GetLong(data.Value, "dynamic_count"));
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取用户计数失败: " + ex.Message, 0, 0, 0);
        }
    }

    /// <summary>
    /// 当前登录用户的收藏夹列表(**带封面/隐私字段**, 「我的」页的文件夹卡片用)。
    /// 接口: /x/v3/fav/folder/created/list(分页接口, pn=1/ps=50 一次拿够)。
    /// 与 GetFavFoldersAsync(list-all) 的区别: 那个只取 id/标题/数量(收藏页 chip 够用),
    /// 这个才下发 cover 和 privacy。失败语义与 GetFavFoldersAsync 一致。
    /// </summary>
    public async Task<(bool ok, string? err, List<FavFolder>? folders)> GetFavFoldersFullAsync()
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端收藏需要登录", null);
            var mid = SessionManager.Instance.Current.DedeUserID;
            if (string.IsNullOrEmpty(mid)) return (false, "无法获取用户 mid", null);

            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/folder/created/list",
                new Dictionary<string, string>
                {
                    ["up_mid"] = mid,
                    ["pn"] = "1",
                    ["ps"] = "50",
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://space.bilibili.com/{mid}/favlist");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<FavFolder>();
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    // ★ 公开/私密不是 privacy 字段 —— 列表接口里根本没有它, 藏在 attr 位掩码里:
                    //   attr bit0 = 是否默认收藏夹, attr bit1 = 1 表示私密(bilibili-API-collect 收藏夹文档)。
                    //   之前按 privacy 解析永远是 0, 私密夹全显示成"公开"。
                    var attr = GetLong(f, "attr");
                    list.Add(new FavFolder
                    {
                        Id = GetLong(f, "id"),
                        Title = GetStr(f, "title"),
                        MediaCount = GetInt(f, "media_count"),
                        Cover = UrlUtil.Normalize(GetStr(f, "cover")),
                        Privacy = (attr & 2) != 0 ? 1 : 0
                    });
                }
            }
            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }


    /// <summary>
    /// 获取登录用户的**直播**观看历史。
    ///
    /// ★★★ 接口选型(2026-10-03 实测定的, 别换回 /x/v2/history):
    ///   旧的那条 `/x/v2/history` **只给视频稿件**, 直播条目它根本不返回; 而且 `type=live`
    ///   作为参数传过去会直接 -400(实测)。
    ///   真正管用的是**新游标接口** `/x/web-interface/history/cursor`, 它自带一个 `tab` 数组:
    ///       [{type:"archive",name:"视频"}, {type:"live",name:"直播"}, {type:"article",name:"专栏"}]
    ///   —— 这正是官方历史页那三个标签, 说明官方自己就是靠 `type` 分流的。传 `type=live`
    ///   返回的就是真实直播记录(实测拿到 kid=22603245 / uri=https://live.bilibili.com/22603245)。
    ///   ★ 参数名必须是 **type**。`business=live` / `tab=live` / `business_type=live` 都会被忽略、
    ///     静默返回视频记录(实测), 那种"看起来成功但内容不对"的错最难查。
    ///
    /// 分页: 游标式 —— 用上一页返回的 `cursor.view_at` 与 `cursor.max` 当下一页的
    /// `view_at` / `max`。这里对外仍保持"页码"形状(pn), 内部用保存下来的游标推进,
    /// 免得把游标状态泄漏给页面/VM。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetLiveHistoryAsync(int pn = 1)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "直播历史需要登录", null);
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["type"] = "live",
                ["ps"] = "20"
            };
            // 第 1 页不带游标; 之后用上一次拿到的游标(见 _liveCursor)
            if (pn > 1 && _liveCursorViewAt > 0)
            {
                ps["view_at"] = _liveCursorViewAt.ToString();
                ps["max"] = _liveCursorMax.ToString();
            }

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/history/cursor", ps,
                referer: "https://www.bilibili.com/account/history");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in list.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.Object) continue;

                    // 直播记录的"房间号"有两个来源, 都试一遍:
                    //   · 顶层 kid —— 实测就是房间号(22603245);
                    //   · history.oid —— 同为房间号, 顶层缺失时兜底。
                    var roomId = GetLong(h, "kid");
                    if (roomId <= 0 && h.TryGetProperty("history", out var hist))
                        roomId = GetLong(hist, "oid");
                    if (roomId <= 0) continue;

                    var cover = GetStr(h, "cover");
                    if (cover.Length == 0 && h.TryGetProperty("covers", out var covers) &&
                        covers.ValueKind == JsonValueKind.Array && covers.GetArrayLength() > 0)
                        cover = covers[0].GetString() ?? "";

                    // 主播名 / mid: 顶层 author_name / author_mid(与视频那条同一组字段名)
                    var author = GetStr(h, "author_name");
                    var ownerMid = GetLong(h, "author_mid");
                    if (h.TryGetProperty("owner", out var owner))
                    {
                        if (author.Length == 0) author = GetStr(owner, "name");
                        if (ownerMid <= 0) ownerMid = GetLong(owner, "mid");
                    }

                    items.Add(new VideoItem
                    {
                        RoomId = roomId,
                        Title = GetStr(h, "title").StripHtml(),
                        Cover = UrlUtil.Normalize(cover),
                        Author = author,
                        OwnerMid = ownerMid,
                        // 时长位显示开播状态: live_status 1=直播中, 0=未开播(接口还给 badge 文案)
                        Duration = GetInt(h, "live_status") == 1 ? "直播中" : "未开播",
                        ViewCount = 0,   // 历史接口不给人气, 留 0 让卡片隐藏那个胶囊
                        Pubdate = GetLong(h, "view_at"),
                    });
                }
            }

            // 记下游标供下一页用。到底时 cursor 会归零(max/view_at 都是 0), 下次自然空手而归。
            if (data.Value.TryGetProperty("cursor", out var cur))
            {
                _liveCursorViewAt = GetLong(cur, "view_at");
                _liveCursorMax = GetLong(cur, "max");
            }
            else
            {
                _liveCursorViewAt = 0;
                _liveCursorMax = 0;
            }

            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>直播历史的游标(接口是游标式, 不是页码式)。由 <see cref="GetLiveHistoryAsync"/> 维护。</summary>
    private long _liveCursorViewAt;
    private long _liveCursorMax;

    /// <summary>重置直播历史游标(重新从头拉之前调, 否则第 2 页会接着上次的位置)</summary>
    public void ResetLiveHistoryCursor()
    {
        _liveCursorViewAt = 0;
        _liveCursorMax = 0;
    }

    /// <summary>获取登录用户的云端观看历史(x/v2/history, 分页)。
    /// 返回 (ok, err, items): items 为 VideoItem 列表, IsSelected 可复用</summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetCloudHistoryAsync(int pn = 1, int ps = 30)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端历史需要登录", null);
            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/history",
                new Dictionary<string, string>
                {
                    ["pn"] = pn.ToString(),
                    ["ps"] = ps.ToString(),
                    ["platform"] = "web"
                },
                sign: true,
                referer: "https://www.bilibili.com/account/history");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in data.Value.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.Object) continue;
                    var bvid = GetStr(h, "bvid");
                    if (string.IsNullOrEmpty(bvid)) continue;
                    // 新旧版字段兼容: pic/cover, author_name/owner.name
                    var cover = GetStr(h, "pic");
                    if (cover.Length == 0 && h.TryGetProperty("covers", out var covers) &&
                        covers.ValueKind == JsonValueKind.Array && covers.GetArrayLength() > 0)
                        cover = covers[0].GetString() ?? "";
                    string author = GetStr(h, "author_name");
                    // 历史接口的 UP 主 mid 在顶层 author_mid; 老版结构在 owner.mid
                    long ownerMid = GetLong(h, "author_mid");
                    if (h.TryGetProperty("owner", out var owner))
                    {
                        if (author.Length == 0) author = GetStr(owner, "name");
                        if (ownerMid <= 0) ownerMid = GetLong(owner, "mid");
                    }
                    items.Add(new VideoItem
                    {
                        Bvid = bvid,
                        Aid = GetLong(h, "aid"),
                        Title = GetStr(h, "title").StripHtml(),
                        Cover = UrlUtil.Normalize(cover),
                        Author = author,
                        OwnerMid = ownerMid,
                        Duration = VideoItem.FormatSeconds(GetInt(h, "duration")),
                        // 播放量在 **stat.view** 里, 顶层没有 view 字段。
                        // 之前写成 GetLong(h, "view") 拿到的一直是 0, 于是云端历史每条都显示"0 播放"。
                        // 另外 stat.view 为 -1 表示"播放量被屏蔽", 当成 0 处理而不是显示负数。
                        ViewCount = Math.Max(0, GetNestedLong(h, "stat", "view")),
                        DanmakuCount = Math.Max(0, GetNestedLong(h, "stat", "danmaku")),
                        LikeCount = Math.Max(0, GetNestedLong(h, "stat", "like")),
                        Pubdate = GetLong(h, "view_at")
                    });
                }
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    // ---------------------------------------------------------------- 删除 / 清空

    /// <summary>
    /// 删除**单条**云端观看历史。
    /// 接口: /x/v2/history/delete (POST)。
    ///
    /// 参数 `kid` 不是裸 id, 而是 `{业务类型}_{目标id}`:
    ///   · 视频 → `archive_{avid}`(稿件 avid, 不是 bvid)
    ///   · 直播 → `live_{房间号}`
    /// ★ 前缀写错不会报错, 而是**静默不生效**(接口对未知 kid 也回 code=0, 实测),
    ///   所以视频和直播必须各传对(2026-10-03 加直播历史时踩到这条)。
    /// </summary>
    public async Task<(bool ok, string? err)> DeleteHistoryAsync(long aid, ContentKind kind = ContentKind.Video)
    {
        var prefix = kind == ContentKind.Live ? "live" : "archive";
        if (aid <= 0) return (false, kind == ContentKind.Live ? "缺少直播间号" : "缺少视频 avid");
        if (!SessionManager.Instance.HasLogin) return (false, "云端历史需要登录");

        return await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/delete",
            new Dictionary<string, string> { ["kid"] = $"{prefix}_{aid}" },
            referer: "https://www.bilibili.com/account/history");
    }

    /// <summary>清空**全部**云端观看历史。接口: /x/v2/history/clear (POST), 只需要 csrf。</summary>
    public async Task<(bool ok, string? err)> ClearHistoryAsync()
    {
        if (!SessionManager.Instance.HasLogin) return (false, "云端历史需要登录");

        return await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/clear",
            new Dictionary<string, string>(),
            referer: "https://www.bilibili.com/account/history");
    }

    /// <summary>
    /// 批量把视频从**指定收藏夹**里移出。
    /// 接口: /x/v3/fav/resource/batch-del (POST, 注意是连字符不是斜杠)。
    ///
    /// `resources` 的格式是 `{内容id}:{内容类型}`, 成员之间用逗号分隔 ——
    /// 内容 id 是**稿件 avid**(不是 bvid, 也不需要 cid), 视频的 type 固定为 2。
    /// 一次请求可以塞多个, 所以"批量移出"不需要挨个调 deal 接口。
    /// </summary>
    public async Task<(bool ok, string? err)> BatchDelFavResourcesAsync(long mediaId, IEnumerable<long> aids)
    {
        if (mediaId <= 0) return (false, "缺少收藏夹 id");
        if (!SessionManager.Instance.HasLogin) return (false, "收藏需要登录");

        var res = string.Join(",", aids.Where(x => x > 0).Select(x => $"{x}:2"));
        if (res.Length == 0) return (false, "没有可移出的内容");

        var mid = SessionManager.Instance.Current.DedeUserID;
        return await PostFormAsync(
            "https://api.bilibili.com/x/v3/fav/resource/batch-del",
            new Dictionary<string, string>
            {
                ["resources"] = res,
                ["media_id"] = mediaId.ToString(),
                ["platform"] = "web"
            },
            referer: $"https://space.bilibili.com/{mid}/favlist?fid={mediaId}");
    }

    // ---------------------------------------------------------------- 云端收藏夹

    /// <summary>
    /// 获取当前登录用户的云端收藏夹列表。
    /// 接口: /x/v3/fav/folder/created/list-all —— 一次拿全(不分页), 返回 (ok, err, folders)
    /// </summary>
    public async Task<(bool ok, string? err, List<FavFolder>? folders)> GetFavFoldersAsync()
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端收藏需要登录", null);
            var mid = SessionManager.Instance.Current.DedeUserID;
            if (string.IsNullOrEmpty(mid)) return (false, "无法获取用户 mid", null);

            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/folder/created/list-all",
                new Dictionary<string, string>
                {
                    ["up_mid"] = mid,
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://space.bilibili.com/{mid}/favlist");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<FavFolder>();
            // data.list 是收藏夹数组; 部分账号返回 null 表示没有自建收藏夹
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    list.Add(new FavFolder
                    {
                        Id = GetLong(f, "id"),
                        Title = GetStr(f, "title"),
                        MediaCount = GetInt(f, "media_count")
                    });
                }
            }
            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 获取某个云端收藏夹内的视频(分页)。
    /// 接口: /x/v3/fav/resource/list —— 需要 wbi 签名, 未登录时会被风控。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items, bool hasMore)>
        GetFavResourcesAsync(long mediaId, int pn = 1, int ps = 20)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端收藏需要登录", null, false);
            await EnsureBuvidAsync();
            var mid = SessionManager.Instance.Current.DedeUserID;

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/resource/list",
                new Dictionary<string, string>
                {
                    ["media_id"] = mediaId.ToString(),
                    ["pn"] = pn.ToString(),
                    ["ps"] = ps.ToString(),
                    ["keyword"] = "",
                    ["order"] = "mtime",   // 按收藏时间倒序
                    ["type"] = "0",
                    ["tid"] = "0",
                    ["platform"] = "web",
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://space.bilibili.com/{mid}/favlist?fid={mediaId}");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null, false);

            var items = new List<VideoItem>();
            var hasMore = false;
            if (data.Value.ValueKind == JsonValueKind.Object)
            {
                hasMore = data.Value.TryGetProperty("has_more", out var hm) &&
                          hm.ValueKind == JsonValueKind.True;
                if (data.Value.TryGetProperty("medias", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;
                        // 失效视频的 title 为 "已失效视频", bvid 为空 —— 跳过, 否则点开是空窗
                        var bvid = GetStr(m, "bvid");
                        if (string.IsNullOrEmpty(bvid)) continue;
                        // 播放量藏在 cnt_info.play 里(upper 也可能为空, 用 upper.name 兜底)
                        var author = GetStr(m, "upper");
                        if (author.Length == 0) author = GetUpperName(m);
                        items.Add(new VideoItem
                        {
                            Bvid = bvid,
                            // 收藏夹条目里 aid 字段名是 "id"
                            Aid = GetLong(m, "id"),
                            Title = GetStr(m, "title").StripHtml(),
                            Cover = UrlUtil.Normalize(GetStr(m, "cover")),
                            Author = author,
                            // upper 是对象时拿它的 mid(upper 为纯字符串的老格式则取不到, 保持 0)
                            OwnerMid = GetNestedLong(m, "upper", "mid"),
                            Duration = VideoItem.FormatSeconds(GetInt(m, "duration")),
                            ViewCount = GetNestedLong(m, "cnt_info", "play"),
                            DanmakuCount = GetNestedLong(m, "cnt_info", "danmaku"),
                            Pubdate = GetLong(m, "pubtime")
                        });
                    }
                }
            }
            return (true, null, items, hasMore);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, false);
        }
    }

    /// <summary>
    /// 查询"这个视频被收藏进了哪些收藏夹"。
    ///
    /// 接口必须用 **/x/v3/fav/folder/created/list-all**。
    /// 之前写成 /x/v3/fav/folder/created/list(没有 -all), 那个接口**不接受 rid 参数**,
    /// 带上它只会得到 -400「请求错误」—— 这就是"收藏状态获取失败: 请求错误"的根因。
    /// list-all 加上 rid 后, 每个收藏夹会多一个 fav_state 字段表示该视频是否在里面,
    /// 于是"是否已收藏"和"该往哪个夹放"一次拿到。
    /// </summary>
    public async Task<(bool ok, string? err, List<FavFolder>? folders)> GetVideoFavFoldersAsync(long aid)
    {
        try
        {
            if (aid <= 0) return (false, "缺少视频 aid", null);
            if (!SessionManager.Instance.HasLogin) return (false, "收藏需要登录", null);
            var mid = SessionManager.Instance.Current.DedeUserID;
            if (string.IsNullOrEmpty(mid)) return (false, "无法获取用户 mid", null);

            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/folder/created/list-all",
                new Dictionary<string, string>
                {
                    ["up_mid"] = mid,
                    ["type"] = "2",        // 2 = 视频稿件
                    ["rid"] = aid.ToString(),
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://www.bilibili.com/video/");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<FavFolder>();
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    list.Add(new FavFolder
                    {
                        Id = GetLong(f, "id"),
                        Title = GetStr(f, "title"),
                        MediaCount = GetInt(f, "media_count"),
                        // fav_state 为 1 表示该视频已在此收藏夹中
                        HasVideo = GetInt(f, "fav_state") == 1,
                        IsDefault = GetInt(f, "is_default") == 1 || GetStr(f, "title") == "默认收藏夹"
                    });
                }
            }
            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 收藏 / 取消收藏(现行 Web 端接口, 双端通用)。
    /// 接口: /x/v3/fav/resource/deal
    ///   rid          稿件 avid (**不是 bvid**)
    ///   type         固定 2(视频稿件)
    ///   add_media_ids 要加入的收藏夹 mlid, 多个用逗号分隔
    ///   del_media_ids 要移出的收藏夹 mlid, 多个用逗号分隔
    /// 已知业务码: 11201 已经收藏过了 / 11202 已经取消收藏了 —— 这两种对用户来说都是"目标已达成",
    /// 所以在上层当成成功处理, 不要弹红字报错。
    /// </summary>
    public async Task<(bool ok, string? err)> DealFavoriteAsync(
        long aid, IEnumerable<long>? addIds = null, IEnumerable<long>? delIds = null)
    {
        if (aid <= 0) return (false, "缺少视频 aid");
        if (!SessionManager.Instance.HasLogin) return (false, "收藏需要登录");

        var add = addIds == null ? "" : string.Join(",", addIds.Where(x => x > 0));
        var del = delIds == null ? "" : string.Join(",", delIds.Where(x => x > 0));
        if (add.Length == 0 && del.Length == 0) return (false, "未指定收藏夹");

        var mid = SessionManager.Instance.Current.DedeUserID;
        var ps = new Dictionary<string, string>
        {
            ["rid"] = aid.ToString(),
            ["type"] = "2",
            ["platform"] = "web"
        };
        if (add.Length > 0) ps["add_media_ids"] = add;
        if (del.Length > 0) ps["del_media_ids"] = del;

        var (code, msg) = await PostFormCodeAsync(
            "https://api.bilibili.com/x/v3/fav/resource/deal",
            ps,
            referer: $"https://space.bilibili.com/{mid}/favlist");

        // 11201/11202: "已经收藏过了 / 已经取消收藏了" —— 用户想要的状态本来就成立,
        // 属于幂等成功, 不该弹报错(重复点击时非常容易撞上这两个码)
        if (code is 0 or 11201 or 11202) return (true, null);
        return (false, msg);
    }

    private static string GetUpperName(JsonElement m)
        => m.TryGetProperty("upper", out var u) && u.ValueKind == JsonValueKind.Object
            ? GetStr(u, "name") : "";

    private static long GetNestedLong(JsonElement e, string objName, string propName)
        => e.TryGetProperty(objName, out var o) && o.ValueKind == JsonValueKind.Object
            ? GetLong(o, propName) : 0;

    // ---------------------------------------------------------------- 稍后再看

    /// <summary>
    /// 获取「稍后再看」列表。
    /// 接口: GET /x/v2/history/toview(需要登录 Cookie, 不需要 WBI 签名)。
    ///
    /// 返回形状实测有两种(服务端改过版): `data` 既可能是 `{count, list:[...]}`, 也可能是裸数组 ——
    /// 两种都接住, 免得某次灰度之后这里静默变成"空列表"。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items, int count)> GetWatchLaterAsync()
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录", null, 0);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/history/toview",
                null,
                sign: false,
                referer: "https://www.bilibili.com/watchlater/list");

            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null, 0);

            var count = 0;
            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array)
            {
                arr = data.Value;
                count = arr.GetArrayLength();
            }
            else if (data.Value.TryGetProperty("list", out var lst) && lst.ValueKind == JsonValueKind.Array)
            {
                arr = lst;
                count = GetInt(data.Value, "count");
                if (count <= 0) count = arr.GetArrayLength();
            }
            else
            {
                return (false, "接口返回结构不认识", null, 0);
            }

            var items = new List<VideoItem>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var bvid = GetStr(e, "bvid");
                if (string.IsNullOrEmpty(bvid)) continue;

                var cover = GetStr(e, "pic");
                string author = "";
                long ownerMid = 0;
                if (e.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
                {
                    author = GetStr(owner, "name");
                    ownerMid = GetLong(owner, "mid");
                }
                items.Add(new VideoItem
                {
                    Bvid = bvid,
                    Aid = GetLong(e, "aid"),
                    Title = GetStr(e, "title").StripHtml(),
                    Cover = UrlUtil.Normalize(cover),
                    Author = author,
                    OwnerMid = ownerMid,
                    Duration = VideoItem.FormatSeconds(GetInt(e, "duration")),
                    ViewCount = Math.Max(0, GetNestedLong(e, "stat", "view")),
                    DanmakuCount = Math.Max(0, GetNestedLong(e, "stat", "danmaku")),
                    Pubdate = GetLong(e, "add_at")
                });
            }
            return (true, null, items, count > 0 ? count : items.Count);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, 0);
        }
    }

    /// <summary>
    /// 某条视频是否已在「稍后再看」里。
    ///
    /// 服务端没有"单条查询"的接口, 只能拉整个列表再找 —— 而列表可能有两百多条
    /// (实测某个账号 249 条), 每开一个视频都拉一遍太浪费。所以这里缓存一份 aid 集合,
    /// 60 秒内直接复用; 我们自己增删之后会立刻让缓存失效(见 InvalidateWatchLaterCache)。
    ///
    /// 返回 <c>ok=false</c> = **没查到**(未登录 / 请求失败) —— 调用方不要把 value 当成
    /// "不在列表里", 否则界面会显示一个错误的状态。2026-10-01 由 <c>Task&lt;bool?&gt;</c>
    /// 改为 (ok, err, value), 未登录/查询失败时 err 会说明是哪一种。
    /// </summary>
    public async Task<(bool ok, string? err, bool value)> IsInWatchLaterAsync(long aid)
    {
        if (aid <= 0) return (false, "参数不完整", false);
        if (!SessionManager.Instance.HasLogin) return (false, "未登录", false);

        var set = await GetWatchLaterAidSetAsync();
        if (set == null) return (false, "查询稍后再看失败", false);
        return (true, null, set.Contains(aid));
    }

    private HashSet<long>? _watchLaterAids;
    private SessionAccount _watchLaterAccount;
    private DateTime _watchLaterFetchedAt = DateTime.MinValue;
    private readonly object _watchLaterGate = new();

    /// <summary>aid 集合的缓存有效期。列表本身刷新很慢(实测服务端写入后约 2s 才可见), 60 秒足够新。</summary>
    private static readonly TimeSpan WatchLaterCacheTtl = TimeSpan.FromSeconds(60);

    private async Task<HashSet<long>?> GetWatchLaterAidSetAsync()
    {
        var account = SessionManager.Instance.CaptureAccount();
        lock (_watchLaterGate)
        {
            if (_watchLaterAids != null &&
                _watchLaterAccount.IsCurrent &&
                DateTime.UtcNow - _watchLaterFetchedAt < WatchLaterCacheTtl)
                return _watchLaterAids;
        }

        var (ok, _, items, _) = await GetWatchLaterAsync().ConfigureAwait(false);
        if (!ok || items == null || !account.IsCurrent) return null;   // 失败或切号不缓存, 也不谎报状态

        var set = new HashSet<long>(items.Where(x => x.Aid > 0).Select(x => x.Aid));
        lock (_watchLaterGate)
        {
            if (account.IsCurrent)
            {
                _watchLaterAids = set;
                _watchLaterAccount = account;
                _watchLaterFetchedAt = DateTime.UtcNow;
            }
        }
        return set;
    }

    /// <summary>本机刚改过稍后再看 —— 让 aid 缓存立刻失效, 否则按钮状态会停在旧值上。</summary>
    private void InvalidateWatchLaterCache()
    {
        lock (_watchLaterGate)
        {
            _watchLaterAids = null;
            _watchLaterAccount = default;
            _watchLaterFetchedAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 加入「稍后再看」。
    /// 接口: POST /x/v2/history/toview/add, 参数只有 `aid`(稿件 avid)+ csrf。
    ///
    /// 幂等: 已经在列表里再调一次仍然返回 code 0(服务端不报错也不重复加),
    /// 所以调用方不需要先查一遍列表。
    /// </summary>
    public async Task<(bool ok, string? err)> AddWatchLaterAsync(long aid)
    {
        if (aid <= 0) return (false, "缺少视频 avid");
        if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录");

        var r = await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/toview/add",
            new Dictionary<string, string> { ["aid"] = aid.ToString() },
            referer: "https://www.bilibili.com/");
        if (r.ok) InvalidateWatchLaterCache();
        return r;
    }

    /// <summary>
    /// 从「稍后再看」移除一条。
    /// 接口: POST /x/v2/history/toview/del, 参数 `aid` —— 注意同名的 `viewed=1` 是另一种语义
    /// ("清掉已经看完的"), 这里只做单条移除, 所以永远带 aid。
    /// </summary>
    public async Task<(bool ok, string? err)> RemoveWatchLaterAsync(long aid)
    {
        if (aid <= 0) return (false, "缺少视频 avid");
        if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录");

        var r = await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/toview/del",
            new Dictionary<string, string> { ["aid"] = aid.ToString() },
            referer: "https://www.bilibili.com/watchlater/list");
        if (r.ok) InvalidateWatchLaterCache();
        return r;
    }

    /// <summary>
    /// 清空「稍后再看」。接口: POST /x/v2/history/toview/clear(只需要 csrf)。
    /// 服务端不可撤销, 调用方必须先确认。
    /// </summary>
    public async Task<(bool ok, string? err)> ClearWatchLaterAsync()
    {
        if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录");

        var r = await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/toview/clear",
            new Dictionary<string, string>(),
            referer: "https://www.bilibili.com/watchlater/list");
        if (r.ok) InvalidateWatchLaterCache();
        return r;
    }


    // ---------------------------------------------------------------- 观看进度上报

    /// <summary>
    /// 上报观看进度到 B 站云端历史。
    ///
    /// 接口: /x/v2/history/report (POST 表单, 需 csrf)
    /// 参数: aid / cid / progress(秒) / type=3(投稿视频)
    ///
    /// 调用时机由播放器控制: 起播后节流上报 + 暂停/关闭时补报一次,
    /// 这样"用 BiliTouch 看过的视频"会出现在 App 与网页端的观看记录里。
    /// 未登录直接返回 ok=false, 不产生任何网络请求。
    ///
    /// ★ 2026-10-01 由 <c>Task&lt;bool&gt;</c> 改为 (ok, err): 上报是 fire-and-forget,
    ///   失败**绝不能影响播放**, 但原因仍要带出来 —— 以前"没登录"和"网络失败"都是同一个
    ///   false, 排查"我的视频没进观看记录"时无从下手。
    /// </summary>
    public async Task<(bool ok, string? err)> ReportHistoryAsync(long aid, long cid, long progressSeconds)
    {
        if (!SessionManager.Instance.HasLogin) return (false, "未登录");
        if (aid <= 0 || cid <= 0) return (false, "参数不完整");

        try
        {
            await EnsureBuvidAsync();
            var (ok, err) = await PostFormAsync(
                "https://api.bilibili.com/x/v2/history/report",
                new Dictionary<string, string>
                {
                    ["aid"] = aid.ToString(),
                    ["cid"] = cid.ToString(),
                    ["progress"] = Math.Max(0, progressSeconds).ToString(),
                    ["type"] = "3",
                    ["csrf"] = SessionManager.Instance.Current.BiliJct ?? ""
                },
                referer: "https://www.bilibili.com/");
            return (ok, err);
        }
        catch (Exception ex)
        {
            // 上报失败绝不能影响播放, 但记一笔便于排查
            App.ReportError(ex);
            return (false, "上报失败: " + ex.Message);
        }
    }

}
