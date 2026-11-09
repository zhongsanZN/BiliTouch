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
        public double Progress;
        public double Velocity;
        public double Target = 1.0;
        public double Omega = SpringMotion.HyperOmegaContainer;
        public Rect StartRect;
        public Rect EndRect;
        public Rect CurrentRect;
        public double StartRadius = 8.0;
        public double EndRadius;
        public long LastFrameTicks;
        public TimeSpan LastRenderingTime = TimeSpan.MinValue;
        public readonly ScaleTransform HeroScale = new(1, 1);
        public readonly TranslateTransform HeroTranslate = new(0, 0);
        public readonly RectangleGeometry HeroClip = new();
        public readonly TranslateTransform InfoPanelShift = new(0, 0);
        public bool TransformsMounted;
    }

    private readonly SeamlessMorphState _seamless = new();

    internal bool IsSeamlessMorphRunning => _seamless.IsRunning;
    internal double SeamlessMorphProgress => _seamless.Progress;
    internal Rect SeamlessCurrentRect => _seamless.CurrentRect;

    private void EnsureSeamlessTransformsMounted()
    {
        if (_seamless.TransformsMounted) return;
        _seamless.TransformsMounted = true;

        var group = new TransformGroup();
        group.Children.Add(_seamless.HeroScale);
        group.Children.Add(_seamless.HeroTranslate);
        SeamlessHeroCard.RenderTransform = group;
        SeamlessHeroCard.RenderTransformOrigin = new Point(0, 0);
        SeamlessHeroCard.Clip = _seamless.HeroClip;

        if (InfoPanel.RenderTransform == null || ReferenceEquals(InfoPanel.RenderTransform, Transform.Identity))
            InfoPanel.RenderTransform = _seamless.InfoPanelShift;
    }

    /// <summary>
    /// 在播放器窗口 <see cref="Window.Show"/> 之前调用：
    /// 1) 禁用系统 DWM 窗口弹出缩放动画（消除"从屏幕中间展开新窗口"）；
    /// 2) 预挂载源窗口背景快照并隐藏原生 HwndHost，使窗口上屏第一帧与源页面完全连续。
    /// </summary>
    internal void PrepareSeamlessPreShow()
    {
        if (_closing) return;
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
            SeamlessWindowSnapshot.Background = anchor.SourceWindowBackdrop;
            SeamlessWindowSnapshot.Opacity = anchor.SourceWindowBackdrop != null ? 1.0 : 0.0;
        }
        if (SeamlessBackdrop != null) SeamlessBackdrop.Opacity = 0;
        if (SeamlessMorphOverlay != null) SeamlessMorphOverlay.Visibility = Visibility.Visible;
        if (!_vmemMode && VideoView != null) VideoView.Visibility = Visibility.Hidden;
        if (LoadingPoster != null) LoadingPoster.Opacity = 0;
        if (TopBar != null) TopBar.Opacity = 0;
        if (InfoPanel != null) InfoPanel.Opacity = 0;
        if (WebLeftPane != null) WebLeftPane.Opacity = 0;
    }

    /// <summary>
    /// 尝试消费挂起的共享元素锚点，启动从源视频卡片到播放器视频舞台的「一镜到底」展开过渡。
    /// </summary>
    internal bool TryStartSeamlessEntry(string bvid)
    {
        if (_closing || string.IsNullOrEmpty(bvid) || RootGrid == null) return false;
        if (!SeamlessTransitionDirector.TryConsumeForPlayer(bvid, this, RootGrid, out var anchor, out var startRect))
        {
            if (_seamless.PreShowPrepared)
            {
                _seamless.PreShowPrepared = false;
                RestorePlayerChromeAfterSeamless();
            }
            return false;
        }

        _seamless.PreShowPrepared = false;
        _seamless.IsClosingMorph = false;
        if (SeamlessWindowSnapshot != null)
        {
            SeamlessWindowSnapshot.Background = anchor.SourceWindowBackdrop;
            SeamlessWindowSnapshot.Opacity = anchor.SourceWindowBackdrop != null ? 1.0 : 0.0;
        }
        ApplySharedCoverArtwork(anchor.CoverImage, anchor.FallbackBrush);
        return StartSeamlessMorph(startRect, ResolveVideoStageTargetRect(), anchor.CornerRadius, ResolveVideoStageTargetRadius(), reverse: false);
    }

    /// <summary>
    /// 点击右上角关闭按钮时启动逆向「一镜到底」：将视频舞台连续收拢回源视频卡片位置，再无缝隐藏窗口。
    /// </summary>
    internal bool TryBeginSeamlessClose()
    {
        if (_closing || _seamless.IsClosingMorph || !IsVisible || WindowState == WindowState.Minimized || _isFullscreen)
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

        var backdrop = SeamlessTransitionDirector.CaptureWindowSnapshotBrush(srcWin) ?? anchor?.SourceWindowBackdrop;
        if (SeamlessWindowSnapshot != null)
        {
            SeamlessWindowSnapshot.Background = backdrop;
            SeamlessWindowSnapshot.Opacity = 0;
        }

        if (_vmemMode && VmemView?.Source != null)
        {
            var frameBrush = new ImageBrush(VmemView.Source)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            if (frameBrush.CanFreeze) frameBrush.Freeze();
            SeamlessHeroCard.Background = frameBrush;
        }
        else if (anchor?.CoverImage != null)
        {
            ApplySharedCoverArtwork(anchor.CoverImage, anchor.FallbackBrush);
        }

        Rect cardRect;
        double cardRadius = 8.0;
        if (anchor != null && (string.IsNullOrEmpty(_currentBvid) || string.Equals(anchor.Bvid, _currentBvid, StringComparison.OrdinalIgnoreCase)))
        {
            cardRect = SeamlessTransitionDirector.ResolveStageStartRect(anchor, this, RootGrid);
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
        if (!_vmemMode && VideoView != null) VideoView.Visibility = Visibility.Hidden;
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
        _seamless.Omega = 28.0;
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
            _seamless.EndRect = reverse ? startRect : endRect;
            _seamless.StartRadius = reverse ? endRadius : startRadius;
            _seamless.EndRadius = reverse ? startRadius : endRadius;
            _seamless.Progress = 0;
            _seamless.Velocity = 0;
            _seamless.Target = 1.0;
            _seamless.Omega = reverse ? SpringMotion.HyperOmegaInterrupt : SpringMotion.HyperOmegaContainer;
        }

        // 固定基准尺寸为最大目标尺寸，运动全程只改 ScaleTransform 与 TranslateTransform，零 Measure/Arrange
        var baseW = Math.Max(1, Math.Max(_seamless.StartRect.Width, _seamless.EndRect.Width));
        var baseH = Math.Max(1, Math.Max(_seamless.StartRect.Height, _seamless.EndRect.Height));
        SeamlessHeroCard.Width = baseW;
        SeamlessHeroCard.Height = baseH;
        SeamlessMorphOverlay.Visibility = Visibility.Visible;

        if (!_vmemMode && VideoView != null)
            VideoView.Visibility = Visibility.Hidden;

        _seamless.IsRunning = true;
        ApplySeamlessMorphFrame(0);
        AttachSeamlessFrameClock();
        return true;
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

        var settleTolerance = _seamless.IsClosingMorph ? 0.015 : 0.00075;
        var velTolerance = _seamless.IsClosingMorph ? 0.18 : 0.025;
        if (Math.Abs(_seamless.Progress - _seamless.Target) < settleTolerance && Math.Abs(_seamless.Velocity) < velTolerance)
        {
            _seamless.Progress = _seamless.Target;
            _seamless.Velocity = 0;
            ApplySeamlessMorphFrame(_seamless.Progress);
            var wasClosing = _seamless.IsClosingMorph;
            StopSeamlessMorph();
            if (wasClosing && !_closing)
            {
                Close();
            }
        }
    }

    private void ApplySeamlessMorphFrame(double p)
    {
        var rect = SpringMotion.InterpolateRect(_seamless.StartRect, _seamless.EndRect, p);
        _seamless.CurrentRect = rect;

        var baseW = Math.Max(1, SeamlessHeroCard.Width > 0 ? SeamlessHeroCard.Width : Math.Max(_seamless.StartRect.Width, _seamless.EndRect.Width));
        var baseH = Math.Max(1, SeamlessHeroCard.Height > 0 ? SeamlessHeroCard.Height : Math.Max(_seamless.StartRect.Height, _seamless.EndRect.Height));
        var sx = rect.Width / baseW;
        var sy = rect.Height / baseH;

        _seamless.HeroScale.ScaleX = sx;
        _seamless.HeroScale.ScaleY = sy;
        _seamless.HeroTranslate.X = rect.X;
        _seamless.HeroTranslate.Y = rect.Y;

        var radius = SpringMotion.InterpolateRadius(_seamless.StartRadius, _seamless.EndRadius, p);
        var invScale = Math.Max(0.05, Math.Min(sx, sy));
        _seamless.HeroClip.Rect = new Rect(0, 0, baseW, baseH);
        _seamless.HeroClip.RadiusX = _seamless.HeroClip.RadiusY = radius / invScale;

        if (_seamless.IsReverse)
        {
            // 逆向一镜到底（关闭收拢回源视频卡片）：
            var chromeFadeOut = 1.0 - SpringMotion.StageProgress(p, 0.0, 0.52);
            if (TopBar != null) TopBar.Opacity = chromeFadeOut;
            if (InfoPanel != null) InfoPanel.Opacity = chromeFadeOut;
            if (WebLeftPane != null) WebLeftPane.Opacity = chromeFadeOut;
            if (LoadingPoster != null) LoadingPoster.Opacity = 0;
            _seamless.InfoPanelShift.X = (1.0 - chromeFadeOut) * 28.0;

            if (SeamlessWindowSnapshot != null && SeamlessWindowSnapshot.Background != null)
                SeamlessWindowSnapshot.Opacity = SpringMotion.StageProgress(p, 0.0, 0.42);
            if (SeamlessBackdrop != null)
                SeamlessBackdrop.Opacity = (1.0 - SpringMotion.StageProgress(p, 0.15, 0.96)) * 0.62;
            SeamlessHeroCard.Opacity = 1.0 - SpringMotion.StageProgress(p, 0.85, 1.0) * 0.18;
            SeamlessHeroShade.Opacity = (1.0 - p) * 0.25;
        }
        else
        {
            // 正向一镜到底（从源卡片展开至播放器舞台）：
            // 1. 源窗口快照层在前半程承接视觉连续性，随主容器展开平滑退场
            if (SeamlessWindowSnapshot != null && SeamlessWindowSnapshot.Background != null)
                SeamlessWindowSnapshot.Opacity = 1.0 - SpringMotion.StageProgress(p, 0.22, 0.86);
            // 2. 背景阻尼遮罩在初段渐显压暗源背景，末段随主容器落位消散
            if (SeamlessBackdrop != null)
                SeamlessBackdrop.Opacity = Math.Sin(Math.Clamp(p, 0, 1) * Math.PI) * 0.68;

            // 3. 顶栏、右侧信息栏与左栏错峰滑入淡显（底层不提前露底）
            var railStage = SpringMotion.StageProgress(p, 0.16, 0.92);
            _seamless.InfoPanelShift.X = (1.0 - railStage) * 24.0;
            if (TopBar != null) TopBar.Opacity = railStage;
            if (InfoPanel != null) InfoPanel.Opacity = railStage;
            if (WebLeftPane != null) WebLeftPane.Opacity = railStage;

            // 4. 底层加载海报仅在飞跃卡片接近全尺寸时显现，避免小卡阶段全屏底图穿帮
            var posterReveal = SpringMotion.StageProgress(p, 0.68, 0.96);
            if (LoadingPoster != null) LoadingPoster.Opacity = posterReveal;
            if (!_vmemMode && VideoView != null)
                VideoView.Visibility = p >= 0.86 ? Visibility.Visible : Visibility.Hidden;

            // 5. 飞跃卡片在末段 [0.76, 1.0] 与底层已就位的视频/海报面无缝溶入
            var dissolve = SpringMotion.StageProgress(p, 0.76, 1.0);
            SeamlessHeroCard.Opacity = 1.0 - dissolve * 0.94;
            SeamlessHeroShade.Opacity = dissolve * 0.30;
        }
    }

    private void RestorePlayerChromeAfterSeamless()
    {
        _seamless.InfoPanelShift.X = 0;
        _seamless.InfoPanelShift.Y = 0;
        if (TopBar != null) TopBar.Opacity = 1;
        if (InfoPanel != null) InfoPanel.Opacity = 1;
        if (WebLeftPane != null) WebLeftPane.Opacity = 1;
        if (LoadingPoster != null) LoadingPoster.Opacity = 1;
        if (!_vmemMode && VideoView != null && !_closing)
            VideoView.Visibility = Visibility.Visible;
        if (SeamlessMorphOverlay != null) SeamlessMorphOverlay.Visibility = Visibility.Collapsed;
        if (SeamlessHeroCard != null) SeamlessHeroCard.Opacity = 1;
        if (SeamlessBackdrop != null) SeamlessBackdrop.Opacity = 0;
        if (SeamlessWindowSnapshot != null) SeamlessWindowSnapshot.Opacity = 0;
    }

    internal void StopSeamlessMorph()
    {
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
            RestorePlayerChromeAfterSeamless();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_closing) DwmInterop.SetTransitionsDisabled(this, false);
            }));
        }
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
        if (_closing || !IsVisible || WindowState == WindowState.Minimized)
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

    private Rect ResolveVideoStageTargetRect()
    {
        try
        {
            if (VideoSurface != null && RootGrid != null && VideoSurface.ActualWidth > 4 && VideoSurface.ActualHeight > 4)
            {
                return VideoSurface.TransformToAncestor(RootGrid)
                    .TransformBounds(new Rect(VideoSurface.RenderSize));
            }
            if (RootGrid != null)
            {
                RootGrid.UpdateLayout();
                if (VideoSurface != null && VideoSurface.ActualWidth > 4 && VideoSurface.ActualHeight > 4)
                {
                    return VideoSurface.TransformToAncestor(RootGrid)
                        .TransformBounds(new Rect(VideoSurface.RenderSize));
                }
            }
        }
        catch { }

        var totalW = ActualWidth > 0 ? ActualWidth : Width;
        var totalH = ActualHeight > 0 ? ActualHeight : Height;
        var railW = _isFullscreen || _isFillWindow ? 0 : InfoPanelWidthFor(totalW);
        var videoW = Math.Max(320, totalW - railW);
        var topH = _isFullscreen ? 0 : 44;
        var videoH = _webLayoutMounted
            ? Math.Min(videoW * 9.0 / 16.0, Math.Max(180, totalH - topH))
            : Math.Max(180, totalH - topH);
        return new Rect(0, topH, videoW, videoH);
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
            var brush = new ImageBrush(image)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            if (brush.CanFreeze) brush.Freeze();

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
        try
        {
            var img = await CoverLoader.LoadAsync(url, 640);
            if (img == null || _closing) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing) return;
                ApplySharedCoverArtwork(img);
            });
        }
        catch { }
    }
}
