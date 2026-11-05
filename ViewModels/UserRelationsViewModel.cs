using System.Collections.ObjectModel;
using BiliDesk.Models;
using BiliDesk.Services;
namespace BiliDesk.ViewModels;
public sealed class UserRelationsViewModel : ObservableObject
{
 private readonly long _mid;private readonly bool _followers;private int _page;private bool _loading,_more=true;private string _error="";
 public UserRelationsViewModel(long mid,bool followers){_mid=mid;_followers=followers;}
 public string Title=>_followers?"粉丝":"关注";
 public ObservableCollection<FollowUser> Users {get;}=new();
 public bool Loading {get=>_loading;private set=>SetProperty(ref _loading,value);}
 public string Error {get=>_error;private set=>SetProperty(ref _error,value);}
 public bool ShowEmpty=>!Loading && Users.Count==0 && Error.Length==0;
 public async Task LoadAsync(bool reset=false)
 {
  if(Loading || (!reset && (!_more || Error.Length>0)))return;Loading=true;Error="";
  try
  {
   int page=reset?1:_page+1;
   var(ok,error,users)=_followers?await Svc.Api.GetFollowersAsync(_mid,page,50):await Svc.Api.GetFollowingsAsync(_mid,page,50);
   if(!ok || users==null){Error=error??"列表暂时无法加载";return;}
   if(reset)Users.Clear();var seen=Users.Select(u=>u.Mid).ToHashSet();var added=0;
   foreach(var u in users)if(seen.Add(u.Mid)){Users.Add(u);added++;}
   _page=page;_more=users.Count>=50 && added>0;
  }
  catch(Exception ex){Error=ex.Message;}
  finally{Loading=false;OnPropertyChanged(nameof(ShowEmpty));}
 }
}
