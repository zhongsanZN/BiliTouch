using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliTouch.Models;
using BiliTouch.Helpers;
using BiliTouch.Views.Controls;
using BiliTouch.ViewModels;

namespace BiliTouch.Views.Pages;

public partial class MinePage : UserControl
{
    public Func<PageKey,FrameworkElement>? SectionFactory {get;set;}
    private readonly Dictionary<int,FrameworkElement> _sections=new();
    private readonly PageKey[] _keys={PageKey.Mine,PageKey.History,PageKey.Favorites,PageKey.WatchLater,PageKey.Cache};
    internal int CurrentTabIndex {get;private set;}
    private readonly TranslateTransform _slide=new();
    private Point _start;
    private long _started;
    private bool _armed,_horizontal,_decided,_moving,_suppressClick;
    private int _pendingWheel;
    private double _dx;
    private VideoCard? _gestureCard;
    private ScrollViewer? _frozen;
    private PanningMode _panning;
    private double _frozenOffset;
    public MinePage()
    {
        InitializeComponent();SectionHost.RenderTransform=_slide;
        OverviewTab.IsChecked=true;
        SectionHost.PreviewTouchDown+=(_,e)=>SwipeDown(e.GetTouchPoint(null).Position,e.OriginalSource as DependencyObject);
        SectionHost.PreviewTouchMove+=(_,e)=>{if(SwipeMove(e.GetTouchPoint(null).Position))e.Handled=true;};
        SectionHost.PreviewTouchUp+=(_,e)=>{if(SwipeUp())e.Handled=true;};
        SectionHost.PreviewMouseLeftButtonUp+=(_,e)=>{if(_suppressClick){e.Handled=true;_suppressClick=false;}};
        Unloaded+=(_,_)=>{RestorePanning();_armed=false;};
    }
    private RadioButton[] Tabs=>new[]{OverviewTab,HistoryTab,FavoritesTab,LaterTab,CacheTab};
    private FrameworkElement CreateSection(int index)
    {
        FrameworkElement page=index==0?new MineOverviewPage():SectionFactory?.Invoke(_keys[index]) ?? index switch
        {
            1=>new HistoryPage {DataContext=new HistoryViewModel()},
            2=>new FavoritesPage {DataContext=new FavoritesViewModel()},
            3=>new WatchLaterPage {DataContext=new WatchLaterViewModel()},
            _=>new CachePage {DataContext=new CacheViewModel()}
        };
        if(index==0 && page.FindName("PageHeader") is FrameworkElement header)header.Visibility=Visibility.Collapsed;
        else if(page is ContentControl {Content:Grid layout})
        {
            var toolbar=layout.Children.OfType<Grid>().FirstOrDefault(g=>Grid.GetRow(g)==0);
            if(toolbar!=null){toolbar.Margin=new Thickness(36,4,36,6);toolbar.MinHeight=Math.Max(40,toolbar.MinHeight);}
        }
        return page;
    }
    private void OnTabChecked(object sender,RoutedEventArgs e)
    {
        if(SectionHost==null || sender is not RadioButton tab || !int.TryParse(tab.Tag?.ToString(),out var index))return;
        _armed=false;RestorePanning();_slide.BeginAnimation(TranslateTransform.XProperty,null);_slide.X=0;_moving=false;
        CurrentTabIndex=index;_pendingWheel=0;
        if(!_sections.TryGetValue(index,out var page))
        {
            page=CreateSection(index);_sections[index]=page;SectionHost.Children.Add(page);
        }
        foreach(var item in _sections)item.Value.Visibility=item.Key==index?Visibility.Visible:Visibility.Collapsed;
    }
    public void SelectSection(PageKey key)
    {
        var index=Array.IndexOf(_keys,key);if(index>=0)Tabs[index].IsChecked=true;
    }
    internal void SwipeDown(Point point,DependencyObject? source)
    {
        _gestureCard=source==null?null:VisualTreeUtil.FindAncestor<VideoCard>(source);
        _suppressClick=false;_start=point;_started=Environment.TickCount64;_dx=0;_decided=false;_horizontal=false;
        _armed=!_moving;
        for(var d=source;d!=null && d!=SectionHost;d=d is ContentElement c?ContentOperations.GetParent(c) ?? (c as FrameworkContentElement)?.Parent:VisualTreeHelper.GetParent(d))
            if(d is ButtonBase or TextBoxBase or Slider or Hyperlink){_armed=false;break;}
    }
    internal bool SwipeMove(Point point)
    {
        if(!_armed)return false;
        _dx=point.X-_start.X;var dy=point.Y-_start.Y;
        if(!_decided)
        {
            if(Math.Max(Math.Abs(_dx),Math.Abs(dy))<12)return false;
            _decided=true;_horizontal=Math.Abs(_dx)>Math.Abs(dy)*1.4;
        }
        if(!_horizontal)return false;
        TouchSupport.CancelPendingTap();_gestureCard?.CancelGestureTap();
        if(_frozen==null && _sections.TryGetValue(CurrentTabIndex,out var page))
        {
            _frozen=FindScroll(page);if(_frozen!=null){_panning=_frozen.PanningMode;_frozenOffset=_frozen.VerticalOffset;_frozen.PanningMode=PanningMode.None;BiliTouch.Helpers.TrackpadScroll.Cancel(_frozen);}
        }
        if(_frozen!=null && Math.Abs(_frozen.VerticalOffset-_frozenOffset)>0.5)_frozen.ScrollToVerticalOffset(_frozenOffset);
        var edge=CurrentTabIndex==0 && _dx>0 || CurrentTabIndex==4 && _dx<0;
        _slide.X=_dx*(edge?0.35:1);return true;
    }
    internal bool SwipeUp()
    {
        if(!_armed)return false;_armed=false;RestorePanning();
        if(!_horizontal)return false;
        _gestureCard?.FinishGestureTap();_gestureCard=null;
        _suppressClick=true;
        var velocity=Math.Abs(_dx)/Math.Max(1,Environment.TickCount64-_started);
        var change=Math.Abs(_dx)>Math.Max(1,SectionHost.ActualWidth)*0.18 || Math.Abs(_dx)>24 && velocity>0.35;
        Snap(change?Math.Clamp(CurrentTabIndex+(_dx<0?1:-1),0,4):CurrentTabIndex);
        return true;
    }
    internal void CancelPendingSwipe(){_armed=false;RestorePanning();}
    private void RestorePanning(){if(_frozen!=null)_frozen.PanningMode=_panning;_frozen=null;}
    internal void HandleHorizontalWheel(int direction){if(direction==0)return;if(_moving){_pendingWheel=Math.Sign(direction);return;}Snap(Math.Clamp(CurrentTabIndex+Math.Sign(direction),0,4));}
    private void Snap(int target)
    {
        if(_moving)return;_moving=true;
        var animation=new DoubleAnimation(target==CurrentTabIndex?0:(target>CurrentTabIndex?-1:1)*Math.Max(1,SectionHost.ActualWidth),TimeSpan.FromMilliseconds(160)){EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut}};
        animation.Completed+=(_,_)=>{_slide.BeginAnimation(TranslateTransform.XProperty,null);_slide.X=0;var pending=_pendingWheel;_pendingWheel=0;Tabs[target].IsChecked=true;_moving=false;if(pending!=0)HandleHorizontalWheel(pending);};
        _slide.BeginAnimation(TranslateTransform.XProperty,animation);
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if(root is FrameworkElement fe && !fe.IsVisible)return null;
        if(root is ScrollViewer sv)return sv;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var found=FindScroll(VisualTreeHelper.GetChild(root,i));if(found!=null)return found;}
        return null;
    }
}
