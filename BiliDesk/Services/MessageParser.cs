using System.Text.Json;
using BiliDesk.Models;
namespace BiliDesk.Services;
/// <summary>Decode the structured message payload without throwing away shared-card metadata.</summary>
internal static class MessageParser
{
 private static string S(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object && e.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
 private static long N(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object && e.TryGetProperty(key,out var v) && long.TryParse(v.ToString(),out var n)?n:0;
 private static string First(params string[] values)=>values.FirstOrDefault(v=>!string.IsNullOrWhiteSpace(v))??"";
 private static string Url(string text)=>text.StartsWith("//")?"https:"+text:text;
 public static (string name,string face) Profile(JsonElement e)
 {
  if(e.ValueKind==JsonValueKind.Object && e.TryGetProperty("account_info",out var account) && account.ValueKind==JsonValueKind.Object)
   return(S(account,"name"),Url(First(S(account,"pic_url"),S(account,"face"))));
  return("","");
 }
 public static MsgItem Parse(JsonElement message,long me)
 {
  var item=new MsgItem {SenderUid=N(message,"sender_uid"),Seqno=N(message,"msg_seqno"),IsMine=N(message,"sender_uid")==me};
  var ts=N(message,"timestamp");if(ts>0 && ts<253402300799)item.TimeText=DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("MM-dd HH:mm");
  var type=N(message,"msg_type");var raw=S(message,"content");
  try
  {
   using var doc=JsonDocument.Parse(raw);var d=doc.RootElement;
   if(d.ValueKind==JsonValueKind.String){item.Text=d.GetString()??"";return item;}
   if(d.ValueKind!=JsonValueKind.Object){item.Text=raw;return item;}
   item.Title=First(S(d,"title"),S(d,"name"));item.Description=First(S(d,"desc"),S(d,"description"),S(d,"text"));
   item.CoverUrl=Url(First(S(d,"cover"),S(d,"pic"),S(d,"thumb")));
   item.Bvid=S(d,"bvid");item.TargetUrl=Url(First(S(d,"jump_url"),S(d,"jump_uri"),S(d,"url")));
   item.Text=First(S(d,"content"),S(d,"text"),item.Title);
   if(type==2){item.Kind=MsgKind.Image;item.ImageUrl=Url(S(d,"url"));if(item.ImageUrl.Length==0)item.Text="图片暂时无法显示";return item;}
   if(type==18 && d.TryGetProperty("content",out var parts) && parts.ValueKind==JsonValueKind.Array)
    item.Text=string.Concat(parts.EnumerateArray().Select(p=>S(p,"text")));
   if(item.Bvid.Length>0 || type==7 || (type==11 && N(d,"type_")==1))
   {item.Kind=MsgKind.Video;item.Aid=N(d,"rid");if(item.TargetUrl.Length==0 && item.Bvid.Length>0)item.TargetUrl="https://www.bilibili.com/video/"+item.Bvid;}
   else if(type==10){item.Kind=MsgKind.Notification;item.Text="";}
   else if(type==11 && N(d,"type_")==2){item.Kind=MsgKind.Article;if(item.TargetUrl.Length==0)item.TargetUrl="https://www.bilibili.com/read/cv"+N(d,"rid");}
   else if(item.Title.Length>0 && (item.TargetUrl.Length>0 || item.CoverUrl.Length>0))item.Kind=MsgKind.Link;
   for(int i=1;i<=3;i++)
   {
    var suffix=i==1?"":"_"+i;var label=S(d,"jump_text"+suffix);var url=S(d,"jump_uri"+suffix);
    if(d.TryGetProperty("jump_uri"+suffix+"_config",out var config))url=First(S(config,"web_uri"),url);
    if(label.Length>0 && url.Length>0)item.Actions.Add(new MsgAction {Label=label,Url=Url(url)});
   }
   if(item.IsCard && item.Description.Length==0 && item.Text!=item.Title)item.Description=item.Text;
   if(!item.IsCard && item.Text.Length==0)item.Text=type==5?"对方撤回了一条消息":"暂不支持的消息类型";
  }
  catch{item.Text=raw.Length>0?raw:"暂不支持的消息类型";}
  return item;
 }
 public static string Preview(JsonElement last)
 {
  if(last.ValueKind==JsonValueKind.String){try{using var d=JsonDocument.Parse(last.GetString()??"{}");return Preview(d.RootElement);}catch{return "";}}
  var item=Parse(last,-1);
  return item.IsCard?$"[{item.KindLabel}] {item.Title}":item.IsImage?"[图片]":item.Text;
 }
}
