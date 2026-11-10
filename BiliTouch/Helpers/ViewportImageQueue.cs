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
    internal static int Started { get; private set; }
    public static async Task<bool> WaitAsync(Border element)
    {
        if (!element.IsLoaded)
        {
            var loaded = new TaskCompletionSource();
            RoutedEventHandler? handler = null;
            handler = (_, _) => { element.Loaded -= handler; loaded.TrySetResult(); };
            element.Loaded += handler;
            await loaded.Task;
        }
        var viewer = VisualTreeUtil.FindAncestor<ScrollViewer>(element);
        // Retained home tabs have real dimensions while hidden. Their explicit
        // two-slot preloader owns cold images; layout must not start another
        // foreground burst. Keep a waiter so an early reveal can still load it.
        if (viewer is TabViewportScrollViewer { CanPrepareHidden: true })
            return await Queues.GetValue(viewer, v => new Queue(v)).Add(element);
        if (VisualTreeUtil.FindAncestor<VideoCard>(element) == null) return true;
        if (viewer == null || viewer.ViewportHeight <= 0) return true;
        return await Queues.GetValue(viewer, v => new Queue(v)).Add(element);
    }
    private sealed class Pending
    {
        public required Border Element;
        public readonly TaskCompletionSource<bool> Completion = new();
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
            viewer.ScrollChanged += (_, _) => { _remaining = _pending.Count; Schedule(); };
            viewer.IsVisibleChanged += (_, _) => { _remaining = _pending.Count; Schedule(); };
            viewer.Unloaded += (_, _) => { foreach (var p in _pending) p.Completion.TrySetResult(false); _pending.Clear(); };
        }
        public Task<bool> Add(Border element)
        {
            var existing = _pending.Find(p => ReferenceEquals(p.Element, element));
            if (existing != null) return existing.Completion.Task;
            var pending = new Pending { Element = element }; _pending.Add(pending); _remaining = _pending.Count; Schedule(); return pending.Completion.Task;
        }
        private void Schedule()
        {
            if (_queued || _pending.Count == 0) return;
            _queued = true; _viewer.Dispatcher.BeginInvoke(DispatcherPriority.Background, Scan);
        }
        private void Scan()
        {
            _queued = false;
            var watch = Stopwatch.StartNew(); int started = 0;
            while (_pending.Count > 0 && _remaining > 0 && started < 6 && watch.Elapsed.TotalMilliseconds < 2)
            {
                _remaining--;
                _cursor %= _pending.Count;
                var pending = _pending[_cursor]; var element = pending.Element;
                bool? ready = null;
                if (!element.IsLoaded) ready = false;
                else if (_viewer.IsVisible) try
                {
                    var bounds = element.TransformToAncestor(_viewer).TransformBounds(new Rect(element.RenderSize));
                    var prefetch = _viewer.ViewportHeight * .75;
                    if (bounds.Bottom >= -prefetch && bounds.Top <= _viewer.ViewportHeight + prefetch) ready = true;
                }
                catch (InvalidOperationException) { ready = false; }
                if (ready.HasValue)
                {
                    _pending.RemoveAt(_cursor);
                    if (ready.Value) { started++; Started++; }
                    pending.Completion.TrySetResult(ready.Value);
                }
                else _cursor++;
            }
            if (_remaining > 0 && _pending.Count > 0) Schedule();
        }
    }
}
