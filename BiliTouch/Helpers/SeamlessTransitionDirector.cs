using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace BiliTouch.Helpers;

/// <summary>
/// 金标联盟「一镜到底」(Continuous Shared-Element Morph) 跨页面/跨窗口共享元素调度器。
/// <para>
/// 负责在点击视频卡片（首页/搜索/动态/收藏/历史/推荐页签）时捕获源封面的精确几何、圆角、已解码位图与宿主窗口快照，
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
        public Brush? SourceWindowBackdrop { get; set; }
        public WeakReference<FrameworkElement>? SourceElement { get; init; }
        public WeakReference<Window>? SourceWindow { get; init; }
        public required long CapturedAtTick { get; init; }
    }

    /// <summary>锚点有效期（毫秒）。超过此时间视为过期点击，不触发跨窗一镜到底。</summary>
    internal const long AnchorTtlMs = 2500;

    private static SeamlessAnchor? _pending;
    private static SeamlessAnchor? _lastActive;

    internal static SeamlessAnchor? CurrentAnchor => _pending ?? _lastActive;

    internal static bool HasPendingAnchor
        => _pending != null && Environment.TickCount64 - _pending.CapturedAtTick <= AnchorTtlMs;

    /// <summary>
    /// 从被点击的视频封面元素捕获「一镜到底」起始锚点。
    /// 直接复用元素上已解码的 <see cref="ImageBrush.ImageSource"/>，并捕获源窗口背景快照以消除跨窗跳变。
    /// </summary>
    internal static SeamlessAnchor? Capture(
        FrameworkElement? coverVisual,
        string? bvid,
        double cornerRadius = 8.0,
        FrameworkElement? imageCarrier = null)
    {
        if (coverVisual == null || string.IsNullOrWhiteSpace(bvid)) return null;
        var visualTarget = FindCoverElement(imageCarrier ?? coverVisual) ?? coverVisual;
        if (visualTarget.ActualWidth < 4 || visualTarget.ActualHeight < 4)
            visualTarget = coverVisual;
        if (visualTarget.ActualWidth < 4 || visualTarget.ActualHeight < 4) return null;

        try
        {
            var win = Window.GetWindow(visualTarget);
            var dipRect = new Rect(0, 0, visualTarget.ActualWidth, visualTarget.ActualHeight);
            if (win != null && visualTarget.IsDescendantOf(win))
            {
                dipRect = visualTarget.TransformToAncestor(win)
                    .TransformBounds(new Rect(visualTarget.RenderSize));
            }

            var screenRect = dipRect;
            if (PresentationSource.FromVisual(visualTarget) is { CompositionTarget: not null } && visualTarget.IsVisible)
            {
                var topLeft = visualTarget.PointToScreen(new Point(0, 0));
                var bottomRight = visualTarget.PointToScreen(new Point(visualTarget.ActualWidth, visualTarget.ActualHeight));
                screenRect = new Rect(
                    Math.Min(topLeft.X, bottomRight.X),
                    Math.Min(topLeft.Y, bottomRight.Y),
                    Math.Max(1, Math.Abs(bottomRight.X - topLeft.X)),
                    Math.Max(1, Math.Abs(bottomRight.Y - topLeft.Y)));
            }

            var (img, fallback) = ExtractVisualCover(imageCarrier ?? visualTarget);
            var backdrop = CaptureWindowSnapshotBrush(win);

            var anchor = new SeamlessAnchor
            {
                Bvid = bvid,
                SourceDipRect = dipRect,
                ScreenPixelRect = screenRect,
                CornerRadius = Math.Max(0, cornerRadius),
                CoverImage = img,
                FallbackBrush = fallback,
                SourceWindowBackdrop = backdrop,
                SourceElement = new WeakReference<FrameworkElement>(visualTarget),
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

    /// <summary>捕获宿主窗口当前客户区快照画刷，用于跨窗口一镜到底底层连续过渡。</summary>
    internal static Brush? CaptureWindowSnapshotBrush(Window? win)
    {
        if (win == null || !win.IsVisible || win.WindowState == WindowState.Minimized) return null;
        if (win.ActualWidth < 32 || win.ActualHeight < 32) return null;
        try
        {
            int w = Math.Clamp((int)Math.Round(win.ActualWidth), 64, 1920);
            int h = Math.Clamp((int)Math.Round(win.ActualHeight), 64, 1200);
            double scaleX = w / win.ActualWidth;
            double scaleY = h / win.ActualHeight;
            var rtb = new RenderTargetBitmap(
                w, h, 96.0 * scaleX, 96.0 * scaleY, PixelFormats.Pbgra32);
            if (win.Content is Visual contentVisual)
                rtb.Render(contentVisual);
            else
                rtb.Render(win);
            if (rtb.CanFreeze) rtb.Freeze();
            var brush = new ImageBrush(rtb)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            if (brush.CanFreeze) brush.Freeze();
            return brush;
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
        var win = sourceElement != null ? Window.GetWindow(sourceElement) : null;
        var anchor = new SeamlessAnchor
        {
            Bvid = bvid,
            SourceDipRect = sourceDipRect,
            ScreenPixelRect = sourceDipRect,
            CornerRadius = Math.Max(0, cornerRadius),
            CoverImage = coverImage,
            SourceWindowBackdrop = CaptureWindowSnapshotBrush(win),
            SourceElement = sourceElement != null ? new WeakReference<FrameworkElement>(sourceElement) : null,
            SourceWindow = win != null ? new WeakReference<Window>(win) : null,
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
    /// 若源卡片仍在可视树中，优先实时测量其最新屏幕坐标（支持关闭时逆向一镜到底精准落位）。
    /// </summary>
    internal static Rect ResolveStageStartRect(
        SeamlessAnchor anchor,
        Window playerWindow,
        FrameworkElement playerStageRoot)
    {
        try
        {
            // 若源元素仍在可视树且可见，优先刷新其实时位置
            if (anchor.SourceElement != null
                && anchor.SourceElement.TryGetTarget(out var liveEl)
                && liveEl.IsLoaded
                && liveEl.IsVisible
                && liveEl.ActualWidth >= 4
                && liveEl.ActualHeight >= 4)
            {
                var liveWin = Window.GetWindow(liveEl);
                if (liveWin != null && ReferenceEquals(liveWin, playerWindow) && playerStageRoot.IsDescendantOf(playerWindow))
                {
                    var rootOffset = playerStageRoot.TransformToAncestor(playerWindow).Transform(new Point(0, 0));
                    var dip = liveEl.TransformToAncestor(playerWindow).TransformBounds(new Rect(liveEl.RenderSize));
                    return new Rect(dip.X - rootOffset.X, dip.Y - rootOffset.Y, dip.Width, dip.Height);
                }
                if (PresentationSource.FromVisual(liveEl) is { CompositionTarget: not null }
                    && PresentationSource.FromVisual(playerStageRoot) is { CompositionTarget: not null }
                    && playerStageRoot.IsVisible)
                {
                    var tlScreen = liveEl.PointToScreen(new Point(0, 0));
                    var brScreen = liveEl.PointToScreen(new Point(liveEl.ActualWidth, liveEl.ActualHeight));
                    var tl = playerStageRoot.PointFromScreen(tlScreen);
                    var br = playerStageRoot.PointFromScreen(brScreen);
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
            if (PresentationSource.FromVisual(playerStageRoot) is { CompositionTarget: not null }
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
            var dur = TimeSpan.FromMilliseconds(260);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1.0, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1.0, dur) { EasingFunction = ease });
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

    private static FrameworkElement? FindCoverElement(DependencyObject root, int depth = 0)
    {
        if (depth > 5) return null;
        if (root is Border { Background: ImageBrush { ImageSource: not null } } b && b.ActualWidth >= 24 && b.ActualHeight >= 16)
            return b;
        if (root is Image { Source: not null } img && img.ActualWidth >= 24 && img.ActualHeight >= 16)
            return img;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindCoverElement(VisualTreeHelper.GetChild(root, i), depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static (ImageSource? Image, Brush? Fallback) ExtractVisualCover(FrameworkElement element)
    {
        if (element is Border { Background: ImageBrush ib } && ib.ImageSource != null)
            return (ib.ImageSource, null);
        if (element is Image { Source: not null } img)
            return (img.Source, null);
        var coverEl = FindCoverElement(element);
        if (coverEl is Border { Background: ImageBrush cib } && cib.ImageSource != null)
            return (cib.ImageSource, null);
        if (coverEl is Image { Source: not null } cimg)
            return (cimg.Source, null);
        if (element is Border b)
            return (null, b.Background);
        return (null, null);
    }
}
