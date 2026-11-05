using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk.Models;
using BiliDesk.Views.Controls;

namespace BiliDesk.Views;

public partial class PlayerWindow
{
    private InlineImageGallery? _commentGallery;
    private ContentControl? _commentImageSlot;
    private FrameworkElement? _commentThumbnail;
    private ItemsControl? _commentImageGroup;
    private bool _pictureSplitActive;

    private void OpenCommentPicture(FrameworkElement source,CommentPicture picture)
    {
        ContentControl? slot=null;ItemsControl? pictures=null;
        for(DependencyObject? parent=source;parent!=null;parent=VisualTreeHelper.GetParent(parent))
        {
            if(parent is Grid {Tag:"CommentPictureCell"} root)slot=root.Children.OfType<ContentControl>().FirstOrDefault();
            if(parent is ItemsControl items){pictures=items;break;}
        }
        if(slot==null)return;
        CloseCommentPicture();_commentImageSlot=slot;_commentThumbnail=source;_commentImageGroup=pictures;
        if(pictures!=null)pictures.SizeChanged+=OnInlinePictureSizeChanged;
        var urls=pictures?.Items.OfType<CommentPicture>().Select(p=>p.Url).ToList() ?? new List<string>{picture.Url};
        _commentGallery=new InlineImageGallery();_commentGallery.CloseRequested+=CloseCommentPicture;_commentGallery.ImageReady+=UpdateInlinePictureSize;
        UpdateCommentPicturePlacement();
        _=_commentGallery.OpenAsync(urls,Math.Max(0,urls.IndexOf(picture.Url)));
    }
    private void UpdateCommentPicturePlacement()
    {
        if(_commentGallery==null || _closing)return;
        var hidden=_isFullscreen || _isFillWindow || _isLive || _isListenMode;
        if(hidden){RestorePictureSplit();return;}
        if(IsWebVideoLayout && _commentImageSlot!=null)
        {
            RestorePictureSplit();DetachWebElement(_commentGallery);
            _commentGallery.SetInline(true);
            if(_commentThumbnail!=null)_commentThumbnail.Visibility=Visibility.Collapsed;
            _commentImageSlot.Content=_commentGallery;_commentImageSlot.Visibility=Visibility.Visible;
            UpdateInlinePictureSize();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{if(_commentGallery?.IsLoaded==true && IsWebVideoLayout)_commentGallery.BringIntoView();}));
        }
        else
        {
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
    private void UpdateInlinePictureSize()
    {
        if(!IsWebVideoLayout || _commentGallery==null || _commentImageSlot==null)return;
        var width=Math.Max(120,(_commentImageGroup?.ActualWidth??0)-6);
        _commentImageSlot.Width=width;
        var height=_commentGallery.NaturalWidth>0?_commentGallery.NaturalHeight*Math.Min(1,width/_commentGallery.NaturalWidth):260;
        _commentGallery.Height=Math.Clamp(height,120,Math.Max(320,PlayerStage.ActualHeight/BiliDesk.Helpers.UiScale.Factor*1.6));
    }
    private void UpdatePictureSplitGeometry()
    {
        if(!_pictureSplitActive || IsWebVideoLayout || _closing)return;
        var width=VideoColumn.ActualWidth;
        var height=Math.Min(width*9/16,Math.Max(1,PlayerStage.ActualHeight*0.6));
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
        if(_commentGallery!=null){_commentGallery.CloseRequested-=CloseCommentPicture;_commentGallery.ImageReady-=UpdateInlinePictureSize;_commentGallery.Cancel();DetachWebElement(_commentGallery);_commentGallery=null;}
        if(_commentThumbnail!=null){_commentThumbnail.Visibility=Visibility.Visible;_commentThumbnail=null;}
        if(_commentImageGroup!=null){_commentImageGroup.SizeChanged-=OnInlinePictureSizeChanged;_commentImageGroup=null;}
        if(_commentImageSlot!=null){_commentImageSlot.Content=null;_commentImageSlot.Visibility=Visibility.Collapsed;_commentImageSlot=null;}
        RestorePictureSplit();
    }
}
