using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.ViewModels;

/// <summary>原生专栏 / Opus 阅读页 ViewModel（正文块 + 互动三连 + 评论区）</summary>
public class ArticleViewModel : ObservableObject
{
    private readonly string? _initialCvId;
    private readonly string? _initialOpusId;
    private readonly DynamicItem? _sourceItem;
    private CancellationTokenSource? _loadCts;

    public ArticleViewModel(string? cvId, string? opusId = null, string? title = null, DynamicItem? sourceItem = null)
    {
        _initialCvId = string.IsNullOrWhiteSpace(cvId) ? null : cvId.Trim();
        _initialOpusId = string.IsNullOrWhiteSpace(opusId) ? null : opusId.Trim();
        _sourceItem = sourceItem;

        var preTitle = !string.IsNullOrWhiteSpace(title) ? title :
            sourceItem?.ArticleTitle.Length > 0 ? sourceItem.ArticleTitle :
            sourceItem?.ContentTitle ?? "";

        Detail = new ArticleDetail
        {
            ArticleId = _initialCvId ?? sourceItem?.ArticleId ?? "",
            OpusId = _initialOpusId ?? sourceItem?.OpusId ?? "",
            DynamicId = sourceItem?.Id ?? "",
            CommentOid = sourceItem?.CommentOid ?? "",
            CommentType = sourceItem?.CommentType ?? 12,
            Title = preTitle,
            BannerUrl = sourceItem?.ArticleCover ?? "",
            AuthorMid = sourceItem?.AuthorMid ?? 0,
            AuthorName = sourceItem?.AuthorName ?? "",
            AuthorFace = sourceItem?.AuthorFace ?? "",
            PubTimeText = sourceItem?.PubTimeText ?? "",
            LikeCount = sourceItem?.LikeCount ?? 0,
            ReplyCount = sourceItem?.CommentCount ?? 0,
            IsLiked = sourceItem?.IsLiked ?? false,
        };
    }

