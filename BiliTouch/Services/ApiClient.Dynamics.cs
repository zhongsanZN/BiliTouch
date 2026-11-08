using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiliTouch.Models;

namespace BiliTouch.Services;

public partial class ApiClient
{
    public Task<(bool ok, string? err)> SubscribeSeasonAsync(long seasonId) =>
        PostFormAsync("https://api.bilibili.com/x/v3/fav/season/fav", new() { ["season_id"] = seasonId.ToString(), ["platform"] = "web", ["switch"] = "1" });

    public async Task<HashSet<long>> GetDynamicUnreadAuthorsAsync()
    {
        var state = await GetDynamicUnreadStateAsync();
        return state.ok ? state.authors.Where(p => p.Value).Select(p => p.Key).ToHashSet() : new();
    }

    /// <summary>Fetch the official UP list, including its overflow pages. Failure is not an empty read state.</summary>
    public async Task<(bool ok, string? err, Dictionary<long, bool> authors)> GetDynamicUnreadStateAsync()
        => await GetDynamicUnreadStateAsync(includeOverflowPages: true);

    /// <summary>
    /// 取「谁有新动态」的官方快照。
    ///
    /// ★★★ <paramref name="includeOverflowPages"/>（2026-11-08 新增，为"刷动态刷一会就限流"而加）：
    ///   <c>up_list_more=1</c> 时服务端把关注列表**分页**下发，原实现每次调用都把
    ///   后面的页**全部翻完**（关注 200+ 人 ≈ 7~8 次请求）。
    ///   而动态页的未读轮询每 45 秒就调一次它 ⇒ 光"什么都没干"每分钟就要打十几次请求，
    ///   叠上滚动加载的 feed 请求，正是 -412（IP 级限流）的温床。
    ///
    ///   ⇒ 拆成两档：
    ///     · <c>true</c>（默认）= 全量。**进页面 / 手动刷新 / 标记已读后**用这一次，
    ///       保证红点覆盖所有关注的 UP；
    ///     · <c>false</c> = 只读第一页。给**定时轮询**用 —— 轮询只需要"有没有新东西"这个
    ///       粗结论，翻完整个关注列表纯属浪费额度。
    ///   ★ 为什么只读第一页也够：服务端是按"最近更新"排的，有新动态的 UP 一定排在最前。
    ///     真出现"第 2 页的人更新了但第 1 页没变"的极端情况，下一次全量刷新（或用户手动刷新）
    ///     会补上，红点晚几分钟亮远比被限流好。
    /// </summary>
    public async Task<(bool ok, string? err, Dictionary<long, bool> authors)> GetDynamicUnreadStateAsync(
        bool includeOverflowPages)
    {
        var authors = new Dictionary<long, bool>();
        var (code, message, data) = await GetJsonAsync("https://api.bilibili.com/x/polymer/web-dynamic/v1/portal",
            new() { ["up_list_more"] = "1" }, referer: "https://t.bilibili.com/");
        if (code != 0 || data == null) return (false, message ?? "动态未读状态加载失败", authors);
        var root = data.Value;
        if (root.ValueKind != JsonValueKind.Object) return (false, "动态未读状态响应不完整", authors);
        if (!root.TryGetProperty("up_list", out var list))
            return (false, "动态未读状态响应不完整", authors);
        JsonElement more = default;
        if (list.ValueKind == JsonValueKind.Array) ReadUsers(list);
        else if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("items", out var initial) && initial.ValueKind == JsonValueKind.Array)
        {
            // With up_list_more=1 the official response nests items/has_more/offset
            // inside up_list itself; older responses return a plain up_list array.
            ReadUsers(initial);
            more = list;
        }
        else return (false, "动态未读状态响应不完整", authors);
        // ★ 轮询档：首页已经给了结论，不再往下翻（见方法注释里的取舍）。
        if (!includeOverflowPages) return (true, null, authors);
        var cursors = new HashSet<string>();
        while (more.ValueKind == JsonValueKind.Object && more.TryGetProperty("has_more", out var hasMore) &&
               (hasMore.ValueKind == JsonValueKind.True || hasMore.ToString() == "1"))
        {
            var cursor = GetStr(more, "offset");
            if (cursor.Length == 0 || !cursors.Add(cursor) || cursors.Count > 256)
                return (false, "动态未读列表分页不完整，请稍后刷新", authors);
            var next = await GetJsonAsync("https://api.bilibili.com/x/polymer/web-dynamic/v1/uplist",
                new() { ["offset"] = cursor, ["platform"] = "web" }, referer: "https://t.bilibili.com/");
            if (next.code != 0 || next.data == null) return (false, next.msg ?? "动态未读列表翻页失败", authors);
            more = next.data.Value;
            if (!more.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return (false, "动态未读列表分页响应不完整", authors);
            ReadUsers(items);
        }
        return (true, null, authors);

        void ReadUsers(JsonElement users)
        {
            if (users.ValueKind != JsonValueKind.Array) return;
            foreach (var user in users.EnumerateArray())
            {
                var mid = GetLongLoose(user, "mid");
                if (mid <= 0 || !user.TryGetProperty("has_update", out var update)) continue;
                authors[mid] = update.ValueKind == JsonValueKind.True || update.ToString() == "1";
            }
        }
    }

