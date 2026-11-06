using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.Views.Controls;

/// <summary>Native in-page image gallery, shared by both video comment layouts.</summary>
public sealed class InlineImageGallery:UserControl
{
    private readonly Grid _viewport=new(){ClipToBounds=true,Background=Brushes.Transparent,IsManipulationEnabled=true};
    private readonly Image _image=new(){Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,RenderTransformOrigin=new Point(0.5,0.5)};
    private readonly ScaleTransform _scale=new(1,1);
    private readonly TranslateTransform _pan=new();
    private readonly TextBlock _status=new(){VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,4,0)};
    private readonly TextBlock _hint=new(){HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
    private readonly Button _previous,_next;
    private readonly Button _fitButton,_save;
    private bool _fitted=true;
    private readonly Border _frame;
    private readonly Button _collapse;
    private string[] _urls=Array.Empty<string>();
    private int _index,_generation;
    private Point? _drag;
    private double _naturalWidth,_naturalHeight;
    public event Action? CloseRequested;
    public event Action? ImageReady;
    public double NaturalWidth=>_naturalWidth;
    public double NaturalHeight=>_naturalHeight;
    public bool IsInline {get;private set;}
    public string CurrentUrl=>_urls.Length>0?_urls[_index]:"";
    public double Zoom=>_scale.ScaleX;

    public InlineImageGallery()
    {
        SetResourceReference(FontFamilyProperty,AppTypography.ResourceKey);
        _image.RenderTransform=new TransformGroup{Children={_scale,_pan}};_viewport.Children.Add(_image);_viewport.Children.Add(_hint);
        _previous=Button("‹",()=>Move(-1));_previous.ToolTip="上一张";_previous.HorizontalAlignment=HorizontalAlignment.Left;_previous.VerticalAlignment=VerticalAlignment.Center;
        _next=Button("›",()=>Move(1));_next.ToolTip="下一张";_next.HorizontalAlignment=HorizontalAlignment.Right;_next.VerticalAlignment=VerticalAlignment.Center;
        _collapse=Button("×",()=>CloseRequested?.Invoke());_collapse.ToolTip="关闭图片";_collapse.HorizontalAlignment=HorizontalAlignment.Right;_collapse.VerticalAlignment=VerticalAlignment.Top;
        var save=_save=Button("保存",()=>_=MediaActions.SaveImageAsync(CurrentUrl,"评论图片"));save.HorizontalAlignment=HorizontalAlignment.Right;save.VerticalAlignment=VerticalAlignment.Bottom;
        _fitButton=Button("原尺寸",()=>{if(Zoom>1)ResetView();else{_fitted=!_fitted;ApplyFit();}});_fitButton.ToolTip="切换适应窗口与原始尺寸";_fitButton.HorizontalAlignment=HorizontalAlignment.Left;_fitButton.VerticalAlignment=VerticalAlignment.Bottom;
        _status.HorizontalAlignment=HorizontalAlignment.Center;_status.VerticalAlignment=VerticalAlignment.Bottom;_status.Margin=new Thickness(0,0,0,18);_status.Foreground=Brushes.White;_status.Background=new SolidColorBrush(Color.FromArgb(160,24,24,24));
        foreach(var control in new UIElement[]{_previous,_next,_collapse,save,_fitButton,_status})_viewport.Children.Add(control);
        _frame=new Border{CornerRadius=new CornerRadius(8),Padding=new Thickness(0),Background=Brushes.Transparent,Child=_viewport};Content=_frame;
        _viewport.SizeChanged+=(_,_)=>ResetView();
        _viewport.PreviewMouseWheel+=(_,e)=>{if(IsInline)return;SetZoom(Zoom*(e.Delta>0?1.15:1/1.15));e.Handled=true;};
        _viewport.MouseLeftButtonDown+=(_,e)=>{if(e.StylusDevice!=null || InsideButton(e.OriginalSource as DependencyObject))return;if(e.ClickCount==2)ResetView();else{_drag=e.GetPosition(_viewport);_viewport.CaptureMouse();}e.Handled=true;};
        _viewport.MouseMove+=(_,e)=>{if(_drag is not{} last || e.LeftButton!=MouseButtonState.Pressed)return;var now=e.GetPosition(_viewport);_pan.X+=now.X-last.X;_pan.Y+=now.Y-last.Y;_drag=now;ClampPan();e.Handled=true;};
        _viewport.MouseLeftButtonUp+=(_,e)=>{_drag=null;_viewport.ReleaseMouseCapture();e.Handled=true;};
        _viewport.LostMouseCapture+=(_,_)=>_drag=null;
        _viewport.ManipulationStarting+=(_,e)=>{e.ManipulationContainer=_viewport;e.Mode=ManipulationModes.Scale|ManipulationModes.Translate;e.Handled=true;};
        _viewport.ManipulationDelta+=(_,e)=>{if(e.IsInertial){e.Complete();e.Handled=true;return;}SetZoom(Zoom*(e.DeltaManipulation.Scale.X+e.DeltaManipulation.Scale.Y)/2);_pan.X+=e.DeltaManipulation.Translation.X;_pan.Y+=e.DeltaManipulation.Translation.Y;ClampPan();e.Handled=true;};
        Unloaded+=(_,_)=>{_drag=null;_viewport.ReleaseMouseCapture();};
    }
    private static bool InsideButton(DependencyObject? node){while(node!=null){if(node is Button)return true;node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);}return false;}
    public void SetInline(bool inline)
    {
        IsInline=inline;
    }
    private static Button Button(string label,Action action)
    {
        var button=new Button{Content=new TextBlock{Text=label,Foreground=Brushes.White,FontWeight=FontWeights.SemiBold},MinWidth=44,Height=44,Margin=new Thickness(8),Padding=new Thickness(12,0,12,0),Cursor=Cursors.Hand,Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(190,30,30,30))};
        button.SetResourceReference(StyleProperty,"SubtleButtonStyle");button.Click+=(_,e)=>{e.Handled=true;action();};return button;
    }
    public async Task OpenAsync(IEnumerable<string> urls,int index)
    {
        _urls=urls.Select(UrlUtil.Normalize).Where(u=>u.Length>0).ToArray();
        _index=Math.Clamp(index,0,Math.Max(0,_urls.Length-1));await LoadCurrentAsync();
    }
    private void Move(int direction)
    {var next=_index+direction;if(next<0 || next>=_urls.Length)return;_index=next;_=LoadCurrentAsync();}
    private async Task LoadCurrentAsync()
    {
        var generation=++_generation;_image.Source=null;_naturalWidth=_naturalHeight=0;
        _status.Text=$"{_index+1} / {_urls.Length}";_previous.IsEnabled=_index>0;_next.IsEnabled=_index+1<_urls.Length;
        _previous.Visibility=_next.Visibility=_urls.Length>1?Visibility.Visible:Visibility.Collapsed;
        _hint.Text="正在加载图片…";_hint.Visibility=Visibility.Visible;ResetView();
        var url=CurrentUrl;if(url.Length==0){_hint.Text="没有可显示的图片";return;}
        var image=await CoverLoader.LoadAsync(url,4096);
        if(generation!=_generation)return;
        if(image==null){_hint.Text="图片加载失败";return;}
        _image.Source=image;_naturalWidth=image.Width;_naturalHeight=image.Height;_hint.Visibility=Visibility.Collapsed;ImageReady?.Invoke();ResetView();
    }
    public void Cancel(){_generation++;_image.Source=null;_urls=Array.Empty<string>();}
    public void ResetView()
    {
        _scale.ScaleX=_scale.ScaleY=1;_pan.X=_pan.Y=0;
        if(_naturalWidth<=0 || _naturalHeight<=0 || _viewport.ActualWidth<=0 || _viewport.ActualHeight<=0)return;
        _fitted=true;ApplyFit();
    }
    private void ApplyFit()
    {
        _scale.ScaleX=_scale.ScaleY=1;_pan.X=_pan.Y=0;
        if(_naturalWidth<=0 || _naturalHeight<=0 || _viewport.ActualWidth<=0 || _viewport.ActualHeight<=0)return;
        var fit=_fitted?Math.Min(_viewport.ActualWidth/_naturalWidth,_viewport.ActualHeight/_naturalHeight):1;
        _image.Width=_naturalWidth*fit;_image.Height=_naturalHeight*fit;
        ((TextBlock)_fitButton.Content).Text=_fitted?"原尺寸":"适应窗口";
        PositionControls();
    }
    private void SetZoom(double value){_scale.ScaleX=_scale.ScaleY=Math.Clamp(value,1,8);ClampPan();PositionControls();}
    private void PositionControls(){
        var x=Math.Max(0,(_viewport.ActualWidth-_image.Width*Zoom)/2)+8;var y=Math.Max(0,(_viewport.ActualHeight-_image.Height*Zoom)/2)+8;
        _previous.Margin=new Thickness(x,8,8,8);_next.Margin=new Thickness(8,8,x,8);_collapse.Margin=new Thickness(8,y,x,8);_save.Margin=new Thickness(8,8,x,y);_fitButton.Margin=new Thickness(x,8,8,y);_status.Margin=new Thickness(0,0,0,y+10);
    }
    private void ClampPan()
    {
        var x=Math.Max(0,(_image.ActualWidth*Zoom-_viewport.ActualWidth)/2);var y=Math.Max(0,(_image.ActualHeight*Zoom-_viewport.ActualHeight)/2);
        _pan.X=Math.Clamp(_pan.X,-x,x);_pan.Y=Math.Clamp(_pan.Y,-y,y);
    }
}
