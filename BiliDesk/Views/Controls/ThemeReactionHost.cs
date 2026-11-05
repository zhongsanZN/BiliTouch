using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliDesk.Helpers;
using BiliDesk.Services;

namespace BiliDesk.Views.Controls;

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
    public ThemeReactionHost()
    {
        IsHitTestVisible = false; Focusable = false; ClipToBounds = true;
        Loaded += (_, _) => { SkinService.Instance.Changed += Clear; Svc.Theme.ThemeChanged += Clear; LayoutUpdated += OnLayout; };
        Unloaded += (_, _) => { SkinService.Instance.Changed -= Clear; Svc.Theme.ThemeChanged -= Clear; LayoutUpdated -= OnLayout; Clear(); };
        SizeChanged += (_, _) => Clear();
    }
    private Rect Bounds(FrameworkElement anchor) => anchor.TransformToVisual(this).TransformBounds(new Rect(anchor.RenderSize));
    private void OnLayout(object? sender, EventArgs args)
    {
        if (_anchor == null) return;
        try { if (!_anchor.IsVisible || Bounds(_anchor) != _anchorBounds) Clear(); }
        catch (InvalidOperationException) { Clear(); }
    }
    public bool Show(ThemeReaction action, FrameworkElement anchor)
    {
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
        double x = Math.Clamp(bounds.X + bounds.Width / 2 - 44, 4, ActualWidth - 92);
        double y = bounds.Top >= 80 ? bounds.Top - 72 : bounds.Bottom + 8;
        if (y + 74 > ActualHeight) { Clear(); return false; }
        _spriteBounds = new Rect(x, y, 88, 66);
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
