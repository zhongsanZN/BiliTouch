using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BiliTouch.Helpers;

/// <summary>
/// 专为评论流定制的单列变高视口虚拟化面板。
/// 挂在评论列表（<see cref="ItemsControl"/>）上，监听外层 <see cref="ScrollViewer"/> 的视口与滚动。
/// 仅为视口范围（加上下缓冲）内的评论条目生成真实容器，视口外的评论自动回收，
/// 从而将视觉树中的富文本、图片与边框节点减少 95% 以上，保证 4K 高帧率视频播放时评论区依然满帧丝滑。
/// </summary>
public sealed class VirtualizingCommentPanel : VirtualizingPanel
{
    private ScrollViewer? _scroll;
    private readonly Dictionary<int, double> _heights = new();
    private const double DefaultItemHeight = 105.0;
    private int _firstIndex = -1, _lastIndex = -1;
    private double _lastWidth;

    public VirtualizingCommentPanel()
    {
        Loaded += (_, _) => HookScroll();
        Unloaded += (_, _) => UnhookScroll();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) InvalidateMeasure();
        };
    }

    private void HookScroll()
    {
        var next = VisualTreeUtil.FindAncestor<ScrollViewer>(this);
        if (ReferenceEquals(next, _scroll)) return;
        if (_scroll != null) _scroll.ScrollChanged -= OnScroll;
        _scroll = next;
        _firstIndex = _lastIndex = -1;
        if (_scroll != null) _scroll.ScrollChanged += OnScroll;
        InvalidateMeasure();
    }

    private void UnhookScroll()
    {
        if (_scroll != null)
        {
            _scroll.ScrollChanged -= OnScroll;
            _scroll = null;
            _firstIndex = _lastIndex = -1;
        }
    }

    private void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, _scroll)) return;
        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
        {
            var owner = ItemsControl.GetItemsOwner(this);
            var count = owner?.Items.Count ?? 0;
            if (count > 0)
            {
                var (newFirst, newLast) = VisibleRange(count);
                if (newFirst != _firstIndex || newLast != _lastIndex)
                    InvalidateMeasure();
            }
        }
    }

    private double OriginY()
    {
        if (_scroll?.Content is UIElement content && IsDescendantOf(content))
        {
            try { return TranslatePoint(new Point(0, 0), content).Y; } catch { }
        }
        return 0;
    }

    private double PanelYToScrollY(double panelY)
    {
        if (_scroll?.Content is UIElement content && IsDescendantOf(content))
        {
            try
            {
                var mapped = TransformToAncestor(content).Transform(new Point(0, panelY)).Y;
                if (double.IsFinite(mapped)) return mapped;
            }
            catch { }
        }
        return OriginY() + panelY;
    }

    internal static VirtualizingCommentPanel? FindFor(ItemsControl? itemsControl)
    {
        if (itemsControl == null) return null;
        return FindDescendant(itemsControl);
    }

    private static VirtualizingCommentPanel? FindDescendant(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is VirtualizingCommentPanel panel) return panel;
            var found = FindDescendant(child);
            if (found != null) return found;
        }
        return null;
    }

    private static bool TryGetViewportBounds(FrameworkElement element, ScrollViewer scroll, out Rect bounds)
    {
        bounds = default;
        if (!element.IsLoaded || !element.IsVisible || scroll.ViewportHeight <= 0) return false;
        try
        {
            bounds = element.TransformToAncestor(scroll)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return double.IsFinite(bounds.Top) && double.IsFinite(bounds.Bottom);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将指定评论条目滚入外层 <see cref="ScrollViewer"/> 视口并确保其虚拟化容器完成实例化。
    /// 若传入 <paramref name="preferredScrollOffset"/> / <paramref name="preferredViewportTop"/>
    /// （例如进评论详情前保存的原位置），则优先把目标评论还原到进入前的视口高度，避免退出详情后跳回评论区顶部或发生视野跳动。
    /// </summary>
    internal FrameworkElement? ScrollItemIntoView(
        ItemsControl owner,
        object targetItem,
        double? preferredScrollOffset = null,
        double? preferredViewportTop = null)
    {
        HookScroll();
        if (_scroll == null) return owner.ItemContainerGenerator.ContainerFromItem(targetItem) as FrameworkElement;

        var targetIndex = owner.Items.IndexOf(targetItem);
        if (targetIndex < 0) return null;

        TrackpadScroll.Cancel(_scroll);
        // 先让刚从 Collapsed 恢复 Visible 的列表完成一次布局测量，确保 ScrollableHeight 反映真实评论总高
        _scroll.UpdateLayout();

        double desiredTop = preferredViewportTop is { } savedTop
            && double.IsFinite(savedTop)
            && _scroll.ViewportHeight > 0
            ? Math.Clamp(savedTop, 0, Math.Max(0, _scroll.ViewportHeight - 48))
            : 24.0;

        if (preferredScrollOffset is { } saved && double.IsFinite(saved) && saved >= 0)
        {
            var clamped = Math.Clamp(saved, 0, Math.Max(0, _scroll.ScrollableHeight));
            _scroll.ScrollToVerticalOffset(clamped);
            _scroll.UpdateLayout();
        }

        FrameworkElement? container = null;
        for (int pass = 0; pass < 5; pass++)
        {
            container = owner.ItemContainerGenerator.ContainerFromItem(targetItem) as FrameworkElement;
            if (container != null && TryGetViewportBounds(container, _scroll, out var rect))
            {
                var delta = rect.Top - desiredTop;
                if (Math.Abs(delta) <= 4)
                    return container;

                var adjusted = Math.Clamp(_scroll.VerticalOffset + delta, 0, Math.Max(0, _scroll.ScrollableHeight));
                if (Math.Abs(adjusted - _scroll.VerticalOffset) <= 1)
                    return container;

                _scroll.ScrollToVerticalOffset(adjusted);
                _scroll.UpdateLayout();
                continue;
            }

            var estimatedY = PanelYToScrollY(GetTop(targetIndex));
            var targetOffset = Math.Clamp(Math.Max(0, estimatedY - desiredTop), 0, Math.Max(0, _scroll.ScrollableHeight));
            _scroll.ScrollToVerticalOffset(targetOffset);
            _scroll.UpdateLayout();
        }

        return owner.ItemContainerGenerator.ContainerFromItem(targetItem) as FrameworkElement ?? container;
    }

    private (double Top, double Bottom) VisibleBoundsInPanel()
    {
        var scrollOffset = _scroll != null ? _scroll.VerticalOffset : 0;
        var viewport = _scroll != null ? _scroll.ViewportHeight : 800;
        if (!double.IsFinite(viewport) || viewport <= 0) viewport = 800;

        // 网页布局中 WebCommentsScaleRoot 的 LayoutTransform 在 ScrollViewer 内部缩放评论区；
        // 把视口上下边界从 ScrollViewer.Content 坐标系直接映射到本面板局部坐标系，
        // 避免缩放 != 100% 时按比例累积偏移导致上方可见评论被误回收而消失。
        if (_scroll?.Content is UIElement content && IsDescendantOf(content))
        {
            try
            {
                var xform = content.TransformToDescendant(this);
                var p0 = xform.Transform(new Point(0, scrollOffset));
                var p1 = xform.Transform(new Point(0, scrollOffset + viewport));
                var top = Math.Max(0, Math.Min(p0.Y, p1.Y));
                var bottom = Math.Max(top, Math.Max(p0.Y, p1.Y));
                if (double.IsFinite(top) && double.IsFinite(bottom))
                    return (top, bottom);
            }
            catch { }
        }

        var origin = OriginY();
        var fallbackTop = Math.Max(0, scrollOffset - origin);
        return (fallbackTop, fallbackTop + viewport);
    }

    private double GetHeight(int index) => _heights.TryGetValue(index, out var h) && h > 0 ? h : DefaultItemHeight;

    private double GetTop(int targetIndex)
    {
        double top = 0;
        for (int i = 0; i < targetIndex; i++)
            top += GetHeight(i);
        return top;
    }

    private (int First, int Last) VisibleRange(int count)
    {
        if (count <= 0) return (-1, -1);
        var (viewTop, viewBottom) = VisibleBoundsInPanel();

        // 上下各保留约 700px 的缓冲带（约 6~7 条评论），保证轻度滚动绝不出现未测空白
        var bufferTop = Math.Max(0, viewTop - 700);
        var bufferBottom = viewBottom + 700;

        int first = -1, last = -1;
        double currentTop = 0;

        for (int i = 0; i < count; i++)
        {
            var h = GetHeight(i);
            var currentBottom = currentTop + h;

            if (currentBottom >= bufferTop && first < 0)
                first = i;

            if (currentTop <= bufferBottom)
                last = i;

            if (currentTop > bufferBottom && first >= 0)
                break;

            currentTop = currentBottom;
        }

        if (first < 0) return (Math.Max(0, count - 10), count - 1);
        if (last < first) last = Math.Min(count - 1, first + 10);
        last = Math.Min(count - 1, last);

        return (first, last);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        HookScroll();
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null) return new Size(0, 0);
        var count = owner.Items.Count;
        if (count == 0)
        {
            CleanOutside(0, -1);
            _firstIndex = _lastIndex = -1;
            return new Size(0, 0);
        }

        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : (_scroll?.ViewportWidth > 0 ? _scroll.ViewportWidth : (_lastWidth > 0 ? _lastWidth : 400));
        if (_lastWidth > 0 && Math.Abs(width - _lastWidth) > 0.5) _heights.Clear();
        _lastWidth = width;

        var (first, last) = VisibleRange(count);
        _firstIndex = first;
        _lastIndex = last;

        CleanOutside(first, last);

        if (last >= first)
        {
            var generator = ItemContainerGenerator;
            var position = generator.GeneratorPositionFromIndex(first);
            var childIndex = 0;

            using (generator.StartAt(position, GeneratorDirection.Forward, true))
            {
                for (int index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var fresh);
                    var existing = fresh ? -1 : childIndex < InternalChildren.Count && ReferenceEquals(InternalChildren[childIndex], child) ? childIndex : InternalChildren.IndexOf(child);
                    if (existing < 0)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    else if (existing != childIndex)
                    {
                        RemoveInternalChildRange(existing, 1);
                        InsertInternalChild(childIndex, child);
                    }

                    child.Measure(new Size(width, double.PositiveInfinity));
                    _heights[index] = child.DesiredSize.Height;
                }
            }
        }

        double totalHeight = 0;
        for (int i = 0; i < count; i++)
            totalHeight += GetHeight(i);

        return new Size(width, totalHeight);
    }

    private int ResolveContainerIndex(IItemContainerGenerator generator, UIElement child, int childPosition)
    {
        if (generator is ItemContainerGenerator concrete)
            return concrete.IndexFromContainer(child);
        return generator.IndexFromGeneratorPosition(new GeneratorPosition(childPosition, 0));
    }

    private void CleanOutside(int first, int last)
    {
        var generator = ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var index = ResolveContainerIndex(generator, InternalChildren[i], i);
            if (index < 0)
            {
                RemoveInternalChildRange(i, 1);
                continue;
            }
            if (index >= first && index <= last) continue;

            var pos = generator.GeneratorPositionFromIndex(index);
            if (pos.Index >= 0 && pos.Offset == 0)
            {
                if (generator is IRecyclingItemContainerGenerator recycling)
                    recycling.Recycle(pos, 1);
                else
                    generator.Remove(pos, 1);
            }

            RemoveInternalChildRange(i, 1);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var width = finalSize.Width;

        int nextIndex = -1;
        double top = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var index = ResolveContainerIndex(generator, child, i);
            if (index < 0) continue;

            if (index != nextIndex) top = GetTop(index);
            var h = _heights.TryGetValue(index, out var cached) && cached > 0 ? cached : child.DesiredSize.Height;
            child.Arrange(new Rect(0, top, width, h));
            top += GetHeight(index);
            nextIndex = index + 1;
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            _heights.Clear();
            RemoveInternalChildRange(0, InternalChildren.Count);
        }
        else if (args.Action == NotifyCollectionChangedAction.Add && args.ItemCount > 0)
        {
            // 插入新评论（例如发评论插入 index 0）时，将插入点及之后的缓存高度键整体后移，
            // 否则后续每条评论的高度都会错位 1 格导致裁切或重叠。
            var insertAt = ItemContainerGenerator.IndexFromGeneratorPosition(args.Position);
            if (insertAt >= 0 && _heights.Count > 0)
            {
                var shifted = new Dictionary<int, double>(_heights.Count);
                foreach (var kv in _heights)
                    shifted[kv.Key >= insertAt ? kv.Key + args.ItemCount : kv.Key] = kv.Value;
                _heights.Clear();
                foreach (var kv in shifted) _heights[kv.Key] = kv.Value;
            }
        }
        else if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
        {
            _heights.Clear();
            if (ItemContainerGenerator is ItemContainerGenerator concrete)
            {
                for (int i = InternalChildren.Count - 1; i >= 0; i--)
                {
                    if (concrete.IndexFromContainer(InternalChildren[i]) < 0)
                        RemoveInternalChildRange(i, 1);
                }
            }
            else
            {
                var at = Math.Max(0, args.Action == NotifyCollectionChangedAction.Move ? args.OldPosition.Index : args.Position.Index);
                var count = Math.Min(args.ItemUICount, InternalChildren.Count - at);
                if (count > 0) RemoveInternalChildRange(at, count);
            }
        }
        InvalidateMeasure();
    }
}
