using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliTouch.Services;

namespace BiliTouch.Helpers;

/// <summary>A themed image with its existing control retained as the fallback.</summary>
public sealed class SkinGlyph : Decorator
{
    public string Kind {get;set;}="Refresh";
    public double IconSize {get;set;}=20;
    public static readonly DependencyProperty StateProperty=DependencyProperty.Register(nameof(State),typeof(string),typeof(SkinGlyph),new FrameworkPropertyMetadata("",FrameworkPropertyMetadataOptions.AffectsRender));
    public string State{get=>(string)GetValue(StateProperty);set=>SetValue(StateProperty,value);}
    public Brush? Tint {get;set;}
    public SkinGlyph()
    {
        Loaded+=(_,_)=>{SkinService.Instance.Changed+=Refresh;Svc.Theme.ThemeChanged+=Refresh;Refresh();};
        Unloaded+=(_,_)=>{SkinService.Instance.Changed-=Refresh;Svc.Theme.ThemeChanged-=Refresh;};
    }
    private void Refresh(){if(Child!=null)Child.SetCurrentValue(OpacityProperty,SkinService.Instance.IsPhantomBlade?0d:1d);InvalidateMeasure();InvalidateVisual();}
    protected override Size MeasureOverride(Size available){var size=base.MeasureOverride(available);return SkinService.Instance.IsPhantomBlade?new Size(IconSize,IconSize):size;}
    protected override void OnRender(DrawingContext dc)
    {
        if(!SkinService.Instance.IsPhantomBlade)return;
        var tint=Tint??PhantomUiArtwork.Ink;
        PhantomUiArtwork.Draw(dc,PhantomUiArtwork.Asset(Kind,Svc.Theme.IsDark),new Rect(RenderSize),tint);
        if(Kind=="Volume" && State==PlayerGlyphs.VolumeGlyph(true))
            dc.DrawLine(new Pen(tint,1.8),new Point(1,1),new Point(ActualWidth-1,ActualHeight-1));
    }
}
