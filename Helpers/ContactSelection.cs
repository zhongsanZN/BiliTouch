using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Views.Controls;
namespace BiliDesk.Helpers;

/// <summary>Contact selection commits on a stationary touch release; native scrolling keeps ownership of drags.</summary>
internal sealed class ContactSelection
{
 public static readonly DependencyProperty IsPressedProperty=DependencyProperty.RegisterAttached("IsPressed",typeof(bool),typeof(ContactSelection),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.Inherits));
 public static bool GetIsPressed(DependencyObject d)=>(bool)d.GetValue(IsPressedProperty);
 public static void SetIsPressed(DependencyObject d,bool value)=>d.SetValue(IsPressedProperty,value);
 public static readonly DependencyProperty SuppressHoverProperty=DependencyProperty.RegisterAttached("SuppressHover",typeof(bool),typeof(ContactSelection),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.Inherits));
 public static bool GetSuppressHover(DependencyObject d)=>(bool)d.GetValue(SuppressHoverProperty);
 public static void SetSuppressHover(DependencyObject d,bool value)=>d.SetValue(SuppressHoverProperty,value);
 private readonly FrameworkElement _root;private readonly Action<object> _select;
 private Point _start;private bool _moved;private TouchDevice? _device;private FrameworkElement? _row;private ScrollViewer? _scroll;private double _offset;private long _lastTouch;
 public static void Install(FrameworkElement root,Action<object> select)=>new ContactSelection(root,select);
 private ContactSelection(FrameworkElement root,Action<object> select)
 {
  _root=root;_select=select;
  root.AddHandler(UIElement.PreviewTouchDownEvent,new EventHandler<TouchEventArgs>(Down),true);
  root.AddHandler(UIElement.PreviewTouchMoveEvent,new EventHandler<TouchEventArgs>(Move),true);
  root.AddHandler(UIElement.PreviewTouchUpEvent,new EventHandler<TouchEventArgs>(Up),true);
  root.PreviewMouseDown+=BlockTouchMouse;root.PreviewMouseUp+=BlockTouchMouse;
  root.PreviewMouseMove+=(_,e)=>{if(_device==null && !TouchSupport.IsTouchMouse(e) && Environment.TickCount64-_lastTouch>400)SetSuppressHover(root,false);};
  root.AddHandler(UIElement.LostTouchCaptureEvent,new EventHandler<TouchEventArgs>((_,e)=>{if(e.TouchDevice==_device && e.TouchDevice.Captured==null){_moved=true;if(_row!=null)SetIsPressed(_row,false);_device=null;_row=null;}}),true);
  root.Unloaded+=(_,_)=>{if(_row!=null)SetIsPressed(_row,false);_device=null;_row=null;};
 }
 private static T? Parent<T>(DependencyObject? d) where T:DependencyObject
 {while(d!=null){if(d is T t)return t;d=d is ContentElement c?ContentOperations.GetParent(c)??(c as FrameworkContentElement)?.Parent:VisualTreeHelper.GetParent(d);}return null;}
 private void Down(object? sender,TouchEventArgs e)
 {
  _lastTouch=Environment.TickCount64;SetSuppressHover(_root,true);
  if(_device!=null){_moved=true;if(_row!=null)SetIsPressed(_row,false);return;}
  _device=e.TouchDevice;_start=e.GetTouchPoint(null).Position;_moved=false;
  _row=Parent<ContactListRow>(e.OriginalSource as DependencyObject);
  if(_row==null){for(var d=e.OriginalSource as DependencyObject;d!=null;d=VisualTreeHelper.GetParent(d))if(d is FrameworkElement {Tag:"AllDynamics"} row){_row=row;break;}}
  _scroll=Parent<ScrollViewer>(_row??e.OriginalSource as DependencyObject);_offset=_scroll?.VerticalOffset??0;if(_row!=null)SetIsPressed(_row,true);
 }
 private void Move(object? sender,TouchEventArgs e)
 {if(e.TouchDevice!=_device)return;_lastTouch=Environment.TickCount64;if((e.GetTouchPoint(null).Position-_start).Length>10){_moved=true;if(_row!=null)SetIsPressed(_row,false);}}
 private void Up(object? sender,TouchEventArgs e)
 {
  if(e.TouchDevice!=_device)return;_lastTouch=Environment.TickCount64;
  var row=_row;var moved=_moved || (e.GetTouchPoint(null).Position-_start).Length>10 || Math.Abs((_scroll?.VerticalOffset??0)-_offset)>1;
  if(row!=null)SetIsPressed(row,false);_device=null;_row=null;
  TouchSupport.CancelPendingTap();
  if(!moved && row?.DataContext is {} item)_root.Dispatcher.BeginInvoke(new Action(()=>_select(item)));
 }
 private void BlockTouchMouse(object sender,MouseButtonEventArgs e)
 {if(TouchSupport.IsTouchMouse(e) || _device!=null || Environment.TickCount64-_lastTouch<400)e.Handled=true;}
}
