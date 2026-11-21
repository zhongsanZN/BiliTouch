using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Helpers;

namespace BiliTouch.Views;

/// <summary>
/// 金标联盟「一镜到底」(Seamless Shared-Element Morph) 与澎湃 OS 4 (HyperOS 4) 详情页分层动效编排。
/// <para>
/// 核心特性：
/// <list type="bullet">
///   <item><description>从列表卡片进入详情页时，禁用系统窗口中心缩放动画，基于宿主窗口快照、封面纹理、圆角与容器几何按临界阻尼弹簧（ζ=1.0，零过冲）连续展开到播放器视频区；</description></item>
///   <item><description>点击关闭时执行逆向「一镜到底」，将播放器容器平滑收拢回源视频卡片原位后再无缝隐藏窗口；</description></item>
///   <item><description>全程通过复用的 <see cref="ScaleTransform"/>、<see cref="TranslateTransform"/> 与单一 <see cref="RectangleGeometry"/> 驱动，运动期间零 Measure/Arrange 重排、零 GC 几何分配；</description></item>
///   <item><description>支持任意时刻打断或反向（Retargeting），保留当前位置与速度向量平滑转向。</description></item>
/// </list>
/// </para>
/// </summary>
public partial class PlayerWindow
{
    private sealed class SeamlessMorphState
    {
        public bool IsRunning;
        public bool IsReverse;
        public bool IsClosingMorph;
        public bool FrameAttached;
        public bool PreShowPrepared;
        public bool HasPresented;
        public bool PresentationHooked;
        public double Progress;
        public double Velocity;
        public double Target = 1.0;
        public double Omega = SpringMotion.HyperOmegaContainer;
        public Rect StartRect;
        public Rect EndRect;
        public Rect CurrentRect;
        public double StartRadius = 8.0;
        public double EndRadius;
        public Rect ShellStartRect;
        public Rect ShellEndRect;
        public Rect CurrentShellRect;
        public long LastFrameTicks;
        public TimeSpan LastRenderingTime = TimeSpan.MinValue;
        public readonly ScaleTransform HeroScale = new(1, 1);
        public readonly TranslateTransform HeroTranslate = new(0, 0);
        public readonly ScaleTransform HeroArtScale = new(1, 1);
        public readonly RectangleGeometry HeroClip = new();
        public readonly ScaleTransform ShellScale = new(1, 1);
        public readonly TranslateTransform ShellTranslate = new(0, 0);
        public readonly SolidColorBrush ShellBrush = new(Color.FromArgb(0, 20, 22, 26));
        public readonly SolidColorBrush ShellBorderBrush = new(Color.FromArgb(0, 255, 255, 255));
        public readonly SolidColorBrush BackdropBrush = new(Color.FromArgb(0, 6, 8, 12));
        public readonly TranslateTransform TopBarShift = new(0, 0);
        public readonly TranslateTransform InfoPanelShift = new(0, 0);
        public bool TransformsMounted;
        public Window? EntryWindow;
        public MainWindow? SourceMainWindow;
        public bool EntryPresented;
        public bool HandedOffToPlayer;
        public Action? PendingPresentation;
        public readonly List<Action> PostEntryUiActions = new();
    }

    private readonly SeamlessMorphState _seamless = new();

    internal bool IsSeamlessMorphRunning => _seamless.IsRunning;
    internal bool IsSeamlessEntryActive => _seamless.EntryWindow != null && _seamless.IsRunning && !_seamless.IsReverse;
    internal double SeamlessMorphProgress => _seamless.Progress;
    internal Rect SeamlessCurrentRect => _seamless.CurrentRect;

    internal void RunOrDeferAfterSeamlessEntry(Action action)
    {
        if (action == null || _closing) return;
        if (!IsSeamlessEntryActive)
        {
            action();
            return;
        }
        _seamless.PostEntryUiActions.Add(action);
    }

    private void FlushPostEntryUiActions(bool execute)
    {
        if (_seamless.PostEntryUiActions.Count == 0) return;
        var batch = _seamless.PostEntryUiActions.ToArray();
        _seamless.PostEntryUiActions.Clear();
        if (!execute || _closing) return;
        foreach (var action in batch)
        {
            if (_closing) break;
            action();
        }
    }

