using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BiliTouch.Helpers;

/// <summary>
/// 金标联盟「一镜到底」(Continuous Shared-Element Morph) 跨页面/跨窗口共享元素调度器。
/// <para>
/// 负责在点击视频卡片（首页/搜索/动态/收藏/历史/推荐页签）时捕获源封面的精确几何、圆角与已解码位图，
/// 并在播放器详情页展开或收起时提供连续的坐标映射与源卡片回弹联动。
/// </para>
/// </summary>
internal static class SeamlessTransitionDirector
{
    internal sealed class SeamlessAnchor
    {
        public required string Bvid { get; init; }
        public required Rect SourceDipRect { get; init; }
        public required Rect ScreenPixelRect { get; init; }
        public required double CornerRadius { get; init; }
        public ImageSource? CoverImage { get; init; }
        public Brush? FallbackBrush { get; init; }
        public WeakReference<FrameworkElement>? SourceElement { get; init; }
        public WeakReference<Window>? SourceWindow { get; init; }
        public required long CapturedAtTick { get; init; }
    }

    /// <summary>锚点有效期（毫秒）。超过此时间视为过期点击，不触发跨窗一镜到底。</summary>
    internal const long AnchorTtlMs = 1800;

    private static SeamlessAnchor? _pending;
    private static SeamlessAnchor? _lastActive;

    internal static SeamlessAnchor? CurrentAnchor => _pending ?? _lastActive;

