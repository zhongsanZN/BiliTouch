using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Views;

internal static partial class StarPartyTests
{
 private static void CheckSelectedUi(Action<bool,string> check,string artifacts,MainWindow main,PlayerWindow player)
 {
  var oldMode=Svc.Theme.Mode;
  var roles=new[]{"Home","Dynamic","Settings","Mine","Refresh","Left","Right","Pause","Puzzle","Profile","Back","Quality","DanmakuSettings","Finished","Volume"};
  foreach(var role in roles)
   check(SkinService.Instance.Image(PhantomUiArtwork.Asset(role,Svc.Theme.IsDark)).IsFrozen,"selected original UI artwork loads: "+role);
  check(Descendants<SkinGlyph>(main).Where(g=>g.Kind is "Home" or "Dynamic" or "Settings" or "Mine").Count()==4,"all four sidebar entries use their designated game icons");
  var fav=(SkinActionIcon)player.FindName("ThemedFavoriteIcon");
  Set(player,"_isFavorited",true);Call(player,"UpdateActionCounts");check(fav.IsActive,"favorite success updates the displayed flower state");
  Set(player,"_isFavorited",false);Call(player,"UpdateActionCounts");check(!fav.IsActive,"removing favorite restores the inactive flower");
  for(int coin=0;coin<=2;coin++)
   check(new SkinActionIcon{Kind="Coin",Width=36,Count=coin}.ThemedAsset==new[]{"ui_1327","ui_1336","ui_1335"}[coin],"full bell distinguishes coin count "+coin);
  var panel=new StackPanel{Margin=new Thickness(24)};
  var host=new Window{Content=panel,Width=1000,Height=600,ShowActivated=false};host.Show();
  try
  {
   foreach(var mode in new[]{AppTheme.Light,AppTheme.Dark})
   {
    Svc.Theme.SetMode(mode);panel.SetResourceReference(Panel.BackgroundProperty,"AppBackgroundBrush");panel.Children.Clear();
    var icons=new WrapPanel();panel.Children.Add(new TextBlock{Text="影之刃零 · 指定界面图标 / "+mode,Margin=new Thickness(0,0,0,20),FontSize=20});panel.Children.Add(icons);
    foreach(var role in roles)
    {
     var box=new StackPanel{Width=105,Height=92};box.Children.Add(new SkinGlyph{Kind=role,IconSize=42,HorizontalAlignment=HorizontalAlignment.Center});box.Children.Add(new TextBlock{Text=role,FontSize=11,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,8,0,0)});icons.Children.Add(box);
    }
    foreach(var size in new[]{36d,20d})
    {
     var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,18,0,0)};panel.Children.Add(row);
     foreach(var spec in new[]{("Like",false,0),("Like",true,0),("Coin",false,0),("Coin",true,1),("Coin",true,2),("Favorite",false,0),("Favorite",true,0)})
     {
      var action=new SkinActionIcon{Kind=spec.Item1,IsActive=spec.Item2,Count=spec.Item3,Width=size,Height=size,Data=PlayerGlyphs.PlayGeometry,Margin=new Thickness(18,0,18,0)};row.Children.Add(action);
     }
    }
    var strip=new StackPanel{Orientation=Orientation.Horizontal,Background=Brushes.Black,Margin=new Thickness(0,20,0,0)};panel.Children.Add(strip);
    foreach(var role in new[]{"Volume","Volume","Quality","DanmakuSettings"})
     strip.Children.Add(new SkinGlyph{Kind=role,State=strip.Children.Count==1?PlayerGlyphs.VolumeGlyph(true):"",Tint=Brushes.White,IconSize=24,Margin=new Thickness(18,8,18,8)});
    Pump(100);Save(host,Path.Combine(artifacts,$"phantom-selected-icons-{mode}.png"),1.5);
    var activeFlower=new SkinActionIcon{Kind="Favorite",IsActive=true,Width=36,Height=36};activeFlower.Measure(new Size(36,36));activeFlower.Arrange(new Rect(0,0,36,36));activeFlower.UpdateLayout();
    var image=new RenderTargetBitmap(36,36,96,96,PixelFormats.Pbgra32);image.Render(activeFlower);var pixels=new byte[36*36*4];image.CopyPixels(pixels,36*4,0);
    check(Enumerable.Range(0,36*36).Any(i=>pixels[i*4]>pixels[i*4+2]+20 && pixels[i*4+3]>60),"active flower renders blue on "+mode);
    var inactiveLike=new SkinActionIcon{Kind="Like",Width=36,Height=36};inactiveLike.Measure(new Size(36,36));inactiveLike.Arrange(new Rect(0,0,36,36));inactiveLike.UpdateLayout();
    var flame=new RenderTargetBitmap(36,36,96,96,PixelFormats.Pbgra32);flame.Render(inactiveLike);flame.CopyPixels(pixels,36*4,0);
    var tones=Enumerable.Range(0,36*36).Where(i=>pixels[i*4+3]>240).Select(i=>(int)pixels[i*4]).ToArray();
    check(tones.Length>10 && tones.Max()-tones.Min()>70,"inactive flame retains distinct internal tones rather than a solid silhouette on "+mode);
    if(mode==AppTheme.Dark)
    {
     static double Linear(double channel)=>channel<=.04045?channel/12.92:Math.Pow((channel+.055)/1.055,2.4);
     var background=((SolidColorBrush)Application.Current.FindResource("CardBackgroundBrush")).Color;
     var backgroundLuminance=.2126*Linear(background.R/255d)+.7152*Linear(background.G/255d)+.0722*Linear(background.B/255d);
     var edgeLuminance=Linear(tones.Min()/255d);
     check((edgeLuminance+.05)/(backgroundLuminance+.05)>=3,"the dimmest opaque inactive-flame detail remains distinct against the dark card background");
    }
   }
  }
  finally{host.Close();Svc.Theme.SetMode(oldMode);}
 }
}
