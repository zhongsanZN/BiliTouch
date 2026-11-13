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

            var anchor = new SeamlessAnchor
            {
                Bvid = bvid,
                SourceDipRect = dipRect,
                ScreenPixelRect = screenRect,
                CornerRadius = Math.Max(0, cornerRadius),
                CoverImage = img,
                FallbackBrush = fallback,
                SourceWindowBackdrop = null,
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
    internal static Brush? CaptureWindowSnapshotBrush(Window? win, FrameworkElement? exclude = null)
    {
        if (win == null || !win.IsVisible || win.WindowState == WindowState.Minimized) return null;
        if (win.ActualWidth < 32 || win.ActualHeight < 32) return null;
        try
        {
            var content = win.Content as FrameworkElement;
            var target = content ?? (Visual)win;
            var width = content?.ActualWidth ?? win.ActualWidth;
            var height = content?.ActualHeight ?? win.ActualHeight;
            var dpi = VisualTreeHelper.GetDpi(target);
            int w = Math.Clamp((int)Math.Ceiling(width * dpi.DpiScaleX), 64, 4096);
            int h = Math.Clamp((int)Math.Ceiling(height * dpi.DpiScaleY), 64, 4096);
            var rtb = new RenderTargetBitmap(w, h, 96.0 * w / width, 96.0 * h / height, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            {
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(win.Background ?? Brushes.Black, null, new Rect(0, 0, width, height));
                    // Mask only the captured drawing. Mutating the live card's opacity
                    // here can commit a blank frame to the source window during capture.
                    var masked = exclude != null && exclude.IsDescendantOf(content ?? (Visual)win);
                    if (masked)
                    {
                        var excluded = exclude!.TransformToAncestor(content ?? (Visual)win).TransformBounds(new Rect(exclude.RenderSize));
                        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                            new RectangleGeometry(new Rect(0, 0, width, height)), new RectangleGeometry(excluded)));
                    }
                    dc.DrawRectangle(new VisualBrush(content ?? (Visual)win) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height) }, null,
                        new Rect(0, 0, width, height));
                    if (masked) dc.Pop();
                }
                rtb.Render(visual);
            }
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
            SourceWindowBackdrop = null,
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
        StopReturnMorph();
        _pending = null;
        _lastActive = null;
    }

    private static Grid? _returnOverlay;
    private static Panel? _returnHost;
    private static EventHandler? _returnTick;

    internal static void StopReturnMorph()
    {
        if (_returnTick != null)
        {
            CompositionTarget.Rendering -= _returnTick;
            _returnTick = null;
        }
        if (_returnOverlay != null && _returnHost != null)
        {
            _returnHost.Children.Remove(_returnOverlay);
        }
        _returnOverlay = null;
        _returnHost = null;
    }

    /// <summary>
    /// 在宿主窗口（如主窗口）GPU 合成层上执行关闭视频页时的逆向「一镜到底」落位动画：
    /// 播放器窗口立即隐藏露出实时宿主窗口，无需截取全屏位图快照，零闪烁、零重排。
    /// </summary>
    internal static bool PlayReturnMorph(
        Window targetWindow,
        Rect startScreenRect,
        double startRadius,
        Brush? heroBrush,
        SeamlessAnchor? anchor,
        string? bvid)
    {
        if (targetWindow.Content is not Panel rootPanel || !targetWindow.IsVisible || targetWindow.WindowState == WindowState.Minimized)
            return false;
        if (PresentationSource.FromVisual(rootPanel) is not { CompositionTarget: not null })
            return false;

        StopReturnMorph();

        try
        {
            var sTl = rootPanel.PointFromScreen(startScreenRect.TopLeft);
            var sBr = rootPanel.PointFromScreen(startScreenRect.BottomRight);
            var startRect = new Rect(
                Math.Min(sTl.X, sBr.X),
                Math.Min(sTl.Y, sBr.Y),
                Math.Max(16, Math.Abs(sBr.X - sTl.X)),
                Math.Max(16, Math.Abs(sBr.Y - sTl.Y)));

            var rootW = rootPanel.ActualWidth > 0 ? rootPanel.ActualWidth : targetWindow.ActualWidth;
            var rootH = rootPanel.ActualHeight > 0 ? rootPanel.ActualHeight : targetWindow.ActualHeight;

            Rect endRect = Rect.Empty;
            double endRadius = 8.0;
            if (anchor != null)
            {
                endRadius = Math.Max(6.0, anchor.CornerRadius);
                if (anchor.SourceElement != null
                    && anchor.SourceElement.TryGetTarget(out var liveEl)
                    && liveEl.IsLoaded
                    && liveEl.IsVisible
                    && liveEl.ActualWidth >= 4
                    && liveEl.ActualHeight >= 4
                    && liveEl.IsDescendantOf(rootPanel))
                {
                    endRect = liveEl.TransformToAncestor(rootPanel).TransformBounds(new Rect(liveEl.RenderSize));
                }
                else if (anchor.ScreenPixelRect.Width >= 4 && anchor.ScreenPixelRect.Height >= 4)
                {
                    var eTl = rootPanel.PointFromScreen(anchor.ScreenPixelRect.TopLeft);
                    var eBr = rootPanel.PointFromScreen(anchor.ScreenPixelRect.BottomRight);
                    endRect = new Rect(
                        Math.Min(eTl.X, eBr.X),
                        Math.Min(eTl.Y, eBr.Y),
                        Math.Max(8, Math.Abs(eBr.X - eTl.X)),
                        Math.Max(8, Math.Abs(eBr.Y - eTl.Y)));
                }
            }

            if (endRect.IsEmpty || endRect.Width < 8 || endRect.Height < 8)
            {
                var cw = Math.Clamp(rootW * 0.22, 220, 340);
                var ch = cw * 9.0 / 16.0;
                endRect = new Rect((rootW - cw) * 0.5, (rootH - ch) * 0.5, cw, ch);
            }

            var baseW = Math.Max(1, Math.Max(startRect.Width, endRect.Width));
            var baseH = Math.Max(1, Math.Max(startRect.Height, endRect.Height));

            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform(0, 0);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);
            var clip = new RectangleGeometry(new Rect(0, 0, baseW, baseH));

            var hero = new Border
            {
                Width = baseW,
                Height = baseH,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = heroBrush ?? Brushes.Black,
                RenderTransformOrigin = new Point(0, 0),
                RenderTransform = group,
                Clip = clip,
            };
            RenderOptions.SetBitmapScalingMode(hero, BitmapScalingMode.Linear);

            var backdrop = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x52, 0x0A, 0x0C, 0x10)),
                Opacity = 1.0,
            };

            var overlay = new Grid
            {
                ClipToBounds = true,
                IsHitTestVisible = false,
            };
            Panel.SetZIndex(overlay, 9999);
            if (rootPanel is Grid grid)
            {
                Grid.SetRowSpan(overlay, Math.Max(1, grid.RowDefinitions.Count));
                Grid.SetColumnSpan(overlay, Math.Max(1, grid.ColumnDefinitions.Count));
            }
            overlay.Children.Add(backdrop);
            overlay.Children.Add(hero);
            rootPanel.Children.Add(overlay);

            _returnOverlay = overlay;
            _returnHost = rootPanel;

            double progress = 0;
            double velocity = 0;
            long lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            TimeSpan lastRenderTime = TimeSpan.MinValue;

            void ApplyFrame(double p)
            {
                var rect = SpringMotion.InterpolateRect(startRect, endRect, p);
                var sx = rect.Width / baseW;
                var sy = rect.Height / baseH;
                scale.ScaleX = sx;
                scale.ScaleY = sy;
                translate.X = rect.X;
                translate.Y = rect.Y;

                var radius = SpringMotion.InterpolateRadius(startRadius, endRadius, p);
                var invScale = Math.Max(0.05, Math.Min(sx, sy));
                clip.RadiusX = clip.RadiusY = radius / invScale;

                backdrop.Opacity = 1.0 - SpringMotion.StageProgress(p, 0.0, 0.85);
                hero.Opacity = 1.0 - SpringMotion.StageProgress(p, 0.86, 1.0) * 0.25;
            }

            ApplyFrame(0);

            _returnTick = (_, e) =>
            {
                if (!targetWindow.IsVisible || targetWindow.WindowState == WindowState.Minimized)
                {
                    StopReturnMorph();
                    return;
                }
                if (e is RenderingEventArgs frame)
                {
                    if (lastRenderTime == frame.RenderingTime) return;
                    lastRenderTime = frame.RenderingTime;
                }
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var dt = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(lastTicks, now).TotalSeconds, 0.001, 0.05);
                lastTicks = now;

                (progress, velocity) = SpringMotion.Advance(progress, velocity, 1.0, dt, SpringMotion.HyperOmegaInterrupt);
                ApplyFrame(progress);

                if (Math.Abs(progress - 1.0) < 0.008 && Math.Abs(velocity) < 0.12)
                {
                    StopReturnMorph();
                    PlayReturnToSourceCard(bvid);
                }
            };
            CompositionTarget.Rendering += _returnTick;
            return true;
        }
        catch
        {
            StopReturnMorph();
            return false;
        }
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
