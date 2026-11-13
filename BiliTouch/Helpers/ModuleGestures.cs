using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliTouch.Views.Controls;

namespace BiliTouch.Helpers;

/// <summary>Module refresh and card selection share cancellation rules for every pointing device.</summary>
internal sealed class ModuleGestures
{
    private readonly UserControl _page;
    private readonly Func<Task> _refresh;
    private readonly Action<object>? _edit;
    private readonly Action<object>? _activate;
    private readonly DispatcherTimer _hold=new() { Interval=TimeSpan.FromMilliseconds(550) };
    private readonly DispatcherTimer _wheelIdle=new() { Interval=TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _fadeHide=new() { Interval=TimeSpan.FromMilliseconds(135) };
    private Point _start;
    private TouchDevice? _touchDevice;
    private bool _touchContact;
    private long _lastTouchAt;
    private bool TouchInProgress=>_touchContact && Environment.TickCount64-_lastTouchAt<1500;
    private double _scrollAtDown;
    /// <summary>下拉触发刷新的阈值(DIP): 比普通轻滑更大，避免误触</summary>
    internal const double PullTriggerDistance = 120;
    private const double PullMaxVisualOffset = 108;
    private const double RefreshRestOffset = 54;
    private const int WheelTriggerThreshold = 420;

    private ScrollViewer? _scroll;
    private UIElement? _pullTarget;
    private readonly TranslateTransform _contentOffset=new();
    private VideoCard? _card;
    private object? _item;
    private bool _down,_pull,_held,_refreshing,_moved;
    private double _distance,_wheel;
    private long _suppressMouseUntil;
    private PanningMode _panning;
    private readonly Func<ScrollViewer?, bool>? _canPull;
    private readonly Border _hint;
    private readonly TextBlock _hintText;
    private readonly LoadingRing _refreshRing;
    private readonly TranslateTransform _hintOffset=new();
    private readonly ScaleTransform _hintScale=new(1,1);
    internal static ModuleGestures Install(
        UserControl page,
        Func<Task> refresh,
        Action<object>? edit=null,
        Action<object>? activate=null,
        Panel? hintHost=null,
        Func<ScrollViewer?, bool>? canPull=null)=>new(page,refresh,edit,activate,hintHost,canPull);

    internal static bool IsDescendantOf(DependencyObject? child, DependencyObject? ancestor)
    {
        while (child != null)
        {
            if (ReferenceEquals(child, ancestor)) return true;
            child = child is ContentElement c
                ? ContentOperations.GetParent(c) ?? (c as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    private ModuleGestures(UserControl page,Func<Task> refresh,Action<object>? edit,Action<object>? activate,Panel? hintHost,Func<ScrollViewer?, bool>? canPull)
    {
        _page=page;_refresh=refresh;_edit=edit;_activate=activate;_canPull=canPull;
        _hintText=new TextBlock { FontSize=13 };
        _hintText.SetResourceReference(TextBlock.ForegroundProperty,"AccentTextBrush");
        _refreshRing=new LoadingRing {Width=24,Height=24,Visibility=Visibility.Collapsed};
        var visual=new Grid();visual.Children.Add(_refreshRing);visual.Children.Add(_hintText);
        var hintGroup=new TransformGroup();hintGroup.Children.Add(_hintScale);hintGroup.Children.Add(_hintOffset);
        _hint=new Border { Child=visual,Padding=new Thickness(10),CornerRadius=new CornerRadius(22),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,8,0,0),Visibility=Visibility.Collapsed,IsHitTestVisible=false,RenderTransformOrigin=new Point(0.5,0.5),RenderTransform=hintGroup };
        _hint.SetResourceReference(Border.BorderBrushProperty,"CardBorderBrush");
        _hint.BorderThickness=new Thickness(1);
        System.Windows.Automation.AutomationProperties.SetName(_hint,"下拉刷新");
        _hint.SetResourceReference(Border.BackgroundProperty,"CardBackgroundBrush");
        var host = hintHost ?? page.Content as Panel;
        if(host != null)
        {
            if(host is Grid grid)
            {
                Grid.SetRowSpan(_hint,Math.Max(1,grid.RowDefinitions.Count));
                Grid.SetColumnSpan(_hint,Math.Max(1,grid.ColumnDefinitions.Count));
            }
            Panel.SetZIndex(_hint,100);
            host.Children.Add(_hint);
        }
        _hold.Tick+=(_,_)=>CompleteHold();
        _wheelIdle.Tick+=async(_,_)=>{_wheelIdle.Stop();var trigger=_wheel>=WheelTriggerThreshold;_wheel=0;if(trigger)await RefreshAsync();else Hint("");};
        _fadeHide.Tick+=(_,_)=>{_fadeHide.Stop();if(!_refreshing && !_down && _wheel==0)_hint.Visibility=Visibility.Collapsed;};
        page.PreviewTouchDown+=(_,e)=>{_touchContact=true;_lastTouchAt=Environment.TickCount64;Begin(e.GetTouchPoint(null).Position,e.OriginalSource as DependencyObject);_touchDevice=e.TouchDevice;};
        page.PreviewTouchMove+=(_,e)=>{_lastTouchAt=Environment.TickCount64;if(Move(e.GetTouchPoint(null).Position)){e.Handled=true;e.TouchDevice.Capture(page);}};
        page.PreviewTouchUp+=async(_,e)=>{_touchContact=false;if(!_down)return;var tap=!_held&&!_pull&&!_moved && _activate!=null?_item:null;var consumed=_held||_pull||tap!=null;var run=End();if(tap!=null)_activate?.Invoke(tap);if(consumed){_suppressMouseUntil=Environment.TickCount64+350;TouchSupport.CancelPendingTap();e.Handled=true;e.TouchDevice.Capture(null);}if(run)await RefreshAsync();};
        page.PreviewMouseLeftButtonDown+=(_,e)=>{if(!TouchInProgress && !TouchSupport.IsTouchMouse(e))Begin(e.GetPosition(null),e.OriginalSource as DependencyObject);};
        page.PreviewMouseMove+=(_,e)=>{if(!TouchInProgress && !TouchSupport.IsTouchMouse(e) && e.LeftButton==MouseButtonState.Pressed && Move(e.GetPosition(null))){e.Handled=true;page.CaptureMouse();}};
        page.PreviewMouseLeftButtonUp+=async(_,e)=>{if(TouchInProgress || TouchSupport.IsTouchMouse(e)){if(Environment.TickCount64<_suppressMouseUntil)e.Handled=true;return;}if(!_down)return;var consumed=_held||_pull;var run=End();if(consumed){e.Handled=true;page.ReleaseMouseCapture();}if(run)await RefreshAsync();};
        page.PreviewMouseWheel+=OnWheel;
        page.PreviewKeyDown+=async(_,e)=>{if(e.Key==Key.R && Keyboard.Modifiers==ModifierKeys.Control){e.Handled=true;await RefreshAsync();}};
        page.Unloaded+=(_,_)=>Reset();
        page.IsVisibleChanged+=(_,_)=>{if(!page.IsVisible)Reset();};
        page.LostMouseCapture+=(_,e)=>{if(ReferenceEquals(e.OriginalSource,page) && !page.IsMouseCaptured && _down&&!_held)Reset();};
    }
    private static T? Ancestor<T>(DependencyObject? source) where T:DependencyObject
    {
        while(source!=null)
        {
            if(source is T t)return t;
            source=source is ContentElement c?ContentOperations.GetParent(c)??(c as FrameworkContentElement)?.Parent:VisualTreeHelper.GetParent(source);
        }
        return null;
    }
    private void Begin(Point point,DependencyObject? source)
    {
        Reset();if(_refreshing || Ancestor<ButtonBase>(source)!=null || Ancestor<TextBoxBase>(source)!=null || Ancestor<Slider>(source)!=null)return;
        _start=point;_down=true;_moved=false;_scroll=Ancestor<ScrollViewer>(source);
        if(_scroll!=null && _canPull!=null && !_canPull(_scroll))_scroll=null;
        _card=Ancestor<VideoCard>(source);
        _scrollAtDown=_scroll?.VerticalOffset??0;
        _item=_card?.DataContext ?? (source as FrameworkElement)?.DataContext;
        if(_edit!=null && _item!=null && (_card!=null || Ancestor<ListBoxItem>(source)!=null))_hold.Start();
    }
    internal bool Move(Point point)
    {
        if(!_down || _held)return _held;
        var dx=point.X-_start.X;var dy=point.Y-_start.Y;
        if(Math.Max(Math.Abs(dx),Math.Abs(dy))>10){_hold.Stop();_moved=true;}
        if(!_pull)
        {
            if(Math.Abs(dx)>12 && Math.Abs(dx)>Math.Abs(dy)){Reset();return false;}
            if(dy<14 || Math.Abs(dy)<Math.Abs(dx)*1.4 || _scroll==null || _scroll.VerticalOffset>0.5)return false;
            _pull=true;_panning=_scroll.PanningMode;_scroll.PanningMode=PanningMode.None;TrackpadScroll.Cancel(_scroll);
            AttachPullTarget(_scroll);
            _card?.CancelGestureTap();TouchSupport.CancelPendingTap();
        }
        _distance=Math.Max(0,dy);
        var visualOffset=ComputeRubberBandOffset(_distance);
        ApplyPullOffsetImmediate(visualOffset);
        Hint(_distance>=PullTriggerDistance?"松开刷新":"下拉刷新");
        return true;
    }

    private static double ComputeRubberBandOffset(double dy)
        => dy<=0 ? 0 : PullMaxVisualOffset*(1.0-Math.Exp(-dy/165.0));

    private void AttachPullTarget(ScrollViewer scroll)
    {
        var candidate = scroll.Content as UIElement ?? scroll;
        if (ReferenceEquals(_pullTarget, candidate)) return;
        DetachPullTarget();
        if (candidate.RenderTransform == null || ReferenceEquals(candidate.RenderTransform, Transform.Identity) || ReferenceEquals(candidate.RenderTransform, _contentOffset))
        {
            _contentOffset.BeginAnimation(TranslateTransform.YProperty, null);
            _contentOffset.Y = 0;
            candidate.RenderTransform = _contentOffset;
            _pullTarget = candidate;
        }
        else if (candidate.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            _contentOffset.BeginAnimation(TranslateTransform.YProperty, null);
            _contentOffset.Y = 0;
            if (!group.Children.Contains(_contentOffset)) group.Children.Add(_contentOffset);
            _pullTarget = candidate;
        }
    }

    private void DetachPullTarget()
    {
        if (_pullTarget == null) return;
        _contentOffset.BeginAnimation(TranslateTransform.YProperty, null);
        _contentOffset.Y = 0;
        _pullTarget = null;
    }

    private void ApplyPullOffsetImmediate(double y)
    {
        _contentOffset.BeginAnimation(TranslateTransform.YProperty, null);
        _contentOffset.Y = y;
    }

    private void AnimatePullOffset(double toY, int durationMs = 240)
    {
        var anim = new DoubleAnimation(_contentOffset.Y, toY, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _contentOffset.BeginAnimation(TranslateTransform.YProperty, anim);
    }
    private void CompleteHold()
    {
        _hold.Stop();if(!_down || _pull || _item==null || _edit==null)return;
        if(_touchDevice!=null && (_touchDevice.GetTouchPoint(null).Position-_start).Length>10)return;
        if(_scroll!=null && Math.Abs(_scroll.VerticalOffset-_scrollAtDown)>1)return;
        _held=true;Ancestor<BiliTouch.Views.Pages.MinePage>(_page)?.CancelPendingSwipe();_card?.CancelGestureTap();TouchSupport.CancelPendingTap();_edit(_item);Hint("已选择，轻点其他卡片可多选");
    }
    private bool End()
    {
        var run=_pull && _distance>=PullTriggerDistance;
        if(_pull||_held)_card?.FinishGestureTap();
        Reset(animateBack: !run);
        if(run && _pullTarget != null) AnimatePullOffset(RefreshRestOffset, 180);
        return run;
    }
    private void Reset(bool animateBack = false)
    {
        _hold.Stop();_wheelIdle.Stop();_wheel=0;
        if(_pull && _scroll!=null)_scroll.PanningMode=_panning;
        if(_pullTarget != null)
        {
            if(animateBack && _contentOffset.Y > 0.5) AnimatePullOffset(0, 220);
            else if(!_refreshing) ApplyPullOffsetImmediate(0);
        }
        _down=_pull=_held=false;_distance=0;_scroll=null;_card=null;_item=null;_touchDevice=null;if(!_refreshing)Hint("");
    }
    private void OnWheel(object sender,MouseWheelEventArgs e)
    {
        if(_refreshing || _down)return;
        var scroll=Ancestor<ScrollViewer>(e.OriginalSource as DependencyObject);
        if(scroll==null || (_canPull!=null && !_canPull(scroll)) || scroll.VerticalOffset>0.5 || e.Delta<=0){_wheel=0;_wheelIdle.Stop();Hint("");return;}
        _wheel+=e.Delta;Hint(_wheel>=WheelTriggerThreshold?"松开刷新":"继续上滚刷新");_wheelIdle.Stop();_wheelIdle.Start();
    }
    private void Hint(string text)
    {
        _fadeHide.Stop();
        _hint.BeginAnimation(UIElement.OpacityProperty,null);
        var spinner=text.Length>0 && (_refreshing || _pull || _wheel>0);
        _hintText.Text=spinner?"":text;_hintText.Visibility=spinner?Visibility.Collapsed:Visibility.Visible;
        _refreshRing.Visibility=spinner?Visibility.Visible:Visibility.Collapsed;
        _hint.Padding=spinner?new Thickness(10):new Thickness(18,10,18,10);
        var visualOffset=_pull && !_refreshing?ComputeRubberBandOffset(_distance):0;
        _hintOffset.Y=_pull && !_refreshing?Math.Clamp(visualOffset*0.52-16,-24,42):8;
        var ready=_pull && _distance>=PullTriggerDistance;
        _hintScale.ScaleX=_hintScale.ScaleY=ready?1.08:1.0;
        _hint.Opacity=_pull && !_refreshing?Math.Clamp(_distance/(PullTriggerDistance*0.75),0.2,1):1;
        _hint.Visibility=text.Length==0?Visibility.Collapsed:Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(_hint,text);
    }
    internal async Task RefreshAsync()
    {
        if(_refreshing)return;_refreshing=true;Hint("正在刷新…");
        if(_pullTarget != null && _contentOffset.Y < RefreshRestOffset - 1) AnimatePullOffset(RefreshRestOffset, 180);
        try{await _refresh();}catch(Exception ex){await _page.Dispatcher.InvokeAsync(()=>Svc.Toast.Show("刷新失败："+ex.Message));}
        finally
        {
            await _page.Dispatcher.InvokeAsync(()=>
            {
                _refreshing=false;
                if(_pullTarget != null) AnimatePullOffset(0, 240);
                _fadeHide.Stop();
                _fadeHide.Start();
                var fade=new DoubleAnimation(_hint.Opacity,0,TimeSpan.FromMilliseconds(120));
                fade.Completed+=(_,_)=>{_fadeHide.Stop();if(!_refreshing && !_down && _wheel==0)_hint.Visibility=Visibility.Collapsed;};
                _hint.BeginAnimation(UIElement.OpacityProperty,fade);
            });
        }
    }
}
