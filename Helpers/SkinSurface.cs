using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliTouch.Services;

namespace BiliTouch.Helpers;

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
        var foundation = dark ? Color.FromRgb(88,37,63) : Color.FromRgb(255,220,237);
        if (Kind == "Rail")
        {
            var baseColor = ((SolidColorBrush)FindResource("SideBarBackgroundBrush")).Color;
            var baseFill = new LinearGradientBrush();
            baseFill.MappingMode = BrushMappingMode.Absolute;
            baseFill.StartPoint = new Point(0,0); baseFill.EndPoint = new Point(0,Math.Max(240,ActualHeight));
            baseFill.GradientStops.Add(new GradientStop(foundation,0));
            baseFill.GradientStops.Add(new GradientStop(foundation,Math.Min(.95,112/Math.Max(240,ActualHeight))));
            baseFill.GradientStops.Add(new GradientStop(baseColor,1));
            dc.DrawRectangle(baseFill,null,bounds);
            dc.DrawRectangle(SkinService.Instance.Pattern(dark ? .14 : .12),null,bounds);
        }
        else
        {
            var fade = new LinearGradientBrush(Colors.White,Colors.Transparent,0);
            fade.GradientStops.Insert(1,new GradientStop(Colors.White,.15));
            dc.PushOpacityMask(fade);
            dc.DrawRectangle(new SolidColorBrush(foundation),null,bounds);
            dc.DrawRectangle(SkinService.Instance.Pattern(dark ? .14 : .12),null,bounds);
            // Keep the original wordmark subdued; its left edge fades in from the rail seam.
            var art = SkinService.Instance.Image("head_bg");
            var width = ActualHeight * art.PixelWidth / (art.PixelHeight-27);
            var brush = new ImageBrush(art) { Viewbox = new Rect(0,27d/264,1,237d/264), Stretch=Stretch.Fill };
            var mask = new LinearGradientBrush();
            mask.GradientStops.Add(new GradientStop(Colors.Transparent,0));
            mask.GradientStops.Add(new GradientStop(Colors.White,.14));
            mask.GradientStops.Add(new GradientStop(Colors.White,.65));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent,1));
            dc.PushOpacity(dark ? .16 : .22); dc.PushOpacityMask(mask);
            dc.DrawRectangle(brush,null,new Rect(0,0,width,ActualHeight));
            dc.Pop(); dc.Pop(); dc.Pop();
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
        _narrow = available.Width < 760;
        _artWidth = _narrow ? 0 : available.Width * .52;
        Child.Measure(new Size(Math.Max(0, available.Width - _artWidth), available.Height));
        return new Size(Child.DesiredSize.Width + _artWidth,
            _narrow ? Child.DesiredSize.Height + 260 : Math.Max(260, Child.DesiredSize.Height));
    }
    protected override Size ArrangeOverride(Size final)
    {
        if (Child != null)
        {
            var active = SkinService.Instance.IsEnabled;
            Child.Arrange(active ? (_narrow ? new Rect(0, 260, final.Width, Math.Max(0, final.Height - 260))
                : new Rect(0, 0, Math.Max(0, final.Width - _artWidth - 12), final.Height)) : new Rect(final));
        }
        return final;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (!SkinService.Instance.IsEnabled) return;
        var area = _narrow ? new Rect(0, 0, ActualWidth, 260)
            : new Rect(0, 0, ActualWidth, ActualHeight);
        if (area.Width <= 0 || area.Height <= 0) return;
        dc.PushClip(new RectangleGeometry(area, 12, 12));
        var image = SkinService.Instance.Image("head_myself_bg");
        // Reserve the complete width of the two faces, cropping only the lower body.
        var art = _narrow ? area : new Rect(ActualWidth - _artWidth,0,_artWidth,ActualHeight);
        dc.PushClip(new RectangleGeometry(art));
        var ratio = Math.Max(art.Width/image.PixelWidth, art.Height/(image.PixelHeight*.62));
        var width = image.PixelWidth*ratio; var height = image.PixelHeight*ratio;
        dc.DrawImage(image,new Rect(art.X+(art.Width-width)/2,art.Y+art.Height*.5-height*.36,width,height));
        dc.Pop();
        if (!_narrow)
        {
            var card = ((SolidColorBrush)FindResource("CardBackgroundBrush")).Color; card.A=255;
            var edge = new LinearGradientBrush(card,Color.FromArgb(0,card.R,card.G,card.B),0);
            dc.DrawRectangle(edge,null,new Rect(art.X-1,0,64,ActualHeight));
        }
        dc.Pop();
    }
}
