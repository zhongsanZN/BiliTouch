using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliDesk.Services;

namespace BiliDesk.Helpers;

/// <summary>Code-native extension of the original pattern; never participates in input.</summary>
public sealed class SkinSurface : FrameworkElement
{
    public string Kind { get; set; } = "Home";
    public SkinSurface()
    {
        IsHitTestVisible = false; Focusable = false;
        Loaded += (_, _) => { SkinService.Instance.Changed += Refresh; Svc.Theme.ThemeChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => { SkinService.Instance.Changed -= Refresh; Svc.Theme.ThemeChanged -= Refresh; };
    }
    private void Refresh() => InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        if (!SkinService.Instance.IsEnabled || ActualWidth <= 0 || ActualHeight <= 0) return;
        var dark = Svc.Theme.IsDark;
        var bounds = new Rect(RenderSize);
        dc.PushClip(new RectangleGeometry(bounds));
        if (Kind == "Rail")
        {
            dc.DrawRectangle(SkinService.Instance.Pattern(dark ? .09 : .07), null, bounds);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(130, 255, 190, 56)), null,
                new Rect(Math.Max(0, ActualWidth - 2), 0, 2, ActualHeight));
        }
        else
        {
            // Preserve the wordmark on the original LEFT. The entire decoration fades
            // towards the right, where the underlying window material remains visible.
            var fade = new LinearGradientBrush(Colors.White, Colors.Transparent, 0);
            fade.GradientStops.Insert(1, new GradientStop(Color.FromArgb(185, 255, 255, 255), .38));
            dc.PushOpacityMask(fade);
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(54, 29, 47) : Color.FromRgb(255, 232, 243)), null, bounds);
            var original = SkinService.Instance.Image("head_bg");
            double originalWidth = ActualHeight * original.PixelWidth / original.PixelHeight;
            // Exclude the original 27-pixel yellow rim. Draw one continuous rim below,
            // rather than joining a thick source-image stripe to a thinner new stripe.
            originalWidth = ActualHeight * original.PixelWidth / (original.PixelHeight - 27);
            var originalBrush = new ImageBrush(original) { Viewbox = new Rect(0, 27.0 / 264, 1, 237.0 / 264), Stretch = Stretch.Fill };
            var blendStart = Math.Max(0, originalWidth - 100);
            var blendOut = new LinearGradientBrush(Colors.White, Colors.Transparent, new Point(blendStart, 0), new Point(originalWidth, 0)) { MappingMode = BrushMappingMode.Absolute };
            var blendIn = new LinearGradientBrush(Colors.Transparent, Colors.White, new Point(blendStart, 0), new Point(originalWidth, 0)) { MappingMode = BrushMappingMode.Absolute };
            dc.PushOpacity(dark ? .30 : .45);
            dc.PushOpacityMask(blendIn);
            dc.DrawRectangle(SkinService.Instance.Pattern(), null, bounds);
            dc.Pop();
            dc.PushOpacityMask(blendOut);
            dc.DrawRectangle(originalBrush, null, new Rect(0, 0, originalWidth, ActualHeight));
            dc.Pop(); dc.Pop();
            dc.Pop();
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(255, 177, 43), Color.FromRgb(255, 220, 65), 0), null,
                new Rect(0, 0, ActualWidth, 3));
        }
        dc.Pop();
    }
}

/// <summary>Responsive cover artwork with a readable gradient behind profile information.</summary>
public sealed class SkinProfileLayout : Decorator
{
    private bool _narrow;
    private double _artWidth;
    public SkinProfileLayout()
    {
        Loaded += (_, _) => { SkinService.Instance.Changed += Refresh; Svc.Theme.ThemeChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => { SkinService.Instance.Changed -= Refresh; Svc.Theme.ThemeChanged -= Refresh; };
    }
    private void Refresh() { InvalidateMeasure(); InvalidateVisual(); }
    protected override Size MeasureOverride(Size available)
    {
        if (Child == null) return new Size();
        if (!SkinService.Instance.IsEnabled) { Child.Measure(available); return Child.DesiredSize; }
        _narrow = available.Width < 660;
        _artWidth = _narrow ? 0 : available.Width * .40;
        Child.Measure(new Size(Math.Max(0, available.Width - _artWidth), available.Height));
        return new Size(Child.DesiredSize.Width + _artWidth,
            _narrow ? Child.DesiredSize.Height + 190 : Math.Max(210, Child.DesiredSize.Height));
    }
    protected override Size ArrangeOverride(Size final)
    {
        if (Child != null)
        {
            var active = SkinService.Instance.IsEnabled;
            Child.Arrange(active ? (_narrow ? new Rect(0, 190, final.Width, Math.Max(0, final.Height - 190))
                : new Rect(0, 0, Math.Max(0, final.Width - _artWidth - 12), final.Height)) : new Rect(final));
        }
        return final;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (!SkinService.Instance.IsEnabled) return;
        var area = _narrow ? new Rect(0, 0, ActualWidth, 174)
            : new Rect(0, 0, ActualWidth, ActualHeight);
        if (area.Width <= 0 || area.Height <= 0) return;
        dc.PushClip(new RectangleGeometry(area, 12, 12));
        var image = SkinService.Instance.Image("head_myself_bg");
        var art = _narrow ? area : new Rect(ActualWidth * .30, 0, ActualWidth * .70, ActualHeight);
        var ratio = Math.Max(art.Width / image.PixelWidth, art.Height / image.PixelHeight);
        var width = image.PixelWidth * ratio; var height = image.PixelHeight * ratio;
        dc.DrawImage(image, new Rect(art.X + (art.Width - width) / 2, art.Y - (height - art.Height) * .22, width, height));
        if (!_narrow)
        {
            var card = ((SolidColorBrush)FindResource("CardBackgroundBrush")).Color;
            card.A = 255;
            var veil = new LinearGradientBrush();
            veil.GradientStops.Add(new GradientStop(card, 0));
            veil.GradientStops.Add(new GradientStop(card, .36));
            veil.GradientStops.Add(new GradientStop(Color.FromArgb(215, card.R, card.G, card.B), .52));
            veil.GradientStops.Add(new GradientStop(Color.FromArgb(0, card.R, card.G, card.B), .80));
            dc.DrawRectangle(veil, null, area);
        }
        dc.Pop();
    }
}
