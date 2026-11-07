using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliTouch.Services;

namespace BiliTouch.Helpers;

/// <summary>Original woven/foil UI materials on chrome; ink paper belongs under content.</summary>
internal static class PhantomBladeSurface
{
    private static readonly Dictionary<string,Brush> Brushes=new();
    private static Brush Tile(string name,double opacity,Rect crop,Size tile)
    {
        var key=$"{name}/{opacity}/{crop}/{tile}";
        if(Brushes.TryGetValue(key,out var cached))return cached;
        BitmapSource source=SkinService.Instance.Image(name);
        // The official cloth stores a uniform 12% alpha. Keep its original RGB
        // fibres and set the overall opacity here rather than attenuating twice.
        if(name=="weave"){source=new FormatConvertedBitmap(source,PixelFormats.Bgr32,null,0);source.Freeze();}
        var brush=new ImageBrush(source){
            Viewbox=crop,ViewboxUnits=BrushMappingMode.RelativeToBoundingBox,
            Viewport=new Rect(new Point(),tile),ViewportUnits=BrushMappingMode.Absolute,
            TileMode=TileMode.FlipXY,Stretch=Stretch.Fill,Opacity=opacity};
        brush.Freeze();Brushes[key]=brush;return brush;
    }

    internal static void DrawContent(DrawingContext dc,Size size,bool card)
    {
        var bounds=new Rect(size);bool dark=Svc.Theme.IsDark;
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawRectangle(Tile("paper",dark?(card?.14:.23):(card?.34:.60),new Rect(.06,.06,.88,.88),new Size(760,610)),null,bounds);
        if(!card){
            dc.PushOpacity(dark?.46:.18);
            dc.DrawImage(SkinService.Instance.Image("brush"),new Rect(0,Math.Max(0,size.Height-170),size.Width,170));dc.Pop();
        }
        dc.Pop();
    }

    private static void Divider(DrawingContext dc,Size size,bool rail)
    {
        var length=rail?size.Height:size.Width;
        if(rail)dc.PushTransform(new MatrixTransform(0,1,-1,0,size.Width,0));
        var y=rail?0:size.Height-3;
        var mask=new ImageBrush(SkinService.Instance.Image("divider")){
            Viewbox=new Rect(0,.30,1,.40),ViewboxUnits=BrushMappingMode.RelativeToBoundingBox,
            Viewport=new Rect(0,y,length,3),ViewportUnits=BrushMappingMode.Absolute,Stretch=Stretch.Fill};
        dc.PushOpacityMask(mask);
        dc.DrawRectangle(new SolidColorBrush(Svc.Theme.IsDark?Color.FromArgb(210,220,219,209):Color.FromArgb(180,96,77,48)),null,new Rect(0,y,length,3));
        dc.Pop();if(rail)dc.Pop();
    }

    internal static void Draw(DrawingContext dc,Size size,string kind)
    {
        bool dark=Svc.Theme.IsDark,rail=kind=="Rail";
        var bounds=new Rect(size);
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawRectangle(new SolidColorBrush(dark?Color.FromRgb(26,29,28):Color.FromRgb(230,225,212)),null,bounds);
        dc.DrawRectangle(Tile("weave",dark?.92:.22,new Rect(.06,.20,.88,.60),new Size(310,78)),null,bounds);
        dc.DrawRectangle(Tile("foil",dark?(rail?.13:.07):(rail?.12:.07),new Rect(.03,.08,.94,.84),new Size(350,56)),null,bounds);
        Divider(dc,size,rail);
        dc.Pop();
    }
}