    private void EnsureSeamlessTransformsMounted()
    {
        if (_seamless.TransformsMounted) return;
        _seamless.TransformsMounted = true;

        var group = new TransformGroup();
        group.Children.Add(_seamless.HeroScale);
        group.Children.Add(_seamless.HeroTranslate);

        var heroHost = SeamlessHeroHost ?? (FrameworkElement)SeamlessHeroCard;
        heroHost.RenderTransform = group;
        heroHost.RenderTransformOrigin = new Point(0, 0);
        heroHost.Clip = _seamless.HeroClip;
        heroHost.UseLayoutRounding = false;
        heroHost.SnapsToDevicePixels = false;

        if (SeamlessHeroHost != null)
        {
            // 外层 SeamlessHeroHost 负责变换与圆角裁剪（无 CacheMode），
            // 内层 SeamlessHeroCard 持有小尺寸静态 BitmapCache（永不击穿）+ 等比补偿，确保 60~120fps 零拉伸。
            SeamlessHeroCard.RenderTransform = _seamless.HeroArtScale;
            SeamlessHeroCard.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        if (SeamlessPlayerShell != null)
        {
            var shellGroup = new TransformGroup();
            shellGroup.Children.Add(_seamless.ShellScale);
            shellGroup.Children.Add(_seamless.ShellTranslate);
            SeamlessPlayerShell.RenderTransform = shellGroup;
            SeamlessPlayerShell.RenderTransformOrigin = new Point(0, 0);
            SeamlessPlayerShell.Clip = null;
            SeamlessPlayerShell.CornerRadius = new CornerRadius(10);
            SeamlessPlayerShell.Background = _seamless.ShellBrush;
            SeamlessPlayerShell.BorderBrush = _seamless.ShellBorderBrush;
            SeamlessPlayerShell.Opacity = 1.0;
            SeamlessPlayerShell.UseLayoutRounding = false;
            SeamlessPlayerShell.SnapsToDevicePixels = false;
            SeamlessPlayerShell.CacheMode = null;
        }
        if (SeamlessBackdrop != null)
        {
            SeamlessBackdrop.Background = _seamless.BackdropBrush;
            SeamlessBackdrop.Opacity = 1.0;
        }
        if (SeamlessWindowSnapshot != null)
        {
            SeamlessWindowSnapshot.CacheMode = null;
            SeamlessWindowSnapshot.UseLayoutRounding = false;
            SeamlessWindowSnapshot.SnapsToDevicePixels = false;
        }
        // Disable integer pixel rounding and pre-cache hero card at compact GPU texture scale
        // so every composition frame transforms smoothly without re-rasterizing artwork.
        SeamlessMorphOverlay.UseLayoutRounding = false;
        SeamlessMorphOverlay.SnapsToDevicePixels = false;
        SeamlessHeroCard.UseLayoutRounding = false;
        SeamlessHeroCard.SnapsToDevicePixels = false;
        SeamlessHeroCard.CacheMode ??= new BitmapCache(0.45) { EnableClearType = false, SnapsToDevicePixels = false };

        if (TopBar != null && (TopBar.RenderTransform == null || ReferenceEquals(TopBar.RenderTransform, Transform.Identity)))
            TopBar.RenderTransform = _seamless.TopBarShift;
        if (InfoPanel.RenderTransform == null || ReferenceEquals(InfoPanel.RenderTransform, Transform.Identity))
            InfoPanel.RenderTransform = _seamless.InfoPanelShift;
    }

    /// <summary>
    /// 在播放器窗口 <see cref="Window.Show"/> 之前调用：
    /// 1) 禁用系统 DWM 窗口弹出缩放动画（消除"从屏幕中间展开新窗口"）；
    /// 2) 预挂载源窗口背景快照并隐藏原生 HwndHost，使窗口上屏第一帧与源页面完全连续。
    /// </summary>
    private void EnsureSeamlessPresentationHook()
    {
        if (_seamless.PresentationHooked) return;
        _seamless.PresentationHooked = true;
        ContentRendered += (_, _) =>
        {
            _seamless.HasPresented = true;
            if (_seamless.IsRunning && !_closing) AttachSeamlessFrameClock();
        };
    }

    internal void PrepareSeamlessPreShow()
    {
        if (_closing) return;
        ShowActivated = true;
        EnsureSeamlessPresentationHook();
        try
        {
            new WindowInteropHelper(this).EnsureHandle();
            DwmInterop.SetTransitionsDisabled(this, true);
        }
        catch { }

        if (!SeamlessTransitionDirector.HasPendingAnchor) return;
        var anchor = SeamlessTransitionDirector.CurrentAnchor;
        if (anchor == null) return;

        _seamless.PreShowPrepared = true;
        if (SeamlessWindowSnapshot != null)
        {
            SeamlessWindowSnapshot.Background = null;
            SeamlessWindowSnapshot.Opacity = 0;
            SeamlessWindowSnapshot.Visibility = Visibility.Collapsed;
        }
        if (SeamlessBackdrop != null)
        {
            SeamlessBackdrop.Opacity = 0;
            SeamlessBackdrop.Visibility = Visibility.Collapsed;
        }
        if (SeamlessMorphOverlay != null) SeamlessMorphOverlay.Visibility = Visibility.Visible;
        if (!_vmemMode && VideoView != null) { DetachVideoOverlay(); VideoView.Visibility = Visibility.Hidden; }
        if (LoadingPoster != null) LoadingPoster.Opacity = 1;
        if (TopBar != null) TopBar.Opacity = 1;
        if (InfoPanel != null) InfoPanel.Opacity = 1;
        if (WebLeftPane != null) WebLeftPane.Opacity = 1;
        ApplySharedCoverArtwork(anchor.CoverImage, anchor.FallbackBrush);
        MountSeamlessEntryWindow(anchor);
    }

    /// <summary>
    /// 尝试消费挂起的共享元素锚点，启动从源视频卡片到播放器视频舞台的「一镜到底」展开过渡。
    /// </summary>
    internal bool TryStartSeamlessEntry(string bvid)
    {
        if (_closing || string.IsNullOrEmpty(bvid) || RootGrid == null) return false;
        if (!SeamlessTransitionDirector.TryConsumeForPlayer(bvid, this, SeamlessStage, out var anchor, out var startRect))
        {
            if (_seamless.PreShowPrepared || _seamless.IsRunning || _seamless.EntryWindow != null)
            {
                StopSeamlessMorph();
            }
            return false;
        }

        _seamless.PreShowPrepared = false;
        _seamless.IsClosingMorph = false;
        if (_seamless.EntryWindow is { } stage)
        {
            stage.Topmost = false;
            stage.Topmost = true;
        }
        ApplySharedCoverArtwork(anchor.CoverImage, anchor.FallbackBrush);
        // 尽早注入视频宽高（来自列表项 dimension 或竖版封面），让「竖屏优化」在首帧起飞时即指向竖屏视频列
        if (anchor.VideoWidth > 0 && anchor.VideoHeight > 0)
        {
            UpdateDetectedVideoSize(anchor.VideoWidth, anchor.VideoHeight, fromMetadata: false);
        }
        else if (anchor.CoverImage is System.Windows.Media.Imaging.BitmapSource bs &&
                 bs.PixelWidth > 0 && bs.PixelHeight > bs.PixelWidth * 1.05)
        {
            UpdateDetectedVideoSize(bs.PixelWidth, bs.PixelHeight, fromMetadata: false);
        }
        ApplyAlternateLayoutPreference(allowPreLoad: true);
        if (_webLayoutMounted)
        {
            _webDockProgress = _webAnimationTo = 0;
            _webSavedScroll = 0;
        }
        return StartSeamlessMorph(startRect, ResolveVideoStageTargetRect(), anchor.CornerRadius, ResolveVideoStageTargetRadius(), reverse: false);
    }

    private bool DoesEntryCoverPlayerTarget()
    {
        if (_seamless.EntryWindow is not { } stage) return false;
        try
        {
            var wa = SystemParameters.WorkArea;
            if (stage.Owner is { } owner && PresentationSource.FromVisual(owner) != null)
            {
                var dpi = VisualTreeHelper.GetDpi(stage);
                var handle = new WindowInteropHelper(owner).Handle;
                if (handle != IntPtr.Zero)
                {
                    var bounds = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                    wa = new Rect(bounds.Left / dpi.DpiScaleX, bounds.Top / dpi.DpiScaleY,
                        bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
                }
            }
            var target = WindowState == WindowState.Maximized
                ? wa
                : new Rect(
                    double.IsNaN(Left) ? wa.Left : Left,
                    double.IsNaN(Top) ? wa.Top : Top,
                    ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : wa.Width),
                    ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : wa.Height));
            var stageRect = new Rect(stage.Left, stage.Top,
                stage.ActualWidth > 0 ? stage.ActualWidth : stage.Width,
                stage.ActualHeight > 0 ? stage.ActualHeight : stage.Height);
            const double tol = 48.0;
            return stageRect.Left <= target.Left + tol
                && stageRect.Top <= target.Top + tol
                && stageRect.Right >= target.Right - tol
                && stageRect.Bottom >= target.Bottom - tol;
        }
        catch { return false; }
    }

