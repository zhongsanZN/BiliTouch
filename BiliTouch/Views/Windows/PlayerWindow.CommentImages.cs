using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Models;
using BiliTouch.Helpers;
using BiliTouch.Views.Controls;

namespace BiliTouch.Views;

public partial class PlayerWindow
{
    private InlineImageGallery? _commentGallery;
    private ContentControl? _commentImageSlot;
    private FrameworkElement? _commentThumbnail;
    private ItemsControl? _commentImageGroup;
    private bool _pictureSplitActive;
    private bool _commentPicturePortrait;

    private void OpenCommentPicture(FrameworkElement source,CommentPicture picture)
    {
        ContentControl? slot=null;ItemsControl? pictures=null;
        for(DependencyObject? parent=source;parent!=null;parent=VisualTreeHelper.GetParent(parent))
        {
            if(parent is Grid {Tag:"CommentPictureCell"} root)slot=root.Children.OfType<ContentControl>().FirstOrDefault();
            if(parent is ItemsControl items){pictures=items;break;}
        }
        if(slot==null)return;
        CloseCommentPicture();_commentPicturePortrait=picture.Height>picture.Width && picture.Width>0;_commentImageSlot=slot;_commentThumbnail=source;_commentImageGroup=pictures;
        if(pictures!=null)pictures.SizeChanged+=OnInlinePictureSizeChanged;
        var urls=pictures?.Items.OfType<CommentPicture>().Select(p=>p.Url).ToList() ?? new List<string>{picture.Url};
        _commentGallery=new InlineImageGallery();_commentGallery.CloseRequested+=CloseCommentPicture;_commentGallery.ImageReady+=UpdateCommentPicturePlacement;
        UpdateCommentPicturePlacement();
        _=_commentGallery.OpenAsync(urls,Math.Max(0,urls.IndexOf(picture.Url)));
    }
    private bool InlineCommentPicture=>IsWebVideoLayout || (_commentGallery is {NaturalHeight:>0,NaturalWidth:>0} g ? g.NaturalHeight>g.NaturalWidth : _commentPicturePortrait);
    private void UpdateCommentPicturePlacement()
    {
        if(_commentGallery==null || _closing)return;
        var hidden=_isFullscreen || _isFillWindow || _isLive || _isListenMode;
        if(hidden){RestorePictureSplit();return;}
        if(InlineCommentPicture && _commentImageSlot!=null)
        {
            RestorePictureSplit();
            var gallery = _commentGallery;
            bool newlyMounted = !ReferenceEquals(_commentImageSlot.Content, gallery);
            if (newlyMounted)
            {
                DetachWebElement(gallery);
                gallery.SetInline(true);
                if (_commentThumbnail != null) _commentThumbnail.Visibility = Visibility.Collapsed;
                _commentImageSlot.Content = gallery;
                _commentImageSlot.Visibility = Visibility.Visible;
            }
            UpdateInlinePictureSize();
            // 每次重新量完都确认一次可见性: 图片是异步解码的, 解码完高度才从占位值变成真实值,
            // 只在新挂载时滚动会错过这一刻(竖长图就停在折叠线以下)。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RevealInlinePicture);
        }
        else
        {
            if (!ReferenceEquals(OriginalImageHost.Content, _commentGallery))
                DetachWebElement(_commentGallery);
            _commentGallery.SetInline(false);
            if(_commentThumbnail!=null)_commentThumbnail.Visibility=Visibility.Visible;
            if(_commentImageSlot!=null){_commentImageSlot.Content=null;_commentImageSlot.Visibility=Visibility.Collapsed;}
            _commentGallery.Height=double.NaN;OriginalImageHost.Content=_commentGallery;
            OriginalPicturePane.Visibility=Visibility.Visible;_pictureSplitActive=true;
            UpdatePictureSplitGeometry();
        }
    }
    private void OnInlinePictureSizeChanged(object sender,SizeChangedEventArgs e)=>UpdateInlinePictureSize();

