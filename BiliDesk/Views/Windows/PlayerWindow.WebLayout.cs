using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Services;

namespace BiliDesk.Views;

/// <summary>A single native VideoView moves between the inline video and the right introduction rail.</summary>
public partial class PlayerWindow
{
    private bool _webInitialized, _webLayoutMounted, _webLayoutQueued, _webChangingLayout, _webRendering;
    private bool _webPreviousComments, _webHasMounted;
    private double _webSavedScroll, _webSavedBodyScroll, _webLegacyInfoScroll;
    private double _webDockProgress, _webAnimationFrom, _webAnimationTo;
    private double _webAnimationElapsed;
    private long _webAnimationStarted;
    private long _webSurfaceSettledAt;
    private int _webScrollGeneration;
    private FrameworkElement[] _webHeaderParts=Array.Empty<FrameworkElement>();
    private readonly StackPanel _webHeaderStack=new();
    private ScrollViewer? _webHeaderScroll;
    private bool IsWebVideoLayout=>_webLayoutMounted;
    private static readonly DependencyProperty WebScrollOffsetProperty=DependencyProperty.Register(
        "WebScrollOffset",typeof(double),typeof(PlayerWindow),new PropertyMetadata(0.0,(d,e)=>
        {var player=(PlayerWindow)d;if(player._webLayoutMounted)player.WebLeftScroll.ScrollToVerticalOffset((double)e.NewValue);}));