    private void PreRevealPlayerBehindEntry()
    {
        if (IsVisible || _closing || _seamless.EntryWindow is not { } stage) return;
        // 保持 TopBar / InfoPanel / WebLeftPane 全程 Opacity=1（零整棵子树离屏重绘），
        // 隐藏原生 HwndHost 仅留 LoadingPoster 与飞跃封面完全对齐，在 Topmost 入场舞台背后静默就位。
        if (TopBar != null) TopBar.Opacity = 1;
        if (InfoPanel != null) InfoPanel.Opacity = 1;
        if (WebLeftPane != null) WebLeftPane.Opacity = 1;
        if (LoadingPoster != null) LoadingPoster.Opacity = 1;
        if (!_vmemMode && VideoView != null) { DetachVideoOverlay(); VideoView.Visibility = Visibility.Hidden; }
        // WPF 规定 WindowState=Maximized 时必须保持 ShowActivated=true；
        // 由于 EntryWindow 为 WS_EX_TOPMOST，Show() 后播放器仍严格位于 EntryWindow 背后，零闪烁。
        ShowActivated = true;
        Show();
        UpdateLayout();
        ApplyAlternateLayoutPreference(allowPreLoad: true);
        if (_webLayoutMounted) UpdateWebMeasurements();
        else if (_portraitLayoutMounted) UpdatePortraitMeasurements();
        else if (_tabletPortraitLayoutMounted) UpdateTabletPortraitMeasurements();
        UpdateLayout();
        stage.Topmost = false;
        stage.Topmost = true;
    }

    internal void RetargetSeamlessTargetIfNeeded()
    {
        if (!_seamless.IsRunning || _seamless.IsReverse || _seamless.IsClosingMorph || _closing) return;
        // UpdatePortraitMeasurements 等只写了 Width/Height，需先提交一次布局以刷新 VideoSurface.RenderSize
        if (IsVisible)
        {
            try { RootGrid?.UpdateLayout(); } catch { }
        }
        var nextEnd = ResolveVideoStageTargetRect();
        if (nextEnd.Width < 16 || nextEnd.Height < 16) return;
        var prev = _seamless.EndRect;
        if (Math.Abs(nextEnd.X - prev.X) < 4 && Math.Abs(nextEnd.Y - prev.Y) < 4 &&
            Math.Abs(nextEnd.Width - prev.Width) < 4 && Math.Abs(nextEnd.Height - prev.Height) < 4)
            return;

        // 播放详情元数据在中途到达（例如切换为「竖屏优化」布局）：从当前瞬时矩形平滑改道，零跳变
        var current = _seamless.CurrentRect.Width >= 2 && _seamless.CurrentRect.Height >= 2
            ? _seamless.CurrentRect
            : _seamless.StartRect;
        if (_seamless.CurrentShellRect.Width >= 2 && _seamless.CurrentShellRect.Height >= 2)
            _seamless.ShellStartRect = _seamless.CurrentShellRect;
        _seamless.StartRect = current;
        _seamless.StartRadius = SpringMotion.InterpolateRadius(_seamless.StartRadius, _seamless.EndRadius, _seamless.Progress);
        _seamless.EndRect = nextEnd;
        _seamless.EndRadius = ResolveVideoStageTargetRadius();
        _seamless.Progress = 0;
        _seamless.Velocity = Math.Clamp(_seamless.Velocity * 0.5, 0, 3.0);
        _seamless.Omega = SpringMotion.HyperOmegaInterrupt;
        ConfigureSeamlessHeroBaseSize();
        ConfigureSeamlessShellBaseSize();
        ApplySeamlessMorphFrame(0);
    }

