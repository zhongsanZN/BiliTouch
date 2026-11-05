using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 动态混合流卡片的交互(2026-11-04)。
/// 点作者 → 个人主页; 点内容区 → 按类型去视频/专栏/直播。
/// </summary>
public partial class DynamicFeedCard : UserControl
{
    public DynamicFeedCard()
    {
        InitializeComponent();
        Loaded+=(_,_)=>{ _ = LoadVisiblePreviewAsync(); _ = LoadVoteIfNeededAsync(); };
        DataContextChanged+=(_,_)=> {if(IsLoaded){ _ = LoadVisiblePreviewAsync(); _ = LoadVoteIfNeededAsync(); }};
        Unloaded+=(_,_)=>_previewCancellation?.Cancel();
        PreviewMouseLeftButtonDown+=(_,e)=>_pressPoint=e.GetPosition(this);
        PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Enter && e.OriginalSource is not Button && !IsDetail && DataContext is DynamicItem d)
            {DynamicActions.OpenDetail(d);e.Handled=true;}
        };
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Content is FrameworkElement root && root.ContextMenu is { } menu)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }

    private void OnBlockUp(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DynamicItem d) return;
        BiliDesk.Helpers.FilterService.Instance.BlockUp(d.AuthorName, d.AuthorMid);
        Svc.Toast.Show("已屏蔽此 UP 主，可在首页屏蔽面板撤销");
        e.Handled = true;
    }

    private void OnBlockTitle(object sender, RoutedEventArgs e)
    {
        if (DataContext is DynamicItem { Video: { } v })
        {
            BiliDesk.Helpers.FilterService.Instance.BlockTitle(v.Title);
            Svc.Toast.Show("已屏蔽此视频标题，可在首页屏蔽面板撤销");
        }
        e.Handled = true;
    }

    private static readonly System.Threading.SemaphoreSlim PreviewGate = new(2);
    private System.Threading.CancellationTokenSource? _previewCancellation;
    private async Task LoadVisiblePreviewAsync()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancellation=_previewCancellation=new();
        if(IsDetail || DataContext is not DynamicItem d || d.CommentsPreviewLoaded || d.CommentsPreviewLoading ||
            d.HasComments || d.CommentCount<=0 || d.CommentOid.Length==0 || d.CommentType<=0)return;
        var entered=false;
        d.CommentsPreviewLoading=true;
        try
        {
            await PreviewGate.WaitAsync(cancellation.Token);entered=true;
            if(cancellation.IsCancellationRequested)return;
            var comments=await Svc.Api.GetDynamicCommentsAsync(d.CommentOid,d.CommentType);
            if(comments==null)return;
            d.TopComments=comments;d.CommentsPreviewLoaded=true;d.HasCommentsChanged();
        }
        catch(OperationCanceledException) { }
        finally {d.CommentsPreviewLoading=false;if(entered)PreviewGate.Release();}
    }

    public static readonly DependencyProperty IsDetailProperty = DependencyProperty.Register(
        nameof(IsDetail), typeof(bool), typeof(DynamicFeedCard), new PropertyMetadata(false, (d,e) =>
        {
            var card=(DynamicFeedCard)d;
            if(card.BodyText!=null)card.BodyText.MaxHeight=(bool)e.NewValue?double.PositiveInfinity:180;
        }));
    public bool IsDetail {get=>(bool)GetValue(IsDetailProperty);set=>SetValue(IsDetailProperty,value);}
    public event EventHandler? CommentRequested;
    public event EventHandler? RepostRequested;

    private void OnAuthorClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsClick(e)) return;
        if (DataContext is not DynamicItem d) return;
        DynamicActions.OpenAuthor(d);
        e.Handled = true;
    }

    /// <summary>
    /// 点正文里的话题 / @提及(2026-11-04)。
    ///
    /// 话题与 @ 都走**客户端内**的落地页, 不弹网页:
    ///   · @某人 → Rid 就是被 @ 的 mid, 直接开原生个人主页(与点头像同一条路);
    ///   · #话题# → 走原生搜索页(话题在客户端里没有独立详情页, 搜索是最接近官方的行为)。
    /// 表情/普通文字/链接不响应 —— 链接若也走"复制地址"会和附加卡按钮重复, 留给右键菜单。
    /// </summary>
    private void OnRichNodeActivated(object? sender, DynamicRichNode node)
    {
        if (node.IsMention && long.TryParse(node.Rid, out var mid) && mid > 0)
        {
            Svc.Navigate?.OpenUserSpace(mid.ToString());
            return;
        }
        if (node.IsTopic)
        {
            // 话题文本带两侧 #, 搜索词要去掉; orig_text 有就直接用
            var keyword = node.OrigText.Length > 0 ? node.OrigText : node.Text.Trim('#', ' ');
            if (keyword.Length > 0) Svc.Navigate?.OpenSearch(keyword);
        }
    }

    private void OnContentClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsClick(e)) return;
        if (DataContext is not DynamicItem d) return;
        switch (d.Kind)
        {
            case DynamicKind.Video: DynamicActions.OpenVideo(d); break;
            case DynamicKind.Article: if(!IsDetail)DynamicActions.OpenArticle(d); break;
            case DynamicKind.Live: DynamicActions.OpenLive(d); break;
            case DynamicKind.Forward:
                // 点引用框: 原动态是视频就播, 其它进详情页
                if (d.Original?.Video != null) DynamicActions.OpenVideo(d.Original);
                else if(d.Original!=null)DynamicActions.OpenDetail(d.Original);
                break;
            default:
                // 图文/纯文字/未知: 进详情页看完整内容与全部评论
                DynamicActions.OpenDetail(d);
                break;
        }
        e.Handled = true;
    }

    /// <summary>
    /// 点投票的某一项(2026-11-04, 按实测端点与缺口重写)。
    ///
    /// ★ 三个"实测决定"的取舍:
    ///   1. 卡片节点**不带 options**, 所以选项压根没渲染出来时先要把 vote_info 拉回来
    ///      (见 DynamicFeedCard 的 LoadVoteIfNeededAsync)。这里只处理"已经有选项"的情况。
    ///   2. 服务端**不下发**"我投过没有", 所以已投状态只认 <see cref="DynamicVote.VotedThisSession"/>,
    ///      不拿 status 判(status 是 '1' 进行中 / '4' 已结束, 与参与状态无关)。
    ///   3. 投票不可撤销: 请求失败只还原本地状态, 绝不自动重试 —— 避免"重试一次变成投两票"。
    /// </summary>
    private async void OnVoteOptionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not DynamicItem d || d.Vote is not { HasOptions: true } vote) return;
        if (!vote.CanVote) { Svc.Toast.Show(vote.IsClosed ? "投票已结束" : "你已经投过票了"); return; }
        if (sender is not FrameworkElement { DataContext: DynamicVoteOption option }) return;
        if (vote.Id.Length == 0) { Svc.Toast.Show("这条投票暂时不能参与"); return; }
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("投票需要先登录"); return; }

        var busyKey = d.Id + "#" + vote.Id;
        if (!_voting.Add(busyKey)) return;   // 连点保护: 同一张投票同时只放一个请求过去
        try
        {
            var (ok, err) = await Svc.Api.VoteDynamicAsync(vote.Id, option.Index);
            if (!ok) { Svc.Toast.Show(err ?? "投票失败，请重试"); return; }

            // 本地记票(服务端不告诉我们结果, 只能自己记), 然后拉一次 vote_info 对账
            vote.VotedThisSession = true;
            vote.MyChoices.Add(option.Index);
            option.Count += 1;
            vote.TotalCount += 1;
            DynamicParser.RecomputeVoteOptions(vote);
            d.NotifyContentChanged();
            Svc.Toast.Show("投票成功");

            var refreshed = await Svc.Api.GetVoteInfoAsync(vote.Id);
            if (refreshed.ok && refreshed.vote is { } server)
            {
                server.VotedThisSession = true;
                server.MyChoices = vote.MyChoices;
                server.OptionsLoaded = true;
                d.Vote = server;
                d.NotifyContentChanged();
            }
        }
        catch (Exception ex) { Svc.Toast.Show("投票失败：" + ex.Message); }
        finally { _voting.Remove(busyKey); }
    }

    /// <summary>同一张投票的请求去重(连点两次不能投两次 —— 服务端也只认一次, 但会报错)</summary>
    private static readonly HashSet<string> _voting = new();

    /// <summary>
    /// 卡片上的投票**卡片节点没有选项**, 得按 vote_id 再查一次 /x/vote/vote_info。
    /// 只在卡片真正可见时调(见构造函数里的 Loaded), 并发上限 2 —— 与热评预览同一套理由:
    /// 一屏几十张卡同时打接口会把风控和线程池一起打满。
    /// </summary>
    private static readonly SemaphoreSlim VoteGate = new(2);

    private async Task LoadVoteIfNeededAsync()
    {
        if (DataContext is not DynamicItem d || d.Vote is not { } vote) return;
        if (vote.OptionsLoaded || vote.OptionsLoading) return;
        if (vote.Id.Length == 0) return;

        vote.OptionsLoading = true;
        var entered = false;
        try
        {
            await VoteGate.WaitAsync(); entered = true;
            var result = await Svc.Api.GetVoteInfoAsync(vote.Id);
            if (result.ok && result.vote is { } loaded)
            {
                // 保留卡片节点上的 title / 人数(接口两处都有, 以详情为准)
                loaded.MyChoices = vote.MyChoices;
                loaded.VotedThisSession = vote.VotedThisSession;
                d.Vote = loaded;
            }
            else
            {
                vote.OptionsFailed = true;
                vote.OptionsError = result.err ?? "投票选项加载失败";
            }
        }
        catch (Exception ex)
        {
            vote.OptionsFailed = true;
            vote.OptionsError = ex.Message;
        }
        finally
        {
            vote.OptionsLoading = false;
            vote.OptionsLoaded = vote.Options.Count > 0;
            if (entered) VoteGate.Release();
            d.NotifyContentChanged();
        }
    }

    /// <summary>
    /// 附加卡片的行动按钮(2026-11-04)。
    /// 卡片本身点哪儿都进动态详情; 这个按钮代表"去活动页/商品页" ——
    /// 但那类落地页在客户端里没有原生实现, 所以这里只把链接复制给用户,
    /// 并**明说**为什么, 而不是悄悄什么都不做。
    /// </summary>
    private void OnCommonButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not DynamicItem { Common: { } common }) return;
        if (common.JumpUrl.Length == 0) { Svc.Toast.Show("这个活动暂时没有可用链接"); return; }
        try { Clipboard.SetText(common.JumpUrl); Svc.Toast.Show("活动链接已复制，可在浏览器打开"); }
        catch { Svc.Toast.Show("剪贴板暂不可用，请重试"); }
    }

    /// <summary>
    /// 点图片 → 看大图(2026-11-04)。
    /// ★ 官方是点图进可左右翻的看图器; 这里打开原图地址, 能缩放/保存。
    /// ★ 必须 e.Handled = true: 否则会继续冒泡到整卡的点击, 变成"点图进了动态详情"。
    /// </summary>
    private void OnPictureClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsClick(e)) return;
        if (sender is FrameworkElement { DataContext: DynamicPicture pic })
        {
            var item=DataContext as DynamicItem;
            var pictures=item?.Pictures.Contains(pic)==true?item.Pictures:item?.Original?.Pictures;
            if(pictures is {Count:>0})
            {
                var preview=new BiliDesk.Views.Windows.ImagePreviewWindow {Owner=Window.GetWindow(this)};
                preview.Show();
                _=preview.LoadGalleryAsync(pictures.Select(p=>p.Url).ToList(),Math.Max(0,pictures.IndexOf(pic)));
            }
            else DynamicActions.OpenPicture(pic);
            e.Handled = true;   // 别再冒泡到整卡
        }
    }

    /// <summary>
    /// 点整条动态(头像/正文/空白处) → 进详情页(2026-11-04)。
    /// ★ 排除已经被子元素处理过的事件: WPF 里子元素 Handler 设了 Handled 就不会到这里,
    ///   所以视频卡/图片/按钮仍然走它们自己的动作。
    /// </summary>
    private Point _pressPoint;
    private bool IsClick(MouseButtonEventArgs e) => e.ChangedButton==MouseButton.Left && (e.GetPosition(this)-_pressPoint).Length<6;
    private async void OnLikeClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not DynamicItem d || d.IsLiking || d.Id.Length == 0) return;
        if (d.LikeForbidden) { Svc.Toast.Show("这条动态暂不支持点赞"); return; }
        d.IsLiking = true;
        try
        {
            var liked = !d.IsLiked;
            var result = await Svc.Api.LikeDynamicAsync(d.Id, liked);
            if (result.ok) d.SetLiked(liked);
            else Svc.Toast.Show(result.err ?? "点赞失败，请重试");
        }
        finally { d.IsLiking = false; }
    }
    private void OnCommentClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if(IsDetail) {CommentRequested?.Invoke(this,EventArgs.Empty);return;}
        if (DataContext is DynamicItem d) DynamicActions.OpenDetail(d);
    }
    private void OnRepostClick(object sender,RoutedEventArgs e)
    {
        e.Handled=true;
        if(IsDetail) {RepostRequested?.Invoke(this,EventArgs.Empty);return;}
        if(DataContext is DynamicItem d)Svc.Navigate?.OpenDynamic(d,true);
    }
    private void OnCopyBody(object sender,RoutedEventArgs e)
    {
        e.Handled=true;
        if(sender is not FrameworkElement {DataContext:DynamicItem d})return;
        try {Clipboard.SetText(d.ContentTitle.Length>0?d.ContentTitle+"\n"+d.Text:d.Text);Svc.Toast.Show("已复制正文");}
        catch {Svc.Toast.Show("剪贴板暂不可用，请重试");}
    }
    private void OnCopyLink(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var d=(sender as FrameworkElement)?.DataContext as DynamicItem ?? DataContext as DynamicItem;
        if (d==null || d.Id.Length == 0) return;
        try { Clipboard.SetText($"https://t.bilibili.com/{d.Id}"); Svc.Toast.Show("已复制动态链接"); }
        catch { Svc.Toast.Show("剪贴板暂不可用，请重试"); }
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (IsDetail || !IsClick(e) || e.Handled) return;
        if (DataContext is not DynamicItem d) return;
        // 作者名/头像有自己的点击(去个人主页), 那些事件已被子元素 Handled, 到不了这里
        DynamicActions.OpenDetail(d);
        e.Handled = true;
    }
}
