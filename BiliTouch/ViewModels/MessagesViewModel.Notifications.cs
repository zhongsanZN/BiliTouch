using System.Collections.ObjectModel;
using System.Windows.Input;
using BiliTouch.Models;
using BiliTouch.Helpers;
using BiliTouch.Services;
namespace BiliTouch.ViewModels;
public partial class MessagesViewModel
{
 public string[] Channels{get;}={"私信","回复与@","收到的喜欢","新增粉丝"};
 private string _channel="私信";
 public string Channel{get=>_channel;set{if(!SetProperty(ref _channel,value))return;++_notificationGeneration;OnPropertyChanged(nameof(IsPrivateChannel));OnPropertyChanged(nameof(IsNotificationChannel));foreach(var name in new[]{nameof(PrivateTab),nameof(ReplyTab),nameof(LikeTab),nameof(FansTab)})OnPropertyChanged(name);Error="";_ = LoadAsync();}}
 public bool PrivateTab{get=>Channel=="私信";set{if(value)Channel="私信";}}
 public bool ReplyTab{get=>Channel=="回复与@";set{if(value)Channel="回复与@";}}
 public bool LikeTab{get=>Channel=="收到的喜欢";set{if(value)Channel="收到的喜欢";}}
 public bool FansTab{get=>Channel=="新增粉丝";set{if(value)Channel="新增粉丝";}}
 private readonly Dictionary<string,(long id,long time,bool more)> _noticeCursors=new();
 private Task<(bool ok,string? error,List<MessageNotification> items,bool more)> FetchNotice(string kind,bool more)
 {
  var cursor=_noticeCursors.GetValueOrDefault(kind);
  if(more && !cursor.more)return Task.FromResult((true,(string?)null,new List<MessageNotification>(),false));
  return Svc.Api.GetMessageNotificationsAsync(kind,more?_fansPage+1:1,more?cursor.id:0,more?cursor.time:0);
 }
 private void KeepNoticeCursor(string kind,List<MessageNotification> items,bool more)
 {
  var last=items.LastOrDefault();if(last==null){_noticeCursors[kind]=(0,0,false);return;}
  long.TryParse(last.Id.Split(':').Last(),out var id);_noticeCursors[kind]=(id,last.Timestamp,more);
 }
 public bool IsPrivateChannel=>Channel=="私信";
 public bool IsNotificationChannel=>!IsPrivateChannel;
 public ObservableCollection<MessageNotification> Notifications{get;}=new();
 private int _notificationGeneration,_fansPage=1;
 private bool _notificationLoading,_notificationMore;
 public bool NotificationLoading{get=>_notificationLoading;private set{SetProperty(ref _notificationLoading,value);OnPropertyChanged(nameof(NotificationsEmpty));}}
 public bool NotificationsEmpty=>!NotificationLoading && Notifications.Count==0 && string.IsNullOrWhiteSpace(Error);
 public bool NotificationMore{get=>_notificationMore;private set=>SetProperty(ref _notificationMore,value);}
 public ICommand LoadMoreNotificationsCommand=>new RelayCommand(()=>_ = LoadNotificationsAsync(true));
 private async Task LoadNotificationsAsync(bool more=false)
 {
  if(NotificationLoading && more)return;
  var generation=++_notificationGeneration;var channel=Channel;NotificationLoading=true;Error="";
  if(!more){Notifications.Clear();_fansPage=1;_noticeCursors.Clear();}
  try {
   List<MessageNotification> items;bool next=false;
   if(channel=="回复与@"){
    var results=await Task.WhenAll(FetchNotice("reply",more),FetchNotice("at",more));
    if(generation!=_notificationGeneration)return;
    KeepNoticeCursor("reply",results[0].items,results[0].more);KeepNoticeCursor("at",results[1].items,results[1].more);
    next=results.Any(x=>x.more);items=results.SelectMany(x=>x.items).OrderByDescending(x=>x.Timestamp).ToList();
    var errors=results.Where(x=>!x.ok).Select(x=>x.error).ToArray();if(errors.Length>0)Error=string.Join("；",errors);
   }else{
    var kind=channel=="新增粉丝"?"fans":"like";
    var result=await FetchNotice(kind,more);
    if(generation!=_notificationGeneration)return;
    items=result.items;next=result.more;KeepNoticeCursor(kind,items,next);if(more && result.ok)_fansPage++;if(!result.ok)Error=result.error??"消息加载失败";
   }
   var seen=Notifications.Select(x=>x.Id).ToHashSet();var added=0;foreach(var item in items)if(seen.Add(item.Id)){Notifications.Add(item);added++;}
   NotificationMore=next && (!more || added>0);
  }catch(Exception ex){if(generation==_notificationGeneration)Error="消息加载失败："+ex.Message;}
  finally{if(generation==_notificationGeneration){NotificationLoading=false;OnPropertyChanged(nameof(NotificationsEmpty));}}
 }
}
