using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliTouch.Helpers;

namespace BiliTouch.Views;

/// <summary>
/// 金标联盟「一镜到底」(Seamless Shared-Element Morph) 与澎湃 OS 4 (HyperOS 4) 详情页分层动效编排。
/// <para>
/// 核心特性：
/// <list type="bullet">
///   <item><description>从列表卡片进入详情页时，封面图、圆角与容器几何按临界阻尼弹簧（ζ=1.0，零过冲）连续过渡到播放器视频区；</description></item>
///   <item><description>全程通过复用的 <see cref="ScaleTransform"/>、<see cref="TranslateTransform"/> 与单一 <see cref="RectangleGeometry"/> 驱动，运动期间零 Measure/Arrange 重排、零 GC 几何分配；</description></item>
///   <item><description>支持任意时刻打断或反向（Retargeting），保留当前位置与速度向量平滑转向；</description></item>
///   <item><description>与首帧加载海报 <c>LoadingPoster</c> 及普通模式停靠卡片 <c>DockTransitionPoster</c> 共享已解码封面纹理。</description></item>
/// </list>
/// </para>
/// </summary>
public partial class PlayerWindow
{
    private sealed class SeamlessMorphState
    {
        public bool IsRunning;
        public bool FrameAttached;
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
    /// 尝试消费挂起的共享元素锚点，启动从源视频卡片到播放器视频舞台的「一镜到底」展开过渡。
    /// </summary>
    internal bool TryStartSeamlessEntry(string bvid)
    {
        if (_closing || string.IsNullOrEmpty(bvid) || RootGrid == null) return false;
        if (!SeamlessTransitionDirector.TryConsumeForPlayer(bvid, this, RootGrid, out var anchor, out var startRect))
            return false;

        ApplySharedCoverArtwork(anchor.CoverImage, anchor.FallbackBrush);
        return StartSeamlessMorph(startRect, ResolveVideoStageTargetRect(), anchor.CornerRadius, ResolveVideoStageTargetRadius());
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

        // 固定基准尺寸为 Target 大小，运动全程只改 ScaleTransform 与 TranslateTransform，零 Measure/Arrange
        var baseW = Math.Max(1, _seamless.EndRect.Width);
        var baseH = Math.Max(1, _seamless.EndRect.Height);
        SeamlessHeroCard.Width = baseW;
        SeamlessHeroCard.Height = baseH;
        SeamlessMorphOverlay.Visibility = Visibility.Visible;

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

        if (Math.Abs(_seamless.Progress - _seamless.Target) < 0.00075 && Math.Abs(_seamless.Velocity) < 0.025)
        {
            _seamless.Progress = _seamless.Target;
            _seamless.Velocity = 0;
            ApplySeamlessMorphFrame(_seamless.Progress);
            StopSeamlessMorph();
        }
    }

    private void ApplySeamlessMorphFrame(double p)
    {
        var rect = SpringMotion.InterpolateRect(_seamless.StartRect, _seamless.EndRect, p);
        _seamless.CurrentRect = rect;

        var baseW = Math.Max(1, SeamlessHeroCard.Width > 0 ? SeamlessHeroCard.Width : _seamless.EndRect.Width);
        var baseH = Math.Max(1, SeamlessHeroCard.Height > 0 ? SeamlessHeroCard.Height : _seamless.EndRect.Height);
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

        // 澎湃 OS 4 分层编排：
        // 1. 背景柔化层在初段浮现，末段随主容器落位渐隐
        SeamlessBackdrop.Opacity = Math.Sin(Math.Clamp(p, 0, 1) * Math.PI) * 0.48;
        // 2. 飞跃卡片在末段 [0.74, 1.0] 与底层已就位的视频/海报面无缝溶入
        var dissolve = SpringMotion.StageProgress(p, 0.74, 1.0);
        SeamlessHeroCard.Opacity = 1.0 - dissolve * 0.92;
        SeamlessHeroShade.Opacity = dissolve * 0.35;

        // 3. 右侧信息栏错峰滑入（不改 Margin/Width，仅改 RenderTransform）
        var railStage = SpringMotion.StageProgress(p, 0.12, 0.96);
        _seamless.InfoPanelShift.X = (1.0 - railStage) * 16.0;
    }

    internal void StopSeamlessMorph()
    {
        _seamless.IsRunning = false;
        _seamless.Velocity = 0;
        if (_seamless.FrameAttached)
        {
            CompositionTarget.Rendering -= OnSeamlessMorphRendering;
            _seamless.FrameAttached = false;
        }
        _seamless.InfoPanelShift.X = 0;
        _seamless.InfoPanelShift.Y = 0;
        if (SeamlessMorphOverlay != null) SeamlessMorphOverlay.Visibility = Visibility.Collapsed;
        if (SeamlessHeroCard != null) SeamlessHeroCard.Opacity = 1;
        if (SeamlessBackdrop != null) SeamlessBackdrop.Opacity = 0;
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

    private async void PopulateDockAndLoadingPosterAsync(string coverUrl)
    {
        if (_closing || string.IsNullOrWhiteSpace(coverUrl)) return;
        try
        {
            var img = await CoverLoader.LoadAsync(coverUrl, 640);
            if (_closing || img == null) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing) return;
                ApplySharedCoverArtwork(img);
            });
        }
        catch { }
    }
}
