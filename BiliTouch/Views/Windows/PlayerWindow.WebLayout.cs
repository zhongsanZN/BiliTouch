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
/// 网页布局的内联/右栏停靠控制。跟手和自动收拢共用合成帧时钟与连续速度。
/// 原生 HwndHost 保持可见，在过渡末端一次性落位；vmem 则对整个播放表面
/// 做连续变换与圆角裁剪，视频、黑边、弹幕和手势层一起移动。
/// 布局矩形在运动期间保持稳定，只在落位或外部尺寸变化时提交。
/// </summary>
public partial class PlayerWindow
{
    private bool _webInitialized, _webLayoutMounted, _webLayoutQueued, _webChangingLayout, _webRendering;
    private bool _webPreviousComments, _webHasMounted;
    private bool _webRecommendedSelected;
    private int _webRelatedIndex;
    private Thickness _webRelatedMargin;
    private bool _webDockSpacingPrepared;
    private double _webDockBaseGap, _webDockFullGap, _webDockFullViewport;
    private Thickness _webDockContentMargin;
    private readonly TranslateTransform _webDockBodyMove = new();
    private double _webSavedScroll, _webSavedBodyScroll, _webLegacyInfoScroll;
    private double _webDockProgress, _webAnimationTo;
    private long _webSurfaceSettledAt;
    private int _webScrollGeneration;
    private bool _webRestoreScrolling;
    private bool _webApplyingBounds;
    private bool _webFrameAttached;
    private TimeSpan _webLastRenderingTime = TimeSpan.MinValue;
    private long _webLastFrameTicks;
    private double _webDockVelocity;
    // 本次过渡的时长与「遮罩该缩到哪」的目标几何（相对视频区归一化）。
    // 由 StartLegacyWebDockTarget 一次性算好，逐帧只做插值。
    private double _webAnimationDurationMs = WebMorphDurationMs;
    private double _webCoverTargetScale = .42;
    private double _webCoverTargetNx = .5;
    private double _webCoverTargetNy = .5;
    private double _webCoverAreaW, _webCoverAreaH;

    // vmem 的画面与浮层共用 VideoSurface 的变换和裁剪。
    private bool _vmemMorphPrepared;
    private ScaleTransform? _vmemMorphScale;
    private TranslateTransform? _vmemMorphMove;
    private Rect _vmemMorphSource, _vmemInlineBounds, _vmemDockBounds;
    private readonly RectangleGeometry _vmemClip = new();

    // ── 动效参数（2026-11-09 第六轮定稿）────────────────────────────────────
    /// <summary>停靠/还原的基础时长。HyperOS 那种"贵"的手感落在 400ms 上下。</summary>
    internal const double WebMorphDurationMs = 420;
    /// <summary>反向（滚回去打断正在进行的过渡）用更短的时长，减少"追不上手"的迟滞。</summary>
    internal const double WebMorphReverseDurationMs = 240;
    /// <summary>临界阻尼固有频率：约 420ms 收敛、**零过冲**。旧曲线 ζ≈0.54 会冲到 1.089。</summary>
    internal const double WebMorphOmega = 24;
    private FrameworkElement[] _webHeaderParts = Array.Empty<FrameworkElement>();
    private readonly StackPanel _webHeaderStack = new();
    private ScrollViewer? _webHeaderScroll;
    private bool IsWebVideoLayout => _webLayoutMounted;

    /// <summary>过渡遮罩里那个品牌块的缩放/位移动画(纯 WPF, 每帧只改矩阵)</summary>
    private readonly ScaleTransform _webCoverScale = new(1, 1);
    private readonly TranslateTransform _webCoverMove = new();
    // ★ 品牌块缩放原来每帧 `new ScaleTransform` 赋给 LayoutTransform，会强制整棵浮层
    //   measure/arrange。改成一次性挂实例、逐帧只改 ScaleX/ScaleY，行为等价但不再每帧重排。
    private readonly ScaleTransform _webBrandLayoutScale = new(1, 1);

    private static readonly DependencyProperty WebScrollOffsetProperty = DependencyProperty.Register(
        "WebScrollOffset", typeof(double), typeof(PlayerWindow), new PropertyMetadata(0.0, (d, e) =>
        {
            var player = (PlayerWindow)d;
            if (player._webLayoutMounted) player.WebLeftScroll.ScrollToVerticalOffset((double)e.NewValue);
        }));