    private void InitializeAlternatePlayerLayout()
    {
        _webInitialized=true;
        _webHeaderParts=InfoContentStack.Children.Cast<FrameworkElement>().Take(3).ToArray();
        _webHeaderScroll=new ScrollViewer {Content=_webHeaderStack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        WebHeaderHost.Content=_webHeaderScroll;
        WebMiniTime.SetBinding(TextBlock.TextProperty,new Binding(nameof(TextBlock.Text)){Source=TimeText});
        WebMiniPlayGlyph.SetBinding(System.Windows.Shapes.Path.DataProperty,new Binding("Content.Data"){Source=BtnPlay});
        SmoothScroll.SetEnabled(WebLeftScroll,true);
        WebLeftScroll.PreviewTouchDown+=(_,_)=>StopWebScrollAnimation();
        WebLeftScroll.AddHandler(Mouse.PreviewMouseWheelEvent,new MouseWheelEventHandler((_,_)=>StopWebScrollAnimation()),true);
        WebLeftPane.SizeChanged+=(_,_)=>QueueAlternateLayout();
        RightLayoutGrid.SizeChanged+=(_,_)=>QueueAlternateLayout();
        WebDockAnchor.SizeChanged+=(_,_)=>{if(_webLayoutMounted && !_webRendering)ApplyWebVideoBounds();};
        Svc.Settings.LayoutChanged+=OnAlternateLayoutSettingChanged;
        Loaded+=(_,_)=>QueueAlternateLayout();
    }
    private void OnAlternateLayoutSettingChanged()=>QueueAlternateLayout();
    private void QueueAlternateLayout()
    {
        if(!_webInitialized || _closing || _webLayoutQueued)return;
        _webLayoutQueued=true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>
        {
            _webLayoutQueued=false;
            if(!_closing){ApplyAlternateLayoutPreference();if(_webLayoutMounted)UpdateWebMeasurements();}
        }));
    }
    private void ApplyAlternateLayoutPreference()
    {
        if(!_webInitialized || _closing || _webChangingLayout || !IsLoaded)return;
        bool use=Svc.Settings.UseWebPlayerLayout && !_isFullscreen && !_isFillWindow && !_isLive && !_isListenMode;
        if(use==_webLayoutMounted)return;
        _webChangingLayout=true;
        try {if(use)MountWebLayout();else UnmountWebLayout();}
        finally {_webChangingLayout=false;}
    }
    private static void DetachWebElement(FrameworkElement element)
    {
        if(element.Parent is Panel panel)panel.Children.Remove(element);
        else if(element.Parent is ContentControl content)content.Content=null;
        else if(element.Parent is Decorator decorator)decorator.Child=null;
    }
    private void ResetWebPageAnimations()
    {
        _infoSwipeArmed=_infoDraggingPage=_infoPageAnimating=_infoHorizontalDecided=false;
        PageHost.BeginAnimation(HeightProperty,null);PageHost.Height=double.NaN;
        foreach(var page in new FrameworkElement[]{IntroPanel,CommentsPanel})
        {page.BeginAnimation(OpacityProperty,null);page.Opacity=1;page.RenderTransform=Transform.Identity;}
    }
    private void MountWebLayout()
    {
        _webPreviousComments=TabComments.IsChecked==true;_webLegacyInfoScroll=InfoScroll.VerticalOffset;
        _webLayoutMounted=true;
        ResetWebPageAnimations();
        foreach(var part in _webHeaderParts){DetachWebElement(part);_webHeaderStack.Children.Add(part);}
        DetachWebElement(CommentsPanel);WebCommentsHost.Content=CommentsPanel;
        foreach(var control in new FrameworkElement[]{BtnBackToComments,CommentFab,CommentComposerPanel})
        {DetachWebElement(control);WebCommentChrome.Children.Add(control);}
        InfoContentStack.Margin=new Thickness(20,0,20,24);
        WebLeftPane.Visibility=WebHeaderHost.Visibility=WebDockAnchor.Visibility=Visibility.Visible;
        TabComments.Visibility=Visibility.Collapsed;TabIntro.IsChecked=true;
        IntroPanel.Visibility=CommentsPanel.Visibility=Visibility.Visible;
        UiScale.Register(WebCommentsScaleRoot);UiScale.Register(WebCommentChrome);
        Grid.SetColumnSpan(VideoSurface,2);VideoSurface.HorizontalAlignment=HorizontalAlignment.Left;VideoSurface.VerticalAlignment=VerticalAlignment.Top;Panel.SetZIndex(VideoSurface,10);
        _webDockProgress=0;_webAnimationFrom=_webAnimationTo=0;
        UpdateWebMeasurements();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>
        {
            if(!_webLayoutMounted || _closing)return;
            WebLeftScroll.ScrollToVerticalOffset(_webSavedScroll);
            InfoScroll.ScrollToVerticalOffset(_webHasMounted?_webSavedBodyScroll:0);
            _webHasMounted=true;UpdateWebMeasurements();
        }));
        UpdateCommentComposer();
        if(_aid>0)_=LoadCommentsAsync();
        UpdateCommentPicturePlacement();
    }
    private void UnmountWebLayout()
    {
        _webSavedScroll=WebLeftScroll.VerticalOffset;_webSavedBodyScroll=InfoScroll.VerticalOffset;
        StopWebVideoAnimation();StopWebScrollAnimation();_webLayoutMounted=false;
        DetachWebElement(CommentsPanel);PageHost.Children.Add(CommentsPanel);
        for(int i=0;i<_webHeaderParts.Length;i++){DetachWebElement(_webHeaderParts[i]);InfoContentStack.Children.Insert(i,_webHeaderParts[i]);}
        foreach(var control in new FrameworkElement[]{BtnBackToComments,CommentFab,CommentComposerPanel})
        {DetachWebElement(control);InfoPanel.Children.Add(control);}
        InfoContentStack.Margin=new Thickness(20,18,20,24);
        WebLeftPane.Visibility=WebHeaderHost.Visibility=WebDockAnchor.Visibility=Visibility.Collapsed;
        WebDockAnchor.Height=0;TabComments.Visibility=Visibility.Visible;
        Grid.SetColumnSpan(VideoSurface,1);Panel.SetZIndex(VideoSurface,0);
        VideoSurface.Width=VideoSurface.Height=double.NaN;VideoSurface.Margin=new Thickness(0);
        VideoSurface.HorizontalAlignment=HorizontalAlignment.Stretch;VideoSurface.VerticalAlignment=VerticalAlignment.Stretch;
        ResetWebPageAnimations();
        var comments=_webPreviousComments || IsCommentDetailOpen;
        IntroPanel.Visibility=comments?Visibility.Collapsed:Visibility.Visible;CommentsPanel.Visibility=comments?Visibility.Visible:Visibility.Collapsed;
        if(comments)TabComments.IsChecked=true;else TabIntro.IsChecked=true;
        InfoScroll.ScrollToVerticalOffset(_webLegacyInfoScroll);
        UpdateCommentComposer();UpdateAlternateVideoControls();
        UpdateCommentPicturePlacement();
    }
    private void UpdateWebMeasurements()
    {
        if(!_webLayoutMounted || _closing || PlayerStage.ActualWidth<=0)return;
        var width=WebLeftScroll.ViewportWidth>0?WebLeftScroll.ViewportWidth:WebLeftPane.ActualWidth;
        if(width<=0)return;
        var height=InlineWebVideoHeight(width);
        if(Math.Abs(WebVideoPlaceholder.Height-height)>0.5 || double.IsNaN(WebVideoPlaceholder.Height))WebVideoPlaceholder.Height=height;
        var scale=UiScale.Percent/100.0;
        WebCommentsScaleRoot.MinHeight=Math.Max(120,WebLeftPane.ActualHeight/scale);
        var dockHeight=Math.Max(0,RightLayoutGrid.ActualWidth-40)*9/16;
        WebHeaderHost.MaxHeight=Math.Max(80,Math.Min(RightLayoutGrid.ActualHeight*0.55,RightLayoutGrid.ActualHeight-dockHeight-44));
        // Finish docking after the threshold: releasing the scroll must never leave video floating between columns.
        var threshold=_webAnimationTo>=0.5?Math.Min(48,height*0.12):Math.Min(120,height*0.3);
        SetWebDockTarget(WebLeftScroll.VerticalOffset>threshold?1:0);
        if(!_webRendering)ApplyWebVideoBounds();
    }
    private double InlineWebVideoHeight(double width)=>Math.Min(width*9/16,Math.Max(1,WebLeftPane.ActualHeight));
    private void OnWebLeftScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(!ReferenceEquals(e.OriginalSource,WebLeftScroll) || !_webLayoutMounted)return;
        if(e.VerticalChange!=0 || e.ViewportWidthChange!=0)UpdateWebMeasurements();
    }
    private void SetWebDockTarget(double target)
    {
        if(Math.Abs(target-_webAnimationTo)<0.0001 && _webRendering)return;
        if(Math.Abs(target-_webDockProgress)<0.0001){_webAnimationTo=target;return;}
        _webAnimationFrom=_webDockProgress;_webAnimationTo=target;_webAnimationStarted=0;_webAnimationElapsed=0;
        _webSurfaceSettledAt=0;
        DockTransitionCover.Visibility=Visibility.Visible;
        if(!_webRendering){_webRendering=true;CompositionTarget.Rendering+=OnWebVideoFrame;}
    }
    private void OnWebVideoFrame(object? sender,EventArgs e)
    {
        if(!_webLayoutMounted || _closing){StopWebVideoAnimation();return;}
        var now=Stopwatch.GetTimestamp();
        if(_webAnimationStarted!=0)_webAnimationElapsed+=Math.Min(50,Stopwatch.GetElapsedTime(_webAnimationStarted,now).TotalMilliseconds);
        _webAnimationStarted=now;
        var t=Math.Clamp(_webAnimationElapsed/220,0,1);
        var eased=1-Math.Pow(1-t,3);
        _webDockProgress=_webAnimationFrom+(_webAnimationTo-_webAnimationFrom)*eased;
        ApplyWebVideoBounds();
        if(t>=1)
        {
            _webDockProgress=_webAnimationTo;
            if(_webSurfaceSettledAt==0)_webSurfaceSettledAt=now;
            // Native VLC HWND resizing is not composited with WPF. Keep the branded
            // transition until its destination surface has had several frames to settle.
            if(Stopwatch.GetElapsedTime(_webSurfaceSettledAt,now).TotalMilliseconds>=140)StopWebVideoAnimation();
        }
    }
    private void ApplyWebVideoBounds()
    {
        if(!_webLayoutMounted || _closing || WebLeftPane.ActualWidth<=0 || !WebDockAnchor.IsVisible)return;
        var width=WebLeftScroll.ViewportWidth>0?WebLeftScroll.ViewportWidth:WebLeftPane.ActualWidth;
        var fullHeight=InlineWebVideoHeight(width);
        var logicalDockWidth=WebDockAnchor.ActualWidth;
        if(logicalDockWidth<=0)return;
        var logicalDockHeight=logicalDockWidth*9/16;
        var p=Math.Clamp(_webDockProgress,0,1);
        WebDockAnchor.Margin=new Thickness(20,12*p,20,0);
        WebDockAnchor.Height=logicalDockHeight*p;
        Rect dock;
        try{dock=WebDockAnchor.TransformToAncestor(PlayerStage).TransformBounds(new Rect(0,0,logicalDockWidth,logicalDockHeight));}
        catch(InvalidOperationException){return;}
        var left=dock.Left*p;var top=dock.Top*p;
        var w=width+(dock.Width-width)*p;var h=fullHeight+(dock.Height-fullHeight)*p;
        VideoSurface.Margin=new Thickness(left,top,0,0);VideoSurface.Width=Math.Max(1,w);VideoSurface.Height=Math.Max(1,h);
        UpdateAlternateVideoControls();
    }
    private void StopWebVideoAnimation()
    {if(_webRendering){CompositionTarget.Rendering-=OnWebVideoFrame;_webRendering=false;}if(DockTransitionCover!=null)DockTransitionCover.Visibility=Visibility.Collapsed;}
    private void UpdateAlternateVideoControls()
    {
        if(!_webInitialized || ControlBar==null || WebMiniControls==null)return;
        var compact=_webLayoutMounted && _webDockProgress>0.1 && VideoSurface.Width<560;
        ControlBar.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
        WebMiniControls.Visibility=compact && !_controlsHidden?Visibility.Visible:Visibility.Collapsed;
        if(WebCommentsHeading!=null)WebCommentsHeading.Text=IsCommentDetailOpen?"评论详情":"评论";
    }
    private bool HandleWebVideoWheel(int delta)
    {
        if(!_webLayoutMounted || _webDockProgress>=0.85 || delta==0)return false;
        WebLeftScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,delta)
            {RoutedEvent=Mouse.PreviewMouseWheelEvent,Source=WebLeftScroll});
        return true;
    }
    private void StopWebScrollAnimation()
    {
        _webScrollGeneration++;
        var offset=WebLeftScroll.VerticalOffset;
        BeginAnimation(WebScrollOffsetProperty,null);SetValue(WebScrollOffsetProperty,offset);
        WebLeftScroll.ScrollToVerticalOffset(offset);
    }
    private void OnWebRestoreVideo(object sender,RoutedEventArgs e)
    {
        if(!_webLayoutMounted)return;
        SmoothScroll.Stop(WebLeftScroll);StopWebScrollAnimation();
        SetValue(WebScrollOffsetProperty,WebLeftScroll.VerticalOffset);
        var generation=_webScrollGeneration;
        var animation=new DoubleAnimation(WebLeftScroll.VerticalOffset,0,TimeSpan.FromMilliseconds(300))
            {EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop};
        animation.Completed+=(_,_)=>{if(generation!=_webScrollGeneration)return;StopWebScrollAnimation();WebLeftScroll.ScrollToTop();};
        BeginAnimation(WebScrollOffsetProperty,animation);
    }
    private void OnWebMediaChanging()
    {
        if(!_webInitialized)return;
        CloseCommentPicture();
        _webSavedScroll=0;
        if(_webLayoutMounted){StopWebVideoAnimation();StopWebScrollAnimation();WebLeftScroll.ScrollToTop();_webDockProgress=_webAnimationFrom=_webAnimationTo=0;ApplyWebVideoBounds();}
        QueueAlternateLayout();
    }
    private void DisposeAlternatePlayerLayout()
    {
        CloseCommentPicture();
        AppTypography.Changed-=OnPlayerFontChanged;
        UiScale.Unregister(OriginalImageHost);
        Svc.Settings.LayoutChanged-=OnAlternateLayoutSettingChanged;StopWebVideoAnimation();StopWebScrollAnimation();
        UiScale.Unregister(WebCommentsScaleRoot);UiScale.Unregister(WebCommentChrome);
    }
}
