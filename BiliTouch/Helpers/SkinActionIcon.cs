using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
namespace BiliTouch.Helpers;
/// <summary>Shared semantic action and playback artwork with native geometry fallback.</summary>
public sealed class SkinActionIcon : Shape
{
 public static readonly DependencyProperty DataProperty=DependencyProperty.Register(nameof(Data),typeof(Geometry),typeof(SkinActionIcon),new FrameworkPropertyMetadata(Geometry.Empty,FrameworkPropertyMetadataOptions.AffectsRender|FrameworkPropertyMetadataOptions.AffectsMeasure,(d,_)=>((SkinActionIcon)d).Refresh()));
 public Geometry Data{get=>(Geometry)GetValue(DataProperty);set=>SetValue(DataProperty,value);}
 public static readonly DependencyProperty KindProperty=DependencyProperty.Register(nameof(Kind),typeof(string),typeof(SkinActionIcon),new FrameworkPropertyMetadata("Like",FrameworkPropertyMetadataOptions.AffectsRender));
 public string Kind{get=>(string)GetValue(KindProperty);set=>SetValue(KindProperty,value);}
 public static readonly DependencyProperty CountProperty=DependencyProperty.Register(nameof(Count),typeof(int),typeof(SkinActionIcon),new FrameworkPropertyMetadata(0,FrameworkPropertyMetadataOptions.AffectsRender));
 public int Count{get=>(int)GetValue(CountProperty);set=>SetValue(CountProperty,value);}
 public static readonly DependencyProperty IsActiveProperty=DependencyProperty.Register(nameof(IsActive),typeof(bool),typeof(SkinActionIcon),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender));
 public bool IsActive{get=>(bool)GetValue(IsActiveProperty);set=>SetValue(IsActiveProperty,value);}
 private static readonly Geometry ArtworkGeometry=CreateArtworkGeometry();
 private static Geometry CreateArtworkGeometry(){var geometry=new RectangleGeometry(new Rect(0,0,1,1));geometry.Freeze();return geometry;}
 protected override Geometry DefiningGeometry=>Kind=="Play" && ThemedAsset!=null?ArtworkGeometry:Kind=="Puzzle" && Data==Geometry.Empty?Geometry.Parse("M0,0 H8 V8 H0 Z M12,0 H20 V8 H12 Z M0,12 H8 V20 H0 Z M12,12 H20 V20 H12 Z"):Data;
 private bool _hasPlaybackSize;
 private double _baseWidth,_baseHeight;
 private Button? _playButton;
 public SkinActionIcon(){Loaded+=(_,_)=>{SkinService.Instance.Changed+=Refresh;Svc.Theme.ThemeChanged+=Refresh;Refresh();};Unloaded+=(_,_)=>{SkinService.Instance.Changed-=Refresh;Svc.Theme.ThemeChanged-=Refresh;if(_playButton!=null)_playButton.SizeChanged-=OnPlayButtonSize;_playButton=null;ClearGlow();};IsVisibleChanged+=(_,_)=>UpdatePlaybackSize();}
 private void OnPlayButtonSize(object sender,SizeChangedEventArgs e)=>UpdatePlaybackSize();
 private void Refresh(){ClearGlow();UpdatePlaybackSize();InvalidateMeasure();InvalidateVisual();}
 private void UpdatePlaybackSize()
 {
  if(Kind!="Play" || !IsLoaded)return;
  Button? button=null;
  for(DependencyObject? p=VisualTreeHelper.GetParent(this);p!=null;p=VisualTreeHelper.GetParent(p))if(p is Button b){button=b;break;}
  if(button==null)return; // Playback counts on cards keep their compact size.
  if(!ReferenceEquals(_playButton,button)){if(_playButton!=null)_playButton.SizeChanged-=OnPlayButtonSize;_playButton=button;button.SizeChanged+=OnPlayButtonSize;}
  if(!_hasPlaybackSize){_baseWidth=Width;_baseHeight=Height;_hasPlaybackSize=true;}
  var size=Math.Clamp(Math.Min(button.ActualWidth,button.ActualHeight)-2,14,40);
  SetCurrentValue(WidthProperty,ThemedAsset!=null?size:_baseWidth);
  SetCurrentValue(HeightProperty,ThemedAsset!=null?size:_baseHeight);
 }
 private void ClearGlow(){if(Effect is DropShadowEffect glow)glow.BeginAnimation(DropShadowEffect.OpacityProperty,null);Effect=null;}
 public bool PulseGlow()
 {
  if(!SkinService.Instance.IsPhantomBlade || !SkinService.Instance.EffectsEnabled || !IsLoaded || !SystemParameters.ClientAreaAnimation)return false;
  var color=Kind switch {"Coin"=>Color.FromRgb(96,235,165),"Favorite"=>Color.FromRgb(91,156,255),_=>Color.FromRgb(244,184,104)};
  ClearGlow();var glow=new DropShadowEffect{Color=color,ShadowDepth=0,BlurRadius=14,Opacity=0,RenderingBias=RenderingBias.Performance};Effect=glow;
  var pulse=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromMilliseconds(760),FillBehavior=FillBehavior.Stop};
  pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0,KeyTime.FromPercent(0)));
  pulse.KeyFrames.Add(new EasingDoubleKeyFrame(.95,KeyTime.FromPercent(.18)){EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}});
  pulse.KeyFrames.Add(new LinearDoubleKeyFrame(.5,KeyTime.FromPercent(.55)));
  pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0,KeyTime.FromPercent(1)));
  pulse.Completed+=(_,_)=>{if(ReferenceEquals(Effect,glow))Effect=null;};
  glow.BeginAnimation(DropShadowEffect.OpacityProperty,pulse);return true;
 }
 private bool IsPlayGlyph()
 {
  if (ReferenceEquals(Data, PlayerGlyphs.PlayGeometry)) return true;
  if (ReferenceEquals(Data, PlayerGlyphs.PauseGeometry)) return false;
  // Support callers supplying an equivalent custom geometry.
  return Data?.ToString() == PlayerGlyphs.PlayGeometry.ToString();
 }
 public bool IsCompact => (double.IsFinite(Width)?Width:ActualWidth)<=22;
 public string? ThemedAsset => SkinService.Instance.IsPhantomBlade ? Kind switch {
  "Favorite" or "Coin" or "Like" or "Puzzle"=>PhantomUiArtwork.Asset(Kind,Svc.Theme.IsDark,IsActive,Count,IsCompact),
  "Play"=>IsPlayGlyph()?"play":"ui_1316", _=>null
 } : null;
 protected override void OnRender(DrawingContext dc)
 {
  if(Kind=="Play"){
   if(ThemedAsset is {} play){if(play=="play")SkinService.Instance.DrawArtwork(dc,play,new Rect(RenderSize));else PhantomUiArtwork.Draw(dc,play,new Rect(RenderSize),Fill);}else base.OnRender(dc);
   return;
  }
  if(!SkinService.Instance.IsEnabled){base.OnRender(dc);return;}
  if(SkinService.Instance.IsPhantomBlade)
  {
   if(ThemedAsset!=null)PhantomUiArtwork.DrawAction(dc,Kind,new Rect(RenderSize),IsActive,Count,IsCompact);else base.OnRender(dc);
   return;
  }
  var name=Kind switch{"Coin"=>Count>1?"action_coins":"action_coin","Favorite"=>"action_favorite","Puzzle"=>"action_puzzle",_=>"action_like"};
  var bitmap=SkinService.Instance.Image(name);var scale=Math.Min(ActualWidth/bitmap.PixelWidth,ActualHeight/bitmap.PixelHeight);
  var w=bitmap.PixelWidth*scale;var h=bitmap.PixelHeight*scale;dc.DrawImage(bitmap,new Rect((ActualWidth-w)/2,(ActualHeight-h)/2,w,h));
 }
}