    private ArticleDetail _detail = new();
    public ArticleDetail Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value)) NotifyDetailChanged();
        }
    }

    private bool _loading;
    public bool Loading { get => _loading; set => SetProperty(ref _loading, value); }

    private string? _error;
    public string? Error { get => _error; set => SetProperty(ref _error, value); }

    private bool _likeBusy;
    private bool _favBusy;

    // ------------------------------------------------------------ 正文展示属性

    public string Title => Detail.Title.Length > 0 ? Detail.Title : "专栏文章";
    public string BannerUrl => Detail.BannerUrl;
    public bool HasBanner => Detail.BannerUrl.Length > 0;
    public long AuthorMid => Detail.AuthorMid;
    public string AuthorName => Detail.AuthorName;
    public string AuthorFace => Detail.AuthorFace;
    public bool HasAuthor => Detail.AuthorName.Length > 0;

    public string MetaLine
    {
        get
        {
            var parts = new List<string>();
            if (Detail.CategoryName.Length > 0) parts.Add(Detail.CategoryName);
            if (Detail.WordCount > 0) parts.Add($"{Detail.WordCount} 字");
            if (Detail.ViewCount > 0) parts.Add($"{FormatCount(Detail.ViewCount)} 阅读");
            if (Detail.PubTimeText.Length > 0) parts.Add(Detail.PubTimeText);
            return string.Join(" · ", parts);
        }
    }

    public bool IsLiked => Detail.IsLiked;
    public bool IsFavorited => Detail.IsFavorited;
    public bool CanFavorite => Detail.CanFavorite;
    public string LikeText => Detail.LikeCount > 0 ? $"赞 {FormatCount(Detail.LikeCount)}" : "点赞";
    public string FavoriteText => Detail.IsFavorited ? "已收藏" :
        Detail.FavoriteCount > 0 ? $"收藏 {FormatCount(Detail.FavoriteCount)}" : "收藏";
    public string CanonicalUrl => Detail.CanonicalUrl;
    public IReadOnlyList<ArticleBlock> Blocks => Detail.Blocks;
    public IReadOnlyList<DynamicPicture> AllImages => Detail.AllImages;
    public bool HasBlocks => Detail.Blocks.Count > 0;

    // ------------------------------------------------------------ 评论状态

    public ObservableCollection<CommentItem> Comments { get; } = new();

    private int _commentPage = 1;
    private int _sortMode = 2; // 2=按热度, 0=按时间
    public int SortMode { get => _sortMode; private set => SetProperty(ref _sortMode, value); }
    public bool IsHotSort => SortMode == 2;
    public bool IsLatestSort => SortMode == 0;

    private bool _commentsLoading;
    public bool CommentsLoading { get => _commentsLoading; set => SetProperty(ref _commentsLoading, value); }

    private bool _commentsHasMore;
    public bool CommentsHasMore { get => _commentsHasMore; set => SetProperty(ref _commentsHasMore, value); }

    private long _commentTotal;
    public string CommentHeaderText => _commentTotal > 0 ? $"评论（{FormatCount(_commentTotal)}）" : "评论";

    private string? _commentLoadError;
    public string? CommentLoadError { get => _commentLoadError; set => SetProperty(ref _commentLoadError, value); }

    private string _draft = "";
    public string Draft { get => _draft; set => SetProperty(ref _draft, value); }

    private bool _sending;
    public bool Sending
    {
        get => _sending;
        set
        {
            if (SetProperty(ref _sending, value)) OnPropertyChanged(nameof(CanEdit));
        }
    }

    private string _composerError = "";
    public string ComposerError { get => _composerError; set => SetProperty(ref _composerError, value); }

    private string _status = "";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private CommentItem? _replyTarget;
    private long _rootRpid;
    public bool HasReplyTarget => _replyTarget != null;
    public string ComposerTitle => _replyTarget == null ? "" : $"回复 @{_replyTarget.UserName}：";
    public bool CanEdit => !Sending && !string.IsNullOrEmpty(Detail.CommentOid);

    private readonly Dictionary<CommentItem, int> _replyPages = new();
    private readonly HashSet<CommentItem> _replyBusy = new();

    // ------------------------------------------------------------ 数据加载

    private int _commentGen;

    public async Task LoadAsync()
    {
        var prevCts = _loadCts;
        prevCts?.Cancel();
        prevCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();

        Loading = true;
        Error = null;
        try
        {
            var res = await Svc.Api.GetArticleDetailAsync(_initialCvId, _initialOpusId);
            if (cts.IsCancellationRequested) return;

            if (!res.ok || res.detail == null)
            {
                Error = res.err ?? "专栏加载失败，请重试";
            }
            else
            {
                // 保留从 Feed 带来的封面/头像作为兜底
                if (res.detail.BannerUrl.Length == 0 && _sourceItem?.ArticleCover.Length > 0)
                    res.detail.BannerUrl = _sourceItem.ArticleCover;
                if (res.detail.AuthorName.Length == 0 && _sourceItem?.AuthorName.Length > 0)
                {
                    res.detail.AuthorMid = _sourceItem.AuthorMid;
                    res.detail.AuthorName = _sourceItem.AuthorName;
                    res.detail.AuthorFace = _sourceItem.AuthorFace;
                }
                if (res.detail.DynamicId.Length == 0 && _sourceItem?.Id.Length > 0)
                    res.detail.DynamicId = _sourceItem.Id;

                Detail = res.detail;
                OnPropertyChanged(nameof(CanEdit));
                await LoadCommentsAsync(reset: true);
            }
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Error = ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) Loading = false;
        }
    }

    public async Task SwitchSortAsync(int sortMode)
    {
        if (SortMode == sortMode) return;
        SortMode = sortMode;
        OnPropertyChanged(nameof(IsHotSort));
        OnPropertyChanged(nameof(IsLatestSort));
        await LoadCommentsAsync(reset: true);
    }

    public async Task LoadCommentsAsync(bool reset = false)
    {
        if (!reset && CommentsLoading) return;
        if (string.IsNullOrEmpty(Detail.CommentOid) || Detail.CommentType <= 0)
        {
            CommentsHasMore = false;
            return;
        }

        var gen = reset ? ++_commentGen : _commentGen;
        var sort = SortMode;
        if (reset)
        {
            _commentPage = 1;
            Comments.Clear();
            _replyPages.Clear();
        }
        else if (!CommentsHasMore)
        {
            return;
        }

        var page = _commentPage;
        CommentsLoading = true;
        CommentLoadError = null;
        try
        {
            var r = await Svc.Api.GetDynamicRepliesAsync(
                Detail.CommentOid, Detail.CommentType, page, sort);
            if (gen != _commentGen) return;
            if (!r.ok)
            {
                CommentLoadError = r.err ?? "评论加载失败";
                return;
            }
            var seen = new HashSet<long>(Comments.Select(x => x.Rpid));
            foreach (var c in r.items)
            {
                if (seen.Add(c.Rpid)) Comments.Add(c);
            }
            CommentsHasMore = r.hasMore;
            _commentTotal = Math.Max(r.total, Detail.ReplyCount);
            OnPropertyChanged(nameof(CommentHeaderText));
            _commentPage = page + 1;
        }
        catch (Exception ex)
        {
            if (gen == _commentGen) CommentLoadError = ex.Message;
        }
        finally
        {
            if (gen == _commentGen) CommentsLoading = false;
        }
    }

    // ------------------------------------------------------------ 互动操作

    public async Task ToggleLikeAsync()
    {
        if (_likeBusy) return;
        _likeBusy = true;
        ComposerError = "";
        try
        {
            var next = !Detail.IsLiked;
            (bool ok, string? err) res;
            if (Detail.ArticleId.Length > 0)
                res = await Svc.Api.LikeArticleAsync(Detail.ArticleId, next);
            else if (Detail.DynamicId.Length > 0)
                res = await Svc.Api.LikeDynamicAsync(Detail.DynamicId, next);
            else
                res = (false, "当前文章不支持点赞");

            if (!res.ok)
            {
                ComposerError = res.err ?? "操作失败";
                return;
            }

            Detail.IsLiked = next;
            Detail.LikeCount = Math.Max(0, Detail.LikeCount + (next ? 1 : -1));
            _sourceItem?.SetLiked(next);
            if (next) ThemeAudio.PlayLike();
            OnPropertyChanged(nameof(IsLiked));
            OnPropertyChanged(nameof(LikeText));
        }
        finally { _likeBusy = false; }
    }

    public async Task ToggleFavoriteAsync()
    {
        if (_favBusy || !Detail.CanFavorite) return;
        _favBusy = true;
        ComposerError = "";
        try
        {
            var next = !Detail.IsFavorited;
            var res = await Svc.Api.FavoriteArticleAsync(Detail.ArticleId, next);
            if (!res.ok)
            {
                ComposerError = res.err ?? "收藏失败";
                return;
            }

            Detail.IsFavorited = next;
            Detail.FavoriteCount = Math.Max(0, Detail.FavoriteCount + (next ? 1 : -1));
            OnPropertyChanged(nameof(IsFavorited));
            OnPropertyChanged(nameof(FavoriteText));
            Status = next ? "已收藏专栏" : "已取消收藏";
        }
        finally { _favBusy = false; }
    }

    public void ComposeReply(CommentItem? target, long root = 0)
    {
        _replyTarget = target;
        _rootRpid = target == null ? 0 : (root > 0 ? root : target.Rpid);
        OnPropertyChanged(nameof(HasReplyTarget));
        OnPropertyChanged(nameof(ComposerTitle));
    }

    public async Task SendCommentAsync()
    {
        if (Sending || string.IsNullOrWhiteSpace(Draft)) return;
        if (!long.TryParse(Detail.CommentOid, out var oid) || oid <= 0)
        {
            ComposerError = "当前页面暂不支持发表评论";
            return;
        }

        Sending = true;
        ComposerError = "";
        Status = "";
        try
        {
            var r = await Svc.Api.PostCommentAsync(
                oid, Draft.Trim(), _rootRpid, _replyTarget?.Rpid ?? 0, Detail.CommentType);
            if (!r.ok)
            {
                ComposerError = r.err ?? "发送失败，请重试";
                return;
            }
            Draft = "";
            ComposeReply(null);
            await LoadCommentsAsync(reset: true);
            Status = "评论已发送";
        }
        catch (Exception ex)
        {
            ComposerError = "发送失败：" + ex.Message;
        }
        finally { Sending = false; }
    }

    public async Task LikeCommentAsync(CommentItem c)
    {
        if (c.LikeBusy || !long.TryParse(Detail.CommentOid, out var oid) || oid <= 0) return;
        c.LikeBusy = true;
        ComposerError = "";
        try
        {
            var liked = !c.IsLiked;
            var r = await Svc.Api.CommentLikeAsync(oid, c.Rpid, liked, Detail.CommentType);
            if (r.ok)
            {
                c.IsLiked = liked;
                c.LikeCount = Math.Max(0, c.LikeCount + (liked ? 1 : -1));
                if (liked) ThemeAudio.PlayLike();
            }
            else
            {
                ComposerError = r.err ?? "点赞失败";
            }
        }
        catch (Exception ex) { ComposerError = "点赞失败：" + ex.Message; }
        finally { c.LikeBusy = false; }
    }

    public async Task LoadRepliesAsync(CommentItem c)
    {
        if (!long.TryParse(Detail.CommentOid, out var oid) || oid <= 0 || !_replyBusy.Add(c)) return;
        ComposerError = "";
        try
        {
            int page = _replyPages.GetValueOrDefault(c) + 1;
            var r = await Svc.Api.GetReplyRepliesAsync(oid, c.Rpid, page, 20, Detail.CommentType);
            if (!Comments.Contains(c)) return;
            if (!r.ok) { ComposerError = r.err ?? "回复加载失败"; return; }
            if (page == 1) c.Replies.Clear();
            var seen = new HashSet<long>(c.Replies.Select(x => x.Rpid));
            foreach (var reply in r.items)
            {
                if (seen.Add(reply.Rpid)) c.Replies.Add(reply);
            }
            _replyPages[c] = page;
            if (r.items.Count < 20) c.ReplyCount = c.Replies.Count;
            c.NotifyRepliesChanged();
        }
        catch (Exception ex) { if (Comments.Contains(c)) ComposerError = "回复加载失败：" + ex.Message; }
        finally { _replyBusy.Remove(c); }
    }

    private void NotifyDetailChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(BannerUrl));
        OnPropertyChanged(nameof(HasBanner));
        OnPropertyChanged(nameof(AuthorMid));
        OnPropertyChanged(nameof(AuthorName));
        OnPropertyChanged(nameof(AuthorFace));
        OnPropertyChanged(nameof(HasAuthor));
        OnPropertyChanged(nameof(MetaLine));
        OnPropertyChanged(nameof(IsLiked));
        OnPropertyChanged(nameof(IsFavorited));
        OnPropertyChanged(nameof(CanFavorite));
        OnPropertyChanged(nameof(LikeText));
        OnPropertyChanged(nameof(FavoriteText));
        OnPropertyChanged(nameof(CanonicalUrl));
        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(AllImages));
        OnPropertyChanged(nameof(HasBlocks));
        OnPropertyChanged(nameof(CanEdit));
    }

    private static string FormatCount(long n) => n switch
    {
        >= 100_000_000 => (n / 100_000_000.0).ToString("0.#") + "亿",
        >= 10_000 => (n / 10_000.0).ToString("0.#") + "万",
        _ => n.ToString(),
    };
}
