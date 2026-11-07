using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Services;

namespace BiliTouch.Views;

/// <summary>
/// 网页版布局:同一个原生 VideoView 在「页面内大画面」与「右侧简介上方的停靠位」之间移动。
///
/// ★★★ 2026-11-05 重做过渡(用户原话:
///   "视频详情页, 网页版布局, 小窗动效依旧没做好, 还是会在变化过程中不断重建边框, 抖来抖去。
///    按理说等比缩放不应该出现这种情况。")
///
/// 为什么会抖 —— 根因不是缓动曲线, 是**原生窗口**:
///   视频是 LibVLC 的 HwndHost, 它**不参与 WPF 的合成**。以前每一帧都改
///   `VideoSurface.Width/Height/Margin`, 于是每一帧都在给那个子 HWND 发 SetWindowPos,
///   libvlc 的 direct3d11 vout 收到新尺寸就重建一次交换链 —— 一秒钟重建 60 次,
///   用户看到的就是"边框一直在变、画面在抖"。等比缩放只对纯 WPF 元素成立, 对 HWND 不成立。
///
/// 现在的做法(一次到位 + 遮罩过渡):
///   1. 过渡期间**不动原生窗口**: VideoSurface 的几何保持原样, libvlc 一次重建都没有;
///   2. 用盖在视频上的品牌遮罩(DockTransitionCover)承担"动"的观感 —— 它是纯 WPF,
///      由逐帧更新的 RenderTransform 做等比缩放, 开销只是几个矩阵乘法;
///   3. 动画走完的那一帧才把原生窗口**一次性**移到目标几何(此时它被遮罩完全盖住),
///      再等它的新表面稳定几帧之后撤掉遮罩。
///   ⇒ 每个方向(停靠/还原)只发生 **1 次** 原生尺寸变更, 而不是每帧一次。
///
/// ★ `_webDockProgress` 仍然逐帧更新: 它是"过渡进行到哪"的唯一真相, 控制栏切换、遮罩动画、
///   滚轮接管判定都读它, 探针也直接采样它。变的只是"谁在被移动"。
/// </summary>
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
    private bool _webRestoreScrolling;
    private VideoFrameTransition? _webFrameTransition;
    private bool _webFramePending,_webFrameAnimating;
    private bool _webApplyingBounds;
    private int _webFrameGeneration;
    private FrameworkElement[] _webHeaderParts = Array.Empty<FrameworkElement>();
    private readonly StackPanel _webHeaderStack = new();
    private ScrollViewer? _webHeaderScroll;
    private bool IsWebVideoLayout => _webLayoutMounted;

    /// <summary>过渡遮罩里那个品牌块的缩放/位移动画(纯 WPF, 每帧只改矩阵)</summary>
    private readonly ScaleTransform _webCoverScale = new(1, 1);
    private readonly TranslateTransform _webCoverMove = new();
    private readonly TranslateTransform _webBrandMove = new();

    private static readonly DependencyProperty WebScrollOffsetProperty = DependencyProperty.Register(
        "WebScrollOffset", typeof(double), typeof(PlayerWindow), new PropertyMetadata(0.0, (d, e) =>
        {
            var player = (PlayerWindow)d;
            if (player._webLayoutMounted) player.WebLeftScroll.ScrollToVerticalOffset((double)e.NewValue);
        }));

    private void InitializeAlternatePlayerLayout()
    {
        _webInitialized = true;
        _webHeaderParts = InfoContentStack.Children.Cast<FrameworkElement>().Take(3).ToArray();
        _webHeaderScroll = new ScrollViewer { Content = _webHeaderStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        WebHeaderHost.Content = _webHeaderScroll;
        WebMiniTime.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = TimeText });
        WebMiniPlayGlyph.SetBinding(SkinActionIcon.DataProperty, new Binding("Content.Data") { Source = BtnPlay });
        // 过渡遮罩的变换挂在它的内容上, 而不是它自己: 它自己要被 VideoAreaRoot 的布局约束住,
        // 挂 Transform 上去会把它从布局里"抬起来", 反而失去裁剪边界。
        if (DockTransitionCover.Child is UIElement coverChild && coverChild is FrameworkElement coverElement)
        {
            var group = new TransformGroup();
            group.Children.Add(_webCoverScale);
            group.Children.Add(_webCoverMove);
            coverElement.RenderTransform = group;
            coverElement.RenderTransformOrigin = new Point(0, 0);
            _webCoverScale.ScaleX = _webCoverScale.ScaleY = 1;
            _webCoverMove.X = _webCoverMove.Y = 0;
            if (BrandMark is FrameworkElement brand)
            {
                brand.RenderTransform = _webBrandMove;
                brand.RenderTransformOrigin = new Point(0.5, 0.5);
            }
        }
        WebLeftScroll.PreviewTouchDown += (_, _) => StopWebScrollAnimation();
        WebLeftScroll.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => StopWebScrollAnimation()), true);
        WebLeftPane.SizeChanged += (_, _) => QueueAlternateLayout();
        RightLayoutGrid.SizeChanged += (_, _) => QueueAlternateLayout();
        Svc.Settings.LayoutChanged += OnAlternateLayoutSettingChanged;
        Loaded += (_, _) => QueueAlternateLayout();
    }

    private void OnAlternateLayoutSettingChanged() => QueueAlternateLayout();

    private void QueueAlternateLayout()
    {
        if (!_webInitialized || _closing || _webLayoutQueued) return;
        _webLayoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _webLayoutQueued = false;
            if (!_closing) { ApplyAlternateLayoutPreference(); if (_webLayoutMounted) UpdateWebMeasurements(); }
        }));
    }

    private void ApplyAlternateLayoutPreference()
    {
        if (!_webInitialized || _closing || _webChangingLayout || !IsLoaded) return;
        bool use = Svc.Settings.UseWebPlayerLayout && !_isFullscreen && !_isFillWindow && !_isLive && !_isListenMode;
        if (use == _webLayoutMounted) return;
        _webChangingLayout = true;
        try { if (use) MountWebLayout(); else UnmountWebLayout(); }
        finally { _webChangingLayout = false; }
    }

    private static void DetachWebElement(FrameworkElement element)
    {
        if (element.Parent is Panel panel) panel.Children.Remove(element);
        else if (element.Parent is ContentControl content) content.Content = null;
        else if (element.Parent is Decorator decorator) decorator.Child = null;
    }

    private void ResetWebPageAnimations()
    {
        _infoSwipeArmed = _infoDraggingPage = _infoPageAnimating = _infoHorizontalDecided = false;
        PageHost.BeginAnimation(HeightProperty, null); PageHost.Height = double.NaN;
        foreach (var page in new FrameworkElement[] { IntroPanel, CommentsPanel })
        { page.BeginAnimation(OpacityProperty, null); page.Opacity = 1; page.RenderTransform = Transform.Identity; }
    }

    private void MountWebLayout()
    {
        _webPreviousComments = TabComments.IsChecked == true; _webLegacyInfoScroll = InfoScroll.VerticalOffset;
        _webLayoutMounted = true;
        ResetWebPageAnimations();
        foreach (var part in _webHeaderParts) { DetachWebElement(part); _webHeaderStack.Children.Add(part); }
        DetachWebElement(CommentsPanel); WebCommentsHost.Content = CommentsPanel;
        foreach (var control in new FrameworkElement[] { BtnBackToComments, CommentFab, CommentComposerPanel })
        { DetachWebElement(control); WebCommentChrome.Children.Add(control); }
        InfoContentStack.Margin = new Thickness(20, 0, 20, 24);
        WebLeftPane.Visibility = WebHeaderHost.Visibility = WebDockAnchor.Visibility = Visibility.Visible;
        TabComments.Visibility = Visibility.Collapsed; TabIntro.IsChecked = true;
        IntroPanel.Visibility = CommentsPanel.Visibility = Visibility.Visible;
        UiScale.Register(WebCommentsScaleRoot); UiScale.Register(WebCommentChrome);
        Grid.SetColumnSpan(VideoSurface, 2); VideoSurface.HorizontalAlignment = HorizontalAlignment.Left; VideoSurface.VerticalAlignment = VerticalAlignment.Top; Panel.SetZIndex(VideoSurface, 10);
        _webDockProgress = _webAnimationFrom = _webAnimationTo = 0;
        DockTransitionCover.Visibility = Visibility.Collapsed;
        UpdateWebMeasurements();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_webLayoutMounted || _closing) return;
            WebLeftScroll.ScrollToVerticalOffset(_webSavedScroll);
            InfoScroll.ScrollToVerticalOffset(_webHasMounted ? _webSavedBodyScroll : 0);
            _webHasMounted = true; UpdateWebMeasurements();
        }));
        UpdateCommentComposer();
        if (_aid > 0) _ = LoadCommentsAsync();
        UpdateCommentPicturePlacement();
    }

    private void UnmountWebLayout()
    {
        _webSavedScroll = WebLeftScroll.VerticalOffset; _webSavedBodyScroll = InfoScroll.VerticalOffset;
        StopWebVideoAnimation(); StopWebScrollAnimation(); _webLayoutMounted = false;
        DetachWebElement(CommentsPanel); PageHost.Children.Add(CommentsPanel);
        for (int i = 0; i < _webHeaderParts.Length; i++) { DetachWebElement(_webHeaderParts[i]); InfoContentStack.Children.Insert(i, _webHeaderParts[i]); }
        foreach (var control in new FrameworkElement[] { BtnBackToComments, CommentFab, CommentComposerPanel })
        { DetachWebElement(control); InfoPanel.Children.Add(control); }
        InfoContentStack.Margin = new Thickness(20, 18, 20, 24);
        WebLeftPane.Visibility = WebHeaderHost.Visibility = WebDockAnchor.Visibility = Visibility.Collapsed;
        WebDockAnchor.Height = 0; TabComments.Visibility = Visibility.Visible;
        Grid.SetColumnSpan(VideoSurface, 1); Panel.SetZIndex(VideoSurface, 0);
        VideoSurface.Width = VideoSurface.Height = double.NaN; VideoSurface.Margin = new Thickness(0);
        VideoSurface.HorizontalAlignment = HorizontalAlignment.Stretch; VideoSurface.VerticalAlignment = VerticalAlignment.Stretch;
        ResetWebPageAnimations();
        var comments = _webPreviousComments || IsCommentDetailOpen;
        IntroPanel.Visibility = comments ? Visibility.Collapsed : Visibility.Visible; CommentsPanel.Visibility = comments ? Visibility.Visible : Visibility.Collapsed;
        if (comments) TabComments.IsChecked = true; else TabIntro.IsChecked = true;
        InfoScroll.ScrollToVerticalOffset(_webLegacyInfoScroll);
        UpdateCommentComposer(); UpdateAlternateVideoControls();
        UpdateCommentPicturePlacement();
    }

    private void UpdateWebMeasurements()
    {
        if (!_webLayoutMounted || _closing || PlayerStage.ActualWidth <= 0) return;
        var width = WebLeftScroll.ViewportWidth > 0 ? WebLeftScroll.ViewportWidth : WebLeftPane.ActualWidth;
        if (width <= 0) return;
        var height = InlineWebVideoHeight(width);
        if (double.IsNaN(WebVideoPlaceholder.Height) || Math.Abs(WebVideoPlaceholder.Height - height) > 0.5) WebVideoPlaceholder.Height = height;
        var scale = UiScale.Percent / 100.0;
        WebCommentsScaleRoot.MinHeight = Math.Max(120, WebLeftPane.ActualHeight / scale);
        var dockHeight = Math.Max(0, RightLayoutGrid.ActualWidth - 40) * 9 / 16;
        WebHeaderHost.MaxHeight = Math.Max(80, Math.Min(RightLayoutGrid.ActualHeight * 0.55, RightLayoutGrid.ActualHeight - dockHeight - 44));
        UpdateDockForOffset(height);
        if (!_webRendering && !_webFramePending && !_webFrameAnimating) ApplyWebVideoBounds();
    }

    private void UpdateDockForOffset(double height)
    {
        var threshold = _webAnimationTo >= .5 ? Math.Min(48, height * .12) : Math.Min(120, height * .3);
        var target = WebLeftScroll.VerticalOffset > threshold ? 1 : 0;
        if (target != _webAnimationTo) SetWebDockTarget(target);
    }

    private double InlineWebVideoHeight(double width) => Math.Min(width * 9 / 16, Math.Max(1, WebLeftPane.ActualHeight));

    private void OnWebLeftScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WebLeftScroll) || !_webLayoutMounted) return;
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
            UpdateWebMeasurements();
        else if (e.VerticalChange != 0 && WebVideoPlaceholder.Height > 0)
            UpdateDockForOffset(WebVideoPlaceholder.Height);
    }

    /// <summary>
    /// 起一次停靠/还原过渡。
    /// ★ 目标与当前位置几乎相同时不重启动画(滚动时每帧都会调到这里, 不能每帧重建动画状态)。
    /// </summary>
    private void SetWebDockTarget(double target)
    {
        if(_webFramePending || _webFrameAnimating){_webAnimationTo=target;return;}
        if(Math.Abs(target-_webDockProgress)>.001 && _mp.IsPlaying){_webAnimationTo=target;_ = TryWebFrameTransitionAsync();return;}
        StartLegacyWebDockTarget(target);
    }
    private void StartLegacyWebDockTarget(double target)
    {
        if (Math.Abs(target - _webAnimationTo) < 0.0001 && _webRendering) return;
        if (Math.Abs(target - _webDockProgress) < 0.0001) { _webAnimationTo = target; ApplyWebVideoBounds(); return; }
        _webAnimationFrom = _webDockProgress; _webAnimationTo = target; _webAnimationStarted = 0; _webAnimationElapsed = 0;
        _webSurfaceSettledAt = 0;
        DockTransitionCover.Visibility = Visibility.Visible;
        if (!_webRendering) { _webRendering = true; CompositionTarget.Rendering += OnWebVideoFrame; }
        UpdateAlternateVideoControls();
    }

    /// <summary>
    /// 过渡的每一帧: **只动纯 WPF 的遮罩**, 一次原生调用都不做。
    /// (CompositionTarget.Rendering 里做原生调用会直接把帧拖慢, 而且会造成 vout 重建。)
    /// </summary>
    private void OnWebVideoFrame(object? sender, EventArgs e)
    {
        if (!_webLayoutMounted || _closing) { StopWebVideoAnimation(); return; }
        var now = Stopwatch.GetTimestamp();
        if (_webAnimationStarted != 0) _webAnimationElapsed += Math.Min(50, Stopwatch.GetElapsedTime(_webAnimationStarted, now).TotalMilliseconds);
        _webAnimationStarted = now;
        var t = Math.Clamp(_webAnimationElapsed / 220, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        _webDockProgress = _webAnimationFrom + (_webAnimationTo - _webAnimationFrom) * eased;
        UpdateWebCoverAnimation(_webDockProgress);
        UpdateAlternateVideoControls();

        if (t >= 1)
        {
            _webDockProgress = _webAnimationTo;
            // 动画走完的这一帧才把原生窗口**一次性**移到目标几何。此刻它被遮罩完全盖住,
            // 用户看不到这次 SetWindowPos。
            ApplyWebVideoBounds();
            UpdateWebCoverAnimation(_webDockProgress);
            if (_webSurfaceSettledAt == 0) _webSurfaceSettledAt = now;
            // 原生 vout 的新表面需要几帧才能出画面, 遮罩再压一会儿(140ms)才撤。
            if (Stopwatch.GetElapsedTime(_webSurfaceSettledAt, now).TotalMilliseconds >= 140) StopWebVideoAnimation();
        }
    }

    /// <summary>
    /// 遮罩的等比缩放(纯 WPF)。p=0 铺满整个视频区, p=1 收缩成右上角一块小牌。
    /// ★ 为什么不让遮罩真的"飞"到右栏: 它活在视频的浮动窗口里(被 video 区域裁剪),
    ///   飞出边界就会被裁掉。缩向右上角在观感上等价, 而且完全不碰原生窗口。
    /// ★ 品牌块反着缩(1/scale), 保证文字图标始终是原始大小, 不会跟着糊。
    /// </summary>
    private void UpdateWebCoverAnimation(double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        // 从 1.0 缩到 0.42: 再小就只剩一块看不清的色块了。
        var scale = 1 - 0.58 * p;
        _webCoverScale.ScaleX = _webCoverScale.ScaleY = scale;
        if (WebLeftScroll.ActualWidth > 0)
        {
            // 缩放原点在左上角, 所以要补一个位移把缩小的块推到自身上边缘/右边缘之间居中。
            _webCoverMove.X = (1 - scale) * WebLeftScroll.ActualWidth * 0.5;
            _webCoverMove.Y = (1 - scale) * Math.Max(1, WebVideoPlaceholder.ActualHeight) * 0.5;
        }
        if (BrandMark != null)
        {
            var inverse = scale > 0.01 ? 1 / scale : 1;
            var brandScale = 1 + (inverse - 1) * 1.0;
            if (BrandMark.RenderTransform is TranslateTransform) { /* 位移独立 */ }
            BrandMark.LayoutTransform = new ScaleTransform(brandScale * 0.72, brandScale * 0.72);
            BrandMark.Opacity = 0.35 + 0.65 * (1 - p);
        }
        if (DockTransitionCover != null) DockTransitionCover.Opacity = 1 - 0.35 * p;
    }

    /// <summary>
    /// 把原生视频窗口**一次性**放到当前 progress 对应的几何。
    /// ★ 只允许在"过渡结束"或"布局测量完成且没有过渡在跑"时调用 —— 它会产生一次原生 resize。
    /// </summary>
    private void ApplyWebVideoBounds()
    {
        if (_webApplyingBounds || !_webLayoutMounted || _closing || WebLeftPane.ActualWidth <= 0 || !WebDockAnchor.IsVisible) return;
        _webApplyingBounds = true;
        try
        {
            PrepareWebDockAnchor(_webDockProgress);
            var bounds=CalculateWebVideoBounds(_webDockProgress);
            if(bounds.IsEmpty)return;
            if(!Nearly(VideoSurface.Width,bounds.Width) || !Nearly(VideoSurface.Height,bounds.Height)){VideoSurface.Width=bounds.Width;VideoSurface.Height=bounds.Height;}
            var margin=new Thickness(bounds.Left,bounds.Top,0,0);if(VideoSurface.Margin!=margin)VideoSurface.Margin=margin;
            UpdateAlternateVideoControls();
        }
        finally { _webApplyingBounds = false; }
    }

    private void PrepareWebDockAnchor(double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        var margin = new Thickness(20, 12 * p, 20, 0);
        if (WebDockAnchor.Margin != margin) WebDockAnchor.Margin = margin;
        var height = WebDockAnchor.ActualWidth * 9 / 16 * p;
        if (!Nearly(WebDockAnchor.Height, height)) WebDockAnchor.Height = height;
        // TransformToAncestor reads arranged geometry. Settle the placeholder first,
        // otherwise the snapshot lands at the old top and the native video drops 12 DIP.
        PlayerStage.UpdateLayout();
    }
    private Rect CalculateWebVideoBounds(double progress)
    {
        var width = WebLeftScroll.ViewportWidth > 0 ? WebLeftScroll.ViewportWidth : WebLeftPane.ActualWidth;
        var fullHeight = InlineWebVideoHeight(width);
        var logicalDockWidth = WebDockAnchor.ActualWidth;
        if (logicalDockWidth <= 0) return Rect.Empty;
        var logicalDockHeight = logicalDockWidth * 9 / 16;
        var p = Math.Clamp(progress, 0, 1);
        // Geometry calculation must not invalidate the layout it is about to read.
        Rect dock;
        try { dock = WebDockAnchor.TransformToAncestor(PlayerStage).TransformBounds(new Rect(0, 0, logicalDockWidth, logicalDockHeight)); }
        catch (InvalidOperationException) { return Rect.Empty; }
        var left = dock.Left * p; var top = dock.Top * p;
        var w = width + (dock.Width - width) * p; var h = fullHeight + (dock.Height - fullHeight) * p;
        // 取整到设备像素: 亚像素尺寸会让 libvlc 的 vout 在两个像素档之间来回抖。
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi <= 0) dpi = 1;
        w = Math.Max(1, Math.Round(w * dpi) / dpi);
        h = Math.Max(1, Math.Round(h * dpi) / dpi);
        left = Math.Round(left * dpi) / dpi;
        top = Math.Round(top * dpi) / dpi;
        return new Rect(left,top,w,h);
    }

    /// <summary>
    /// "两个长度已经足够接近, 不必再写一次"的判据。
    ///
    /// ★★★ 2026-11-05 踩过的坑: 这里最初写的是 `Math.Abs(a - b) > 0.01`。
    ///   而元素的 Width 初始是 **NaN**, `Math.Abs(NaN - 916)` 的结果是 NaN,
    ///   而 **NaN 与任何数比较都是 false** ⇒ 这个"变了才写"的守卫在第一次永远是 false,
    ///   于是视频区宽度一次都没被设置过, 画面停在 NaN(实际表现: 视频区退化成 0×0,
    ///   停靠动画走完也看不到画面)。探针里 `sw=NaN` 就是它。
    ///   ⇒ 必须显式把 NaN/Infinity 当作"需要写"。这条不能省, 省了就是同样的静默失败。
    /// </summary>
    private static bool Nearly(double current, double target)
    {
        if (double.IsNaN(current) || double.IsInfinity(current)) return false;
        if (double.IsNaN(target) || double.IsInfinity(target)) return false;
        return Math.Abs(current - target) <= 0.01;
    }

    private async Task TryWebFrameTransitionAsync()
    {
        _webFramePending=true;var generation=++_webFrameGeneration;
        UpdateAlternateVideoControls();
        try{
            var frame=await VideoFrameTransition.CaptureAsync(_mp);
            if(generation!=_webFrameGeneration || _closing || !_webLayoutMounted)return;
            if(frame==null){_webFramePending=false;StartLegacyWebDockTarget(_webAnimationTo);return;}
            var target=_webAnimationTo;
            var from=VideoSurface.TransformToAncestor(PlayerStage).TransformBounds(new Rect(VideoSurface.RenderSize));
            PrepareWebDockAnchor(target);
            var to=CalculateWebVideoBounds(target);
            if(from.IsEmpty || to.IsEmpty){_webFramePending=false;StartLegacyWebDockTarget(target);return;}
            var transition=_webFrameTransition=new VideoFrameTransition();_webFrameAnimating=true;_webFramePending=false;
            await transition.RunAsync(this,PlayerStage,from,to,frame,
                ()=>{VideoView.Visibility=Visibility.Hidden;VideoSurface.Background=Brushes.Transparent;},
                ()=>{
                    if(generation!=_webFrameGeneration || _closing)return;
                    _webDockProgress=target;
                    ApplyWebVideoBounds();
                    VideoSurface.Background=Brushes.Black;
                    // Move/resize the hidden HWND before showing it at the new location.
                    VideoSurface.UpdateLayout();
                    ApplyHwndHostBlackBackground();
                    VideoView.Visibility=Visibility.Visible;
                    VideoSurface.UpdateLayout();
                    StartVideoBackdropFix();
                });
            if(generation==_webFrameGeneration && !_closing){_webFrameAnimating=false;_webFrameTransition=null;if(Math.Abs(_webAnimationTo-_webDockProgress)>.001)Dispatcher.BeginInvoke(new Action(()=>{if(!_closing && _webLayoutMounted)SetWebDockTarget(_webAnimationTo);}));}
        }catch{if(!_closing && generation==_webFrameGeneration)StartLegacyWebDockTarget(_webAnimationTo);}
        finally{if(generation==_webFrameGeneration){_webFramePending=false;_webFrameAnimating=false;if(!_closing){VideoView.Visibility=Visibility.Visible;VideoSurface.Background=Brushes.Black;UpdateAlternateVideoControls();}}}
    }
    private void StopWebVideoAnimation()
    {
        ++_webFrameGeneration;_webFramePending=false;_webFrameAnimating=false;_webFrameTransition?.Dispose();_webFrameTransition=null;
        if(!_closing){VideoView.Visibility=Visibility.Visible;VideoSurface.Background=Brushes.Black;}
        if (_webRendering) { CompositionTarget.Rendering -= OnWebVideoFrame; _webRendering = false; }
        if (DockTransitionCover != null) DockTransitionCover.Visibility = Visibility.Collapsed;
        // 收尾必须让原生窗口落在最终几何上: 过渡期间它一直没动, 只有这里会动。
        if (_webLayoutMounted && !_closing) ApplyWebVideoBounds();
        if (BrandMark != null) { BrandMark.Opacity = 1; BrandMark.LayoutTransform = Transform.Identity; }
        if (DockTransitionCover != null) DockTransitionCover.Opacity = 1;
        UpdateAlternateVideoControls();
    }

    private void UpdateAlternateVideoControls()
    {
        if (!_webInitialized || ControlBar == null || WebMiniControls == null) return;
        // Both bars belong to the stationary native overlay. Hide them before snapshot
        // capture/motion starts, and keep them hidden until the native surface settles.
        var moving = _webLayoutMounted && (_webFramePending || _webFrameAnimating || _webRendering);
        // The native overlay stays at its original coordinates during snapshot motion.
        // Hide danmaku before capture starts and restore only after the surface settles.
        DanmakuCanvas.Visibility = moving ? Visibility.Hidden : Visibility.Visible;
        var compact = _webLayoutMounted && _webDockProgress > 0.1 && VideoSurface.Width < 560;
        ControlBar.Visibility = !moving && !compact ? Visibility.Visible : Visibility.Collapsed;
        WebMiniControls.Visibility = !moving && compact && !_controlsHidden ? Visibility.Visible : Visibility.Collapsed;
        if (WebCommentsHeading != null) WebCommentsHeading.Text = IsCommentDetailOpen ? "评论详情" : "评论";
    }

    private bool HandleWebVideoWheel(int delta)
    {
        if (!_webLayoutMounted || _webDockProgress >= 0.85 || delta == 0) return false;
        WebLeftScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = WebLeftScroll });
        return true;
    }

    private void StopWebScrollAnimation()
    {
        if (!_webRestoreScrolling) return;
        _webRestoreScrolling = false;
        _webScrollGeneration++;
        var offset = WebLeftScroll.VerticalOffset;
        BeginAnimation(WebScrollOffsetProperty, null); SetValue(WebScrollOffsetProperty, offset);
        WebLeftScroll.ScrollToVerticalOffset(offset);
    }

    private void OnWebRestoreVideo(object sender, RoutedEventArgs e)
    {
        if (!_webLayoutMounted) return;
        TrackpadScroll.Cancel(WebLeftScroll); StopWebScrollAnimation();
        SetValue(WebScrollOffsetProperty, WebLeftScroll.VerticalOffset);
        _webRestoreScrolling = true;
        var generation = _webScrollGeneration;
        var animation = new DoubleAnimation(WebLeftScroll.VerticalOffset, 0, TimeSpan.FromMilliseconds(300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => { if (generation != _webScrollGeneration) return; StopWebScrollAnimation(); WebLeftScroll.ScrollToTop(); };
        BeginAnimation(WebScrollOffsetProperty, animation);
    }

    private void OnWebMediaChanging()
    {
        if (!_webInitialized) return;
        CloseCommentPicture();
        _webSavedScroll = 0;
        if (_webLayoutMounted)
        {
            StopWebVideoAnimation(); StopWebScrollAnimation(); WebLeftScroll.ScrollToTop();
            _webDockProgress = _webAnimationFrom = _webAnimationTo = 0; ApplyWebVideoBounds();
        }
        QueueAlternateLayout();
    }

    private void DisposeAlternatePlayerLayout()
    {
        CloseCommentPicture();
        AppTypography.Changed -= OnPlayerFontChanged;
        UiScale.Unregister(OriginalImageHost);
        Svc.Settings.LayoutChanged -= OnAlternateLayoutSettingChanged; StopWebVideoAnimation(); StopWebScrollAnimation();
        UiScale.Unregister(WebCommentsScaleRoot); UiScale.Unregister(WebCommentChrome);
    }
}
