using System.Collections.ObjectModel;
using System.Windows.Input;
using BiliTouch.Models;
using BiliTouch.Helpers;
using BiliTouch.Services;
namespace BiliTouch.ViewModels;
public partial class MessagesViewModel
{
 public string[] Channels{get;}={"私信","回复与@","收到的喜欢","新增粉丝"};
 private sealed class NotificationChannelCache
 {
  public List<MessageNotification> Items { get; } = new();
  public Dictionary<string, (long id, long time, bool more)> Cursors { get; } = new();
  public int FansPage { get; set; } = 1;
  public bool HasMore { get; set; }
  public bool Loaded { get; set; }
  public string Error { get; set; } = "";
  public int UnreadSnapshot { get; set; }
 }
 private readonly Dictionary<string, NotificationChannelCache> _channelCache = new();
 internal event Action? ChannelCacheUpdated;

 internal IReadOnlyList<MessageNotification>? TryGetCachedNotifications(int index)
 {
  if (index <= 0 || index >= Channels.Length) return null;
  return _channelCache.TryGetValue(Channels[index], out var c) && c.Loaded ? c.Items : null;
 }

 internal bool IsChannelLoadedAt(int index)
 {
  if (index < 0 || index >= Channels.Length) return false;
  var name = Channels[index];
  return name == "私信" ? _privateLoaded : _channelCache.TryGetValue(name, out var c) && c.Loaded;
 }
 private string _channel="私信";
 public string Channel
 {
  get=>_channel;
  set
  {
   if(!SetProperty(ref _channel,value))return;
   ++_notificationGeneration;
   NotificationLoading=false;
   OnPropertyChanged(nameof(IsPrivateChannel));
   OnPropertyChanged(nameof(IsNotificationChannel));
   foreach(var name in new[]{nameof(PrivateTab),nameof(ReplyTab),nameof(LikeTab),nameof(FansTab)})OnPropertyChanged(name);
   RestoreCachedChannel(value);
   _ = EnsureLoadedAsync();
  }
 }
 public bool PrivateTab{get=>Channel=="私信";set{if(value)Channel="私信";}}
 public bool ReplyTab{get=>Channel=="回复与@";set{if(value)Channel="回复与@";}}
 public bool LikeTab{get=>Channel=="收到的喜欢";set{if(value)Channel="收到的喜欢";}}
 public bool FansTab{get=>Channel=="新增粉丝";set{if(value)Channel="新增粉丝";}}
 private readonly Dictionary<string,(long id,long time,bool more)> _noticeCursors=new();
 private int ChannelUnreadCount(string channel)=>channel switch
 {
  "私信"=>Unread.PrivateCount,
  "回复与@"=>Unread.ReplyCount,
  "收到的喜欢"=>Unread.LikeCount,
  _=>0
 };
 private bool HasChannelUnreadIncrease(string channel)
 {
  var current=ChannelUnreadCount(channel);
  if(channel=="私信")return _privateLoaded && current>_cachedPrivateUnread;
  return _channelCache.TryGetValue(channel,out var cache) && cache.Loaded && current>cache.UnreadSnapshot;
 }
 private void RestoreCachedChannel(string channel)
 {
  if(channel=="私信")
  {
   Error=_privateError;
   return;
  }
  Notifications.Clear();
  _noticeCursors.Clear();
  if(_channelCache.TryGetValue(channel,out var cache) && cache.Loaded)
  {
   foreach(var item in cache.Items)Notifications.Add(item);
   foreach(var pair in cache.Cursors)_noticeCursors[pair.Key]=pair.Value;
   _fansPage=cache.FansPage;
   NotificationMore=cache.HasMore;
   Error=cache.Error;
  }
  else
  {
   _fansPage=1;
   NotificationMore=false;
   Error="";
  }
  OnPropertyChanged(nameof(NotificationsEmpty));
 }
 private NotificationChannelCache SaveChannelCache(string channel,int unreadSnapshot)
 {
  if(!_channelCache.TryGetValue(channel,out var cache))_channelCache[channel]=cache=new();
  cache.Items.Clear();
  cache.Items.AddRange(Notifications);
  cache.Cursors.Clear();
  foreach(var pair in _noticeCursors)cache.Cursors[pair.Key]=pair.Value;
  cache.FansPage=_fansPage;
  cache.HasMore=NotificationMore;
  cache.Error=Error;
  cache.UnreadSnapshot=unreadSnapshot;
  cache.Loaded=true;
  ChannelCacheUpdated?.Invoke();
  return cache;
 }
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
 private async Task LoadNotificationsAsync(bool more=false,bool force=true)
 {
  EnsureAccount();
  if(!Svc.Session.HasLogin){Notifications.Clear();NotificationMore=false;Error="查看消息需要先登录 B 站账号";return;}
  var channel=Channel;
  if(!more && !force && _channelCache.TryGetValue(channel,out var cached) && cached.Loaded && !HasChannelUnreadIncrease(channel))
  {
   RestoreCachedChannel(channel);
   return;
  }
  var account=Svc.Session.CaptureAccount();
  if(NotificationLoading && more)return;
  var unreadSnapshot=ChannelUnreadCount(channel);
  var generation=++_notificationGeneration;NotificationLoading=true;Error="";
  if(!more)
  {
   _fansPage=1;
   _noticeCursors.Clear();
   if(!_channelCache.TryGetValue(channel,out var existing) || !existing.Loaded)Notifications.Clear();
  }
  try {
   List<MessageNotification> items;bool next=false;
   if(channel=="回复与@"){
    var results=await Task.WhenAll(FetchNotice("reply",more),FetchNotice("at",more));
    if(generation!=_notificationGeneration || !account.IsCurrent)return;
    KeepNoticeCursor("reply",results[0].items,results[0].more);KeepNoticeCursor("at",results[1].items,results[1].more);
    next=results.Any(x=>x.more);items=results.SelectMany(x=>x.items).OrderByDescending(x=>x.Timestamp).ToList();
    var errors=results.Where(x=>!x.ok).Select(x=>x.error).ToArray();if(errors.Length>0)Error=string.Join("；",errors);
   }else{
    var kind=channel=="新增粉丝"?"fans":"like";
    var result=await FetchNotice(kind,more);
    if(generation!=_notificationGeneration || !account.IsCurrent)return;
    items=result.items;next=result.more;KeepNoticeCursor(kind,items,next);if(more && result.ok)_fansPage++;if(!result.ok)Error=result.error??"消息加载失败";
   }
   if(!more && string.IsNullOrWhiteSpace(Error))Notifications.Clear();
   var seen=Notifications.Select(x=>x.Id).ToHashSet();var added=0;foreach(var item in items)if(seen.Add(item.Id)){Notifications.Add(item);added++;}
   NotificationMore=next && (!more || added>0);
   if(string.IsNullOrWhiteSpace(Error))SaveChannelCache(channel,unreadSnapshot);
  }catch(Exception ex){if(generation==_notificationGeneration && account.IsCurrent)Error="消息加载失败："+ex.Message;}
  finally{if(generation==_notificationGeneration && account.IsCurrent){NotificationLoading=false;OnPropertyChanged(nameof(NotificationsEmpty));if(Svc.MessageUnread.IsStarted)_ = Svc.MessageUnread.RefreshAsync(true);}}
 }
}