    public async Task<(bool ok, string? err, DynamicItem? item)> GetDynamicDetailAsync(string id)
    {
        if (!long.TryParse(id, out var value) || value <= 0) return (false,"动态 ID 无效",null);
        var (code,msg,data) = await GetJsonAsync("https://api.bilibili.com/x/polymer/web-dynamic/v1/detail",
            new() { ["id"] = id, ["features"] = "itemOpusStyle" }, referer:"https://t.bilibili.com/");
        if (code != 0 || data == null) return (false,msg ?? "动态加载失败",null);
        if (!data.Value.TryGetProperty("item",out var item)) return (false,"动态不存在或已删除",null);
        var parsed = DynamicParser.Parse(item);
        return parsed == null ? (false,"动态内容无法读取",null) : (true,null,parsed);
    }

    public async Task<(bool ok,string? err,List<CommentItem> items,bool hasMore,long total)> GetDynamicRepliesAsync(
        string oid,int type,int page=1,int sort=2)
    {
        var items = new List<CommentItem>();
        if (!long.TryParse(oid,out var id) || id <= 0 || type <= 0) return (false,"这条动态暂未提供评论区信息",items,false,0);
        var (code,msg,data) = await GetJsonAsync("https://api.bilibili.com/x/v2/reply",
            new() { ["oid"]=oid,["type"]=type.ToString(),["pn"]=page.ToString(),["ps"]="20",["sort"]=sort.ToString() },
            referer:"https://t.bilibili.com/");
        if (code != 0 || data == null) return (false,msg ?? "评论加载失败",items,false,0);
        var d=data.Value;
        var upper = d.TryGetProperty("upper",out var u) ? GetLongLoose(u,"mid") : 0;
        if (page == 1 && d.TryGetProperty("top",out var top) && top.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var pinned=TryObject(top,"upper") ?? TryObject(top,"admin");
            if (pinned != null) { var c=ParseComment(pinned.Value,true,upper);c.IsPinned=true;items.Add(c); }
        }
        var count=0;
        if (d.TryGetProperty("replies",out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach(var e in list.EnumerateArray())
            {
                if(e.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                count++;
                var c=ParseComment(e,true,upper);
                if(items.All(x=>x.Rpid!=c.Rpid)) items.Add(c);
            }
        var total=d.TryGetProperty("page",out var p) ? GetLongLoose(p,"count") : 0;
        return (true,null,items,count >= 20 && (total <= 0 || page*20 < total),total);
    }

    public Task<(bool ok,string? err)> RepostDynamicAsync(string id,string text)
    {
        if(!long.TryParse(id,out var value) || value <= 0) return Task.FromResult((false,(string?)"动态 ID 无效"));
        return PostFormAsync("https://api.vc.bilibili.com/dynamic_repost/v1/dynamic_repost/repost",
            new() { ["dynamic_id"]=id,["uid"]=SessionManager.Instance.Current.DedeUserID ?? "",
                ["content"]=string.IsNullOrWhiteSpace(text)?"转发动态":text,["ctrl"]="[]" },"https://t.bilibili.com/");
    }

    /// <summary>
    /// 参与动态投票(2026-11-04, 端点按只读探针实测校正)。
    ///
    /// ★★★ 端点纠正(别改回上一版猜的那些):
    ///   · POST /x/vote/do_vote          → 405 路由存在, 只收 POST  ✔ 用这条
    ///   · /x/dynamic/feed/dyn/vote      → 404 路径不存在            ✘ 上一版写错了
    ///   · /x/vote/vote、/x/vote/vote_decide、dynamic_vote/v1/vote/decide → 全 404
    ///
    /// 参数: <c>vote_id</c> + <c>votes</c> + <c>csrf</c>。
    /// ★ <c>opt_idx</c> 从 **1** 开始(实测 vote_info 返回 1/2/3/4), 传 0 会被拒。
    /// ★ 投票**不可撤销**: 接口层面没有撤票形状。所以本方法**不加自动重试** ——
    ///   网络抖动时重试一次就可能变成"重试成功但用户看到的是失败", 结果投了两票的错觉;
    ///   宁可把错误如实交给用户, 让他自己决定要不要再点。
    /// </summary>
    public async Task<(bool ok,string? err)> VoteDynamicAsync(string voteId,int optionIndex)
    {
        if(string.IsNullOrEmpty(voteId)) return (false,"这条投票缺少编号, 暂时不能参与");
        if(optionIndex <= 0) return (false,"选中的选项无效");
        var (code,msg)=await PostFormCodeAsync("https://api.bilibili.com/x/vote/do_vote",
            new() { ["vote_id"]=voteId,["votes"]=optionIndex.ToString() },
            referer:"https://t.bilibili.com/");
        if(code==0) return (true,null);
        var friendly=code switch
        {
            -101 => "请先登录后再投票",
            -111 => "登录状态已失效, 请重新登录",
            -352 => "操作被风控拦截, 稍后再试",
            -400 => "投票参数不正确",
            260026 => "投票参数不正确",
            260027 => "投票已结束",
            260028 => "你已经投过票了",
            _ => msg ?? $"投票失败 (code {code})",
        };
        return (false,friendly);
    }

    /// <summary>
    /// 拉一个投票的**选项与票数**(2026-11-04, 端点按只读探针实测确定)。
    ///
    /// 接口: <c>GET /x/vote/vote_info?vote_id=&lt;id&gt;</c>, 匿名可读(code=0)。
    /// ★ 为什么必须有这一步: 动态卡片的 additional.vote 节点**不含 options**,
    ///   只有 vote_id/title/join_num/status/choice_cnt/end_time。想要选项与百分比,
    ///   只能拿 vote_id 再查一次。
    /// ★ 实测边界(别踩):
    ///   · 只给 dynamic_id → code=260026「参数错误」, 它**不认** dynamic_id;
    ///   · vote_id 不存在或已结束 → code 仍为 0, 只是 vote_info.status=4,
    ///     所以"查不到"和"已结束"在这个接口里区分不了。
    /// </summary>
    public async Task<(bool ok, string? err, DynamicVote? vote)> GetVoteInfoAsync(string voteId)
    {
        if (string.IsNullOrEmpty(voteId)) return (false, "投票编号为空", null);
        var (code, msg, data) = await GetJsonAsync("https://api.bilibili.com/x/vote/vote_info",
            new() { ["vote_id"] = voteId }, referer: "https://t.bilibili.com/");
        if (code != 0 || data == null) return (false, msg ?? $"投票信息加载失败 (code {code})", null);
        if (data.Value.ValueKind != JsonValueKind.Object ||
            !data.Value.TryGetProperty("vote_info", out var info) ||
            info.ValueKind != JsonValueKind.Object)
            return (false, "投票信息为空", null);

        var vote = new DynamicVote
        {
            Id = GetStr(info, "vote_id"),
            Title = GetStr(info, "title"),
            Status = GetStr(info, "status"),
        };
        if (vote.Id.Length == 0) vote.Id = voteId;
        vote.TotalCount = GetLong(info, "join_num");
        var choices = GetInt(info, "choice_cnt");
        vote.MaxChoices = choices > 0 ? choices : 1;
        vote.EndTime = GetLong(info, "end_time");

        DynamicParser.ReadVoteOptions(info, vote);
        // 选项文字在 vote_info 里是 opt_desc, 上面那个方法已经按这个字段名读;
        // 万一是老结构(desc), ReadVoteOptions 里也做了兜底。
        vote.OptionsLoaded = vote.Options.Count > 0;
        if (vote.Options.Count == 0) return (false, "这条投票没有可选项", vote);
        return (true, null, vote);
    }

    public async Task<(bool ok,string? err,string text,List<DynamicPicture> pictures)> GetNativeArticleAsync(string id)
    {
        var pictures=new List<DynamicPicture>();
        if(!long.TryParse(id,out var article) || article <= 0) return (false,"专栏 ID 无效","",pictures);
        var (code,msg,data)=await GetJsonAsync("https://api.bilibili.com/x/article/view",new() { ["id"]=id },referer:"https://www.bilibili.com/");
        if(code!=0 || data==null) return(false,msg ?? "专栏正文加载失败","",pictures);
        var html=GetStr(data.Value,"content");
        if(html.Length==0) return(false,"暂未取得完整专栏正文，请重试","",pictures);
        foreach(Match match in Regex.Matches(html,"<img[^>]+(?:data-src|src)=[\"']([^\"']+)",RegexOptions.IgnoreCase))
        {
            var url=UrlUtil.Normalize(WebUtility.HtmlDecode(match.Groups[1].Value));
            if(url.StartsWith("https://",StringComparison.OrdinalIgnoreCase) && pictures.All(p=>p.Url!=url)) pictures.Add(new(){Url=url});
        }
        html=Regex.Replace(html,"<(script|style)\\b[^>]*>[\\s\\S]*?</\\1>","",RegexOptions.IgnoreCase);
        html=Regex.Replace(html,"<br\\s*/?>|</(?:p|div|h[1-6]|li)>","\n",RegexOptions.IgnoreCase);
        var text=WebUtility.HtmlDecode(Regex.Replace(html,"<[^>]+>",""));
        return(true,null,text.Trim(),pictures);
    }
}
