using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

public partial class DynamicDetailPage : UserControl
{
    public DynamicDetailViewModel ViewModel { get; }
    public DynamicDetailPage(DynamicItem item,bool repost=false)
    {
        InitializeComponent();
        ViewModel=new(item,repost);DataContext=ViewModel;
        Loaded+=async (_,_)=>{await ViewModel.InitializeAsync();ScheduleCommentsPrefetch();};
        ViewModel.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(ViewModel.Loading) && !ViewModel.Loading)ScheduleCommentsPrefetch();};
    }
    private bool _prefetchScheduled;
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if(parent is ScrollViewer sv)return sv;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            if(FindScroll(VisualTreeHelper.GetChild(parent,i)) is {} child)return child;
        return null;
    }
    private void OnCommentsScroll(object sender,ScrollChangedEventArgs e)
    {
        if(!ReferenceEquals(e.OriginalSource,FindScroll(CommentList)))return;
        if(e.VerticalChange>0 || e.ExtentHeightChange!=0)ScheduleCommentsPrefetch();
    }
    private void ScheduleCommentsPrefetch()
    {
        if(_prefetchScheduled || !IsLoaded)return;
        _prefetchScheduled=true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(async()=>
        {
            _prefetchScheduled=false;
            if(!IsLoaded || !ViewModel.CanLoadMore || ViewModel.Error.Length>0)return;
            var scroll=FindScroll(CommentList);
            if(scroll!=null && scroll.ViewportHeight>0 && scroll.ScrollableHeight-scroll.VerticalOffset<Math.Max(300,scroll.ViewportHeight*0.75))
                await ViewModel.LoadCommentsAsync();
        }));
    }
    private async void OnHot(object sender,RoutedEventArgs e) { if(IsLoaded)await ViewModel.SortAsync(false); }
    private async void OnNewest(object sender,RoutedEventArgs e) { if(IsLoaded)await ViewModel.SortAsync(true); }
    private async void OnRetry(object sender,RoutedEventArgs e)=>await ViewModel.LoadCommentsAsync(true);
    private async void OnReloadDetail(object sender,RoutedEventArgs e)=>await ViewModel.RefreshDetailAsync();
    private async void OnSend(object sender,RoutedEventArgs e)=>await ViewModel.SendAsync();
    private async void OnLike(object sender,RoutedEventArgs e) { if(sender is FrameworkElement {DataContext:CommentItem c})await ViewModel.LikeCommentAsync(c); }
    private async void OnReplies(object sender,RoutedEventArgs e) { if(sender is FrameworkElement {DataContext:CommentItem c})await ViewModel.LoadRepliesAsync(c); }
    private void OnReply(object sender,RoutedEventArgs e) { if(sender is FrameworkElement {DataContext:CommentItem c})ViewModel.ComposeReply(c);Composer.Focus(); }
    private void OnCancelReply(object sender,RoutedEventArgs e)=>ViewModel.ComposeReply(null);
    private void OnFocusComment(object? sender,EventArgs e) {ViewModel.ComposeReply(null);Composer.Focus();}
    private void OnFocusRepost(object? sender,EventArgs e) {ViewModel.ComposeRepost();Composer.Focus();}
    private void OnReplyToReply(object sender,RoutedEventArgs e)
    {
        if(sender is not FrameworkElement {DataContext:CommentItem reply} element)return;
        long root=reply.Rpid;
        for(DependencyObject? parent=VisualTreeHelper.GetParent(element);parent!=null;parent=VisualTreeHelper.GetParent(parent))
            if(parent is FrameworkElement {DataContext:CommentItem c} && c.Rpid!=reply.Rpid) {root=c.Rpid;break;}
        ViewModel.ComposeReply(reply,root);Composer.Focus();
    }
    private void OnAuthor(object sender,MouseButtonEventArgs e)
    {
        if(sender is FrameworkElement {DataContext:CommentItem c} && c.Mid>0)Svc.Navigate?.OpenUserSpace(c.Mid.ToString());
        e.Handled=true;
    }
    /// <summary>
    /// 点评论里的图片(2026-11-04 改) —— **不再弹窗**, 在左下角的原生区域里显示。
    ///
    /// 用户原始反馈:
    ///   ① "点开评论图片是生硬地弹出窗口而不是原生查看";
    ///   ② "动态详情页左下角空空的, 很适合放要查看的图片"。
    /// 所以这里把图片塞进 InlineImageHost, 页面内切换, 保留"用图片查看器打开"作为
    /// 需要缩放/保存时的出口。
    ///
    /// 为什么要收集**整条评论**的图片而不是只显示点的那张:
    ///   一条评论常带 3~9 张图, 官方是原地左右翻。只显示一张的话用户得退出去再点下一张。
    /// </summary>
    private void OnCommentPicture(object sender,MouseButtonEventArgs e)
    {
        e.Handled=true;
        if(sender is not FrameworkElement {DataContext:CommentPicture picture})return;

        var owner=FindCommentOf(sender as DependencyObject);
        var urls=owner?.Pictures.Select(p=>p.Url).Where(u=>!string.IsNullOrEmpty(u)).ToList() ?? new List<string>();
        if(urls.Count==0)urls.Add(picture.Url);
        var index=Math.Max(0,urls.IndexOf(picture.Url));
        ShowInlineImage(urls,index,owner?.UserName);
    }

    /// <summary>从随手的一个元素往上找, 拿到它所属的 CommentItem(用来取整组图片)</summary>
    private static CommentItem? FindCommentOf(DependencyObject? node)
    {
        for(var d=node;d!=null;d=VisualTreeHelper.GetParent(d))
            if(d is FrameworkElement {DataContext:CommentItem c})return c;
        return null;
    }

    private List<string> _inlineImages=new();
    private int _inlineIndex;

    /// <summary>在左下角原生区域显示第 index 张</summary>
    private void ShowInlineImage(List<string> urls,int index,string? author)
    {
        if(urls.Count==0)return;
        _inlineImages=urls;
        _inlineIndex=Math.Clamp(index,0,urls.Count-1);

        InlineImageHost.Visibility=Visibility.Visible;
        InlineImageTitle.Text=string.IsNullOrEmpty(author)?"评论图片":author+" 的图片";
        InlineImageIndex.Text=urls.Count>1?$"{_inlineIndex+1} / {urls.Count}":"";
        InlineImagePrev.IsEnabled=_inlineIndex>0;
        InlineImageNext.IsEnabled=_inlineIndex<urls.Count-1;

        InlineImageError.Visibility=Visibility.Collapsed;
        InlineImageSurface.SetValue(BiliDesk.Helpers.Cover.SourceProperty,null);
        InlineImageSurface.SetValue(BiliDesk.Helpers.Cover.SourceProperty,urls[_inlineIndex]);
        InlineImageHost.BringIntoView();
    }

    private void OnInlineImagePrev(object sender,RoutedEventArgs e)
    { if(_inlineIndex>0)ShowInlineImage(_inlineImages,_inlineIndex-1,InlineImageTitle.Text.Replace(" 的图片","")); }

    private void OnInlineImageNext(object sender,RoutedEventArgs e)
    { if(_inlineIndex<_inlineImages.Count-1)ShowInlineImage(_inlineImages,_inlineIndex+1,InlineImageTitle.Text.Replace(" 的图片","")); }

    private void OnInlineImageClose(object sender,RoutedEventArgs e)
    {
        InlineImageHost.Visibility=Visibility.Collapsed;
        InlineImageSurface.SetValue(BiliDesk.Helpers.Cover.SourceProperty,null);
        _inlineImages=new List<string>();
        _inlineIndex=0;
    }

    /// <summary>用图片查看器打开当前这张(需要缩放/保存时的出口)</summary>
    private void OnInlineImageOpen(object sender,RoutedEventArgs e)
    {
        if(_inlineImages.Count==0)return;
        MediaActions.ShowImage(_inlineImages[_inlineIndex],"评论图片",Window.GetWindow(this));
    }
    private async void OnComposerKey(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {e.Handled=true;await ViewModel.SendAsync();}
    }
}
