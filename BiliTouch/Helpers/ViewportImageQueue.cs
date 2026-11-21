using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Views.Controls;

namespace BiliTouch.Helpers;

/// <summary>Start only nearby card images, in bounded UI batches; one scroll subscription per viewport.</summary>
internal static class ViewportImageQueue
{
    private static readonly ConditionalWeakTable<ScrollViewer, Queue> Queues = new();
    private static readonly ConditionalWeakTable<Border, Queue> ElementQueues = new();
    internal static int Started { get; private set; }

    /// <summary>
    /// 虚拟化容器换源/回收时立即取消该 Border 上挂起的旧视口排队项，
    /// 避免新源复用到旧 TaskCompletionSource 或被回收瞬间的旧状态误杀导致封面永久空白。
    /// </summary>
    public static void Cancel(Border element)
    {
        if (ElementQueues.TryGetValue(element, out var queue))
        {
            ElementQueues.Remove(element);
            queue.CancelElement(element);
        }
    }

    /// <summary>
    /// 当卡片墙完成重排/补帧（未必伴随 ScrollChanged）时唤醒队列扫描并自愈视口内漏载封面。
    /// </summary>
    public static void NotifyLayout(ScrollViewer? viewer)
    {
        if (viewer != null && Queues.TryGetValue(viewer, out var queue))
        {
            queue.Wake();
        }
    }

    public static async Task<bool> WaitAsync(Border element)
    {
        var sourceAtStart = Cover.GetSource(element);
        if (!element.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? handler = null;
            handler = (_, _) => { element.Loaded -= handler; loaded.TrySetResult(); };
            element.Loaded += handler;
            if (!element.IsLoaded)
                await loaded.Task;
            else
                element.Loaded -= handler;
            if (Cover.GetSource(element) != sourceAtStart) return false;
        }
        var viewer = VisualTreeUtil.FindAncestor<ScrollViewer>(element);
        // Retained home tabs have real dimensions while hidden. Their explicit
        // two-slot preloader owns cold images; layout must not start another
        // foreground burst. Keep a waiter so an early reveal can still load it.
        if (viewer is TabViewportScrollViewer { CanPrepareHidden: true })
        {
            var q = Queues.GetValue(viewer, v => new Queue(v));
            ElementQueues.AddOrUpdate(element, q);
            return await q.Add(element, sourceAtStart);
        }
        if (VisualTreeUtil.FindAncestor<VideoCard>(element) == null) return true;
        if (viewer == null || viewer.ViewportHeight <= 0) return true;
        var queue = Queues.GetValue(viewer, v => new Queue(v));
        ElementQueues.AddOrUpdate(element, queue);
        return await queue.Add(element, sourceAtStart);
    }

    private sealed class Pending
    {
        public required Border Element;
        public required string? ExpectedSource;
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Queue
    {
        private readonly ScrollViewer _viewer;
        private readonly List<Pending> _pending = new();
        private bool _queued;
        private int _cursor;
        private int _remaining;
        public Queue(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.ScrollChanged += (_, _) => Wake();
            viewer.SizeChanged += (_, _) => Wake();
            viewer.IsVisibleChanged += (_, _) => Wake();
            viewer.Unloaded += (_, _) =>
            {
                foreach (var p in _pending) p.Completion.TrySetResult(false);
                _pending.Clear();
            };
        }

        public void Wake()
        {
            _remaining = _pending.Count;
            Schedule();
        }

        public void CancelElement(Border element)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_pending[i].Element, element))
                {
                    var old = _pending[i];
                    _pending.RemoveAt(i);
                    if (_cursor > i && _cursor > 0) _cursor--;
                    old.Completion.TrySetResult(false);
                }
            }
            _remaining = _pending.Count;
        }

        public Task<bool> Add(Border element, string? expectedSource)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_pending[i].Element, element))
                {
                    if (_pending[i].ExpectedSource == expectedSource)
                    {
                        _remaining = _pending.Count;
                        Schedule();
                        return _pending[i].Completion.Task;
                    }
                    var stale = _pending[i];
                    _pending.RemoveAt(i);
                    stale.Completion.TrySetResult(false);
                }
            }
            var pending = new Pending { Element = element, ExpectedSource = expectedSource };
            _pending.Add(pending);
            _remaining = _pending.Count;
            Schedule();
            return pending.Completion.Task;
        }

        private void Schedule()
        {
            if (_queued || _pending.Count == 0) return;
            _queued = true;
            _viewer.Dispatcher.BeginInvoke(DispatcherPriority.Background, Scan);
        }

        private void Scan()
        {
            _queued = false;
            var watch = Stopwatch.StartNew();
            int started = 0;
            while (_pending.Count > 0 && _remaining > 0 && started < 6 && watch.Elapsed.TotalMilliseconds < 2)
            {
                _remaining--;
                _cursor %= _pending.Count;
                var pending = _pending[_cursor];
                var element = pending.Element;
                bool? ready = null;
                var currentSource = Cover.GetSource(element);
                if (string.IsNullOrWhiteSpace(currentSource) || currentSource != pending.ExpectedSource || element.Background != null)
                {
                    ready = false;
                }
                else if (_viewer.IsVisible && element.IsLoaded)
                {
                    try
                    {
                        var bounds = element.TransformToAncestor(_viewer).TransformBounds(new Rect(element.RenderSize));
                        // 覆盖 VirtualizingCardWallPanel 的完整实现缓冲区（2~4 行缓冲），
                        // 避免已实现的缓冲行卡片因落在 0.75×ViewportHeight 外而永久停留在 _pending 中。
                        var prefetch = Math.Max(_viewer.ViewportHeight * 1.6, 1080);
                        if (bounds.Bottom >= -prefetch && bounds.Top <= _viewer.ViewportHeight + prefetch)
                            ready = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // 布局/重挂载中途暂不可转换坐标时保留在队列中等待下一帧，绝不直接判死(ready=false)
                        ready = null;
                    }
                }
                if (ready.HasValue)
                {
                    _pending.RemoveAt(_cursor);
                    ElementQueues.Remove(element);
                    if (ready.Value) { started++; Started++; }
                    pending.Completion.TrySetResult(ready.Value);
                }
                else _cursor++;
            }
            if (_remaining > 0 && _pending.Count > 0) Schedule();
        }
    }
}