    /// <summary>
    /// 点击右上角关闭按钮时启动逆向「一镜到底」：将视频舞台连续收拢回源视频卡片位置，再无缝隐藏窗口。
    /// </summary>
    internal bool TryBeginSeamlessClose()
    {
        if (_closing || _seamless.IsClosingMorph || WindowState == WindowState.Minimized || _isFullscreen)
            return false;
        if (_seamless.EntryWindow == null && !IsVisible)
            return false;
        if (RootGrid == null || SeamlessMorphOverlay == null || SeamlessHeroCard == null)
            return false;

        var anchor = SeamlessTransitionDirector.CurrentAnchor;
        Window? srcWin = null;
        if (anchor?.SourceWindow != null && anchor.SourceWindow.TryGetTarget(out var w) && w.IsLoaded && w.IsVisible && w.WindowState != WindowState.Minimized)
            srcWin = w;
        else if (Application.Current?.MainWindow is { IsLoaded: true, IsVisible: true } mw && !ReferenceEquals(mw, this) && mw.WindowState != WindowState.Minimized)
            srcWin = mw;

        if (srcWin == null && anchor == null) return false;

        try { DwmInterop.SetTransitionsDisabled(this, true); } catch { }
        try { _mp?.SetPause(true); } catch { }
        StopDanmakuClock();
        DetachVideoOverlay();

        Brush? heroBrush = null;
        if (_vmemMode && VmemView?.CaptureFrame() is System.Windows.Media.Imaging.BitmapSource liveFrame)
        {
            var frameBrush = new ImageBrush(liveFrame)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            if (liveFrame.IsFrozen && frameBrush.CanFreeze) frameBrush.Freeze();
            heroBrush = frameBrush;
            SeamlessHeroCard.Background = frameBrush;
        }
        else if (anchor?.CoverImage != null)
        {
            ApplySharedCoverArtwork(anchor.CoverImage, anchor.FallbackBrush);
            heroBrush = SeamlessHeroCard.Background;
        }
        else
        {
            heroBrush = LoadingPoster?.Background ?? SeamlessHeroCard.Background ?? anchor?.FallbackBrush;
        }

        // 1. 若还在独立入场浮窗阶段被打断关闭，直接在 EntryWindow 原地反向收拢
        if (_seamless.EntryWindow != null)
        {
            Rect entryCardRect;
            double entryCardRadius = 8.0;
            if (anchor != null && (string.IsNullOrEmpty(_currentBvid) || string.Equals(anchor.Bvid, _currentBvid, StringComparison.OrdinalIgnoreCase)))
            {
                entryCardRect = SeamlessTransitionDirector.ResolveStageStartRect(anchor, this, SeamlessStage);
                entryCardRadius = Math.Max(6.0, anchor.CornerRadius);
            }
            else
            {
                var sw = SeamlessStage.ActualWidth > 0 ? SeamlessStage.ActualWidth : 1280;
                var sh = SeamlessStage.ActualHeight > 0 ? SeamlessStage.ActualHeight : 760;
                var cw = Math.Clamp(sw * 0.22, 220, 340);
                entryCardRect = new Rect((sw - cw) * 0.5, (sh - cw * 9.0 / 16.0) * 0.5, cw, cw * 9.0 / 16.0);
            }

            _seamless.IsClosingMorph = true;
            if (!StartSeamlessMorph(entryCardRect, ResolveVideoStageTargetRect(), entryCardRadius, ResolveVideoStageTargetRadius(), reverse: true))
            {
                _seamless.IsClosingMorph = false;
                return false;
            }
            _seamless.Omega = SpringMotion.HyperOmegaInterrupt;
            return true;
        }

        // 2. 正常关闭回宿主窗口（MainWindow）：立即隐藏播放器露出实时宿主窗口，
        //    在宿主窗口 GPU 合成层上执行零截图、零重排、零闪烁的逆向落位动画。
        if (srcWin != null)
        {
            Rect startScreenRect;
            if (VideoSurface != null && VideoSurface.IsVisible && VideoSurface.ActualWidth > 4 && VideoSurface.ActualHeight > 4 &&
                PresentationSource.FromVisual(VideoSurface) is { CompositionTarget: not null })
            {
                var tl = VideoSurface.PointToScreen(new Point(0, 0));
                var br = VideoSurface.PointToScreen(new Point(VideoSurface.ActualWidth, VideoSurface.ActualHeight));
                startScreenRect = new Rect(tl, br);
            }
            else
            {
                var target = ResolveVideoStageTargetRect();
                var tl = PointToScreen(target.TopLeft);
                var br = PointToScreen(target.BottomRight);
                startScreenRect = new Rect(tl, br);
            }

            Rect windowScreenRect;
            if (IsVisible && PresentationSource.FromVisual(this) is { CompositionTarget: not null })
            {
                var wTl = PointToScreen(new Point(0, 0));
                var wBr = PointToScreen(new Point(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height));
                windowScreenRect = new Rect(wTl, wBr);
            }
            else
            {
                windowScreenRect = startScreenRect;
            }

            if (SeamlessTransitionDirector.PlayReturnMorph(srcWin, startScreenRect, ResolveVideoStageTargetRadius(), heroBrush, anchor, _currentBvid, windowScreenRect))
            {
                Close();
                return true;
            }
        }

        // 3. 无宿主窗口时的兜底（如独立单窗测试场景）：窗内反向收拢
        if (SeamlessWindowSnapshot != null)
        {
            SeamlessWindowSnapshot.Background = null;
            SeamlessWindowSnapshot.Opacity = 0;
            SeamlessWindowSnapshot.Visibility = Visibility.Collapsed;
        }

        Rect cardRect;
        double cardRadius = 8.0;
        if (anchor != null && (string.IsNullOrEmpty(_currentBvid) || string.Equals(anchor.Bvid, _currentBvid, StringComparison.OrdinalIgnoreCase)))
        {
            cardRect = SeamlessTransitionDirector.ResolveStageStartRect(anchor, this, SeamlessStage);
            cardRadius = Math.Max(6.0, anchor.CornerRadius);
        }
        else
        {
            var stageW = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : ActualWidth;
            var stageH = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : ActualHeight;
            var cw = Math.Clamp(stageW * 0.22, 220, 340);
            var ch = cw * 9.0 / 16.0;
            cardRect = new Rect((stageW - cw) * 0.5, (stageH - ch) * 0.5, cw, ch);
        }

        if (cardRect.Width < 16 || cardRect.Height < 16)
            return false;

        _seamless.IsClosingMorph = true;
        if (!_vmemMode && VideoView != null) { DetachVideoOverlay(); VideoView.Visibility = Visibility.Hidden; }
        var started = StartSeamlessMorph(
            cardRect,
            ResolveVideoStageTargetRect(),
            cardRadius,
            ResolveVideoStageTargetRadius(),
            reverse: true);
        if (!started)
        {
            _seamless.IsClosingMorph = false;
            return false;
        }
        _seamless.Omega = SpringMotion.HyperOmegaInterrupt;
        return true;
    }

    /// <summary>
    /// 使用指定起点与终点启动或重定向（Retarget）一镜到底连续过渡。
    /// 若当前已有过渡在飞，保留瞬时几何与速度向量无缝转向。
    /// </summary>
    internal bool StartSeamlessMorph(
        Rect startRect,
        Rect endRect,
        double startRadius = 8.0,
        double endRadius = 0.0,
        bool reverse = false)
    {
        if (_closing || SeamlessMorphOverlay == null || SeamlessHeroCard == null) return false;
        if (startRect.Width < 2 || startRect.Height < 2 || endRect.Width < 2 || endRect.Height < 2) return false;

        EnsureSeamlessTransformsMounted();

        var interrupting = _seamless.IsRunning;
        _seamless.IsReverse = reverse;
        if (interrupting && _seamless.CurrentRect.Width >= 2 && _seamless.CurrentRect.Height >= 2)
        {
            // 中途打断：从当前屏幕上的真实瞬时矩形出发，并将已有速度投影到新方向
            _seamless.StartRect = _seamless.CurrentRect;
            _seamless.ShellStartRect = _seamless.CurrentShellRect.Width >= 2 ? _seamless.CurrentShellRect : _seamless.CurrentRect;
            _seamless.StartRadius = SpringMotion.InterpolateRadius(_seamless.StartRadius, _seamless.EndRadius, _seamless.Progress);
            _seamless.EndRect = reverse ? startRect : endRect;
            _seamless.EndRadius = reverse ? startRadius : endRadius;
            _seamless.Velocity = Math.Clamp(Math.Abs(_seamless.Velocity) * 0.65, 0, 3.5);
            _seamless.Progress = 0;
            _seamless.Target = 1.0;
            _seamless.Omega = SpringMotion.HyperOmegaInterrupt;
        }
        else
        {
            _seamless.StartRect = reverse ? endRect : startRect;
            _seamless.ShellStartRect = Rect.Empty;
            _seamless.EndRect = reverse ? startRect : endRect;
            _seamless.StartRadius = reverse ? endRadius : startRadius;
            _seamless.EndRadius = reverse ? startRadius : endRadius;
            _seamless.Progress = 0;
            _seamless.Velocity = 0;
            _seamless.Target = 1.0;
            _seamless.Omega = reverse ? SpringMotion.HyperOmegaInterrupt : SpringMotion.HyperOmegaContainer;
        }

        ConfigureSeamlessHeroBaseSize();
        ConfigureSeamlessShellBaseSize();
        SeamlessMorphOverlay.Visibility = Visibility.Visible;

        if (!_vmemMode && VideoView != null)
        {
            DetachVideoOverlay();
            VideoView.Visibility = Visibility.Hidden;
        }

        _seamless.IsRunning = true;
        ApplySeamlessMorphFrame(0);
        if (_seamless.HasPresented || _seamless.EntryPresented || _isEmbeddedInMain || !_seamless.PresentationHooked) AttachSeamlessFrameClock();
        return true;
    }