    /// <summary>
    /// 从被点击的视频封面元素捕获「一镜到底」起始锚点。
    /// 直接复用元素上已解码的 <see cref="ImageBrush.ImageSource"/>，零拷贝、零二次解码。
    /// </summary>
    internal static SeamlessAnchor? Capture(
        FrameworkElement? coverVisual,
        string? bvid,
        double cornerRadius = 8.0,
        FrameworkElement? imageCarrier = null)
    {
        if (coverVisual == null || string.IsNullOrWhiteSpace(bvid)) return null;
        if (coverVisual.ActualWidth < 4 || coverVisual.ActualHeight < 4) return null;

        try
        {
            var win = Window.GetWindow(coverVisual);
            var dipRect = new Rect(0, 0, coverVisual.ActualWidth, coverVisual.ActualHeight);
            if (win != null && coverVisual.IsDescendantOf(win))
            {
                dipRect = coverVisual.TransformToAncestor(win)
                    .TransformBounds(new Rect(coverVisual.RenderSize));
            }

            var screenRect = dipRect;
            if (PresentationSource.FromVisual(coverVisual) is { CompositionTarget: not null } source && coverVisual.IsVisible)
            {
                var topLeft = coverVisual.PointToScreen(new Point(0, 0));
                var bottomRight = coverVisual.PointToScreen(new Point(coverVisual.ActualWidth, coverVisual.ActualHeight));
                screenRect = new Rect(
                    Math.Min(topLeft.X, bottomRight.X),
                    Math.Min(topLeft.Y, bottomRight.Y),
                    Math.Max(1, Math.Abs(bottomRight.X - topLeft.X)),
                    Math.Max(1, Math.Abs(bottomRight.Y - topLeft.Y)));
            }

            var (img, fallback) = ExtractVisualCover(imageCarrier ?? coverVisual);

            var anchor = new SeamlessAnchor
            {
                Bvid = bvid,
                SourceDipRect = dipRect,
                ScreenPixelRect = screenRect,
                CornerRadius = Math.Max(0, cornerRadius),
                CoverImage = img,
                FallbackBrush = fallback,
                SourceElement = new WeakReference<FrameworkElement>(coverVisual),
                SourceWindow = win != null ? new WeakReference<Window>(win) : null,
                CapturedAtTick = Environment.TickCount64,
            };
            _pending = anchor;
            _lastActive = anchor;
            return anchor;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>供直接构造或测试注入精确起始锚点。</summary>
    internal static SeamlessAnchor Prime(
        string bvid,
        Rect sourceDipRect,
        double cornerRadius = 8.0,
        ImageSource? coverImage = null,
        FrameworkElement? sourceElement = null)
    {
        var anchor = new SeamlessAnchor
        {
            Bvid = bvid,
            SourceDipRect = sourceDipRect,
            ScreenPixelRect = sourceDipRect,
            CornerRadius = Math.Max(0, cornerRadius),
            CoverImage = coverImage,
            SourceElement = sourceElement != null ? new WeakReference<FrameworkElement>(sourceElement) : null,
            SourceWindow = sourceElement != null && Window.GetWindow(sourceElement) is { } w
                ? new WeakReference<Window>(w)
                : null,
            CapturedAtTick = Environment.TickCount64,
        };
        _pending = anchor;
        _lastActive = anchor;
        return anchor;
    }

    /// <summary>
    /// 播放器起播时消费挂起的共享元素锚点，并将其起始矩形映射到播放器根容器坐标系。
    /// </summary>
    internal static bool TryConsumeForPlayer(
        string bvid,
        Window playerWindow,
        FrameworkElement playerStageRoot,
        out SeamlessAnchor anchor,
        out Rect stageStartRect)
    {
        anchor = null!;
        stageStartRect = Rect.Empty;
        var candidate = _pending;
        _pending = null;
        if (candidate == null) return false;
        if (!string.Equals(candidate.Bvid, bvid, StringComparison.OrdinalIgnoreCase)) return false;
        if (Environment.TickCount64 - candidate.CapturedAtTick > AnchorTtlMs) return false;

        anchor = candidate;
        _lastActive = candidate;
        stageStartRect = ResolveStageStartRect(candidate, playerWindow, playerStageRoot);
        return !stageStartRect.IsEmpty && stageStartRect.Width >= 4 && stageStartRect.Height >= 4;
    }

    /// <summary>
    /// 将源卡片坐标换算到播放器 <paramref name="playerStageRoot"/> 内部 DIP 坐标系。
    /// </summary>
    internal static Rect ResolveStageStartRect(
        SeamlessAnchor anchor,
        Window playerWindow,
        FrameworkElement playerStageRoot)
    {
        try
        {
            // 1. 同窗口内切换（例如点击播放器右栏「推荐」视频卡片）：直接用窗口内坐标加上根节点偏移
            if (anchor.SourceWindow != null
                && anchor.SourceWindow.TryGetTarget(out var srcWin)
                && ReferenceEquals(srcWin, playerWindow)
                && playerStageRoot.IsDescendantOf(playerWindow))
            {
                var rootOffset = playerStageRoot.TransformToAncestor(playerWindow).Transform(new Point(0, 0));
                return new Rect(
                    anchor.SourceDipRect.X - rootOffset.X,
                    anchor.SourceDipRect.Y - rootOffset.Y,
                    anchor.SourceDipRect.Width,
                    anchor.SourceDipRect.Height);
            }

            // 2. 跨窗口切换且双方都有 PresentationSource：通过屏幕物理像素精准换算
            if (PresentationSource.FromVisual(playerStageRoot) is { CompositionTarget: not null } targetSource
                && playerStageRoot.IsVisible
                && anchor.ScreenPixelRect.Width >= 4)
            {
                var tl = playerStageRoot.PointFromScreen(anchor.ScreenPixelRect.TopLeft);
                var br = playerStageRoot.PointFromScreen(anchor.ScreenPixelRect.BottomRight);
                var mapped = new Rect(
                    Math.Min(tl.X, br.X),
                    Math.Min(tl.Y, br.Y),
                    Math.Max(8, Math.Abs(br.X - tl.X)),
                    Math.Max(8, Math.Abs(br.Y - tl.Y)));
                var stageW = playerStageRoot.ActualWidth > 0 ? playerStageRoot.ActualWidth : playerWindow.Width;
                var stageH = playerStageRoot.ActualHeight > 0 ? playerStageRoot.ActualHeight : playerWindow.Height;
                if (stageW > 0 && stageH > 0 && mapped.IntersectsWith(new Rect(-stageW * 0.5, -stageH * 0.5, stageW * 2, stageH * 2)))
                    return mapped;
            }
        }
        catch
        {
            // 回退到窗口相对 DIP 坐标
        }

        return anchor.SourceDipRect;
    }

    /// <summary>
    /// 当从详情页退回主界面时，触发源卡片的「逆向一镜到底」回弹落位微动效。
    /// </summary>
    internal static bool PlayReturnToSourceCard(string? bvid = null)
    {
        var anchor = _lastActive;
        if (anchor == null) return false;
        if (!string.IsNullOrEmpty(bvid) && !string.Equals(anchor.Bvid, bvid, StringComparison.OrdinalIgnoreCase))
            return false;
        if (anchor.SourceElement == null || !anchor.SourceElement.TryGetTarget(out var element))
            return false;
        if (!element.IsLoaded) return false;

        try
        {
            ScaleTransform scale;
            if (element.RenderTransform is ScaleTransform existing && !existing.IsFrozen)
            {
                scale = existing;
            }
            else
            {
                scale = new ScaleTransform(1, 1);
                element.RenderTransform = scale;
                element.RenderTransformOrigin = new Point(0.5, 0.5);
            }

            var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(320);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, 1.0, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, 1.0, dur) { EasingFunction = ease });
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void Clear()
    {
        _pending = null;
        _lastActive = null;
    }

    private static (ImageSource? Image, Brush? Fallback) ExtractVisualCover(FrameworkElement element)
    {
        if (element is Border { Background: ImageBrush ib } && ib.ImageSource != null)
            return (ib.ImageSource, null);
        if (element is Image { Source: not null } img)
            return (img.Source, null);
        if (element is Border b)
        {
            if (b.Child is Panel panel)
            {
                foreach (UIElement child in panel.Children)
                {
                    if (child is Border { Background: ImageBrush childIb } && childIb.ImageSource != null)
                        return (childIb.ImageSource, b.Background);
                }
            }
            return (null, b.Background);
        }
        return (null, null);
    }
}
