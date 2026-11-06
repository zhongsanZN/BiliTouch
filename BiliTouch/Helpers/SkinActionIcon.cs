using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
namespace BiliTouch.Helpers;
/// <summary>Same shape contract as the standard icons, with original Star Party assets when decorated.</summary>
public sealed class SkinActionIcon : Shape
{
 public static readonly DependencyProperty DataProperty=DependencyProperty.Register(nameof(Data),typeof(Geometry),typeof(SkinActionIcon),new FrameworkPropertyMetadata(Geometry.Empty,FrameworkPropertyMetadataOptions.AffectsRender|FrameworkPropertyMetadataOptions.AffectsMeasure));
 public Geometry Data{get=>(Geometry)GetValue(DataProperty);set=>SetValue(DataProperty,value);}
 public static readonly DependencyProperty KindProperty=DependencyProperty.Register(nameof(Kind),typeof(string),typeof(SkinActionIcon),new FrameworkPropertyMetadata("Like",FrameworkPropertyMetadataOptions.AffectsRender));
 public string Kind{get=>(string)GetValue(KindProperty);set=>SetValue(KindProperty,value);}
 public static readonly DependencyProperty CountProperty=DependencyProperty.Register(nameof(Count),typeof(int),typeof(SkinActionIcon),new FrameworkPropertyMetadata(0,FrameworkPropertyMetadataOptions.AffectsRender));
 public int Count{get=>(int)GetValue(CountProperty);set=>SetValue(CountProperty,value);}
 protected override Geometry DefiningGeometry=>Kind=="Puzzle" && Data==Geometry.Empty?Geometry.Parse("M0,0 H8 V8 H0 Z M12,0 H20 V8 H12 Z M0,12 H8 V20 H0 Z M12,12 H20 V20 H12 Z"):Data;
 public SkinActionIcon(){Loaded+=(_,_)=>{SkinService.Instance.Changed+=Refresh;InvalidateVisual();};Unloaded+=(_,_)=>SkinService.Instance.Changed-=Refresh;}
 private void Refresh()=>InvalidateVisual();
 protected override void OnRender(DrawingContext dc)
 {
  if(!SkinService.Instance.IsEnabled){base.OnRender(dc);return;}
  var name=Kind switch{"Coin"=>Count>1?"action_coins":"action_coin","Favorite"=>"action_favorite","Puzzle"=>"action_puzzle",_=>"action_like"};
  var bitmap=SkinService.Instance.Image(name);var scale=Math.Min(ActualWidth/bitmap.PixelWidth,ActualHeight/bitmap.PixelHeight);
  var w=bitmap.PixelWidth*scale;var h=bitmap.PixelHeight*scale;dc.DrawImage(bitmap,new Rect((ActualWidth-w)/2,(ActualHeight-h)/2,w,h));
 }
}
