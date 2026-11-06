using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BiliTouch.Helpers;

/// <summary>Shared directional arbitration for module tabs and scrollable content.</summary>
internal sealed class TabNavigationGestures
{
    private readonly FrameworkElement _content;
    private readonly NavigationTabs _tabs;
    private readonly TranslateTransform _slide = new();
    private Point _start;
    private long _started;
    private double _dx;
    private bool _armed, _decided, _horizontal, _suppressClick;
    private ScrollViewer? _scroll;
    private PanningMode _panning;
    private double _offset;
    private readonly System.Windows.Threading.DispatcherTimer _wheelIdle=new(){Interval=TimeSpan.FromMilliseconds(90)};
    private int _wheelAccum;private bool _wheelConsumed;
    private RadioButton[] Buttons => _tabs.Children.OfType<Panel>().SelectMany(p=>p.Children.OfType<RadioButton>()).ToArray();
    internal int SelectedIndex => Array.FindIndex(Buttons,b=>b.IsChecked==true);
    internal TabNavigationGestures(UserControl page,FrameworkElement content,NavigationTabs tabs)
    {
        _content=content;_tabs=tabs;content.RenderTransform=_slide;
        page.AddHandler(UIElement.PreviewTouchDownEvent,new EventHandler<TouchEventArgs>((_,e)=>Begin(e.GetTouchPoint(null).Position,e.OriginalSource as DependencyObject)),true);
        page.AddHandler(UIElement.PreviewTouchMoveEvent,new EventHandler<TouchEventArgs>((_,e)=>{if(Move(e.GetTouchPoint(null).Position))e.Handled=true;}),true);
        page.AddHandler(UIElement.PreviewTouchUpEvent,new EventHandler<TouchEventArgs>((_,e)=>{if(End())e.Handled=true;}),true);
        page.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,new MouseButtonEventHandler((_,e)=>{if(_suppressClick){e.Handled=true;_suppressClick=false;}}),true);
        _wheelIdle.Tick+=(_,_)=>{_wheelIdle.Stop();_wheelAccum=0;_wheelConsumed=false;};
        tabs.PreviewMouseWheel+=(_,e)=>{if(e.Delta==0)return;_wheelIdle.Stop();_wheelIdle.Start();if(Math.Sign(_wheelAccum)!=Math.Sign(e.Delta))_wheelAccum=0;_wheelAccum+=e.Delta;if(!_wheelConsumed && Math.Abs(_wheelAccum)>=120){_wheelConsumed=true;Step(e.Delta<0?1:-1);}e.Handled=true;};
        page.Unloaded+=(_,_)=>Cancel();
        page.IsVisibleChanged+=(_,_)=>{if(!page.IsVisible)Cancel();};
    }
    internal void Step(int direction)
    {
        var buttons=Buttons;var current=SelectedIndex;
        if(current<0 || direction==0)return;
        var next=Math.Clamp(current+Math.Sign(direction),0,buttons.Length-1);
        if(next==current)return;
        Cancel();buttons[next].SetCurrentValue(ToggleButton.IsCheckedProperty,true);
        _slide.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(direction>0?36:-36,0,TimeSpan.FromMilliseconds(180)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
    }
    internal void Begin(Point point,DependencyObject? source)
    {
        Cancel();_suppressClick=false;_start=point;_started=Environment.TickCount64;_armed=true;
        for(var d=source;d!=null;d=Parent(d))
        {
            if(d is ButtonBase or TextBoxBase or Slider or Hyperlink or ComboBox or BiliTouch.Views.Controls.ColorPalette){_armed=false;break;}
            if(_scroll==null && d is ScrollViewer viewer)_scroll=viewer;
        }
        if(_scroll!=null){_panning=_scroll.PanningMode;_offset=_scroll.VerticalOffset;}
    }
    internal bool Move(Point point)
    {
        if(!_armed)return false;
        _dx=point.X-_start.X;var dy=point.Y-_start.Y;
        if(!_decided){if(Math.Max(Math.Abs(_dx),Math.Abs(dy))<12)return false;_decided=true;_horizontal=Math.Abs(_dx)>Math.Abs(dy)*1.4;}
        if(!_horizontal)return false;
        TouchSupport.CancelPendingTap();
        if(_scroll!=null){_scroll.PanningMode=PanningMode.None;SmoothScroll.Stop(_scroll);_scroll.ScrollToVerticalOffset(_offset);}
        var edge=SelectedIndex==0 && _dx>0 || SelectedIndex==Buttons.Length-1 && _dx<0;
        _slide.X=_dx*(edge ? .35 : 1);return true;
    }
    internal bool End()
    {
        if(!_armed)return false;
        var horizontal=_horizontal;var dx=_dx;var elapsed=Math.Max(1,Environment.TickCount64-_started);Cancel();
        if(!horizontal)return false;
        _suppressClick=true;
        if(Math.Abs(dx)>Math.Max(1,_content.ActualWidth)*.18 || Math.Abs(dx)>24 && Math.Abs(dx)/elapsed>.35)Step(dx<0?1:-1);
        else _slide.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(dx,0,TimeSpan.FromMilliseconds(180)));
        return true;
    }
    private static DependencyObject? Parent(DependencyObject d)=>d is ContentElement c?ContentOperations.GetParent(c)??(c as FrameworkContentElement)?.Parent:VisualTreeHelper.GetParent(d);
    private void Cancel(){if(_scroll!=null)_scroll.PanningMode=_panning;_scroll=null;_armed=_decided=_horizontal=false;_dx=0;_slide.BeginAnimation(TranslateTransform.XProperty,null);_slide.X=0;}
}
