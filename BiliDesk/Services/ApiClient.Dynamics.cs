using System.Net;
using System.Text.RegularExpressions;
using BiliDesk.Models;

namespace BiliDesk.Services;

public partial class ApiClient
{
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
