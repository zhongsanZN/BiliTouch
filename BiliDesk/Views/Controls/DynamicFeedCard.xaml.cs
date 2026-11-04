using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Models;

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
        Loaded+=(_,_)=>_ = LoadVisiblePreviewAsync();
        DataContextChanged+=(_,_)=> {if(IsLoaded)_ = LoadVisiblePreviewAsync();};
        Unloaded+=(_,_)=>_previewCancellation?.Cancel();
        PreviewMouseLeftButtonDown+=(_,e)=>_pressPoint=e.GetPosition(this);
        PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Enter && e.OriginalSource is not Button && !IsDetail && DataContext is DynamicItem d)
            {DynamicActions.OpenDetail(d);e.Handled=true;}
        };
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
