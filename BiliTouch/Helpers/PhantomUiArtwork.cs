using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliTouch.Services;

namespace BiliTouch.Helpers;

/// <summary>Original game pixels, fitted by alpha bounds; tinting uses their existing alpha mask.</summary>
public static class PhantomUiArtwork
{
    private static readonly Dictionary<string,Rect> Bounds = new();
    private static readonly Dictionary<bool,BitmapSource> InactiveFlames = new();
    public static string Asset(string role, bool dark, bool active=false, int count=0, bool compact=false) => role switch
    {
        "Like" => compact ? "ui_2114" : active ? "ui_0397" : "ui_0395",
        "Coin" => compact ? "ui_1325" : count>=2 ? "ui_1335" : count==1 ? "ui_1336" : "ui_1327",
        "Favorite" => compact ? "ui_2184" : dark ? "ui_1334" : "ui_1333",
        "Home" => dark ? "ui_2208" : "ui_2206",
        "Dynamic" => dark ? "ui_2211" : "ui_2212",
        "Settings" => dark ? "ui_2213" : "ui_2214",
        "Mine" => "ui_2684", "Refresh" => "ui_1229", "Left" => "ui_1273", "Right" => "ui_1275",
        "Pause" => "ui_1316", "Puzzle" => "ui_1420", "Profile" => "ui_1490",
        "Back" => "ui_2030", "Quality" => "ui_2031", "DanmakuSettings" => "ui_2032",
        "Finished" => "ui_2193", "Volume" => "ui_gramophone", _ => "play"
    };
    public static Rect VisibleBounds(string asset)
    {
        if(Bounds.TryGetValue(asset,out var rect))return rect;
        var image=SkinService.Instance.Image(asset);
        var pixels=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
        var bytes=new byte[pixels.PixelWidth*pixels.PixelHeight*4];pixels.CopyPixels(bytes,pixels.PixelWidth*4,0);
        int left=pixels.PixelWidth,top=pixels.PixelHeight,right=-1,bottom=-1;
        for(int y=0;y<pixels.PixelHeight;y++)for(int x=0;x<pixels.PixelWidth;x++)
            if(bytes[(y*pixels.PixelWidth+x)*4+3]>4){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
        return Bounds[asset]=right<left?new Rect(0,0,image.PixelWidth,image.PixelHeight):new Rect(left,top,right-left+1,bottom-top+1);
    }
    public static void Draw(DrawingContext dc,string asset,Rect area,Brush? tint=null)
        => DrawBitmap(dc,asset,SkinService.Instance.Image(asset),area,tint);
    private static void DrawBitmap(DrawingContext dc,string asset,BitmapSource image,Rect area,Brush? tint=null)
    {
        if(area.Width<=0||area.Height<=0)return;
        var bounds=VisibleBounds(asset);
        var scale=Math.Min(area.Width/bounds.Width,area.Height/bounds.Height);
        var target=new Rect(area.X+(area.Width-bounds.Width*scale)/2,area.Y+(area.Height-bounds.Height*scale)/2,bounds.Width*scale,bounds.Height*scale);
        var brush=new ImageBrush(image){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=bounds,Stretch=Stretch.Fill};
        if(tint==null)dc.DrawRectangle(brush,null,target);
        else {brush.ViewportUnits=BrushMappingMode.Absolute;brush.Viewport=target;dc.PushOpacityMask(brush);dc.DrawRectangle(tint,null,target);dc.Pop();}
    }
    private static BitmapSource InactiveFlame(bool dark)
    {
        if(InactiveFlames.TryGetValue(dark,out var cached))return cached;
        var source=SkinService.Instance.Image("ui_0395");
        var image=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        var stride=image.PixelWidth*4;var pixels=new byte[stride*image.PixelHeight];image.CopyPixels(pixels,stride,0);
        // The original smoke is near 214 and the flame near 255. Keep both layers,
        // expand their contrast for each background, and retain every original alpha value.
        for(int i=0;i<pixels.Length;i+=4)
        {
            var luminance=.0722*pixels[i]+.7152*pixels[i+1]+.2126*pixels[i+2];
            var level=Math.Clamp((luminance-212)/43,0,1);
            var tone=(byte)Math.Round(dark?164+86*level:184-148*level);
            pixels[i]=pixels[i+1]=pixels[i+2]=tone;
        }
        var result=BitmapSource.Create(image.PixelWidth,image.PixelHeight,source.DpiX,source.DpiY,PixelFormats.Bgra32,null,pixels,stride);
        result.Freeze();return InactiveFlames[dark]=result;
    }
    public static Brush Ink => Svc.Theme.IsDark?Brushes.White:new SolidColorBrush(Color.FromRgb(35,34,32));
    public static Brush Blue {get;}=Freeze(65,145,245);
    public static Brush Purple {get;}=Freeze(178,110,239);
    public static Brush Gold {get;}=Freeze(200,155,62);
    private static Brush Freeze(byte r,byte g,byte b){var brush=new SolidColorBrush(Color.FromRgb(r,g,b));brush.Freeze();return brush;}
    public static void DrawAction(DrawingContext dc,string kind,Rect area,bool active,int count,bool compact)
    {
        var asset=Asset(kind,Svc.Theme.IsDark,active,count,compact);Brush? tint=null;
        if(kind=="Like" && !compact && !active)
        {
            DrawBitmap(dc,asset,InactiveFlame(Svc.Theme.IsDark),area);return;
        }
        if(kind=="Like" && compact)tint=active?Purple:Ink;
        if(kind=="Coin" && (compact||count==0))tint=count>=2?null:count==1?Gold:Ink;
        if(kind=="Favorite")tint=active?Blue:Ink;
        if(kind=="Puzzle")tint=Ink;
        Draw(dc,asset,area,tint);
    }
}
