using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views.Windows;

namespace BiliTouch.Views.Pages;

/// <summary>按块类型选择专栏正文模板</summary>
public class ArticleBlockTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeadingTemplate { get; set; }
    public DataTemplate? ParagraphTemplate { get; set; }
    public DataTemplate? QuoteTemplate { get; set; }
    public DataTemplate? ListItemTemplate { get; set; }
    public DataTemplate? DividerTemplate { get; set; }
    public DataTemplate? CodeTemplate { get; set; }
    public DataTemplate? ImageTemplate { get; set; }
    public DataTemplate? VideoCardTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        ArticleBlock b => b.Kind switch
        {
            ArticleBlockKind.Heading => HeadingTemplate,
            ArticleBlockKind.Quote => QuoteTemplate,
            ArticleBlockKind.ListItem => ListItemTemplate,
            ArticleBlockKind.Divider => DividerTemplate,
            ArticleBlockKind.Code => CodeTemplate,
            ArticleBlockKind.Image => ImageTemplate,
            ArticleBlockKind.VideoCard => VideoCardTemplate,
            _ => ParagraphTemplate,
        },
        _ => base.SelectTemplate(item, container),
    };
}

/// <summary>原生专栏 / Opus 阅读页</summary>
public partial class ArticlePage : UserControl
{
    private readonly ArticleViewModel _vm;

    public ArticlePage(string? cvId, string? opusId = null, string? title = null, DynamicItem? sourceItem = null)
    {
        InitializeComponent();
        _vm = new ArticleViewModel(cvId, opusId, title, sourceItem);
        DataContext = _vm;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _vm.LoadAsync();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width > 0 && e.NewSize.Width < 860;
        if (stacked)
        {
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            RightColumn.Width = new GridLength(0);
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            BottomRow.Height = new GridLength(360);
            Grid.SetColumn(CommentPane, 0);
            Grid.SetRow(CommentPane, 1);
            CommentPane.BorderThickness = new Thickness(0, 1, 0, 0);
        }
        else
        {
            LeftColumn.Width = new GridLength(1, GridUnitType.Star);
            RightColumn.Width = new GridLength(e.NewSize.Width > 1250 ? 420 : 380);
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            BottomRow.Height = new GridLength(0);
            Grid.SetColumn(CommentPane, 1);
            Grid.SetRow(CommentPane, 0);
            CommentPane.BorderThickness = new Thickness(1, 0, 0, 0);
        }
    }

    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _ = _vm.LoadAsync();
            e.Handled = true;
        }
    }

    private async void OnRetryLoadArticle(object sender, RoutedEventArgs e) => await _vm.LoadAsync();

    private void OnAuthorClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.AuthorMid > 0)
            Svc.Navigate?.OpenUserSpace(_vm.AuthorMid.ToString());
        e.Handled = true;
    }

    private async void OnLikeArticle(object sender, RoutedEventArgs e) => await _vm.ToggleLikeAsync();
    private async void OnFavoriteArticle(object sender, RoutedEventArgs e) => await _vm.ToggleFavoriteAsync();

    private void OnCopyLink(object sender, RoutedEventArgs e)
    {
        if (_vm.CanonicalUrl.Length == 0) return;
        try
        {
            Clipboard.SetText(_vm.CanonicalUrl);
            _vm.Status = "已复制专栏链接";
        }
        catch { }
    }

    private void OnOpenWeb(object sender, RoutedEventArgs e)
    {
        if (_vm.CanonicalUrl.Length > 0)
            Svc.Navigate?.OpenWebViewWindow(_vm.CanonicalUrl, _vm.Title);
    }

    private void OnCopyCode(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ArticleBlock b } && b.Text.Length > 0)
        {
            try
            {
                Clipboard.SetText(b.Text);
                _vm.Status = "已复制代码块";
            }
            catch { }
        }
    }

    private void OnArticleImageClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ArticleBlock b }) return;
        var urls = _vm.AllImages.Select(p => p.Url).Where(u => u.Length > 0).ToList();
        if (urls.Count == 0 && b.ImageUrl.Length > 0) urls.Add(b.ImageUrl);
        if (urls.Count == 0) return;

        var idx = b.ImageIndex >= 0 && b.ImageIndex < urls.Count ? b.ImageIndex : 0;
        OpenImageGallery(urls, idx, _vm.Title);
        e.Handled = true;
    }

    private void OnArticleVideoClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ArticleBlock { Video: { } v } })
        {
            if (v.Bvid.Length > 0) Svc.Player.PlayVideo(v.Bvid);
            else if (sender is FrameworkElement { DataContext: ArticleBlock { LinkUrl.Length: > 0 } b })
                Svc.Navigate?.HandleUrl(b.LinkUrl);
            e.Handled = true;
        }
    }

    private async void OnSortHot(object sender, RoutedEventArgs e) => await _vm.SwitchSortAsync(2);
    private async void OnSortLatest(object sender, RoutedEventArgs e) => await _vm.SwitchSortAsync(0);

    private void OnCommentScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 180)
            _ = _vm.LoadCommentsAsync(reset: false);
    }

    private void OnCommentAuthorClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommentItem c } && c.Mid > 0)
            Svc.Navigate?.OpenUserSpace(c.Mid.ToString());
        e.Handled = true;
    }

    private void OnCommentPicture(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CommentPicture pic } fe) return;
        var item = FindAncestorDataContext<CommentItem>(fe);
        var urls = item?.Pictures.Select(p => p.Url).ToList() ?? new List<string> { pic.Url };
        var idx = Math.Max(0, urls.IndexOf(pic.Url));
        OpenImageGallery(urls, idx, "评论图片");
        e.Handled = true;
    }

    private async void OnLikeComment(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommentItem c })
            await _vm.LikeCommentAsync(c);
    }

    private void OnReplyComment(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommentItem c })
        {
            _vm.ComposeReply(c);
            Composer.Focus();
        }
    }

    private void OnReplyToReply(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommentItem r } fe)
        {
            var root = FindAncestorDataContext<CommentItem>(fe, stopAt: null, skipSelf: true);
            _vm.ComposeReply(r, root?.Rpid ?? r.Rpid);
            Composer.Focus();
        }
    }

    private async void OnLoadReplies(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommentItem c })
            await _vm.LoadRepliesAsync(c);
    }

    private void OnCancelReply(object sender, RoutedEventArgs e) => _vm.ComposeReply(null);
    private async void OnRetryComments(object sender, RoutedEventArgs e) => await _vm.LoadCommentsAsync(reset: true);
    private async void OnSendComment(object sender, RoutedEventArgs e) => await _vm.SendCommentAsync();

    private async void OnComposerKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            await _vm.SendCommentAsync();
        }
    }

    private void OpenImageGallery(IReadOnlyList<string> urls, int index, string? title)
    {
        var w = new ImagePreviewWindow();
        var owner = Window.GetWindow(this);
        if (owner != null) w.Owner = owner;
        w.Show();
        _ = w.LoadGalleryAsync(urls, index);
    }

    private static T? FindAncestorDataContext<T>(DependencyObject d, DependencyObject? stopAt = null, bool skipSelf = false) where T : class
    {
        var cur = skipSelf ? System.Windows.Media.VisualTreeHelper.GetParent(d) : d;
        while (cur != null && cur != stopAt)
        {
            if (cur is FrameworkElement fe && fe.DataContext is T typed)
            {
                if (!skipSelf || !ReferenceEquals(fe.DataContext, (d as FrameworkElement)?.DataContext))
                    return typed;
            }
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return null;
    }
}
