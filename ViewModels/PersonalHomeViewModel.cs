using System.Collections.ObjectModel;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

public sealed class PersonalHomeViewModel : ObservableObject
{
    private bool _loading, _loggedIn, _pendingRefresh, _statsKnown, _videoCountKnown;
    private string _name="", _face="", _sign="", _error="";
    private long _mid, _following, _followers, _videoCount;
    private int _page;
    private bool _more=true;
    public PersonalHomeViewModel(){Videos.CollectionChanged+=(_,_)=>OnPropertyChanged(nameof(ShowEmpty));}
    public bool Loading { get=>_loading; private set=>SetProperty(ref _loading,value); }
    public bool IsLoggedIn { get=>_loggedIn; private set=>SetProperty(ref _loggedIn,value); }
    public string Name { get=>_name; private set=>SetProperty(ref _name,value); }
    public string Face { get=>_face; private set=>SetProperty(ref _face,value); }
    public string Sign { get=>_sign; private set=>SetProperty(ref _sign,value); }
    public string Error { get=>_error; private set=>SetProperty(ref _error,value); }
    public long Mid { get=>_mid; private set=>SetProperty(ref _mid,value); }
    public string FollowingText => _statsKnown?VideoItem.FormatCount(_following):"—";
    public string FollowersText => _statsKnown?VideoItem.FormatCount(_followers):"—";
    public string VideoCountText => _videoCountKnown?VideoItem.FormatCount(_videoCount):"—";
    public string Stats => $"{FollowingText} 关注     {FollowersText} 粉丝     {VideoCountText} 投稿";
    private void NotifyStats(){OnPropertyChanged(nameof(Stats));OnPropertyChanged(nameof(FollowingText));OnPropertyChanged(nameof(FollowersText));OnPropertyChanged(nameof(VideoCountText));}
    public ObservableCollection<VideoItem> Videos { get; }=new();
    public bool ShowEmpty=>!Loading && Videos.Count==0 && Error.Length==0;
    public ICommand LoginCommand { get; }=new RelayCommand(()=>App.MainVm.OpenLoginWindow());

    public async Task RefreshAsync()
    {
        if(Loading){_pendingRefresh=true;return;}
        Loading=true; Error="";
        try
        {
            IsLoggedIn=Svc.Session.HasLogin;
            if(!IsLoggedIn){Clear();return;}
            var (ok,err,user)=await Svc.Api.GetUserStateAsync();
            if(!ok || user is not { IsLogin:true }) { IsLoggedIn=false;Clear(); Error=err??"登录已过期，请重新登录";return; }
            var mid=user.Mid;
            Name=user.Name; Face=user.Face; Mid=mid;
            var infoTask=Svc.Api.GetSpaceInfoAsync(mid);
            var videoTask=Svc.Api.GetSpaceVideosAsync(mid,1,30);
            var (infoOk,_,info)=await infoTask;
            var (videoOk,videoErr,videos)=await videoTask;
            if(!Svc.Session.HasLogin || Svc.Session.Current.DedeUserID!=mid.ToString()){Clear();return;}
            if(infoOk && info!=null)
            { _statsKnown=info.RelationStatsKnown;_videoCountKnown=info.VideoCountKnown;Sign=string.IsNullOrWhiteSpace(info.Sign)?"还没有填写个性签名":info.Sign; _following=info.Following;_followers=info.Follower;_videoCount=info.VideoCount; }
            else { _statsKnown=_videoCountKnown=false;Sign="个人资料暂时无法加载";_following=_followers=_videoCount=0; }
            NotifyStats();
            if(videoOk && videos!=null){Videos.Clear();foreach(var v in videos)Videos.Add(v);_page=1;_more=videos.Count>=30;}
            else Error=videoErr??"投稿加载失败，下拉重试";
        }
        catch(Exception ex){Error=ex.Message;}
        finally{Loading=false;OnPropertyChanged(nameof(ShowEmpty));if(_pendingRefresh){_pendingRefresh=false;_=RefreshAsync();}}
    }
    public async Task LoadMoreAsync()
    {
        if(Loading || !_more || Mid<=0)return;
        var mid=Mid;Loading=true;
        try
        {
            var(ok,err,items)=await Svc.Api.GetSpaceVideosAsync(mid,_page+1,30);
            if(mid!=Mid || !Svc.Session.HasLogin)return;
            if(!ok || items==null){Error=err??"加载投稿失败";return;}
            var seen=Videos.Select(v=>v.Bvid).ToHashSet();foreach(var v in items)if(seen.Add(v.Bvid))Videos.Add(v);
            _page++;_more=items.Count>=30;
        }
        finally{Loading=false;OnPropertyChanged(nameof(ShowEmpty));}
    }
    public void Clear()
    { _statsKnown=_videoCountKnown=false;Videos.Clear();Name=Face=Sign=Error="";Mid=0;_following=_followers=_videoCount=0;_page=0;_more=true;NotifyStats();OnPropertyChanged(nameof(ShowEmpty)); }
}
