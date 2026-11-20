using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using BiliTouch.Models;

namespace BiliTouch.ViewModels;

/// <summary>
/// 支持批量追加与头部裁剪的弹幕集合：满 500 条上限且处于跟随底部态时，将最多 40 次 Add + 40 次 RemoveAt(0)
/// 合并为单次底层切片与单次 Reset 通知，避免 ObservableCollection 逐条 500 元素搬移和 VirtualizingStackPanel
/// 锚点回退 40 位后把中间 30 条不可见弹幕全部实例化测量一遍的严重卡顿。
/// </summary>
internal sealed class LiveMessageCollection : ObservableCollection<LiveChatEntry>
{
    // 跟随底部态达到上限（500 条）时，只需通知尾部可见窗口内的槽位原地 Replace：
    // 1) ItemContainerGenerator 直接复用现有 ListBoxItem 与 LiveChatRow（constructs = 0，避免 Reset 销毁重建容器）；
    // 2) VirtualizingStackPanel 锚点稳停在底部，不再因头部 RemoveAt(0) 回退 40 位并遍历测量 30 条不可见项；
    // 3) 底层 List<LiveChatEntry> 完整同步全部 500 条，用户向上滚回历史时按真实数据即时实例化。
    private const int VisibleTailNotifyWindow = 20;

    public void AppendBoundedBatch(List<LiveChatEntry> batch, int maxCount, bool allowBatchReset)
    {
        if (batch.Count == 0) return;

        int overflow = Math.Max(0, Count + batch.Count - maxCount);
        if (allowBatchReset && overflow > 0 && Count == maxCount && Items is List<LiveChatEntry> steadyList)
        {
            CheckReentrancy();
            int tailStart = Math.Max(0, maxCount - VisibleTailNotifyWindow);
            int tailLen = maxCount - tailStart;
            var oldTail = new LiveChatEntry[tailLen];
            for (int i = 0; i < tailLen; i++) oldTail[i] = steadyList[tailStart + i];

            if (overflow >= steadyList.Count) steadyList.Clear();
            else steadyList.RemoveRange(0, overflow);

            if (batch.Count > maxCount)
                steadyList.AddRange(batch.GetRange(batch.Count - maxCount, maxCount));
            else
                steadyList.AddRange(batch);

            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            for (int i = 0; i < tailLen; i++)
            {
                int idx = tailStart + i;
                var next = steadyList[idx];
                var prev = oldTail[i];
                if (!ReferenceEquals(prev, next))
                    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, next, prev, idx));
            }
            return;
        }

        if (allowBatchReset && (overflow > 0 || batch.Count >= 16))
        {
            CheckReentrancy();
            if (Items is List<LiveChatEntry> list)
            {
                if (overflow > 0)
                {
                    if (overflow >= list.Count) list.Clear();
                    else list.RemoveRange(0, overflow);
                }
                if (batch.Count > maxCount)
                    list.AddRange(batch.GetRange(batch.Count - maxCount, maxCount));
                else
                    list.AddRange(batch);
            }
            else
            {
                for (int i = 0; i < overflow && Items.Count > 0; i++) Items.RemoveAt(0);
                int start = Math.Max(0, batch.Count - maxCount);
                for (int i = start; i < batch.Count; i++) Items.Add(batch[i]);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return;
        }

        for (int i = 0; i < batch.Count; i++)
        {
            Add(batch[i]);
            while (Count > maxCount) RemoveAt(0);
        }
    }
}

/// <summary>Bounded live chat state, independent from VOD comments, timelines and video actions.</summary>
public sealed class LiveRoomViewModel : ObservableObject, IDisposable
{
    private readonly Queue<LiveChatEntry> _pending = new();
    private readonly HashSet<string> _seenSc = new();
    private readonly Queue<string> _seenScOrder = new();
    private readonly List<LiveChatEntry> _flushBatch = new(40);
    private readonly LiveMessageCollection _messages = new();
    private readonly DispatcherTimer _flush, _clock;
    private string _title = "直播间", _anchor = "", _face = "", _area = "", _notice = "", _watched = "", _connection = "未连接", _status = "", _error = "";
    private bool _followed, _sending, _overlay = true;
    public event Action<IReadOnlyList<LiveChatEntry>>? BatchApplied;
    public long RoomId { get; private set; }
    public long UserId { get; private set; }
    public int LiveState { get; private set; }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Anchor { get => _anchor; set => SetProperty(ref _anchor, value); }
    public string Face { get => _face; set => SetProperty(ref _face, value); }
    public string Area { get => _area; set => SetProperty(ref _area, value); }
    public string Announcement { get => _notice; set => SetProperty(ref _notice, value); }
    public string Watched { get => _watched; set => SetProperty(ref _watched, value); }
    public string ConnectionText { get => _connection; set => SetProperty(ref _connection, value); }
    public string StatusText { get => _status; set => SetProperty(ref _status, value); }
    public string Error { get => _error; set => SetProperty(ref _error, value); }
    public bool Followed { get => _followed; set { if (SetProperty(ref _followed, value)) OnPropertyChanged(nameof(FollowText)); } }
    public string FollowText => Followed ? "已关注" : "+ 关注";
    public bool IsSending { get => _sending; set => SetProperty(ref _sending, value); }
    public bool OverlayEnabled { get => _overlay; set => SetProperty(ref _overlay, value); }
    public bool FollowLatest { get; set; } = true;
    public string RoomText => $"房间 {RoomId}";
    public string LiveStateText => LiveState switch { 1 => "直播中", 2 => "轮播中", 0 => "未开播", _ => "连接中" };
    public bool HasSuperChats => ActiveSuperChats.Count > 0;
    public ObservableCollection<LiveChatEntry> Messages => _messages;
    public ObservableCollection<LiveChatEntry> ActiveSuperChats { get; } = new();

