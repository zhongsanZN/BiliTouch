using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliTouch.Helpers;
using BiliTouch.Services;

namespace BiliTouch.Views.Controls;

public enum ThemeReaction { Like, Favorite, Coin }

/// <summary>One non-interactive, bounded reaction per player; no video HWND overlap or permanent timer.</summary>
public sealed class ThemeReactionHost : FrameworkElement
{
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(ThemeReactionHost), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private FrameworkElement? _anchor;
    private Rect _anchorBounds, _spriteBounds;
    private ThemeReaction _action;
    private long _lastStarted = long.MinValue / 2;
    public bool IsPlaying => _anchor != null;
    public Rect SpriteBounds => _spriteBounds;

    /// <summary>
    /// 上一次动效**开始**的时刻(<see cref="Environment.TickCount64"/>)。
    ///
    /// ★ 为什么需要暴露它: 动效本身只有 740ms, 而"点赞请求回来 + 按钮重新可用"这段时间
    ///   取决于网络/夹具, 探针里最长能到一秒多 —— 等断言跑到的时候动效早已自然收场。
    ///   只断言 <see cref="IsPlaying"/> 会得到"有时过、有时挂"的假失败(实测 8 次挂 2 次)。
    ///   正确的判据是"这一下点赞确实触发过动效": IsPlaying 或者在最近一段时间内开始过。
    /// </summary>
    public long LastStartedTick => _lastStarted;

    /// <summary>
    /// 上一次动效**用过**的精灵区域(动效结束/被清掉之后仍然保留)。
    ///
    /// ★ 与 <see cref="SpriteBounds"/> 的区别: 后者是"现在正在画在哪", 动效一收场就变 Empty。
    ///   而"动效有没有越出信息栏"这件事必须在动效结束后仍然可查 ——
    ///   否则同一时刻网络快慢就能决定断言过不过(探针实测: 十条里挂五条)。
    ///   这个值只在 Show 成功时写入, 不随 Clear 归零。
    /// </summary>
    public Rect LastSpriteBounds { get; private set; } = Rect.Empty;

    /// <summary>
    /// 锚点位置漂移的容差(设备无关像素)。
    ///
    /// ★★★ 2026-11-05 修"点赞动效时有时无"(探针 --star-party 三次里挂一次):
    ///   原来 OnLayout 用 <c>Bounds(_anchor) != _anchorBounds</c> **精确相等**判定
    ///   "按钮是不是被布局挪走了"。而 Rect 的相等比较是逐 double 的 ——
    ///   右栏一改布局(标题换行、滚动条出现、字体度量差一点点), 按钮的实际位置
    ///   会落在 20.8888… 与 20.8889… 这种**肉眼完全一致**的差异上。
    ///   于是"刚弹出来的动效"被下一秒的布局回调当成"位置变了"而立刻清掉,
    ///   用户看到的就是"点赞有时有动效、有时没有"。
    ///   ⇒ 位置比较必须有容差。半像素是这类"同一位置、两种算法"的合适分界。
    /// </summary>
    private const double AnchorDriftTolerance = 0.5;

    public ThemeReactionHost()
    {
        IsHitTestVisible = false; Focusable = false; ClipToBounds = true;
        Loaded += (_, _) => { SkinService.Instance.Changed += Clear; Svc.Theme.ThemeChanged += Clear; LayoutUpdated += OnLayout; };
        Unloaded += (_, _) => { SkinService.Instance.Changed -= Clear; Svc.Theme.ThemeChanged -= Clear; LayoutUpdated -= OnLayout; Clear(); };
        SizeChanged += (_, _) => Clear();
    }

    private Rect Bounds(FrameworkElement anchor) => anchor.TransformToVisual(this).TransformBounds(new Rect(anchor.RenderSize));

