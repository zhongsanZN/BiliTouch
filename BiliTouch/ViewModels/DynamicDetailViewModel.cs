using System.Collections.ObjectModel;
using BiliTouch.Models;

using BiliTouch.Services;

namespace BiliTouch.ViewModels;

public sealed class DynamicDetailViewModel : ObservableObject
{
    public DynamicItem Item { get; }
    public ObservableCollection<CommentItem> Comments { get; } = new();
    private Task? _initialization;
    private bool _commentsReady, _loading, _sending, _hasMore=true;
    private readonly HashSet<long> _commentIds = new();
    private int _page, _generation, _sort=2;
    private string _error="", _draft="", _status="", _detailError="";
    private bool _detailLoading;
    public string DetailError {get=>_detailError;private set=>SetProperty(ref _detailError,value);}
    /// <summary>内联图片查看区当前显示的图片地址(2026-11-04, 绑到 DetailPage 的封面)</summary>
    private string _inlineImageUrl="";
    public string InlineImageUrl {get=>_inlineImageUrl;set=>SetProperty(ref _inlineImageUrl,value);}
    public bool DetailLoading {get=>_detailLoading;private set=>SetProperty(ref _detailLoading,value);}
    private CommentItem? _reply;
    public bool Loading { get=>_loading; private set {SetProperty(ref _loading,value);OnPropertyChanged(nameof(CanLoadMore));} }
    public bool Sending { get=>_sending; private set {SetProperty(ref _sending,value);OnPropertyChanged(nameof(CanSend));OnPropertyChanged(nameof(CanEdit));} }
    public bool CanEdit => !Sending;
    public bool HasMore { get=>_hasMore; private set {SetProperty(ref _hasMore,value);OnPropertyChanged(nameof(CanLoadMore));} }
    public bool CanLoadMore => _commentsReady && !Loading && HasMore && CommentLoadError.Length == 0;
    public string Error { get=>_error;private set=>SetProperty(ref _error,value); }
    public string Status { get=>_status;private set=>SetProperty(ref _status,value); }
    public string Draft {get=>_draft;set {SetProperty(ref _draft,value);OnPropertyChanged(nameof(CanSend));} }
    public bool Reposting {get;private set;}
    public string ComposerTitle => Reposting ? "转发这条动态" : _reply==null ? "发表评论" : "回复 @"+_reply.UserName;
    public bool CanSend => !Sending && Svc.Session.HasLogin && (Reposting || !string.IsNullOrWhiteSpace(Draft));
    public bool HasReplyTarget => _reply!=null || Reposting;
    private long _root;
    public DynamicDetailViewModel(DynamicItem item,bool repost=false) { Item=item;Reposting=repost; }
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        var identity = (Item.CommentOid, Item.CommentType);
        var hasIdentity = long.TryParse(identity.CommentOid, out var oid) && oid > 0 && identity.CommentType > 0;
        _commentsReady = hasIdentity;
        var comments = hasIdentity ? LoadCommentsAsync(true) : Task.CompletedTask;
        await RefreshDetailAsync();
        _commentsReady = true;
        if (!hasIdentity || identity != (Item.CommentOid, Item.CommentType))
            await LoadCommentsAsync(true);
        else
            await comments;
    }
    public async Task RefreshDetailAsync()
    {
        if(DetailLoading)return;
        DetailLoading=true;DetailError="";
        try
        {
        var result=await Svc.Api.GetDynamicDetailAsync(Item.Id);
        if(result.ok && result.item!=null)
        {
            Item.RefreshFrom(result.item);
            if(Item.Kind==DynamicKind.Article && Item.ArticleId.Length>0)
            {
                var article=await Svc.Api.GetNativeArticleAsync(Item.ArticleId);
                if(article.ok) { Item.Text=article.text;Item.Pictures=article.pictures;Item.NotifyContentChanged(); }
                else DetailError=article.err ?? "专栏加载失败";
            }
        }
        else DetailError=result.err ?? "完整动态暂未加载成功，已保留现有内容";        }
        catch(Exception ex) {DetailError="动态加载失败："+ex.Message;}
        finally {DetailLoading=false;}

    }
    private string _commentLoadError = "";
    public string CommentLoadError
    {
        get => _commentLoadError;
        private set
        {
            SetProperty(ref _commentLoadError, value);
            OnPropertyChanged(nameof(CanLoadMore));
        }
    }

    public Task RetryCommentsAsync()
    {
        if (Loading || CommentLoadError.Length == 0) return Task.CompletedTask;
        CommentLoadError = "";
        return LoadCommentsAsync(_page == 0);
    }

    public async Task LoadCommentsAsync(bool reset = false)
    {
        if (!_commentsReady || (!reset && !CanLoadMore)) return;
        int generation = reset ? ++_generation : _generation;
        if (reset) _page = 0;
        int page = _page + 1;
        Loading = true;
        CommentLoadError = "";
        try
        {
            var result = await Svc.Api.GetDynamicRepliesAsync(Item.CommentOid, Item.CommentType, page, _sort);
            if (generation != _generation) return;
            if (!result.ok)
            {
                CommentLoadError = result.err ?? "评论加载失败";
                return;
            }
            if (reset)
            {
                Comments.Clear();
                _commentIds.Clear();
                _replyPages.Clear();
            }
            foreach (var comment in result.items)
                if (_commentIds.Add(comment.Rpid)) Comments.Add(comment);
            _page = page;
            HasMore = result.hasMore;
            Status = Comments.Count == 0 ? "还没有评论，来说两句吧" : "";
        }
        catch (Exception ex)
        {
            if (generation == _generation) CommentLoadError = "评论加载失败：" + ex.Message;
        }
        finally
        {
            if (generation == _generation) Loading = false;
        }
    }

    public Task SortAsync(bool newest)
    {
        var sort = newest ? 0 : 2;
        if (_sort == sort) return Task.CompletedTask;
        _sort = sort;
        return LoadCommentsAsync(true);
    }
    public void ComposeReply(CommentItem? reply,long root=0)
    {
        _reply=reply;_root=root>0?root:reply?.Rpid ?? 0;Reposting=false;NotifyComposer();
    }
    public void ComposeRepost() { _reply=null;_root=0;Reposting=true;NotifyComposer(); }
    private void NotifyComposer() {OnPropertyChanged(nameof(ComposerTitle));OnPropertyChanged(nameof(HasReplyTarget));OnPropertyChanged(nameof(CanSend));}
    public async Task SendAsync()
    {
        if(!CanSend)return;
        Sending=true;Error="";
        var repost=Reposting;
        var text=Draft;
        try
        {
            var r=repost ? await Svc.Api.RepostDynamicAsync(Item.Id,text) :
                long.TryParse(Item.CommentOid,out var oid) ? await Svc.Api.PostCommentAsync(oid,text,_root,_reply?.Rpid ?? 0,Item.CommentType) : (false,"这条动态暂不能评论");
            if(!r.Item1) {Error=r.Item2 ?? "发送失败，请重试";return;}
            Draft="";ComposeReply(null);
            if(!repost) await LoadCommentsAsync(true);
            Status=repost?"转发成功":"评论已发送";
        }
        catch(Exception ex) {Error="发送失败，请重试："+ex.Message;}
        finally {Sending=false;}
    }
    public async Task LikeCommentAsync(CommentItem c)
    {
        if(c.LikeBusy || !long.TryParse(Item.CommentOid,out var oid))return;
        c.LikeBusy=true;
        Error="";
        try
        {
            var liked=!c.IsLiked;
            var r=await Svc.Api.CommentLikeAsync(oid,c.Rpid,liked,Item.CommentType);
            if(r.ok) {c.IsLiked=liked;c.LikeCount=Math.Max(0,c.LikeCount+(liked?1:-1));if(liked)ThemeAudio.PlayLike();}
            else Error=r.err ?? "点赞失败";
        }
        catch(Exception ex) {Error="点赞失败："+ex.Message;}
        finally {c.LikeBusy=false;}
    }
    private readonly Dictionary<CommentItem,int> _replyPages=new();
    private readonly HashSet<CommentItem> _replyBusy=new();
    public async Task LoadRepliesAsync(CommentItem c)
    {
        if(!long.TryParse(Item.CommentOid,out var oid) || !_replyBusy.Add(c))return;
        Error="";
        try
        {
            int page=_replyPages.GetValueOrDefault(c)+1;
            var r=await Svc.Api.GetReplyRepliesAsync(oid,c.Rpid,page,20,Item.CommentType);
            if(!Comments.Contains(c))return;
            if(!r.ok) {Error=r.err ?? "回复加载失败";return;}
            if(page==1)c.Replies.Clear();
            var seen=new HashSet<long>(c.Replies.Select(x=>x.Rpid));
            foreach(var reply in r.items)if(seen.Add(reply.Rpid))c.Replies.Add(reply);
            _replyPages[c]=page;
            if(r.items.Count<20)c.ReplyCount=c.Replies.Count;
            c.NotifyRepliesChanged();
        }
        catch(Exception ex) {if(Comments.Contains(c))Error="回复加载失败："+ex.Message;}
        finally {_replyBusy.Remove(c);}
    }
}
