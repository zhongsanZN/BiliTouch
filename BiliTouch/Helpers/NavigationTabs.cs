using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace BiliTouch.Helpers;
/// <summary>Shared home-style labels with one sliding short underline.</summary>
public sealed class NavigationTabs:Grid {
 private readonly Border _indicator=new(){Width=20,Height=3,CornerRadius=new CornerRadius(1.5),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,5),IsHitTestVisible=false};
 private readonly TranslateTransform _position=new();private bool _placed;
 public NavigationTabs(){
  _indicator.RenderTransform=_position;_indicator.SetResourceReference(Border.BackgroundProperty,"AccentBrush");Panel.SetZIndex(_indicator,10);Children.Add(_indicator);
  Loaded+=(_,_)=>{var buttons=Buttons().ToArray();for(int i=0;i<buttons.Length;i++){buttons[i].SetResourceReference(StyleProperty,"UnderlineTabStyle");buttons[i].Margin=new Thickness(i==0?0:4,0,0,0);}Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>Place(false)));};
  AddHandler(ToggleButton.CheckedEvent,new RoutedEventHandler((_,_)=>Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>Place(true)))));
  SizeChanged+=(_,_)=>Place(false);
 }
 private IEnumerable<RadioButton> Buttons(){foreach(var child in Children.OfType<Panel>())foreach(var button in child.Children.OfType<RadioButton>())yield return button;}
 private void Place(bool animate){var button=Buttons().FirstOrDefault(b=>b.IsChecked==true);if(button==null || !button.IsLoaded)return;var x=button.TranslatePoint(new Point(),this).X+(button.ActualWidth-20)/2;
  if(animate && _placed)_position.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(_position.X,x,TimeSpan.FromMilliseconds(180)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
  else{_position.BeginAnimation(TranslateTransform.XProperty,null);_position.X=x;}_placed=true;
 }
}
