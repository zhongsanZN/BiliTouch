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
        Loaded+=async (_,_)=>await ViewModel.InitializeAsync();
    }
    private async void OnHot(object sender,RoutedEventArgs e) { if(IsLoaded)await ViewModel.SortAsync(false); }
    private async void OnNewest(object sender,RoutedEventArgs e) { if(IsLoaded)await ViewModel.SortAsync(true); }
    private async void OnMore(object sender,RoutedEventArgs e)=>await ViewModel.LoadCommentsAsync();
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
    private void OnCommentPicture(object sender,MouseButtonEventArgs e)
    {
        if(sender is FrameworkElement {DataContext:CommentPicture picture})MediaActions.ShowImage(picture.Url,"评论图片",Window.GetWindow(this));
        e.Handled=true;
    }
    private async void OnComposerKey(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {e.Handled=true;await ViewModel.SendAsync();}
    }
}
