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
    public static SkinService Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    private readonly Dictionary<string, BitmapSource> _images = new();
    public bool IsEnabled => Svc.Settings.SkinId == StarParty;
    public bool EffectsEnabled => IsEnabled && Svc.Settings.SkinEffects;
    public Color EffectiveAccent(Color saved) => IsEnabled && Svc.Settings.SkinUseAccent
        ? Color.FromRgb(0xFF, 0x26, 0x82) : saved;

    public void Configure(string id, bool effects, bool useAccent)
    {
        Svc.Settings.SkinId = id == StarParty ? StarParty : "";
        Svc.Settings.SkinEffects = effects;
        Svc.Settings.SkinUseAccent = useAccent;
        Svc.Settings.Save();
        Svc.Theme.RefreshDecoration();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        Changed?.Invoke();
    }

    public BitmapSource Image(string name)
    {
        if (_images.TryGetValue(name, out var image)) return image;
        var bitmap = new BitmapImage(new Uri($"pack://application:,,,/BiliTouch;component/Assets/Skins/StarParty/{name}.png"));
        bitmap.Freeze();
        _images[name] = bitmap;
        return bitmap;
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