    /// <summary>
    /// 布局回调: 锚点按钮动了, 动效应该**跟着走**, 而不是取消。
    ///
    /// ★★★ 2026-11-05 修"点赞动效时有时无"(探针 --star-party 八次里挂两次):
    ///   原来这里只要发现按钮位置和开始时记的不一样就 <c>Clear()</c>。而点赞成功的同一拍,
    ///   赞数文本会 +1(宽度变化) → 整行重新测量 → 按钮真的挪了 1~2px →
    ///   刚弹出来的动效被当场清掉。用户看到的就是"点了个赞, 人没出来"。
    ///   ⇒ 语义应该是"动效属于这个按钮", 按钮挪到哪儿动效跟到哪儿。
    ///     只有两种情况下才该清: 按钮不可见了, 或者宿主小到放不下。
    ///
    /// ★ 位置没变(在 AnchorDriftTolerance 内)时什么都不做: LayoutUpdated 每帧都在响,
    ///   无脑重算 Rect 是白花开销, 也会让 InvalidateVisual 变成每帧一次。
    /// </summary>
    private void OnLayout(object? sender, EventArgs args)
    {
        if (_anchor == null) return;
        try
        {
            if (!_anchor.IsVisible || ActualWidth < 100 || ActualHeight < 90) { Clear(); return; }
            var bounds = Bounds(_anchor);
            if (SamePlacment(bounds, _anchorBounds)) return;
            _anchorBounds = bounds;
            RecomputeSpriteBounds(bounds);
            InvalidateVisual();
        }
        catch (InvalidOperationException) { Clear(); }
    }

    /// <summary>由锚点矩形算出"小人"应该画在哪(与 Show 里同一套算法, 抽出来保证跟随时不漂)</summary>
    private void RecomputeSpriteBounds(Rect bounds)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var x = Math.Clamp(bounds.X + bounds.Width / 2 - 44, 4, ActualWidth - 92);
        var y = bounds.Top >= 80 ? bounds.Top - 72 : bounds.Bottom + 8;
        if (y + 74 > ActualHeight) { Clear(); return; }
        _spriteBounds = new Rect(x, y, 88, 66);
        LastSpriteBounds = _spriteBounds;
    }

    private static bool SamePlacment(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) <= AnchorDriftTolerance
        && Math.Abs(a.Y - b.Y) <= AnchorDriftTolerance
        && Math.Abs(a.Width - b.Width) <= AnchorDriftTolerance
        && Math.Abs(a.Height - b.Height) <= AnchorDriftTolerance;

    public bool Show(ThemeReaction action, FrameworkElement anchor)
    {
        // Material themes illuminate the existing icon; only character themes spawn sprites.
        if (SkinService.Instance.IsPhantomBlade) return false;
        if (!SkinService.Instance.EffectsEnabled || !IsLoaded || !anchor.IsVisible ||
            !SystemParameters.ClientAreaAnimation || Environment.TickCount64 - _lastStarted < 900) return false;
        Rect bounds;
        try
        {
            bounds = Bounds(anchor);
            // Clip against all scroll viewports, including the web layout's relocated header.
            for (DependencyObject? parent = VisualTreeHelper.GetParent(anchor); parent != null && parent != this; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollContentPresenter viewport && !viewport.TransformToVisual(this).TransformBounds(new Rect(viewport.RenderSize)).Contains(bounds)) return false;
        }
        catch (InvalidOperationException) { return false; }
        if (!new Rect(RenderSize).Contains(bounds) || ActualWidth < 100 || ActualHeight < 90) return false;
        Clear(); _action = action; _anchor = anchor; _anchorBounds = bounds;
        // Briefly rising past the title is intentional; the effect stays inside the information pane.
        RecomputeSpriteBounds(bounds);
        if (!IsPlaying) return false;
        _lastStarted = Environment.TickCount64;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(740)) { FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => Clear();
        BeginAnimation(ProgressProperty, animation);
        return true;
    }

    public void Clear()
    {
        BeginAnimation(ProgressProperty, null); _anchor = null; _spriteBounds = Rect.Empty; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_anchor == null) return;
        var progress = (double)GetValue(ProgressProperty);
        double enter = Math.Min(1, progress / .20), leave = Math.Clamp((1 - progress) / .30, 0, 1);
        double scale = .78 + .22 * (1 - Math.Pow(1 - enter, 3)) + .07 * Math.Sin(enter * Math.PI);
        var area = _spriteBounds;
        string name = _action switch { ThemeReaction.Like => "myself", ThemeReaction.Favorite => "channel", _ => "main" };
        dc.PushOpacity(enter * leave);
        dc.PushTransform(new TranslateTransform(0, 12 * (1 - enter) - 7 * (1 - leave)));
        dc.PushTransform(new ScaleTransform(scale, scale, area.X + area.Width / 2, area.Bottom));
        double pose = Math.Clamp((progress - .12) / .18, 0, 1);
        dc.PushOpacity(1 - pose); dc.DrawImage(SkinService.Instance.Image("tail_icon_" + name), area); dc.Pop();
        dc.PushOpacity(pose); dc.DrawImage(SkinService.Instance.Image("tail_icon_selected_" + name), area); dc.Pop();
        dc.Pop(); dc.Pop(); dc.Pop();
    }
}
