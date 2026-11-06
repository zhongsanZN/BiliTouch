namespace BiliTouch.Models;
public sealed class MessageNotification
{
 public string Id{get;set;}=""; public string Kind{get;set;}="";
 public long UserId{get;set;} public string UserName{get;set;}=""; public string Face{get;set;}="";
 public string Title{get;set;}="";public string Text{get;set;}="";public string TargetTitle{get;set;}="";
 public string Cover{get;set;}="";public string Url{get;set;}="";public long Timestamp{get;set;}
 public string TimeText=>Timestamp>0?DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(Timestamp,0,253402300799)).LocalDateTime.ToString("yyyy-MM-dd HH:mm"):"";
 public bool CanOpen=>Url.Length>0 || UserId>0;
}
