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
    private long _webAnimationWallStart;
    private long _webSurfaceSettledAt;
    private int _webScrollGeneration;
    private bool _webRestoreScrolling;
    private bool _webApplyingBounds;
    // 本次过渡的时长与「遮罩该缩到哪」的目标几何（相对视频区归一化）。
    // 由 StartLegacyWebDockTarget 一次性算好，逐帧只做插值。
    private double _webAnimationDurationMs = WebMorphDurationMs;
    private double _webCoverTargetScale = .42;
    private double _webCoverTargetNx = .5;
    private double _webCoverTargetNy = .5;
    private double _webCoverAreaW, _webCoverAreaH;

    // ---- vmem 模式：直接对 VmemView 做 RenderTransform，让**画面本体**连续变形 ─---
    // 这是和"玻璃片盖在视频上"根本不同的地方：VideoView 是 HwndHost 时只能盖假卡片，
    // vmem 下画面是普通 Image，可以直接缩放/位移/裁圆角。
    private double _vmemScaleX = 1, _vmemScaleY = 1, _vmemTx, _vmemTy;
    private bool _vmemMorphPrepared;
    private ScaleTransform? _vmemMorphScale;
    private TranslateTransform? _vmemMorphMove;

    // ── 动效参数（2026-11-09 第六轮定稿）────────────────────────────────────
    /// <summary>停靠/还原的基础时长。HyperOS 那种"贵"的手感落在 400ms 上下。</summary>
    internal const double WebMorphDurationMs = 420;
    /// <summary>反向（滚回去打断正在进行的过渡）用更短的时长，减少"追不上手"的迟滞。</summary>
    internal const double WebMorphReverseDurationMs = 240;
    /// <summary>临界阻尼固有频率：约 420ms 收敛、**零过冲**。旧曲线 ζ≈0.54 会冲到 1.089。</summary>
    internal const double WebMorphOmega = 15.5;
    private FrameworkElement[] _webHeaderParts = Array.Empty<FrameworkElement>();
    private readonly StackPanel _webHeaderStack = new();
    private ScrollViewer? _webHeaderScroll;
    private bool IsWebVideoLayout => _webLayoutMounted;

    /// <summary>过渡遮罩里那个品牌块的缩放/位移动画(纯 WPF, 每帧只改矩阵)</summary>
    private readonly ScaleTransform _webCoverScale = new(1, 1);
    private readonly TranslateTransform _webCoverMove = new();
    private readonly TranslateTransform _webBrandMove = new();
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
            group.Children.Add(_webBrandLayoutScale);
            coverElement.RenderTransform = group;
            coverElement.RenderTransformOrigin = new Point(0, 0);
            _webCoverScale.ScaleX = _webCoverScale.ScaleY = 1;
            _webCoverMove.X = _webCoverMove.Y = 0;
            _webBrandLayoutScale.ScaleX = _webBrandLayoutScale.ScaleY = 1;
            if (BrandMark is FrameworkElement brand) brand.RenderTransformOrigin = new Point(0.5, 0.5);
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
        // ★★ 必须在**动任何子元素之前**抓尺寸(2026-11-08): 下面要摘挂一堆元素、改可见性,
        //   一旦开始, VideoSurface 的 ActualWidth/ActualHeight 会在重排过程中变成 0,
        //   那时再"钉"就晚了 —— 实测钉到的是 0, 于是 Left/Top + NaN 依旧量成 0×0。
        PinVideoSurfaceSize();
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
        UpdateCommentPicturePlacement();
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
        var width = WebLeftScroll.ViewportWidth > 0 ? WebLeftScroll.ViewportWidth : WebLeftPane.ActualWidth;
        if (width <= 0) return;
        var height = InlineWebVideoHeight(width);
        if (double.IsNaN(WebVideoPlaceholder.Height) || Math.Abs(WebVideoPlaceholder.Height - height) > 0.5) WebVideoPlaceholder.Height = height;
        var scale = UiScale.Percent / 100.0;
        WebCommentsScaleRoot.MinHeight = Math.Max(120, WebLeftPane.ActualHeight / scale);
        var dockHeight = Math.Max(0, RightLayoutGrid.ActualWidth - 40) * 9 / 16;
        WebHeaderHost.MaxHeight = Math.Max(80, Math.Min(RightLayoutGrid.ActualHeight * 0.55, RightLayoutGrid.ActualHeight - dockHeight - 44));
        UpdateDockForOffset(height);
        if (!_webRendering) ApplyWebVideoBounds();
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
    private DispatcherTimer? _vmemTrackTimer;

    /// <summary>
    /// 跟手：把滚动偏移映射成**目标**变形进度，再由一个 16ms 定时器逐帧向目标插值。
    ///
    /// ★ 为什么不直接把进度设成滚动映射值：滚动事件是离散的（滚轮一格就是一个大跳），
    ///   直接赋值等于硬切。插值之后，连续触控板是"跟手"，滚轮是"快速但平滑地追过去"。
    /// ★ 为什么现在能做而第四轮不行：那时画面是 HwndHost，动原生窗口必露伪影；而且
    ///   直接改 `_webDockProgress` 会让 `StartLegacyWebDockTarget` 短路、`_webRendering`
    ///   不置位，把两套布局回归搞挂。vmem 下只改变换矩阵，零原生调用，也不碰 `_webRendering`。
    ///
    /// ★ 映射区间：从 `height*0.18` 起、跨 `height*0.55`，滑完这一段就是完全停靠。
    ///   停止滚动 140ms 后按当前进度就近收敛（>=0.5 停靠，否则回内联）。
    /// </summary>
    private bool TryTrackVmemMorph(double height)
    {
        if (!_vmemMode || _closing || height <= 0) return false;
        var offset = WebLeftScroll.VerticalOffset;
        var start = height * 0.18;
        var band = Math.Max(80, height * 0.55);
        var p = Math.Clamp((offset - start) / band, 0, 1);
        if (!_vmemMorphPrepared) PrepareVmemMorph();
        if (!_vmemMorphPrepared) return false;

        _vmemTracking = true;
        _vmemTrackTarget = p;
        _vmemLastScrollMs = Environment.TickCount64;
        StartVmemTrackTimer();
        return true;
    }

    private void StartVmemTrackTimer()
    {
        _vmemTrackTimer ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _vmemTrackTimer.Tick -= OnVmemTrackTick;
        _vmemTrackTimer.Tick += OnVmemTrackTick;
        if (!_vmemTrackTimer.IsEnabled) _vmemTrackTimer.Start();
    }

    private void OnVmemTrackTick(object? sender, EventArgs e)
    {
        if (_closing || !_vmemTracking) { _vmemTrackTimer?.Stop(); return; }
        // 逐帧逼近目标：一帧走 35%，约 6 帧（100ms）贴上去。
        var delta = _vmemTrackTarget - _webDockProgress;
        if (Math.Abs(delta) > 0.0015)
        {
            _webDockProgress += delta * 0.35;
            ApplyVmemMorph(_webDockProgress);
            UpdateAlternateVideoControls();
            return;
        }
        _webDockProgress = _vmemTrackTarget;
        ApplyVmemMorph(_webDockProgress);
        // 已经贴住目标，且 140ms 没有新滚动 → 收敛
        if (Environment.TickCount64 - _vmemLastScrollMs < 140) return;
        _vmemTrackTimer?.Stop();
        _vmemTracking = false;
        StartLegacyWebDockTarget(_webDockProgress >= 0.5 ? 1.0 : 0.0);
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

    /// <summary>
    /// 停靠/还原的进度曲线：临界阻尼弹簧，**零过冲**、单调收敛。
    /// 旧曲线 1-e^(-7t)(cos11t+.42sin11t) 的阻尼比只有 0.54，会冲到 1.089 ——
    /// 用户说的"生硬/廉价"主要来自这个可见过冲。
    /// 抽成静态方法是为了让 --web-morph-motion 能直接断言"不过冲、单调"。
    /// </summary>
    internal static double WebMorphEase(double elapsedMs, double durationMs)
    {
        if (durationMs <= 0) return 1;
        if (elapsedMs <= 0) return 0;
        var x = WebMorphOmega * (elapsedMs / 1000.0);
        var eased = 1 - Math.Exp(-x) * (1 + x);
        return elapsedMs >= durationMs ? 1 : Math.Clamp(eased, 0, 1);
    }

    private void StartLegacyWebDockTarget(double target)
    {
        if (Math.Abs(target - _webAnimationTo) < 0.0001 && _webRendering) return;
        if (Math.Abs(target - _webDockProgress) < 0.0001)
        {
            _webAnimationTo = target;
            ApplyWebVideoBounds();
            // ★ 跟手停在端点上时（进度已经是 0 或 1），这里直接落位，
            //   必须把 VmemView 的变换复位，否则会二次缩放。
            if (_vmemMode) ResetVmemMorph(docked: target >= .5);
            return;
        }
        var reversing = (target < _webDockProgress - .0001 && _webAnimationTo > _webDockProgress + .0001)
                        || (target > _webDockProgress + .0001 && _webAnimationTo < _webDockProgress - .0001);
        _webAnimationFrom = _webDockProgress; _webAnimationTo = target; _webAnimationElapsed = 0;
        _webAnimationDurationMs = reversing ? WebMorphReverseDurationMs : WebMorphDurationMs;
        // ★ 用绝对墙钟算时长：旧的"每帧累加且 clamp 50ms"在滚动事件链占住 Dispatcher 时
        //   会把 380ms 动画拖到 1.5 秒(真机捕获实测)。墙钟保证总时长恒定。
        _webAnimationWallStart = Stopwatch.GetTimestamp();
        _webSurfaceSettledAt = 0;
        // ★ 一次性算好「遮罩该缩到哪」：朝真实的 WebDockAnchor 几何收缩，
        //   而不是硬编码往视频区右上角缩 —— 后者与落点方向不一致，观感就是"缩完瞬移"。
        UpdateWebCoverTargetGeometry();
        if (_vmemMode)
        {
            // ★ vmem：不用遮罩，直接算"画面要缩到哪"，逐帧变换 VmemView。
            PrepareVmemMorph();
            DockTransitionCover.Visibility = Visibility.Collapsed;
        }
        else
        {
            DockTransitionCover.Visibility = Visibility.Visible;
        }
        // 品牌块正好压在视频中心，过渡期间收起，别挡画面。
        if (BrandMark != null) BrandMark.Visibility = Visibility.Collapsed;
        if (!_webRendering) { _webRendering = true; CompositionTarget.Rendering += OnWebVideoFrame; }
        UpdateAlternateVideoControls();
    }

    /// <summary>
    /// 预计算 vmem 变形要用的缩放/位移：从"视频区当前几何"到"真实停靠几何"。
    /// 全部相对 `PlayerStage` 计算，落地时 `ApplyWebVideoBounds` 把 VideoSurface
    /// 直接放到同一个矩形，所以 commit 那一帧**位置完全重合、零跳变**。
    /// </summary>
    private void PrepareVmemMorph()
    {
        _vmemMorphPrepared = false;
        try
        {
            var from = VideoSurface.TransformToAncestor(PlayerStage)
                .TransformBounds(new Rect(VideoSurface.RenderSize));
            var to = CalculateWebVideoBounds(1);
            if (from.Width < 2 || from.Height < 2 || to.IsEmpty || to.Width < 2) return;
            _vmemScaleX = to.Width / from.Width;
            _vmemScaleY = to.Height / from.Height;
            _vmemTx = to.Left - from.Left;
            _vmemTy = to.Top - from.Top;
            if (VmemView.RenderTransform is not TransformGroup)
            {
                _vmemMorphScale = new ScaleTransform(1, 1);
                _vmemMorphMove = new TranslateTransform(0, 0);
                var g = new TransformGroup();
                g.Children.Add(_vmemMorphScale);
                g.Children.Add(_vmemMorphMove);
                VmemView.RenderTransform = g;
                VmemView.RenderTransformOrigin = new Point(0, 0);
            }
            _vmemMorphPrepared = true;
        }
        catch { }
    }

    /// <summary>逐帧把 p 应用到 VmemView：缩放 + 位移 + 圆角裁剪。</summary>
    private void ApplyVmemMorph(double p)
    {
        if (!_vmemMorphPrepared || _vmemMorphScale == null || _vmemMorphMove == null) return;
        // ★ 画面缩小时，VideoSurface 的黑色底会露出一圈黑框（像"黑盒子在缩"）。
        //   vmem 下把它设成透明，页面/评论从四周透出来 —— 这才是"画面从页面里浮起来"的观感。
        //   p 接近 0（还在内联位）时保持黑底，避免边缘透出一条缝。
        var wantTransparent = p > 0.02;
        if (wantTransparent && !ReferenceEquals(VideoSurface.Background, Brushes.Transparent))
            VideoSurface.Background = Brushes.Transparent;
        else if (!wantTransparent && !ReferenceEquals(VideoSurface.Background, Brushes.Black))
            VideoSurface.Background = Brushes.Black;
        var sx = 1 + (_vmemScaleX - 1) * p;
        var sy = 1 + (_vmemScaleY - 1) * p;
        _vmemMorphScale.ScaleX = sx;
        _vmemMorphScale.ScaleY = sy;
        _vmemMorphMove.X = _vmemTx * p;
        _vmemMorphMove.Y = _vmemTy * p;
        ApplyVmemRadius(14 * p);
    }

    /// <summary>落位后复位变换；圆角按停靠状态保留（小窗是圆角卡片）。</summary>
    private void ResetVmemMorph(bool docked)
    {
        if (_vmemMorphScale != null) { _vmemMorphScale.ScaleX = _vmemMorphScale.ScaleY = 1; }
        if (_vmemMorphMove != null) { _vmemMorphMove.X = _vmemMorphMove.Y = 0; }
        ApplyVmemRadius(docked ? 14 : 0);
        // 停靠态保持透明（圆角外要透出右栏背景）；回到内联态恢复黑底。
        VideoSurface.Background = docked ? Brushes.Transparent : Brushes.Black;
    }

    private void ApplyVmemRadius(double radius)
    {
        if (radius <= 0.5) { VmemView.Clip = null; return; }
        var w = Math.Max(1, VmemView.ActualWidth);
        var h = Math.Max(1, VmemView.ActualHeight);
        VmemView.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
    }

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

    /// <summary>
    /// 过渡的每一帧: **只动纯 WPF 的遮罩**, 一次原生调用都不做。
    /// (CompositionTarget.Rendering 里做原生调用会直接把帧拖慢, 而且会造成 vout 重建。)
    /// ★ 2026-11-09 第四轮真机实测：让原生 VideoSurface 跟着平移/分档缩放都会露伪影
    ///   （WPF 黑底与 vout HWND 分离、纯白帧），已全部撤销，见 FPS-REGRESSION 报告。
    /// </summary>
    private void OnWebVideoFrame(object? sender, EventArgs e)
    {
        if (!_webLayoutMounted || _closing) { StopWebVideoAnimation(); return; }
        var now = Stopwatch.GetTimestamp();
        _webAnimationElapsed = Stopwatch.GetElapsedTime(_webAnimationWallStart, now).TotalMilliseconds;
        // ★ 临界阻尼弹簧（零过冲）：旧曲线 ζ≈0.54 会冲到 1.089，观感是"弹一下"，
        //   廉价感主要来自这个过冲。现在用 1 - e^(-ωt)(1+ωt)，单调收敛、末端极轻。
        //   只改纯 WPF 变换矩阵，不重采样位图、不隐藏原生窗口、不等 DWM。
        var t = Math.Clamp(_webAnimationElapsed / _webAnimationDurationMs, 0, 1);
        var eased = WebMorphEase(_webAnimationElapsed, _webAnimationDurationMs);
        _webDockProgress = _webAnimationFrom + (_webAnimationTo - _webAnimationFrom) * eased;
        // ★ vmem 模式走「画面本体变形」；其余走「玻璃卡片」兜底。
        if (_vmemMode && _vmemMorphPrepared) ApplyVmemMorph(_webDockProgress);
        else UpdateWebCoverAnimation(_webDockProgress);
        // ★ 曾试过让原生 VideoSurface 跟着平移/分档缩放，真机截图证实 HwndHost 的
        //   WPF 黑底与 vout HWND 会分离、露黑边白边，分档 resize 还会刷出纯白帧。
        //   ⇒ 过渡期间原生窗口一律不动，只有纯 WPF 遮罩连续变形；结束帧才落位一次。
        // ★ 不再逐帧调 UpdateAlternateVideoControls()：moving 在整段动画里恒定，
        //   StartLegacyWebDockTarget / StopWebVideoAnimation 各刷一次就够。

        if (t >= 1)
        {
            _webDockProgress = _webAnimationTo;
            // 动画走完的这一帧才把原生窗口**一次性**移到目标几何。此刻它被遮罩完全盖住,
            // 用户看不到这次 SetWindowPos。
            ApplyWebVideoBounds();
            // ★ vmem：落位后立刻复位 VmemView 的变换/圆角。
            //   此刻 VideoSurface 已经就是目标矩形，画面尺寸位置完全重合 ⇒ 零跳变。
            if (_vmemMode) ResetVmemMorph(docked: _webAnimationTo >= .5);
            // ★ 删除截图替身后，commit 的这一次原生 resize 会让 vout 宿主窗口重擦一遍
            //   默认浅色底（屏幕采样能抓到 1 帧 ~236,237,228 的白闪）。立刻刷黑宿主类背景刷。
            ApplyHwndHostBlackBackground();
            UpdateWebCoverAnimation(_webDockProgress);
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
        _vmemTrackTimer?.Stop();
        if(!_closing){SetVideoHostVisible(true);VideoSurface.Background=Brushes.Black;}
        // ★ vmem：收尾把画面变换复位，圆角按当前是否停靠保留。
        if (_vmemMode) ResetVmemMorph(docked: _webDockProgress >= .5);
        if (_webRendering) { CompositionTarget.Rendering -= OnWebVideoFrame; _webRendering = false; }
        if (DockTransitionCover != null) DockTransitionCover.Visibility = Visibility.Collapsed;
        // 收尾必须让原生窗口落在最终几何上: 过渡期间它一直没动, 只有这里会动。
        if (_webLayoutMounted && !_closing) { ApplyWebVideoBounds(); ApplyHwndHostBlackBackground(); }
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
