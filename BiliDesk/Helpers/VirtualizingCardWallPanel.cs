using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// Generates only the rows near the surrounding ScrollViewer's viewport. The full desired height
/// preserves the existing page headers, footers, pixel scrolling and touch behavior.
/// </summary>
public sealed class VirtualizingCardWallPanel : VirtualizingPanel
{
    private ScrollViewer? _scroll;
    private int _columns=1;
    private double _rowHeight=240, _slotWidth=252, _lastWidth;
    private bool _anchorQueued, _anchoring;
    private int _anchorIndex, _anchorEpoch;
    private double _anchorFraction;
    private double _anchorOffset;
    public int RealizedCount => InternalChildren.Count;
    public double RowHeight => _rowHeight;

    public VirtualizingCardWallPanel()
    {
        Loaded+=(_,_)=>HookScroll();
        Unloaded+=(_,_)=> {if(_scroll!=null)_scroll.ScrollChanged-=OnScroll;_scroll=null;};
        IsVisibleChanged+=(_,_)=> {if(IsVisible)InvalidateMeasure();};
    }
    private void HookScroll()
    {
        var next=VisualTreeUtil.FindAncestor<ScrollViewer>(this);
        if(ReferenceEquals(next,_scroll))return;
        if(_scroll!=null)_scroll.ScrollChanged-=OnScroll;
        _scroll=next;
        if(_scroll!=null)_scroll.ScrollChanged+=OnScroll;
        InvalidateMeasure();
    }
    private void OnScroll(object sender,ScrollChangedEventArgs e)
    {
        if(!ReferenceEquals(e.OriginalSource,_scroll))return;
        if(e.VerticalChange!=0)
        {
            _anchoring=false;_anchorQueued=false;
            ++_anchorEpoch;
        }
        if(e.VerticalChange!=0 || e.ViewportHeightChange!=0 || e.ViewportWidthChange!=0)InvalidateMeasure();
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.Property==CardWall.ItemWidthProperty || e.Property==CardWall.AspectProperty || (e.Property.Name=="Columns" && e.Property.OwnerType==typeof(CardWall)))
            InvalidateMeasure();
    }
    private double OriginY()
    {
        if(_scroll?.Content is UIElement content && IsDescendantOf(content))
            return TranslatePoint(new Point(),content).Y;
        return 0;
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        HookScroll();
        var owner=ItemsControl.GetItemsOwner(this);
        if(owner==null)return new Size();
        var count=owner.Items.Count;
        var width=double.IsFinite(availableSize.Width)?Math.Max(1,availableSize.Width):Math.Max(1,_scroll?.ViewportWidth ?? 800);
        var origin=OriginY();
        var top=Math.Max(0,(_scroll?.VerticalOffset ?? 0)-origin);
        var cols=CardWall.GetColumns(this);
        if(cols<1)cols=CardWall.FixedColumns>0?CardWall.FixedColumns:Math.Max(1,(int)(width/(CardWall.MinItemWidth+CardWall.Gap)));
        var slot=width/cols;
        var itemWidth=Math.Max(1,slot-CardWall.Gap);
        var aspect=Math.Max(0.1,CardWall.GetAspect(this));
        var rowHeight=itemWidth/aspect+98;
        var geometryChanged=_lastWidth>0 && (Math.Abs(_lastWidth-width)>0.5 || _columns!=cols);
        if(geometryChanged && (top>0 || _anchoring))
        {
            if(!_anchoring)
            {
                var oldRow=(int)(top/_rowHeight);
                _anchorIndex=oldRow*_columns;
                _anchorFraction=top/_rowHeight-oldRow;
                _anchoring=true;
            }
            top=(_anchorIndex/cols+_anchorFraction)*rowHeight;
            _anchorOffset=origin+top;
            if(!_anchorQueued)
            {
                _anchorQueued=true;
                var epoch=_anchorEpoch;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>
                {
                    if(epoch!=_anchorEpoch)return;
                    _anchorQueued=false;
                    if(IsLoaded && _scroll!=null)
                    {
                        _scroll.ScrollToVerticalOffset(_anchorOffset);
                    }
                    else _anchoring=false;
                }));
            }
        }
        else if(_anchoring)top=Math.Max(0,_anchorOffset-origin);
        _columns=cols;_slotWidth=slot;_rowHeight=rowHeight;_lastWidth=width;
        var viewport=_scroll?.ViewportHeight ?? 0;
        if(!double.IsFinite(viewport) || viewport<=0)viewport=800;
        var rows=(count+cols-1)/cols;
        var firstRow=Math.Max(0,Math.Min(Math.Max(0,rows-1),(int)(top/rowHeight)-1));
        var lastRow=Math.Min(rows-1,(int)Math.Ceiling((top+viewport)/rowHeight)+1);
        var first=firstRow*cols;
        var last=Math.Min(count-1,(lastRow+1)*cols-1);
        CleanOutside(first,last);
        if(last>=first)
        {
            var generator=ItemContainerGenerator;
            var position=generator.GeneratorPositionFromIndex(first);
            var childIndex=0;
            using(generator.StartAt(position,GeneratorDirection.Forward,true))
            {
                for(int index=first;index<=last;index++,childIndex++)
                {
                    var child=(UIElement)generator.GenerateNext(out var fresh);
                    var existing=InternalChildren.IndexOf(child);
                    if(existing<0)
                    {
                        if(childIndex>=InternalChildren.Count)AddInternalChild(child);
                        else InsertInternalChild(childIndex,child);
                        generator.PrepareItemContainer(child);
                    }
                    else if(existing!=childIndex)
                    {
                        RemoveInternalChildRange(existing,1);
                        InsertInternalChild(childIndex,child);
                    }
                    child.SetValue(CardWall.ItemWidthProperty,itemWidth);
                    child.SetValue(CardWall.CoverHeightProperty,itemWidth/aspect);
                    child.Measure(new Size(slot,rowHeight));
                }
            }
        }
        return new Size(width,rows*rowHeight);
    }
    private void CleanOutside(int first,int last)
    {
        var generator=ItemContainerGenerator;
        for(int i=InternalChildren.Count-1;i>=0;i--)
        {
            var index=ItemsControl.GetItemsOwner(this).ItemContainerGenerator.IndexFromContainer(InternalChildren[i]);
            if(index<0) {RemoveInternalChildRange(i,1);continue;}
            var position=generator.GeneratorPositionFromIndex(index);
            if(index>=first && index<=last)continue;
            if(generator is IRecyclingItemContainerGenerator recycling)recycling.Recycle(position,1);
            else generator.Remove(position,1);
            RemoveInternalChildRange(i,1);
        }
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for(int i=0;i<InternalChildren.Count;i++)
        {
            var index=ItemsControl.GetItemsOwner(this).ItemContainerGenerator.IndexFromContainer(InternalChildren[i]);
            if(index<0)continue;
            InternalChildren[i].Arrange(new Rect(index%_columns*_slotWidth,index/_columns*_rowHeight,_slotWidth,_rowHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender,ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender,args);
        if(args.Action==NotifyCollectionChangedAction.Reset)RemoveInternalChildRange(0,InternalChildren.Count);
        else if(args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
        {
            var at=Math.Max(0,args.Action==NotifyCollectionChangedAction.Move?args.OldPosition.Index:args.Position.Index);
            var count=Math.Min(args.ItemUICount,InternalChildren.Count-at);
            if(count>0)RemoveInternalChildRange(at,count);
        }
        InvalidateMeasure();
    }
}