    public LiveRoomViewModel()
    {
        // 120ms（~8Hz）合并刷新侧边弹幕列表：肉眼阅读更稳，并将高密场景下的列表重排频次降低 2.4x
        _flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _flush.Tick += (_, _) => Flush();
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Tick();
    }
    public void Start() { _flush.Start(); _clock.Start(); }
    public void Reset(long roomId)
    {
        RoomId = roomId; UserId = 0; LiveState = -1; Title = $"直播间 {roomId}"; Anchor = Face = Area = Announcement = Watched = Error = StatusText = "";
        Followed = false; ConnectionText = "正在连接"; _pending.Clear(); _seenSc.Clear(); _seenScOrder.Clear(); _messages.Clear(); ActiveSuperChats.Clear();
        OnPropertyChanged(nameof(RoomText)); OnPropertyChanged(nameof(LiveStateText)); OnPropertyChanged(nameof(HasSuperChats));
    }
    public void Apply(LiveRoomDetails room)
    {
        RoomId = room.RoomId; UserId = room.UserId; Title = room.Title; Anchor = room.Anchor; Face = room.Face; Area = room.Area;
        Announcement = room.Announcement; Watched = room.Watched; Followed = room.Followed; SetLiveState(room.LiveStatus);
        foreach (var sc in room.SuperChats) Enqueue(sc);
        OnPropertyChanged(nameof(RoomText));
    }
    public void SetLiveState(int state) { LiveState = state; OnPropertyChanged(nameof(LiveStateText)); }
    public void Enqueue(LiveChatEntry message)
    {
        if (string.IsNullOrWhiteSpace(message.Text)) return;
        if (_pending.Count >= 2048) _pending.Dequeue();
        _pending.Enqueue(message);
    }
    public void Flush()
    {
        if (_pending.Count == 0) return;
        _flushBatch.Clear();
        bool hadSc = HasSuperChats;
        while (_flushBatch.Count < 40 && _pending.TryDequeue(out var item))
        {
            if (item.Kind == LiveChatKind.SuperChat && item.Id.Length > 0)
            {
                if (!_seenSc.Add(item.Id)) continue;
                _seenScOrder.Enqueue(item.Id);
                while (_seenScOrder.Count > 2000) _seenSc.Remove(_seenScOrder.Dequeue());
            }
            _flushBatch.Add(item);
            if (item.Kind == LiveChatKind.SuperChat && !item.IsExpired)
            {
                ActiveSuperChats.Insert(0, item);
                while (ActiveSuperChats.Count > 30) ActiveSuperChats.RemoveAt(ActiveSuperChats.Count - 1);
            }
        }
        if (_flushBatch.Count == 0) return;
        _messages.AppendBoundedBatch(_flushBatch, 500, allowBatchReset: FollowLatest);
        if (hadSc != HasSuperChats) OnPropertyChanged(nameof(HasSuperChats));
        BatchApplied?.Invoke(_flushBatch);
    }
    public void DeleteSuperChats(IReadOnlyList<string> ids)
    {
        bool hadSc = HasSuperChats;
        var removed = ids.ToHashSet();
        foreach (var item in ActiveSuperChats.Where(m => removed.Contains(m.Id)).ToArray()) ActiveSuperChats.Remove(item);
        foreach (var item in _messages.Where(m => m.Kind == LiveChatKind.SuperChat && removed.Contains(m.Id)).ToArray()) _messages.Remove(item);
        // A moderation delete can arrive before its SC event or before the next UI batch.
        foreach (var id in ids) { if (_seenSc.Add(id)) _seenScOrder.Enqueue(id); }
        while (_seenScOrder.Count > 2000) _seenSc.Remove(_seenScOrder.Dequeue());
        if (_pending.Count > 0)
        {
            var keep = _pending.Where(m => m.Kind != LiveChatKind.SuperChat || !removed.Contains(m.Id)).ToArray();
            _pending.Clear();
            foreach (var item in keep) _pending.Enqueue(item);
        }
        if (hadSc != HasSuperChats) OnPropertyChanged(nameof(HasSuperChats));
    }
    public void Tick()
    {
        if (ActiveSuperChats.Count == 0) return;
        bool hadSc = HasSuperChats;
        for (int i = ActiveSuperChats.Count - 1; i >= 0; i--)
        {
            var item = ActiveSuperChats[i];
            if (item.IsExpired) ActiveSuperChats.RemoveAt(i);
            else item.RefreshClock();
        }
        if (hadSc != HasSuperChats) OnPropertyChanged(nameof(HasSuperChats));
    }
    public void ClearMessages() { _pending.Clear(); _messages.Clear(); }
    public void Dispose() { _flush.Stop(); _clock.Stop(); _pending.Clear(); }
}
