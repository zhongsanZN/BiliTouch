using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliTouch.Services;

namespace BiliTouch.Helpers;

/// <summary>Decoration settings are independent of the user's saved accent and color mode.</summary>
public sealed class SkinService : INotifyPropertyChanged
{
    public const string StarParty = "StarParty";
    public const string PhantomBlade = "PhantomBladeZero";
    public static SkinService Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    private readonly Dictionary<string, BitmapSource> _images = new();
    public static bool IsKnown(string id) => id is StarParty or PhantomBlade;
    public bool IsEnabled => IsKnown(Svc.Settings.SkinId);
    public bool IsStarParty => Svc.Settings.SkinId == StarParty;
    public bool IsPhantomBlade => Svc.Settings.SkinId == PhantomBlade;
    public bool EffectsEnabled => IsEnabled && Svc.Settings.SkinEffects;
    public Color EffectiveAccent(Color saved) => IsEnabled && Svc.Settings.SkinUseAccent
        ? (IsPhantomBlade ? Color.FromRgb(0xB6, 0x80, 0x50) : Color.FromRgb(0xFF, 0x26, 0x82)) : saved;

    public void Configure(string id, bool effects, bool useAccent)
    {
        Svc.Settings.SkinId = IsKnown(id) ? id : "";
        Svc.Settings.SkinEffects = effects;
        Svc.Settings.SkinUseAccent = useAccent;
        Svc.Settings.Save();
        Svc.Theme.RefreshDecoration();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        Changed?.Invoke();
    }

    public BitmapSource Image(string name)
    {
        var folder = IsPhantomBlade ? PhantomBlade : StarParty;
        var key = folder + "/" + name;
        if (_images.TryGetValue(key, out var image)) return image;
        var bitmap = new BitmapImage(new Uri($"pack://application:,,,/BiliTouch;component/Assets/Skins/{folder}/{name}.png"));
        bitmap.Freeze();
        _images[key] = bitmap;
        return bitmap;
    }

    /// <summary>Fit original art by its visible bounds without rewriting the supplied pixels.</summary>
    public void DrawArtwork(DrawingContext dc, string name, Rect area)
    {
        var bitmap=Image(name);
        var crop=name switch {
            "memory_flower"=>new Rect(27,27,465,680),
            "jade"=>new Rect(15,15,378,278),
            "like_off"=>new Rect(13,13,280,324),
            "like_on"=>new Rect(15,15,291,364),
            "play"=>new Rect(6,14,101,96),
            _=>new Rect(0,0,bitmap.PixelWidth,bitmap.PixelHeight)
        };
        var scale=Math.Min(area.Width/crop.Width,area.Height/crop.Height);
        var width=crop.Width*scale;var height=crop.Height*scale;
        var normalized=new Rect(crop.X/bitmap.PixelWidth,crop.Y/bitmap.PixelHeight,crop.Width/bitmap.PixelWidth,crop.Height/bitmap.PixelHeight);
        dc.DrawRectangle(new ImageBrush(bitmap){Viewbox=normalized,ViewboxUnits=BrushMappingMode.RelativeToBoundingBox,Stretch=Stretch.Fill},null,
            new Rect(area.X+(area.Width-width)/2,area.Y+(area.Height-height)/2,width,height));
    }

    // Pixel sample excludes the yellow rim and transparent padding. FlipXY makes each
    // boundary continuous without stretching, repainting or duplicating the wordmark.
    public Brush Pattern(double opacity = 1, double phaseX = 0)
    {
        var brush = new ImageBrush(Image("tail_bg"))
        {
            Viewbox = new Rect(0.08, 0.24, 0.80, 0.70), ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(-phaseX, 0, 460, 81), ViewportUnits = BrushMappingMode.Absolute,
            TileMode = TileMode.FlipXY, Stretch = Stretch.Fill, Opacity = opacity
        };
        brush.Freeze();
        return brush;
    }
}