    private void ConfigureSeamlessHeroBaseSize()
    {
        var baseW = Math.Max(1, Math.Max(_seamless.StartRect.Width, _seamless.EndRect.Width));
        var baseH = Math.Max(1, Math.Max(_seamless.StartRect.Height, _seamless.EndRect.Height));
        var heroHost = SeamlessHeroHost ?? (FrameworkElement)SeamlessHeroCard;
        heroHost.Width = baseW;
        heroHost.Height = baseH;
        // 按实际像素尺寸自适应压缩静态 BitmapCache 比例（封顶 ~640px，约 1~1.5MB 显存），
        // 高 DPI 屏显存带宽直降 10 倍，且外层裁剪变化不再击穿内层缓存。
        if (SeamlessHeroCard.CacheMode is BitmapCache cache)
        {
            var dpi = VisualTreeHelper.GetDpi(SeamlessStage);
            var dpiScale = Math.Max(1.0, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
            var targetScale = Math.Clamp(640.0 / (Math.Max(baseW, baseH) * dpiScale), 0.25, 0.75);
            if (Math.Abs(cache.RenderAtScale - targetScale) > 0.05)
                cache.RenderAtScale = targetScale;
        }
    }

    private void ConfigureSeamlessShellBaseSize()
    {
        if (SeamlessPlayerShell == null) return;
        _seamless.ShellEndRect = ResolvePlayerWindowTargetRect();
        SeamlessPlayerShell.Width = Math.Max(1, _seamless.ShellEndRect.Width);
        SeamlessPlayerShell.Height = Math.Max(1, _seamless.ShellEndRect.Height);
    }

    /// <summary>
    /// 推进一帧一镜到底物理弹簧（可供合成时钟与确定性步进测试共用）。
    /// </summary>
    internal void StepSeamlessMorph(double seconds)
    {
        if (!_seamless.IsRunning) return;
        (_seamless.Progress, _seamless.Velocity) = SpringMotion.Advance(
            _seamless.Progress,
            _seamless.Velocity,
            _seamless.Target,
            seconds,
            _seamless.Omega);

        ApplySeamlessMorphFrame(_seamless.Progress);

        // 容器壳体（SeamlessPlayerShell）在 p = 0 -> 0.88 期间平滑展开至详情页目标窗口 100% 边界；
        // 当 p >= 0.88 时壳体已完全覆盖目标窗口位置，此时在 Topmost EntryWindow 背后静默 Show() 播放器，
        // 既不会有播放器边缘提前露出的突兀感，又能在 p = 0.88 -> 1.0 期间让播放器在后台完成首帧渲染，实现落位零闪烁。
        if (_seamless.EntryWindow != null && !_seamless.IsReverse && !_seamless.IsClosingMorph && !IsVisible && !_closing
            && _seamless.Progress >= 0.88)
        {
            PreRevealPlayerBehindEntry();
            var exactEnd = ResolveVideoStageTargetRect();
            if (exactEnd.Width > 32 && exactEnd.Height > 32)
                _seamless.EndRect = exactEnd;
        }

        var settleTolerance = _seamless.IsClosingMorph ? 0.012 : 0.0045;
        var velTolerance = _seamless.IsClosingMorph ? 0.16 : 0.06;
        if (Math.Abs(_seamless.Progress - _seamless.Target) < settleTolerance && Math.Abs(_seamless.Velocity) < velTolerance)
        {
            _seamless.Progress = _seamless.Target;
            _seamless.Velocity = 0;
            ApplySeamlessMorphFrame(_seamless.Progress);
            var wasClosing = _seamless.IsClosingMorph;
            if (!wasClosing && !_seamless.IsReverse && _seamless.EntryWindow != null && !IsVisible && !_closing)
            {
                // 透明舞台全程透出真实 3K 主窗口（零截图）；待容器壳体与封面 100% 展开到位后，
                // 在 Topmost 舞台遮挡下一次性 Show() 播放器并原子卸下舞台，兼得真实背景与零闪烁。
                RevealPlayerAndFinishSeamless();
                return;
            }
            StopSeamlessMorph();
            if (wasClosing && !_closing)
            {
                Close();
            }
        }
    }

    private void RevealPlayerAndFinishSeamless()
    {
        if (_closing) return;
        if (_seamless.FrameAttached)
        {
            CompositionTarget.Rendering -= OnSeamlessMorphRendering;
            _seamless.FrameAttached = false;
        }
        PreRevealPlayerBehindEntry();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (!_closing && _seamless.IsRunning)
                StopSeamlessMorph();
        }));
    }

    private void ApplySeamlessMorphFrame(double p)
    {
        var rect = SpringMotion.InterpolateRect(_seamless.StartRect, _seamless.EndRect, p);
        _seamless.CurrentRect = rect;

        var heroHost = SeamlessHeroHost ?? (FrameworkElement)SeamlessHeroCard;
        var baseW = Math.Max(1, heroHost.Width > 0 ? heroHost.Width : Math.Max(_seamless.StartRect.Width, _seamless.EndRect.Width));
        var baseH = Math.Max(1, heroHost.Height > 0 ? heroHost.Height : Math.Max(_seamless.StartRect.Height, _seamless.EndRect.Height));
        var sx = rect.Width / baseW;
        var sy = rect.Height / baseH;

        _seamless.HeroScale.ScaleX = sx;
        _seamless.HeroScale.ScaleY = sy;
        _seamless.HeroTranslate.X = rect.X;
        _seamless.HeroTranslate.Y = rect.Y;

        if (SeamlessHeroHost != null)
        {
            var (artSx, artSy) = SpringMotion.ComputeAspectPreservingInnerScale(sx, sy);
            _seamless.HeroArtScale.ScaleX = artSx;
            _seamless.HeroArtScale.ScaleY = artSy;
        }

        var radius = SpringMotion.InterpolateRadius(_seamless.StartRadius, _seamless.EndRadius, p);
        var invScale = Math.Max(0.05, Math.Min(sx, sy));
        var nextClipRadius = radius / invScale;
        if (Math.Abs(_seamless.HeroClip.Rect.Width - baseW) > 0.01 || Math.Abs(_seamless.HeroClip.Rect.Height - baseH) > 0.01)
            _seamless.HeroClip.Rect = new Rect(0, 0, baseW, baseH);
        if (Math.Abs(_seamless.HeroClip.RadiusX - nextClipRadius) > 0.05)
            _seamless.HeroClip.RadiusX = _seamless.HeroClip.RadiusY = nextClipRadius;

        // 澎湃 OS 详情页半透明 UI 容器壳体（SeamlessPlayerShell）：
        // 与英雄封面同步从被点击卡片展开到详情页窗口目标矩形（或在逆向时收回卡片），
        // 底层直接透出真实 3K 主窗口，零截图、零 BitmapCache、零 PushOpacity。
        if (SeamlessPlayerShell != null)
        {
            if (_seamless.EntryWindow != null)
            {
                var shellEnd = _seamless.ShellEndRect.Width > 1 && _seamless.ShellEndRect.Height > 1
                    ? _seamless.ShellEndRect
                    : ResolvePlayerWindowTargetRect();
                var shellFrom = _seamless.ShellStartRect.Width >= 2 && _seamless.ShellStartRect.Height >= 2
                    ? _seamless.ShellStartRect
                    : (_seamless.IsReverse ? shellEnd : _seamless.StartRect);
                var shellTo = _seamless.IsReverse ? _seamless.EndRect : shellEnd;
                var shellProgress = _seamless.IsReverse
                    ? p
                    : SpringMotion.SmoothStep(SpringMotion.StageProgress(p, 0.0, 0.88));
                var shellRect = SpringMotion.InterpolateRect(shellFrom, shellTo, shellProgress);
                _seamless.CurrentShellRect = shellRect;
                var sw = Math.Max(1, SeamlessPlayerShell.Width > 0 ? SeamlessPlayerShell.Width : shellEnd.Width);
                var sh = Math.Max(1, SeamlessPlayerShell.Height > 0 ? SeamlessPlayerShell.Height : shellEnd.Height);
                _seamless.ShellScale.ScaleX = shellRect.Width / sw;
                _seamless.ShellScale.ScaleY = shellRect.Height / sh;
                _seamless.ShellTranslate.X = shellRect.X;
                _seamless.ShellTranslate.Y = shellRect.Y;

                var shellColor = (Application.Current?.TryFindResource("AppBackgroundBrush") as SolidColorBrush)?.Color
                    ?? Color.FromRgb(0x14, 0x16, 0x1A);
                var borderColor = (Application.Current?.TryFindResource("DividerBrush") as SolidColorBrush)?.Color
                    ?? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);
                double shellAlphaFactor = _seamless.IsReverse
                    ? 1.0 - SpringMotion.SmoothStep(SpringMotion.StageProgress(p, 0.12, 0.85))
                    : 0.52 + 0.48 * shellProgress;
                _seamless.ShellBrush.Color = Color.FromArgb((byte)Math.Clamp(shellAlphaFactor * 255, 0, 255), shellColor.R, shellColor.G, shellColor.B);
                _seamless.ShellBorderBrush.Color = Color.FromArgb((byte)Math.Clamp(shellAlphaFactor * borderColor.A, 0, 255), borderColor.R, borderColor.G, borderColor.B);
                SeamlessPlayerShell.Visibility = Visibility.Visible;
            }
            else
            {
                SeamlessPlayerShell.Visibility = Visibility.Collapsed;
            }
        }

        if (_seamless.EntryWindow != null)
        {
            if (_seamless.IsReverse)
            {
                var remain = 1.0 - SpringMotion.SmoothStep(SpringMotion.StageProgress(p, 0.0, 0.85));
                _seamless.BackdropBrush.Color = Color.FromArgb((byte)(remain * 72), 6, 8, 12);
                heroHost.Opacity = 1.0;
            }
            else
            {
                var dim = SpringMotion.SmoothStep(SpringMotion.StageProgress(p, 0.0, 0.55));
                _seamless.BackdropBrush.Color = Color.FromArgb((byte)(dim * 72), 6, 8, 12);
                heroHost.Opacity = 1.0;
            }
            return;
        }

        // 平板内嵌模式或单窗兜底（PlayerWindow 内部覆盖层）：绝不改 TopBar/InfoPanel/WebLeftPane 的 Opacity
        if (_seamless.IsReverse)
        {
            var remain = 1.0 - SpringMotion.SmoothStep(SpringMotion.StageProgress(p, 0.0, 0.88));
            _seamless.BackdropBrush.Color = Color.FromArgb((byte)(remain * 92), 6, 8, 12);
            heroHost.Opacity = 1.0;
        }
        else
        {
            var lift = SpringMotion.SmoothStep(Math.Sin(Math.Clamp(p, 0, 1) * Math.PI));
            _seamless.BackdropBrush.Color = Color.FromArgb((byte)(lift * 82), 6, 8, 12);
            if (LoadingPoster != null) LoadingPoster.Opacity = 1;
            if (!_vmemMode && VideoView != null)
                VideoView.Visibility = Visibility.Hidden;
            var dissolve = SpringMotion.SmoothStep(SpringMotion.StageProgress(p, 0.82, 1.0));
            heroHost.Opacity = 1.0 - dissolve * 0.95;
        }
    }

    private void ApplySeamlessWindowSnapshot(Brush? brush, Window? source)
    {
        SeamlessWindowSnapshot.Background = brush;
        SeamlessWindowSnapshot.Opacity = brush != null ? 1 : 0;
        SeamlessWindowSnapshot.HorizontalAlignment = HorizontalAlignment.Stretch;
        SeamlessWindowSnapshot.VerticalAlignment = VerticalAlignment.Stretch;
        SeamlessWindowSnapshot.Width = SeamlessWindowSnapshot.Height = double.NaN;
        SeamlessWindowSnapshot.Margin = new Thickness(0);
        if (source?.Content is not FrameworkElement content || !IsVisible) return;
        try
        {
            var topLeft = SeamlessStage.PointFromScreen(content.PointToScreen(new Point()));
            var bottomRight = SeamlessStage.PointFromScreen(content.PointToScreen(new Point(content.ActualWidth, content.ActualHeight)));
            SeamlessWindowSnapshot.HorizontalAlignment = HorizontalAlignment.Left;
            SeamlessWindowSnapshot.VerticalAlignment = VerticalAlignment.Top;
            SeamlessWindowSnapshot.Width = Math.Max(1, bottomRight.X - topLeft.X);
            SeamlessWindowSnapshot.Height = Math.Max(1, bottomRight.Y - topLeft.Y);
            SeamlessWindowSnapshot.Margin = new Thickness(topLeft.X, topLeft.Y, 0, 0);
        }
        catch { }
    }

    private void RestorePlayerChromeAfterSeamless()
    {
        _seamless.TopBarShift.X = 0;
        _seamless.TopBarShift.Y = 0;
        _seamless.InfoPanelShift.X = 0;
        _seamless.InfoPanelShift.Y = 0;
        if (TopBar != null) TopBar.Opacity = 1;
        if (InfoPanel != null) InfoPanel.Opacity = 1;
        if (WebLeftPane != null) WebLeftPane.Opacity = 1;
        if (LoadingPoster != null) LoadingPoster.Opacity = 1;
        if (!_vmemMode && VideoView != null && !_closing)
        {
            VideoView.Content = VideoAreaRoot;
            VideoView.Visibility = Visibility.Visible;
            HookOverlayKeyboard();
        }
        if (SeamlessMorphOverlay != null) SeamlessMorphOverlay.Visibility = Visibility.Collapsed;
        if (SeamlessHeroHost != null) SeamlessHeroHost.Opacity = 1;
        if (SeamlessHeroCard != null) SeamlessHeroCard.Opacity = 1;
        if (SeamlessPlayerShell != null) SeamlessPlayerShell.Visibility = Visibility.Collapsed;
        _seamless.BackdropBrush.Color = Color.FromArgb(0, 6, 8, 12);
        if (SeamlessWindowSnapshot != null)
        {
            SeamlessWindowSnapshot.Opacity = 0;
            SeamlessWindowSnapshot.Background = null;
        }
        _seamless.HeroArtScale.ScaleX = 1;
        _seamless.HeroArtScale.ScaleY = 1;
    }

    internal void StopSeamlessMorph()
    {
        var hadEntryWindow = _seamless.EntryWindow != null;
        var sourceMain = _seamless.SourceMainWindow;
        var wasReverse = _seamless.IsReverse;
        _seamless.SourceMainWindow = null;
        _seamless.HandedOffToPlayer = false;
        var wasClosing = _seamless.IsClosingMorph;
        _seamless.IsRunning = false;
        _seamless.IsReverse = false;
        _seamless.IsClosingMorph = false;
        _seamless.PreShowPrepared = false;
        _seamless.Velocity = 0;
        if (_seamless.FrameAttached)
        {
            CompositionTarget.Rendering -= OnSeamlessMorphRendering;
            _seamless.FrameAttached = false;
        }
        if (!wasClosing && !_closing)
        {
            // 先确保底层 PlayerWindow 已经可见并挂回 VideoView，再关闭 Topmost EntryWindow，保证单帧原子交接零闪烁
            if (hadEntryWindow && !_isEmbeddedInMain && !IsVisible)
            {
                try { Show(); } catch { }
            }
            RestorePlayerChromeAfterSeamless();
            UnmountSeamlessEntryWindow(restoreOwnership: true);
            if (hadEntryWindow && !_isEmbeddedInMain && IsVisible)
            {
                try { Activate(); } catch { }
            }
            FlushPostEntryUiActions(execute: true);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_closing) DwmInterop.SetTransitionsDisabled(this, false);
            }));
        }
        else
        {
            UnmountSeamlessEntryWindow(restoreOwnership: true);
            FlushPostEntryUiActions(execute: false);
        }
        sourceMain?.ResetSeamlessDepthRecess(animate: wasClosing || wasReverse);
    }

    private void AttachSeamlessFrameClock()
    {
        if (_seamless.FrameAttached) return;
        _seamless.FrameAttached = true;
        _seamless.LastFrameTicks = Stopwatch.GetTimestamp();
        _seamless.LastRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnSeamlessMorphRendering;
    }

    private void OnSeamlessMorphRendering(object? sender, EventArgs e)
    {
        var stageVisible = (_seamless.EntryWindow != null && !_seamless.HandedOffToPlayer)
            ? _seamless.EntryWindow.IsVisible
            : (_isEmbeddedInMain ? (_embeddedHost?.IsVisible == true && RootGrid.IsVisible) : IsVisible);
        if (_closing || !stageVisible || (!_isEmbeddedInMain && WindowState == WindowState.Minimized))
        {
            StopSeamlessMorph();
            return;
        }
        if (e is RenderingEventArgs frame)
        {
            if (_seamless.LastRenderingTime == frame.RenderingTime) return;
            _seamless.LastRenderingTime = frame.RenderingTime;
        }
        var now = Stopwatch.GetTimestamp();
        var dt = Stopwatch.GetElapsedTime(_seamless.LastFrameTicks, now).TotalSeconds;
        _seamless.LastFrameTicks = now;
        StepSeamlessMorph(Math.Clamp(dt, 0.001, 0.05));
    }

    internal Rect ResolvePlayerWindowTargetRect()
    {
        try
        {
            if (_seamless.EntryWindow != null &&
                PresentationSource.FromVisual(SeamlessStage) is { CompositionTarget: not null })
            {
                var dpi = VisualTreeHelper.GetDpi(SeamlessStage);
                var wa = SystemParameters.WorkArea;
                if (_seamless.EntryWindow.Owner is { } owner && PresentationSource.FromVisual(owner) != null)
                {
                    var handle = new WindowInteropHelper(owner).Handle;
                    if (handle != IntPtr.Zero)
                    {
                        var bounds = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                        wa = new Rect(bounds.Left / dpi.DpiScaleX, bounds.Top / dpi.DpiScaleY,
                            bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
                    }
                }
                var targetRect = WindowState == WindowState.Maximized
                    ? wa
                    : new Rect(
                        double.IsNaN(Left) ? wa.Left : Left,
                        double.IsNaN(Top) ? wa.Top : Top,
                        ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : wa.Width),
                        ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : wa.Height));
                var tl = SeamlessStage.PointFromScreen(new Point(targetRect.Left * dpi.DpiScaleX, targetRect.Top * dpi.DpiScaleY));
                var br = SeamlessStage.PointFromScreen(new Point(targetRect.Right * dpi.DpiScaleX, targetRect.Bottom * dpi.DpiScaleY));
                return new Rect(
                    Math.Min(tl.X, br.X),
                    Math.Min(tl.Y, br.Y),
                    Math.Max(16, Math.Abs(br.X - tl.X)),
                    Math.Max(16, Math.Abs(br.Y - tl.Y)));
            }
        }
        catch { }

        var stageW = SeamlessStage.ActualWidth > 1 ? SeamlessStage.ActualWidth : (_seamless.EntryWindow?.Width ?? (ActualWidth > 1 ? ActualWidth : 1280));
        var stageH = SeamlessStage.ActualHeight > 1 ? SeamlessStage.ActualHeight : (_seamless.EntryWindow?.Height ?? (ActualHeight > 1 ? ActualHeight : 760));
        return new Rect(0, 0, Math.Max(1, stageW), Math.Max(1, stageH));
    }

    internal Rect ResolveVideoStageTargetRect()
    {
        try
        {
            if (_seamless.EntryWindow != null && IsVisible &&
                ActiveLayoutMode == DesiredLayoutMode &&
                VideoSurface != null && VideoSurface.IsVisible && VideoSurface.ActualWidth > 4 && VideoSurface.ActualHeight > 4 &&
                PresentationSource.FromVisual(VideoSurface) is { CompositionTarget: not null } &&
                PresentationSource.FromVisual(SeamlessStage) is { CompositionTarget: not null })
            {
                return new Rect(
                    SeamlessStage.PointFromScreen(VideoSurface.PointToScreen(new Point())),
                    SeamlessStage.PointFromScreen(VideoSurface.PointToScreen(new Point(VideoSurface.ActualWidth, VideoSurface.ActualHeight))));
            }
            if (_seamless.EntryWindow != null &&
                PresentationSource.FromVisual(SeamlessStage) is { CompositionTarget: not null })
            {
                var dpi = VisualTreeHelper.GetDpi(SeamlessStage);
                var wa = SystemParameters.WorkArea;
                if (_seamless.EntryWindow.Owner is { } owner && PresentationSource.FromVisual(owner) != null)
                {
                    var handle = new WindowInteropHelper(owner).Handle;
                    if (handle != IntPtr.Zero)
                    {
                        var bounds = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                        wa = new Rect(bounds.Left / dpi.DpiScaleX, bounds.Top / dpi.DpiScaleY,
                            bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
                    }
                }
                var targetRect = WindowState == WindowState.Maximized
                    ? wa
                    : new Rect(
                        double.IsNaN(Left) ? wa.Left : Left,
                        double.IsNaN(Top) ? wa.Top : Top,
                        ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : wa.Width),
                        ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : wa.Height));
                var localVideo = ComputeExpectedVideoStageRect(targetRect.Width, targetRect.Height);
                var tl = SeamlessStage.PointFromScreen(new Point((targetRect.Left + localVideo.Left) * dpi.DpiScaleX, (targetRect.Top + localVideo.Top) * dpi.DpiScaleY));
                var br = SeamlessStage.PointFromScreen(new Point((targetRect.Left + localVideo.Right) * dpi.DpiScaleX, (targetRect.Top + localVideo.Bottom) * dpi.DpiScaleY));
                return new Rect(tl, br);
            }
            if (ActiveLayoutMode == DesiredLayoutMode && VideoSurface != null && RootGrid != null && VideoSurface.ActualWidth > 4 && VideoSurface.ActualHeight > 4)
            {
                return VideoSurface.TransformToAncestor(RootGrid)
                    .TransformBounds(new Rect(VideoSurface.RenderSize));
            }
            if (RootGrid != null)
            {
                RootGrid.UpdateLayout();
                if (ActiveLayoutMode == DesiredLayoutMode && VideoSurface != null && VideoSurface.ActualWidth > 4 && VideoSurface.ActualHeight > 4)
                {
                    return VideoSurface.TransformToAncestor(RootGrid)
                        .TransformBounds(new Rect(VideoSurface.RenderSize));
                }
            }
        }
        catch { }

        var totalW = _isEmbeddedInMain && RootGrid?.ActualWidth > 0 ? RootGrid.ActualWidth : (ActualWidth > 0 ? ActualWidth : Width);
        var totalH = _isEmbeddedInMain && RootGrid?.ActualHeight > 0 ? RootGrid.ActualHeight : (ActualHeight > 0 ? ActualHeight : Height);
        return ComputeExpectedVideoStageRect(totalW, totalH);
    }

    private Rect ComputeExpectedVideoStageRect(double totalW, double totalH)
    {
        var topH = _isFullscreen ? 0 : 44;
        var stageH = Math.Max(180, totalH - topH);

        // 1. 平板竖屏布局：视频位于顶部通栏
        if (_tabletPortraitLayoutMounted || ShouldUseTabletPortraitLayout)
        {
            var vw = Math.Max(240, totalW);
            var vh = CalculateTabletPortraitVideoHeight(vw, stageH);
            return new Rect(0, topH, vw, vh);
        }

        var railW = _isFullscreen || _isFillWindow ? 0 : InfoPanelWidthFor(totalW);
        var availableW = Math.Max(320, totalW - railW);

        // 2. 桌面「竖屏优化」布局：视频位于左侧竖列，紧邻中间评论列与右侧信息栏
        if (_portraitLayoutMounted || ShouldUsePortraitPlayerLayout)
        {
            var vw = CalculatePortraitVideoWidth(availableW, stageH);
            if (vw < 180) vw = Math.Min(availableW, Math.Max(180, stageH * 9.0 / 16.0));
            return new Rect(0, topH, vw, stageH);
        }

        // 3. 网页布局：16:9 视频位于左上区（扣除 8px 分隔空隙）
        if (_webLayoutMounted || ShouldUseWebPlayerLayout)
        {
            var vw = Math.Max(320, availableW - 8);
            var vh = Math.Min(vw * 9.0 / 16.0, stageH);
            return new Rect(0, topH, vw, vh);
        }

        // 4. 经典左右分栏布局
        return new Rect(0, topH, availableW, stageH);
    }

    private double ResolveVideoStageTargetRadius()
        => _webLayoutMounted && _webDockProgress >= 0.5 ? 14.0 : 0.0;

    /// <summary>
    /// 将共享封面纹理同步注入到：
    /// 1) 一镜到底飞跃卡片 <c>SeamlessHeroCard</c>；
    /// 2) 视频首帧就位前的加载海报 <c>LoadingPoster</c>；
    /// 3) 普通模式（非 vmem）下的网页大小窗过渡卡片 <c>DockTransitionPoster</c>。
    /// </summary>
    internal void ApplySharedCoverArtwork(ImageSource? image, Brush? fallback = null)
    {
        if (image != null)
        {
            // 同理：不能冻结别人还在写的 WriteableBitmap（例如封面来自活的视频帧）。
            if (image is System.Windows.Media.Imaging.WriteableBitmap { IsFrozen: false } live)
            {
                try { var copy = live.Clone(); copy.Freeze(); image = copy; } catch { }
            }
            var brush = new ImageBrush(image)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            if (image.IsFrozen && brush.CanFreeze) brush.Freeze();

            if (SeamlessHeroCard != null) SeamlessHeroCard.Background = brush;
            if (LoadingPoster != null)
            {
                LoadingPoster.Background = brush;
                LoadingPoster.Visibility = Visibility.Visible;
            }
            if (DockTransitionPoster != null) DockTransitionPoster.Background = brush;
        }
        else if (fallback != null)
        {
            if (SeamlessHeroCard != null) SeamlessHeroCard.Background = fallback;
        }
    }

    /// <summary>
    /// 异步加载视频封面并注入一镜到底英雄卡片、加载海报与大小窗过渡卡片。
    /// </summary>
    internal async void PopulateDockAndLoadingPosterAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var gen = _loadGen;
        try
        {
            var img = await CoverLoader.LoadAsync(url, 640);
            if (img == null || _closing || gen != _loadGen) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing || gen != _loadGen) return;
                ApplySharedCoverArtwork(img);
            });
        }
        catch { }
    }
}
