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
    private FrameworkElement? _peerSection;
    public MinePage()
    {
        InitializeComponent();SectionHost.RenderTransform=_slide;
        OverviewTab.IsChecked=true;
        SectionHost.PreviewTouchDown+=(_,e)=>SwipeDown(e.GetTouchPoint(null).Position,e.OriginalSource as DependencyObject);
        SectionHost.PreviewTouchMove+=(_,e)=>{if(SwipeMove(e.GetTouchPoint(null).Position))e.Handled=true;};
        SectionHost.PreviewTouchUp+=(_,e)=>{if(SwipeUp())e.Handled=true;};
        SectionHost.PreviewMouseLeftButtonUp+=(_,e)=>{if(_suppressClick){e.Handled=true;_suppressClick=false;}};
        Unloaded+=(_,_)=>{RestorePanning();ClearPeerSection();_armed=false;};
    }
    private RadioButton[] Tabs=>new[]{OverviewTab,HistoryTab,FavoritesTab,LaterTab,CacheTab};
    private static TranslateTransform PeerTransform(UIElement element)
    {
        if(element.RenderTransform is TranslateTransform t)return t;
        t=new TranslateTransform();element.RenderTransform=t;return t;
    }
    private FrameworkElement EnsureSection(int index)
    {
        if(!_sections.TryGetValue(index,out var page))
        {
            page=CreateSection(index);_sections[index]=page;SectionHost.Children.Add(page);
        }
        return page;
    }
    private void EnsurePeerSectionVisible(int targetIndex,double offsetX,bool createIfMissing=true)
    {
        if(targetIndex is <0 or >4 || targetIndex==CurrentTabIndex){ClearPeerSection();return;}
        if(!_sections.TryGetValue(targetIndex,out var peer))
        {
            if(!createIfMissing)return;
            peer=EnsureSection(targetIndex);
        }
        if(!ReferenceEquals(_peerSection,peer))
        {
            ClearPeerSection();
            _peerSection=peer;
            peer.Visibility=Visibility.Visible;
        }
        PeerTransform(peer).X=offsetX;
    }
    private void ClearPeerSection()
    {
        if(_peerSection==null)return;
        var peer=_peerSection;_peerSection=null;
        PeerTransform(peer).X=0;
        if(!_sections.TryGetValue(CurrentTabIndex,out var active) || !ReferenceEquals(active,peer))
            peer.Visibility=Visibility.Collapsed;
    }
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
        CurrentTabIndex=index;_pendingWheel=0;_peerSection=null;
        EnsureSection(index);
        foreach(var item in _sections){PeerTransform(item.Value).X=0;item.Value.Visibility=item.Key==index?Visibility.Visible:Visibility.Collapsed;}
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
            if(d is TextBoxBase or Slider or PasswordBox or Selector or Hyperlink){_armed=false;break;}
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
        var width=Math.Max(1,SectionHost.ActualWidth);
        var target=CurrentTabIndex+(_dx<0?1:-1);
        var edge=target is <0 or >4;
        if(!edge)EnsurePeerSectionVisible(target,_dx<0?width:-width);
        else ClearPeerSection();
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
    internal void CancelPendingSwipe(){_armed=false;RestorePanning();ClearPeerSection();}
    private void RestorePanning(){if(_frozen!=null)_frozen.PanningMode=_panning;_frozen=null;}
    internal void PreviewHorizontalWheel(double progress)
    {
        if(_moving || _horizontal)return;
        var width=Math.Max(1,SectionHost.ActualWidth);
        var dx=-progress*Math.Min(width*0.38,220);
        _dx=dx;
        var target=CurrentTabIndex+(dx<0?1:-1);
        var edge=target is <0 or >4;
        if(!edge)EnsurePeerSectionVisible(target,dx<0?width:-width);
        else ClearPeerSection();
        _slide.X=dx*(edge?0.35:1);
    }
    internal void SettleHorizontalWheelPreview(bool commit)
    {
        if(_moving || _horizontal)return;
        if(Math.Abs(_slide.X)<0.5 && _peerSection==null)return;
        var target=Math.Clamp(CurrentTabIndex+(_dx<0?1:-1),0,4);
        Snap(commit?target:CurrentTabIndex);
    }
    internal void HandleHorizontalWheel(int direction){if(direction==0)return;if(_moving){_pendingWheel=Math.Sign(direction);return;}Snap(Math.Clamp(CurrentTabIndex+Math.Sign(direction),0,4));}
    private void Snap(int target)
    {
        if(_moving)return;_moving=true;
        var width=Math.Max(1,SectionHost.ActualWidth);
        if(_peerSection!=null && target!=CurrentTabIndex)EnsurePeerSectionVisible(target,target>CurrentTabIndex?width:-width,createIfMissing:false);
        var animation=new DoubleAnimation(target==CurrentTabIndex?0:(target>CurrentTabIndex?-1:1)*width,TimeSpan.FromMilliseconds(110)){EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut}};
        var done=false;
        var fallback=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(140)};
        void Complete()
        {
            if(done)return;done=true;fallback.Stop();
            _slide.BeginAnimation(TranslateTransform.XProperty,null);_slide.X=0;ClearPeerSection();
            var pending=_pendingWheel;_pendingWheel=0;
            for(int i=0;i<Tabs.Length;i++)if(i!=target && Tabs[i].IsChecked==true)Tabs[i].IsChecked=false;
            if(Tabs[target].IsChecked!=true)Tabs[target].IsChecked=true;
            if(CurrentTabIndex!=target)OnTabChecked(Tabs[target],new RoutedEventArgs());
            _moving=false;
            if(pending!=0)HandleHorizontalWheel(pending);
        }
        fallback.Tick+=(_,_)=>Complete();
        animation.Completed+=(_,_)=>Complete();
        fallback.Start();
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