    /// <summary>
    /// 把展开的图片滚进视野(2026-11-07 修"网页布局点开评论图片展开得很小")。
    ///
    /// 原来调 BringIntoView: 元素左上角正好落在视口下边缘时它判定"已经可见", 一格都不滚,
    /// 而一张竖图有几屏那么高 —— 用户点完只看到缩略图消失, 大图全在折叠线以下。
    /// 这里按视口自己对齐: 放得下就贴到折叠线, 放不下(竖长图)就把顶端对齐到顶部,
    /// 剩下的长度交给用户继续往下滚。整张已在视口内时什么都不做, 所以重复调用不会拽着页面跑。
    /// </summary>
    private void RevealInlinePicture()
    {
        if (_commentGallery is not { IsLoaded: true } gallery || !InlineCommentPicture || _closing) return;
        var viewer = VisualTreeUtil.FindAncestor<ScrollViewer>(gallery);
        if (viewer == null || viewer.ViewportHeight <= 0) return;   // 原布局的图片不在滚动列里
        var bounds = gallery.TransformToAncestor(viewer).TransformBounds(new Rect(0, 0, gallery.ActualWidth, gallery.ActualHeight));
        if (bounds.Top >= 0 && bounds.Bottom <= viewer.ViewportHeight) return;
        var target = bounds.Height < viewer.ViewportHeight && bounds.Top >= 0
            ? viewer.VerticalOffset + bounds.Bottom - viewer.ViewportHeight + 12
            : viewer.VerticalOffset + bounds.Top - 12;
        TrackpadScroll.Cancel(viewer);
        viewer.ScrollToVerticalOffset(target);
    }
    private void UpdateInlinePictureSize()
    {
        if(_commentGallery!=null && InlineCommentPicture && _pictureSplitActive){UpdateCommentPicturePlacement();return;}
        if(!InlineCommentPicture || _commentGallery==null || _commentImageSlot==null)return;
        var width=Math.Max(120,(_commentImageGroup?.ActualWidth??0)-6);
        _commentImageSlot.Width=width;
        // Inline images are part of the document. A tall strip can extend below
        // the fold and the parent viewer scrolls it at the same reading width.
        _commentGallery.Height = _commentGallery.NaturalWidth > 0
            ? Math.Max(1, width * _commentGallery.NaturalHeight / _commentGallery.NaturalWidth)
            : 260;
    }
    private void UpdatePictureSplitGeometry()
    {
        if(!_pictureSplitActive || IsWebVideoLayout || _closing)return;
        var width=VideoColumn.ActualWidth;
        var height=Math.Min(width*9/16,Math.Max(1,PlayerStage.ActualHeight*0.34));
        PictureVideoSpacer.Height=height;
        VideoSurface.Width=width;VideoSurface.Height=height;VideoSurface.Margin=new Thickness(0);
        VideoSurface.HorizontalAlignment=HorizontalAlignment.Left;VideoSurface.VerticalAlignment=VerticalAlignment.Top;
    }
    private void RestorePictureSplit()
    {
        OriginalPicturePane.Visibility=Visibility.Collapsed;
        if(!_pictureSplitActive)return;_pictureSplitActive=false;
        if(IsWebVideoLayout)return;
        VideoSurface.Width=VideoSurface.Height=double.NaN;VideoSurface.Margin=new Thickness(0);
        VideoSurface.HorizontalAlignment=HorizontalAlignment.Stretch;VideoSurface.VerticalAlignment=VerticalAlignment.Stretch;
    }
    private void CloseCommentPicture()
    {
        if(_commentGallery!=null){_commentGallery.CloseRequested-=CloseCommentPicture;_commentGallery.ImageReady-=UpdateCommentPicturePlacement;_commentGallery.Cancel();DetachWebElement(_commentGallery);_commentGallery=null;}
        if(_commentThumbnail!=null){_commentThumbnail.Visibility=Visibility.Visible;_commentThumbnail=null;}
        if(_commentImageGroup!=null){_commentImageGroup.SizeChanged-=OnInlinePictureSizeChanged;_commentImageGroup=null;}
        if(_commentImageSlot!=null){_commentImageSlot.Content=null;_commentImageSlot.Visibility=Visibility.Collapsed;_commentImageSlot=null;}
        RestorePictureSplit();
    }
}
