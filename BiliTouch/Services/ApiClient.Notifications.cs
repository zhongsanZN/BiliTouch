using System.Text.Json;
using BiliTouch.Models;
namespace BiliTouch.Services;
public partial class ApiClient
{
 public async Task<(bool ok,string? error,List<MessageNotification> items,bool more)> GetMessageNotificationsAsync(string kind,int page=1,long beforeId=0,long beforeTime=0)
 {
  if(!SessionManager.Instance.HasLogin)return(false,"查看消息需要先登录",new(),false);
  var query=new Dictionary<string,string>{{"platform","web"},{"build","0"},{"mobi_app","web"},{"ps","30"}};
  if(kind=="fans"){
   query=new(){{"vmid",SessionManager.Instance.Current.DedeUserID??""},{"pn",page.ToString()},{"ps","30"},{"order","desc"},{"order_type","attention"}};
   var(c,m,d)=await GetJsonAsync("https://api.bilibili.com/x/relation/followers",query,referer:"https://message.bilibili.com/");
   if(c!=0 || d==null)return(false,m??"粉丝列表加载失败",new(),false);
   var fans=ParseMessageNotifications(d.Value,"fans");return(true,null,fans,fans.Count>=30);
  }
  if(kind!="reply" && kind!="at" && kind!="like")return(false,"不支持的消息分类",new(),false);
  if(beforeId>0)query["id"]=beforeId.ToString();
  if(beforeTime>0)query[kind switch{"reply"=>"reply_time","at"=>"at_time",_=>"like_time"}]=beforeTime.ToString();
  var(code,msg,data)=await GetJsonAsync("https://api.bilibili.com/x/msgfeed/"+kind,query,referer:"https://message.bilibili.com/");
  if(code!=0 || data==null)return(false,msg??"消息加载失败",new(),false);
  var items=ParseMessageNotifications(data.Value,kind);
  var cursor=data.Value.TryGetProperty("cursor",out var c0)?c0:data.Value.TryGetProperty("total",out var total) && total.TryGetProperty("cursor",out var c1)?c1:default;
  var ended=cursor.ValueKind==JsonValueKind.Object && cursor.TryGetProperty("is_end",out var end) && (end.ValueKind==JsonValueKind.True || end.ToString()=="1");
  return(true,null,items,!ended && items.Count>=30);
 }
 internal static List<MessageNotification> ParseMessageNotifications(JsonElement data,string kind)
 {
  static string S(JsonElement e,string k)=>e.ValueKind==JsonValueKind.Object && e.TryGetProperty(k,out var v) && v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
  static long N(JsonElement e,string k)=>e.ValueKind==JsonValueKind.Object && e.TryGetProperty(k,out var v) && long.TryParse(v.ToString(),out var n)?n:0;
  static JsonElement O(JsonElement e,string k)=>e.ValueKind==JsonValueKind.Object && e.TryGetProperty(k,out var v)?v:default;
  var result=new List<MessageNotification>();
  void Add(JsonElement array){if(array.ValueKind!=JsonValueKind.Array)return;foreach(var e in array.EnumerateArray()){
   var user=kind=="fans"?e:O(e,"user");if(user.ValueKind!=JsonValueKind.Object && O(e,"users") is{ValueKind:JsonValueKind.Array} users && users.GetArrayLength()>0)user=users[0];
   var item=O(e,"item");var uid=N(user,"mid");if(uid<=0)uid=N(user,"uid");
   var name=S(user,"nickname");if(name.Length==0)name=S(user,"uname");if(name.Length==0)name="UID "+uid;
   var face=S(user,"avatar");if(face.Length==0)face=S(user,"face");
   var time=N(e,kind switch{"fans"=>"mtime","reply"=>"reply_time","at"=>"at_time",_=>"like_time"});
   var text=S(item,"source_content");if(text.Length==0)text=S(item,"target_reply_content");if(kind=="fans")text=S(e,"sign");
   var url=S(item,"uri");if(url.Length==0)url=S(item,"native_uri");
   var id=N(e,"id").ToString();if(kind=="fans")id=uid.ToString();
   var action=kind switch{"reply"=>"回复了你","at"=>"提到了你","like"=>"赞了你的内容",_=>"关注了你"};
   var total=N(e,"counts");if(kind=="like" && total>1)action="等 "+total+" 人赞了你的内容";
   result.Add(new(){Id=kind+":"+id,Kind=kind,UserId=uid,UserName=name,Face=face,Timestamp=time,Title=name+" "+action,Text=text,TargetTitle=S(item,"title"),Cover=S(item,"image"),Url=url});
  }}
  if(kind=="fans")Add(O(data,"list"));else{Add(O(data,"items"));Add(O(O(data,"latest"),"items"));Add(O(O(data,"total"),"items"));}
  return result.GroupBy(x=>x.Id).Select(x=>x.First()).OrderByDescending(x=>x.Timestamp).ToList();
 }
}