    private void InitializeAlternatePlayerLayout()
    {
        _webInitialized = true;
        InfoScroll.RenderTransform = _webDockBodyMove;
        _webHeaderParts = InfoContentStack.Children.Cast<FrameworkElement>().Take(3).ToArray();
        _webHeaderScroll = new ScrollViewer { Content = _webHeaderStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        WebHeaderHost.Content = _webHeaderScroll;
        WebMiniTime.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = TimeText });
        WebMiniPlayGlyph.SetBinding(SkinActionIcon.DataProperty, new Binding("Content.Data") { Source = BtnPlay });
        // ★★★ 2026-11-09 第六轮：变换必须挂在**卡片**上，不是外层容器。
        //   外层 DockTransitionCover 是透明容器（承担裁剪与 Z 序），它自己不能加 Transform
        //   —— 加了会被移出布局、失去裁剪边界；而它的 Child 才是真正要缩放/位移的卡片。
        //   以前这里 Child 是 BrandMark，于是"缩小的只是图标、那块板一直满屏"，
        //   叠加之后又给 brand.RenderTransform 单独赋值把 group 覆盖掉，等于整段动画没生效。
        if (DockTransitionCover.Child is UIElement coverChild && coverChild is FrameworkElement coverElement)
        {
            var group = new TransformGroup();
            group.Children.Add(_webCoverScale);
            group.Children.Add(_webCoverMove);
            coverElement.RenderTransform = group;
            coverElement.RenderTransformOrigin = new Point(0, 0);
            _webCoverScale.ScaleX = _webCoverScale.ScaleY = 1;
            _webCoverMove.X = _webCoverMove.Y = 0;
            _webBrandLayoutScale.ScaleX = _webBrandLayoutScale.ScaleY = 1;
            if (BrandMark is FrameworkElement brand)
            {
                brand.RenderTransform = _webBrandLayoutScale;
                brand.RenderTransformOrigin = new Point(0.5, 0.5);
            }
        }
        WebLeftScroll.PreviewTouchDown += (_, _) => StopWebScrollAnimation();
        WebLeftScroll.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => StopWebScrollAnimation()), true);
        WebLeftPane.SizeChanged += (_, _) => QueueAlternateLayout();
        RightLayoutGrid.SizeChanged += (_, _) => QueueAlternateLayout();
        VideoSurface.SizeChanged += (_, _) =>
        {
            if (_vmemMode && !_vmemMorphPrepared)
                ApplyVmemRadius(_webLayoutMounted && _webDockProgress >= .5 ? 14 : 0);
        };
        Svc.Settings.LayoutChanged += OnAlternateLayoutSettingChanged;
        Loaded += (_, _) => QueueAlternateLayout();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) FinishHiddenWebMotion();
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) FinishHiddenWebMotion(); };
    }

    private void FinishHiddenWebMotion()
    {
        StopSeamlessMorph();
        if (!_webRendering && !_vmemTracking) return;
        _webDockProgress = _vmemTracking ? (_vmemTrackTarget >= .5 ? 1 : 0) : _webAnimationTo;
        _webAnimationTo = _webDockProgress;
        StopWebVideoAnimation();
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

    private bool ShouldUseWebPlayerLayout =>
        Svc.Settings.UseWebPlayerLayout && !_isFullscreen && !_isFillWindow && !_isLive && !_isListenMode;

    private void ApplyAlternateLayoutPreference(bool allowPreLoad = false)
    {
        if (!_webInitialized || _closing || _webChangingLayout || (!allowPreLoad && !IsLoaded)) return;
        bool use = ShouldUseWebPlayerLayout;
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
        RestoreInfoScrollPanning();
        PageHost.BeginAnimation(HeightProperty, null); PageHost.Height = double.NaN;
        foreach (var page in new FrameworkElement[] { IntroPanel, WebRecommendationsPanel, CommentsPanel })
        { page.BeginAnimation(OpacityProperty, null); page.Opacity = 1; page.RenderTransform = Transform.Identity; }
    }

    private void MountWebLayout()
    {
        // ★★ 必须在**动任何子元素之前**抓尺寸(2026-11-08): 下面要摘挂一堆元素、改可见性,
        //   一旦开始, VideoSurface 的 ActualWidth/ActualHeight 会在重排过程中变成 0,
        //   那时再"钉"就晚了 —— 实测钉到的是 0, 于是 Left/Top + NaN 依旧量成 0×0。
        PinVideoSurfaceSize();
        _webPreviousComments = TabComments.IsChecked == true; _webLegacyInfoScroll = InfoScroll.VerticalOffset;
        _webLayoutMounted = true;
        // vmem carries its own letterbox and rounded clip. An opaque placeholder
        // would leave a stationary black rectangle behind during the return gesture.
        WebDockAnchor.Background = _vmemMode ? Brushes.Transparent : Brushes.Black;
        ResetWebPageAnimations();
        foreach (var part in _webHeaderParts) { DetachWebElement(part); _webHeaderStack.Children.Add(part); }
        DetachWebElement(CommentsPanel); WebCommentsHost.Content = CommentsPanel;
        _webRelatedIndex = IntroPanel.Children.IndexOf(RelatedPanel);
        _webRelatedMargin = RelatedPanel.Margin;
        DetachWebElement(RelatedPanel); WebRecommendationsPanel.Children.Add(RelatedPanel);
        RelatedPanel.Margin = new Thickness(0);
        RelatedHeading.Text = "推荐";
        RelatedHeading.Visibility = Visibility.Collapsed;
        if (RelatedHeaderBorder != null)
        {
            RelatedHeaderBorder.BorderThickness = new Thickness(0);
            RelatedHeaderBorder.Padding = new Thickness(0);
        }
        foreach (var control in new FrameworkElement[] { BtnBackToComments, CommentFab, CommentComposerPanel })
        { DetachWebElement(control); WebCommentChrome.Children.Add(control); }
        InfoContentStack.Margin = new Thickness(20, 0, 20, 24);
        WebLeftPane.Visibility = WebHeaderHost.Visibility = WebDockAnchor.Visibility = Visibility.Visible;
        TabComments.Visibility = Visibility.Collapsed; TabRecommended.Visibility = Visibility.Visible;
        if (_webRecommendedSelected) TabRecommended.IsChecked = true; else TabIntro.IsChecked = true;
        SelectWebInfoPage(_webRecommendedSelected, animate: false);
        UiScale.Register(WebCommentsScaleRoot); UiScale.Register(WebCommentChrome);
        // ★★★ 2026-11-08 修「退出全屏后视频变黑」（用户报的第二个问题，与闪退同源）：
        //   下面两行会把 VideoSurface 从 Stretch 改成 Left/Top，而它的 Width/Height 此时是
        //   **NaN**（UnmountWebLayout 里被清成 NaN 了）。Left/Top + NaN ⇒ 元素按内容量尺寸，
        //   而内容 VideoView 是 HwndHost、没有期望尺寸 ⇒ 量成 **0×0**。
        //   0×0 会立刻下发一次 SetWindowPos，libvlc 侧就是 `VoutDisplayEvent 'resize' 0x0`
        //   ⇒ d3d11 销毁 pool 与 vout（`destroying useless vout`）⇒ 之后再也不重建 ⇒ **永久黑屏**。
        //   实测时间线（探针 surfaceSizes）：
        //     1386.7x924.4 → **0x0** → 962.7x541.3 → 752x423.1
        //   中间那个 0×0 就是黑屏的全部原因；后面的正确尺寸救不回来，因为 vout 已经没了。
        //   ⇒ 改对齐方式**之前**先把当前实际尺寸钉住，让宽度高度永远不经过 0。
        //     随后 UpdateWebMeasurements → ApplyWebVideoBounds 会把它改成真正该有的尺寸。
        Grid.SetColumnSpan(VideoSurface, 2); VideoSurface.HorizontalAlignment = HorizontalAlignment.Left; VideoSurface.VerticalAlignment = VerticalAlignment.Top; Panel.SetZIndex(VideoSurface, 10);
        _webDockProgress = _webAnimationTo = 0;
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
        DetachWebElement(RelatedPanel);
        IntroPanel.Children.Insert(Math.Clamp(_webRelatedIndex, 0, IntroPanel.Children.Count), RelatedPanel);
        RelatedPanel.Margin = _webRelatedMargin;
        RelatedHeading.Text = "相关视频";
        RelatedHeading.Visibility = Visibility.Visible;
        if (RelatedHeaderBorder != null)
        {
            RelatedHeaderBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            RelatedHeaderBorder.Padding = new Thickness(0, 12, 0, 0);
        }
        WebRecommendationsPanel.Visibility = TabRecommended.Visibility = Visibility.Collapsed;
        for (int i = 0; i < _webHeaderParts.Length; i++) { DetachWebElement(_webHeaderParts[i]); InfoContentStack.Children.Insert(i, _webHeaderParts[i]); }
        foreach (var control in new FrameworkElement[] { BtnBackToComments, CommentFab, CommentComposerPanel })
        { DetachWebElement(control); InfoPanel.Children.Add(control); }
        InfoContentStack.Margin = new Thickness(20, 18, 20, 24);
        WebLeftPane.Visibility = WebHeaderHost.Visibility = WebDockAnchor.Visibility = Visibility.Collapsed;
        WebDockAnchor.Height = 0; TabComments.Visibility = Visibility.Visible;
        Grid.SetColumnSpan(VideoSurface, 1); Panel.SetZIndex(VideoSurface, 0);
        // ★ 卸载这一侧同样会经过"Left/Top + NaN"：先把对齐方式改回 Stretch，再清 Width/Height。
        //   反过来的话（先清成 NaN 再改对齐），中间那一刻就是 0×0 ⇒ 同样会把 vout 弄没。
        //   Stretch + NaN 是正常的"按父级拉伸"，所以顺序只能是这一个。
        VideoSurface.HorizontalAlignment = HorizontalAlignment.Stretch; VideoSurface.VerticalAlignment = VerticalAlignment.Stretch;
        VideoSurface.Width = VideoSurface.Height = double.NaN; VideoSurface.Margin = new Thickness(0);
        ResetWebPageAnimations();
        var comments = _webPreviousComments || IsCommentDetailOpen;
        IntroPanel.Visibility = comments ? Visibility.Collapsed : Visibility.Visible; CommentsPanel.Visibility = comments ? Visibility.Visible : Visibility.Collapsed;
        if (comments) TabComments.IsChecked = true; else TabIntro.IsChecked = true;
        InfoScroll.ScrollToVerticalOffset(_webLegacyInfoScroll);
        UpdateCommentComposer(); UpdateAlternateVideoControls();
        if (_vmemMode) ResetVmemMorph(docked: false);
        UpdateCommentPicturePlacement();
    }

    private void TabRecommended_Checked(object sender, RoutedEventArgs e)
    {
        if (_webLayoutMounted) SelectWebInfoPage(recommended: true);
    }

    private void SelectWebInfoPage(bool recommended, bool animate = true)
    {
        if (!_webLayoutMounted) return;
        _webRecommendedSelected = recommended;
        IntroPanel.Visibility = recommended ? Visibility.Collapsed : Visibility.Visible;
        WebRecommendationsPanel.Visibility = recommended ? Visibility.Visible : Visibility.Collapsed;
        // The comments keep their independent left-column state, including an open thread/draft.
        CommentsPanel.Visibility = Visibility.Visible;
        UpdateWebRecommendationsHint();
        if (animate && !_webChangingLayout)
            AnimatePageEntrance(recommended ? WebRecommendationsPanel : IntroPanel, fromRight: recommended);
        UpdateCommentComposer();
    }

    private void UpdateWebRecommendationsHint()
    {
        if (WebRecommendationsHint == null) return;
        WebRecommendationsHint.Text = _isLocalPlayback ? "本地视频没有推荐" : _relatedEmptyMessage;
        WebRecommendationsHint.Visibility = RelatedPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 把 <c>VideoSurface</c> 的当前实际尺寸钉成显式 Width/Height，避免切换对齐方式时被量成 0。
    ///
    /// ★ 为什么必须有这一步（2026-11-08，退出全屏黑屏的根因）：
    ///   挂/卸网页布局都会改 <c>VideoSurface</c> 的 <c>HorizontalAlignment/VerticalAlignment</c>。
    ///   而 <c>Width/Height</c> 在"原生布局"下是 <c>NaN</c>（Stretch 拉伸）。
    ///   <c>Stretch + NaN</c> ⇒ 按父级拉伸，尺寸正常；
    ///   一旦改成 <c>Left/Top + NaN</c> ⇒ 按**内容**量尺寸，而内容是 <c>VideoView</c>(HwndHost)，
    ///   它没有期望尺寸 ⇒ **量成 0×0**。
    ///   0×0 会立刻下发 <c>SetWindowPos(0,0)</c>，libvlc 侧就是
    ///   <c>VoutDisplayEvent 'resize' 0x0</c> ⇒ d3d11 销毁 pool 与 vout
    ///   （日志里 <c>destroying useless vout</c> + <c>Win32VoutCloseWindow</c>）⇒ **永久黑屏**。
    ///   之后即便尺寸又变回正确值也不会重建 —— 这就是"退出全屏后视频变黑、怎么等都不恢复"。
    ///   ⇒ 先钉住当前实际尺寸，让宽度/高度永远不经过 0。
    ///
    /// ★ 钉的是 <c>ActualWidth/ActualHeight</c>（已经排过版的真实值），不是 RenderSize 也不是 NaN：
    ///   窗口刚建好还没排过版时两者都可能是 0，那时**不钉**（保持 NaN 让 Stretch 生效），
    ///   否则会把 0 永久写进去 —— 那比原来的 bug 更难查。
    /// </summary>
    private void PinVideoSurfaceSize()
    {
        var width = VideoSurface.ActualWidth;
        var height = VideoSurface.ActualHeight;
        if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height)) return;
        VideoSurface.Width = width;
        VideoSurface.Height = height;
    }

    private void UpdateWebMeasurements()
    {
        if (!_webLayoutMounted || _closing || PlayerStage.ActualWidth <= 0) return;
        // A viewport/DPI change invalidates both endpoint rectangles. Commit first and
        // rebuild on the next gesture, rather than completing toward stale coordinates.
        if (_vmemTracking || _webRendering)
        {
            _webDockProgress = _vmemTracking ? (_vmemTrackTarget >= .5 ? 1 : 0) : _webAnimationTo;
            _webAnimationTo = _webDockProgress;
            StopWebVideoAnimation();
        }
        var width = WebLeftScroll.ViewportWidth > 0 ? WebLeftScroll.ViewportWidth : WebLeftPane.ActualWidth;
        if (width <= 0) return;
        var height = InlineWebVideoHeight(width);
        if (double.IsNaN(WebVideoPlaceholder.Height) || Math.Abs(WebVideoPlaceholder.Height - height) > 0.5) WebVideoPlaceholder.Height = height;
        var scale = UiScale.Percent / 100.0;
        WebCommentsScaleRoot.MinHeight = Math.Max(120, WebLeftPane.ActualHeight / scale);
        var dockHeight = Math.Max(0, RightLayoutGrid.ActualWidth - 40) * 9 / 16;
        WebHeaderHost.MaxHeight = Math.Max(80, Math.Min(RightLayoutGrid.ActualHeight * 0.55, RightLayoutGrid.ActualHeight - dockHeight - 44));
        UpdateDockForOffset(height);
        if (!_webRendering && !_vmemTracking) ApplyWebVideoBounds();
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
        PrefetchCommentPage(WebLeftScroll, e);
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
            UpdateWebMeasurements();
        else if (e.VerticalChange != 0 && WebVideoPlaceholder.Height > 0)
        {
            // ★ vmem 模式：滚动直接驱动画面变形（跟手）。做不到就退回阈值触发。
            if (!(_vmemMode && TryTrackVmemMorph(WebVideoPlaceholder.Height)))
                UpdateDockForOffset(WebVideoPlaceholder.Height);
        }
    }

    // ── vmem 跟手 ────────────────────────────────────────────────────────────
    private bool _vmemTracking;
    private double _vmemTrackTarget;
    private long _vmemLastScrollMs;

    /// <summary>滚动只更新目标；合成帧按真实时间积分，接管时保留当前速度。140ms 无输入后就近收拢。</summary>
    private bool TryTrackVmemMorph(double height)
    {
        if (!_vmemMode || !_webLayoutMounted || _closing || height <= 0) return false;
        var offset = WebLeftScroll.VerticalOffset;
        var start = height * 0.18;
        var band = Math.Max(80, height * 0.55);
        var p = Math.Clamp((offset - start) / band, 0, 1);
        if (!_vmemTracking && !_webRendering && Math.Abs(p - _webDockProgress) < .0001) return true;
        if (!_vmemMorphPrepared) PrepareVmemMorph();
        if (!_vmemMorphPrepared) return false;
        PrepareWebDockSpacing();

        // Tracking takes over the same composition clock, keeping current position and velocity.
        // Two callbacks must never write the progress during an interrupted settle.
        _webRendering = false;
        var starting = !_vmemTracking;
        _vmemTracking = true;
        _vmemTrackTarget = p;
        _vmemLastScrollMs = Environment.TickCount64;
        EnsureWebFrameClock();
        if (starting) UpdateAlternateVideoControls();
        return true;
    }

    private void EnsureWebFrameClock()
    {
        if (_webFrameAttached) return;
        _webFrameAttached = true;
        _webLastFrameTicks = Stopwatch.GetTimestamp();
        _webLastRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnWebVideoFrame;
    }

    private void AdvanceVmemTracking(double seconds)
    {
        var trackOmega = Math.Abs(_vmemTrackTarget - _webDockProgress) > 0.55 ? WebMorphOmega : 36.0;
        (_webDockProgress, _webDockVelocity) = SpringMotion.Advance(
            _webDockProgress, _webDockVelocity, _vmemTrackTarget, seconds, trackOmega);
        UpdateWebDockSpacing(_webDockProgress);
        ApplyVmemMorph(_webDockProgress);
        if (Environment.TickCount64 - _vmemLastScrollMs < 140) return;
        _vmemTracking = false;
        StartLegacyWebDockTarget(_vmemTrackTarget >= 0.5 ? 1.0 : 0.0);
    }

    /// <summary>
    /// 起一次停靠/还原过渡。
    /// ★ 目标与当前位置几乎相同时不重启动画(滚动时每帧都会调到这里, 不能每帧重建动画状态)。
    ///
    /// ★★★ 2026-11-09 停用「截图替身」过渡（TryWebFrameTransitionAsync）：
    ///   它每次停靠/还原要 TakeSnapshot 写 1280px PNG、开一个透明分层窗口、两次 DwmFlush、
    ///   把 VideoView 隐藏 ~0.43s、380ms 弹簧、180ms 停留、commit 时重建一次 vout 交换链，
    ///   正常一轮 0.65~0.8s 画面被静态截图替代，最坏 3s+，之后还挂 3s 守护定时器；
    ///   期间弹幕与控制栏全部隐藏。这正是「开网页版布局卡顿更多」的来源。
    ///   实测与调用链见 docs/FPS-REGRESSION-2026-11-09.md。
    ///   ⇒ 2026-11-09 第三轮**撤销 DWM 镜像**：在 WPF WindowChrome 源 + 225% DPI 真机上，
    ///     DwmRegisterThumbnail 能注册、Update 也返回 0，但 rcSource 裁切要么全黑、
    ///     要么抓成整个播放器窗口缩小版；加上物理像素/DIP 双重缩放会让镜像窗口放大
    ///     2.25 倍飞出屏幕。用户实机看到的是「画面变灰 + 黑方块飞过」，比原来更糟。
    ///     证据见 docs/FPS-REGRESSION-2026-11-09.md 与 artifacts/touch-tests/web-morph/。
    ///   ⇒ 回到纯 WPF 路径，但修掉那个隐藏 bug（遮罩 TransformGroup 被覆盖、从来没生效），
    ///     并把遮罩改成半透明：视频全程可见、不冻结、不重影、不依赖任何原生镜像。
    /// </summary>
    private void SetWebDockTarget(double target)
    {
        StartLegacyWebDockTarget(target);
    }

    private void StartLegacyWebDockTarget(double target)
    {
        _vmemTracking = false;
        if (Math.Abs(target - _webAnimationTo) < 0.0001 && _webRendering) return;
        if (Math.Abs(target - _webDockProgress) < 0.0001 && Math.Abs(_webDockVelocity) < .025)
        {
            _webAnimationTo = target;
            _webDockProgress = target;
            StopWebVideoAnimation();
            return;
        }
        var reversing = (target < _webDockProgress - .0001 && _webAnimationTo > _webDockProgress + .0001)
                        || (target > _webDockProgress + .0001 && _webAnimationTo < _webDockProgress - .0001);
        _webAnimationTo = target;
        _webAnimationDurationMs = reversing ? WebMorphReverseDurationMs : WebMorphDurationMs;
        _webSurfaceSettledAt = 0;
        PrepareWebDockSpacing();
        // ★ 一次性算好「遮罩该缩到哪」：朝真实的 WebDockAnchor 几何收缩，
        //   而不是硬编码往视频区右上角缩 —— 后者与落点方向不一致，观感就是"缩完瞬移"。
        UpdateWebCoverTargetGeometry();
        if (_vmemMode)
        {
            // ★ vmem：不用遮罩，直接算"画面要缩到哪"，逐帧变换整个播放表面。
            if (!_vmemMorphPrepared) PrepareVmemMorph();
            DockTransitionCover.Visibility = Visibility.Collapsed;
        }
        else
        {
            DockTransitionCover.Visibility = Visibility.Visible;
        }
        // 品牌块正好压在视频中心，过渡期间收起，别挡画面。
        if (BrandMark != null) BrandMark.Visibility = Visibility.Collapsed;
        _webRendering = true;
        EnsureWebFrameClock();
        UpdateAlternateVideoControls();
    }

    /// <summary>缓存内联、停靠两个固定端点及当前布局矩形，正反方向共用同一坐标系。</summary>
    private void PrepareVmemMorph()
    {
        _vmemMorphPrepared = false;
        try
        {
            var from = VideoSurface.TransformToAncestor(PlayerStage)
                .TransformBounds(new Rect(VideoSurface.RenderSize));
            _vmemInlineBounds = CalculateWebVideoBounds(0);
            _vmemDockBounds = CalculateWebVideoBounds(1);
            if (from.Width < 2 || from.Height < 2 || _vmemDockBounds.IsEmpty || _vmemInlineBounds.IsEmpty) return;
            _vmemMorphSource = from;
            if (_vmemMorphScale == null || _vmemMorphMove == null)
            {
                _vmemMorphScale = new ScaleTransform(1, 1);
                _vmemMorphMove = new TranslateTransform(0, 0);
                var g = new TransformGroup();
                g.Children.Add(_vmemMorphScale);
                g.Children.Add(_vmemMorphMove);
                // Transform the presentation surface together. Image.Stretch can give
                // VmemView a smaller, centered RenderSize for non-16:9 media; separate
                // transforms would drift against the overlay and snap on landing.
                VideoSurface.RenderTransform = g;
                VideoSurface.RenderTransformOrigin = new Point(0, 0);
            }
            _vmemMorphPrepared = true;
        }
        catch { }
    }

    /// <summary>在固定布局之上变换整个播放表面，画面、黑边、弹幕及命中区域一起移动。</summary>
    private void ApplyVmemMorph(double p)
    {
        if (!_vmemMorphPrepared || _vmemMorphScale == null || _vmemMorphMove == null) return;
        // The black letterbox belongs to the same moving surface as the decoded image.
        VideoSurface.Background = Brushes.Black;
        var bounds = InterpolateWebBounds(_vmemInlineBounds, _vmemDockBounds, p);
        var sx = bounds.Width / _vmemMorphSource.Width;
        var sy = bounds.Height / _vmemMorphSource.Height;
        _vmemMorphScale.ScaleX = sx;
        _vmemMorphScale.ScaleY = sy;
        _vmemMorphMove.X = bounds.Left - _vmemMorphSource.Left;
        _vmemMorphMove.Y = bounds.Top - _vmemMorphSource.Top;
        // Clip is expressed before the scale; keep the visible corner radius in screen DIPs.
        ApplyVmemRadius(14 * p / Math.Max(.01, Math.Min(sx, sy)));
    }

    /// <summary>落位后复位变换；保留小窗圆角，尺寸变化时更新裁剪边界。</summary>
    private void ResetVmemMorph(bool docked)
    {
        _vmemMorphPrepared = false;
        if (_vmemMorphScale != null) { _vmemMorphScale.ScaleX = _vmemMorphScale.ScaleY = 1; }
        if (_vmemMorphMove != null) { _vmemMorphMove.X = _vmemMorphMove.Y = 0; }
        ApplyVmemRadius(docked ? 14 : 0);
        VideoSurface.Background = Brushes.Black;
    }

    private void ApplyVmemRadius(double radius)
    {
        if (radius <= 0.5) { VideoSurface.Clip = null; return; }
        var w = Math.Max(1, VideoSurface.ActualWidth);
        var h = Math.Max(1, VideoSurface.ActualHeight);
        _vmemClip.Rect = new Rect(0, 0, w, h);
        _vmemClip.RadiusX = _vmemClip.RadiusY = radius;
        VideoSurface.Clip = _vmemClip;
    }

    private static Rect InterpolateWebBounds(Rect inline, Rect dock, double p) => new(
        inline.X + (dock.X - inline.X) * p, inline.Y + (dock.Y - inline.Y) * p,
        inline.Width + (dock.Width - inline.Width) * p, inline.Height + (dock.Height - inline.Height) * p);

    /// <summary>
    /// 计算遮罩应从「铺满视频区」缩到「停靠位」的归一化目标：缩放比 + 目标中心。
    /// 输出相对视频区（0..1），在 UpdateWebCoverAnimation 里逐帧插值。
    ///
    /// ★ 用 CalculateWebVideoBounds(1) 取**逻辑**落点，而不是 PrepareWebDockAnchor(1)：
    ///   后者会把 WebDockAnchor.Height 真的设成满高，动画一开始右栏内容就会被顶下去
    ///   ——那是一次新的布局跳变。CalculateWebVideoBounds 只读 ActualWidth、不写布局。
    /// </summary>
    private void UpdateWebCoverTargetGeometry()
    {
        try
        {
            var area = VideoSurface.TransformToAncestor(PlayerStage)
                .TransformBounds(new Rect(VideoSurface.RenderSize));
            if (area.Width < 2 || area.Height < 2) return;
            var dock = CalculateWebVideoBounds(1);
            if (dock.IsEmpty || dock.Width < 2 || dock.Height < 2) return;
            var scale = Math.Clamp(dock.Width / area.Width, .18, .95);
            _webCoverTargetScale = scale;
            _webCoverAreaW = area.Width;
            _webCoverAreaH = area.Height;
            // 目标中心：朝真实停靠位去，但保证收缩后整块仍留在视频区内（否则会被浮层裁掉，
            // 落位那一跳就盖不住了）。
            var rawNx = (dock.Left + dock.Width / 2 - area.Left) / area.Width;
            var rawNy = (dock.Top + dock.Height / 2 - area.Top) / area.Height;
            _webCoverTargetNx = Math.Clamp(rawNx, scale / 2 + .02, 1 - scale / 2 - .02);
            _webCoverTargetNy = Math.Clamp(rawNy, scale / 2 + .02, 1 - scale / 2 - .02);
        }
        catch { /* 几何拿不到就退回默认，不影响动画 */ }
    }

    /// <summary>唯一的合成帧入口：去重同一呈现帧，按实际 dt 推进跟手或自动收拢。</summary>
    private void OnWebVideoFrame(object? sender, EventArgs e)
    {
        if (!_webLayoutMounted || _closing) { StopWebVideoAnimation(); return; }
        if (e is RenderingEventArgs frame)
        {
            if (_webLastRenderingTime == frame.RenderingTime) return;
            _webLastRenderingTime = frame.RenderingTime;
        }
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_webLastFrameTicks, now).TotalSeconds;
        _webLastFrameTicks = now;
        if (_vmemTracking) { AdvanceVmemTracking(seconds); return; }
        if (!_webRendering) return;
        // ★ 临界阻尼弹簧（零过冲）：旧曲线 ζ≈0.54 会冲到 1.089，观感是"弹一下"，
        //   廉价感主要来自这个过冲。现在用 1 - e^(-ωt)(1+ωt)，单调收敛、末端极轻。
        //   只改纯 WPF 变换矩阵，不重采样位图、不隐藏原生窗口、不等 DWM。
        (_webDockProgress, _webDockVelocity) = SpringMotion.Advance(
            _webDockProgress, _webDockVelocity, _webAnimationTo, seconds,
            WebMorphOmega * WebMorphDurationMs / _webAnimationDurationMs);
        // Only the right scroll viewport yields. The video keeps its fixed native
        // size until commit; no UpdateLayout or video-window resize is done per frame.
        UpdateWebDockSpacing(_webDockProgress);
        // ★ vmem 模式走「画面本体变形」；其余走「玻璃卡片」兜底。
        if (_vmemMode && _vmemMorphPrepared) ApplyVmemMorph(_webDockProgress);
        else UpdateWebCoverAnimation(_webDockProgress);
        // ★ 曾试过让原生 VideoSurface 跟着平移/分档缩放，真机截图证实 HwndHost 的
        //   WPF 黑底与 vout HWND 会分离、露黑边白边，分档 resize 还会刷出纯白帧。
        //   ⇒ 过渡期间原生窗口一律不动，只有纯 WPF 遮罩连续变形；结束帧才落位一次。
        // ★ 不再逐帧调 UpdateAlternateVideoControls()：moving 在整段动画里恒定，
        //   StartLegacyWebDockTarget / StopWebVideoAnimation 各刷一次就够。

        if (Math.Abs(_webDockProgress - _webAnimationTo) < .00075 && Math.Abs(_webDockVelocity) < .025)
        {
            _webDockProgress = _webAnimationTo;
            _webDockVelocity = 0;
            // 动画走完的这一帧才把原生窗口**一次性**移到目标几何。此刻它被遮罩完全盖住,
            // 用户看不到这次 SetWindowPos。
            if (_webSurfaceSettledAt == 0) ApplyWebVideoBounds();
            // ★ vmem：落位后复位播放表面的变换，并保留停靠圆角。
            //   此刻 VideoSurface 已经就是目标矩形，画面尺寸位置完全重合 ⇒ 零跳变。
            if (_vmemMode)
            {
                StopWebVideoAnimation();
                return;
            }
            // ★ 删除截图替身后，commit 的这一次原生 resize 会让 vout 宿主窗口重擦一遍
            //   默认浅色底（屏幕采样能抓到 1 帧 ~236,237,228 的白闪）。立刻刷黑宿主类背景刷。
            if (_webSurfaceSettledAt == 0) ApplyHwndHostBlackBackground();
            if (_webSurfaceSettledAt == 0) _webSurfaceSettledAt = now;
            // ★ 交叉淡化：落位那一帧原生窗口尺寸从大跳到小，用 140ms 把遮罩从 0.65
            //   线性淡出，让跳变藏在渐隐里而不是硬切；同时给 vout 新表面几帧恢复时间。
            var settleMs = Stopwatch.GetElapsedTime(_webSurfaceSettledAt, now).TotalMilliseconds;
            DockTransitionCover.Opacity = Math.Max(0, 0.65 * (1 - Math.Clamp(settleMs / 140, 0, 1)));
            if (settleMs >= 140) StopWebVideoAnimation();
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
        // ★ 卡片自身的不透明度在头 12% 进度内快速淡入：卡片刻意做得比原来的薄纱实，
        //   如果 p=0 就整块盖上去，手势一开始视频会"闪一下变暗"。淡入后它才读得出是一张卡。
        if (DockTransitionCard != null) DockTransitionCard.Opacity = Math.Clamp(p / .12, 0, 1);
        // 缩放与中心位置**同时插值**：遮罩朝真实停靠位收缩，而不是原地缩成一块色块。
        var scale = 1 + (_webCoverTargetScale - 1) * p;
        _webCoverScale.ScaleX = _webCoverScale.ScaleY = scale;
        var w = _webCoverAreaW > 2 ? _webCoverAreaW : WebLeftScroll.ActualWidth;
        var h = _webCoverAreaH > 2 ? _webCoverAreaH : Math.Max(1, WebVideoPlaceholder.ActualHeight);
        if (w > 0)
        {
            var nx = .5 + (_webCoverTargetNx - .5) * p;
            var ny = .5 + (_webCoverTargetNy - .5) * p;
            // 变换原点在左上角：把"中心落到 nx,ny"反解成左上角位移。
            _webCoverMove.X = w * (nx - scale / 2);
            _webCoverMove.Y = h * (ny - scale / 2);
        }
        if (BrandMark != null)
        {
            var inverse = scale > 0.01 ? 1 / scale : 1;
            var brandScale = 1 + (inverse - 1) * 1.0;
            // ★ 就地改缓存的缩放实例，不再每帧 `new ScaleTransform` 赋给 LayoutTransform：
            //   后者会让整棵浮层每帧重新 measure/arrange，是过渡期间周期性掉帧的主因。
            _webBrandLayoutScale.ScaleX = _webBrandLayoutScale.ScaleY = brandScale * 0.72;
            BrandMark.Opacity = 0.35 + 0.65 * (1 - p);
        }
        if (DockTransitionPoster != null) DockTransitionPoster.CornerRadius = new CornerRadius(4 + 10 * p);
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
        var restoreSpacing = RestoreWebDockSpacing();
        var changed = SetWebDockAnchorSpacing(progress);
        // Resolve the real placeholder and scroll viewport once, before the video commits.
        if (changed || restoreSpacing) PlayerStage.UpdateLayout();
    }

    private bool RestoreWebDockSpacing()
    {
        if (!_webDockSpacingPrepared) return false;
        _webDockSpacingPrepared = false;
        InfoContentStack.Margin = _webDockContentMargin;
        InfoScroll.Height = double.NaN;
        InfoScroll.VerticalAlignment = VerticalAlignment.Stretch;
        _webDockBodyMove.Y = 0;
        WebDockAnchor.Background = _vmemMode ? Brushes.Transparent : Brushes.Black;
        return true;
    }

    private bool SetWebDockAnchorSpacing(double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        var margin = new Thickness(20, 12 * p, 20, 0);
        var changed = WebDockAnchor.Margin != margin;
        if (changed) WebDockAnchor.Margin = margin;
        var height = WebDockAnchor.ActualWidth * 9 / 16 * p;
        if (!Nearly(WebDockAnchor.Height, height)) { WebDockAnchor.Height = height; changed = true; }
        return changed;
    }

    private void PrepareWebDockSpacing()
    {
        if (_webDockSpacingPrepared || !_webLayoutMounted || InfoScroll.ActualHeight <= 0) return;
        _webDockSpacingPrepared = true;
        _webDockBaseGap = WebDockAnchor.Height + WebDockAnchor.Margin.Top;
        _webDockFullGap = WebDockAnchor.ActualWidth * 9 / 16 + 12;
        _webDockFullViewport = InfoScroll.ActualHeight + _webDockBaseGap;
        _webDockContentMargin = InfoContentStack.Margin;
        // Keep a full-height scroll viewport for the whole gesture. Bottom padding
        // preserves its original scroll range when expanding from a docked viewport.
        InfoContentStack.Margin = new Thickness(_webDockContentMargin.Left, _webDockContentMargin.Top,
            _webDockContentMargin.Right, _webDockContentMargin.Bottom + _webDockBaseGap);
        InfoScroll.VerticalAlignment = VerticalAlignment.Top;
        InfoScroll.Height = _webDockFullViewport;
        WebDockAnchor.Background = Brushes.Transparent;
    }

    private void UpdateWebDockSpacing(double progress)
    {
        if (!_webDockSpacingPrepared) return;
        var gap = _webDockFullGap * Math.Clamp(progress, 0, 1);
        _webDockBodyMove.Y = gap - _webDockBaseGap;
        // Near the end of the list, restore the real scroll limit progressively as
        // the viewport opens up. This prevents a last-frame clamp/jump on undocking.
        var extent = Math.Max(0, InfoScroll.ExtentHeight - _webDockBaseGap);
        var limit = Math.Max(0, extent - _webDockFullViewport + gap);
        if (InfoScroll.VerticalOffset > limit + .1) InfoScroll.ScrollToVerticalOffset(limit);
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
        try
        {
            var transform = WebDockAnchor.TransformToAncestor(PlayerStage);
            dock = transform.TransformBounds(new Rect(0, 0, logicalDockWidth, logicalDockHeight));
            // The dock endpoint includes its final top margin even while the placeholder
            // is still collapsed. Predict it without mutating layout during the gesture.
            var origin = transform.Transform(new Point());
            var inset = transform.Transform(new Point(0, 12 - WebDockAnchor.Margin.Top));
            dock.Offset(inset.X - origin.X, inset.Y - origin.Y);
        }
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

    // ★ 2026-11-09 删除「截图替身」过渡（原 TryWebFrameTransitionAsync + VideoFrameTransition）。
    //   原因与实测见 docs/FPS-REGRESSION-2026-11-09.md：每次停靠/还原要写 1280px PNG、
    //   开透明分层窗口、两次 DwmFlush、把 VideoView 隐藏 ~0.43s、commit 时重建 vout 交换链，
    //   正常一轮 0.65~0.8s 画面被静态截图替代、弹幕与控制栏全隐藏，最坏 3s+。
    //   现在统一走 StartLegacyWebDockTarget 的纯 WPF 遮罩：只改变换矩阵、视频全程可见，
    //   结束帧才动一次原生窗口。DWM 实时镜像(第二轮的 LiveWindowTransition)已撤销，
    //   原因见 SetWebDockTarget 上方注释。
    private void StopWebVideoAnimation()
    {
        _vmemTracking = false;
        _webDockVelocity = 0;
        _webRendering = false;
        if (_webFrameAttached)
        {
            CompositionTarget.Rendering -= OnWebVideoFrame;
            _webFrameAttached = false;
        }
        if(!_closing){SetVideoHostVisible(true);VideoSurface.Background=Brushes.Black;}
        // Commit before dropping the presentation transform; the next layout updates the clip size.
        if (_webLayoutMounted && !_closing) ApplyWebVideoBounds();
        else RestoreWebDockSpacing();
        // ★ vmem：收尾把画面变换复位，圆角按当前是否停靠保留。
        if (_vmemMode) ResetVmemMorph(docked: _webDockProgress >= .5);
        if (DockTransitionCover != null) DockTransitionCover.Visibility = Visibility.Collapsed;
        // 收尾必须让原生窗口落在最终几何上: 过渡期间它一直没动, 只有这里会动。
        if (_webLayoutMounted && !_closing && !_vmemMode) ApplyHwndHostBlackBackground();
        if (BrandMark != null) { BrandMark.Visibility = Visibility.Visible; BrandMark.Opacity = 1; }
        _webCoverScale.ScaleX = _webCoverScale.ScaleY = 1;
        _webCoverMove.X = _webCoverMove.Y = 0;
        _webBrandLayoutScale.ScaleX = _webBrandLayoutScale.ScaleY = 1;
        if (DockTransitionCover != null) DockTransitionCover.Opacity = 1;
        if (DockTransitionCard != null) DockTransitionCard.Opacity = 1;
        UpdateAlternateVideoControls();
    }

    private void UpdateAlternateVideoControls()
    {
        if (!_webInitialized || ControlBar == null || WebMiniControls == null) return;
        // Both bars belong to the stationary native overlay. Hide them before snapshot
        // capture/motion starts, and keep them hidden until the native surface settles.
        var moving = _webLayoutMounted && (_webRendering || _vmemTracking);
        // The native overlay stays at its original coordinates during snapshot motion.
        // Hide danmaku before capture starts and restore only after the surface settles.
        DanmakuCanvas.Visibility = moving && !_vmemMode ? Visibility.Hidden : Visibility.Visible;
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
        CancelDanmakuFiltering();
        if (!_webInitialized) return;
        CloseCommentPicture();
        _webSavedScroll = 0;
        if (_webLayoutMounted)
        {
            StopWebVideoAnimation(); StopWebScrollAnimation(); WebLeftScroll.ScrollToTop();
            _webDockProgress = _webAnimationTo = 0; ApplyWebVideoBounds();
            if (_vmemMode) ResetVmemMorph(docked: false);
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
