using System.Collections.ObjectModel;
using System.Windows.Threading;
using BiliDesk.Models;

namespace BiliDesk.ViewModels;

/// <summary>Bounded live chat state, independent from VOD comments, timelines and video actions.</summary>
public sealed class LiveRoomViewModel : ObservableObject, IDisposable
{
    private readonly Queue<LiveChatEntry> _pending = new();
    private readonly HashSet<string> _seenSc = new();
    private readonly Queue<string> _seenScOrder = new();
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
    public string RoomText => $"房间 {RoomId}";
    public string LiveStateText => LiveState switch { 1 => "直播中", 2 => "轮播中", 0 => "未开播", _ => "连接中" };
    public bool HasSuperChats => ActiveSuperChats.Count > 0;
    public ObservableCollection<LiveChatEntry> Messages { get; } = new();
    public ObservableCollection<LiveChatEntry> ActiveSuperChats { get; } = new();

    public LiveRoomViewModel()
    {
        _flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _flush.Tick += (_, _) => Flush();
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Tick();
    }
    public void Start() { _flush.Start(); _clock.Start(); }
    public void Reset(long roomId)
    {
        RoomId = roomId; UserId = 0; LiveState = -1; Title = $"直播间 {roomId}"; Anchor = Face = Area = Announcement = Watched = Error = StatusText = "";
        Followed = false; ConnectionText = "正在连接"; _pending.Clear(); _seenSc.Clear(); _seenScOrder.Clear(); Messages.Clear(); ActiveSuperChats.Clear();
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
        var batch = new List<LiveChatEntry>();
        while (batch.Count < 40 && _pending.TryDequeue(out var item))
        {
            if (item.Kind == LiveChatKind.SuperChat && item.Id.Length > 0)
            {
                if (!_seenSc.Add(item.Id)) continue;
                _seenScOrder.Enqueue(item.Id);
                while (_seenScOrder.Count > 2000) _seenSc.Remove(_seenScOrder.Dequeue());
            }
            Messages.Add(item); batch.Add(item);
            if (item.Kind == LiveChatKind.SuperChat && !item.IsExpired)
            {
                ActiveSuperChats.Insert(0, item);
                while (ActiveSuperChats.Count > 30) ActiveSuperChats.RemoveAt(ActiveSuperChats.Count - 1);
            }
            while (Messages.Count > 500) Messages.RemoveAt(0);
        }
        if (batch.Count == 0) return;
        OnPropertyChanged(nameof(HasSuperChats)); BatchApplied?.Invoke(batch);
    }
    public void DeleteSuperChats(IReadOnlyList<string> ids)
    {
        var removed = ids.ToHashSet();
        foreach (var item in ActiveSuperChats.Where(m => removed.Contains(m.Id)).ToArray()) ActiveSuperChats.Remove(item);
        foreach (var item in Messages.Where(m => m.Kind == LiveChatKind.SuperChat && removed.Contains(m.Id)).ToArray()) Messages.Remove(item);
        // A moderation delete can arrive before its SC event or before the next UI batch.
        foreach (var id in ids) { if (_seenSc.Add(id)) _seenScOrder.Enqueue(id); }
        while (_seenScOrder.Count > 2000) _seenSc.Remove(_seenScOrder.Dequeue());
        var keep = _pending.Where(m => m.Kind != LiveChatKind.SuperChat || !removed.Contains(m.Id)).ToArray(); _pending.Clear();
        foreach (var item in keep) _pending.Enqueue(item);
        OnPropertyChanged(nameof(HasSuperChats));
    }
    public void Tick()
    {
        foreach (var item in ActiveSuperChats.ToArray()) { if (item.IsExpired) ActiveSuperChats.Remove(item); else item.RefreshClock(); }
        OnPropertyChanged(nameof(HasSuperChats));
    }
    public void ClearMessages() { _pending.Clear(); Messages.Clear(); }
    public void Dispose() { _flush.Stop(); _clock.Stop(); _pending.Clear(); }
}
