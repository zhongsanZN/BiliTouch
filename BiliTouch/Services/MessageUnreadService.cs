using System.Windows;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.ViewModels;

namespace BiliTouch.Services;

public sealed record MessageUnreadSnapshot(int? Private = null, int? Reply = null, int? At = null, int? Like = null);

/// <summary>One account-scoped poller for the sidebar and message-channel badges.</summary>
public sealed class MessageUnreadService : ObservableObject
{
    private readonly Func<Task<MessageUnreadSnapshot>> _fetch;
    private readonly Func<string> _account;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _timer;
    private Task? _refresh;
    private bool _again;
    private int _generation;
    private long _lastAttempt;
    private int _private, _reply, _at, _like;

    public MessageUnreadService() : this(() => Svc.Api.GetMessageUnreadAsync(),
        () => Svc.Session.HasLogin ? Svc.Session.Current.DedeUserID + ":" + Svc.Session.Current.SessData : "",
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher) { }

    internal MessageUnreadService(Func<Task<MessageUnreadSnapshot>> fetch, Func<string> account, Dispatcher dispatcher)
    { _fetch = fetch; _account = account; _dispatcher = dispatcher; }

    public int PrivateCount => _private;
    internal bool IsStarted => _timer != null;
    public int ReplyCount => (int)Math.Min(int.MaxValue, (long)_reply + _at);
    public int LikeCount => _like;
    public int Total => (int)Math.Min(int.MaxValue, (long)_private + _reply + _at + _like);
    public bool HasUnread => Total > 0;
    public string BadgeText => Total > 99 ? "99+" : Total.ToString();
    public string PrivateTitle => Title("私信", PrivateCount);
    public string ReplyTitle => Title("回复与@", ReplyCount);
    public string LikeTitle => Title("收到的喜欢", LikeCount);
    private static string Title(string label, int count) => count > 0 ? $"{label} ({(count > 99 ? "99+" : count.ToString())})" : label;

    public void Start()
    {
        if (_timer != null) return;
        // 订阅 AccountChanged 而非 Changed: 风控 Cookie 后台补齐(BackfillRiskCookies)只补设备字段、
        // 并不切换账号，不应该把当前未读红点闪清归零再多拉一轮。
        Svc.Session.AccountChanged += OnSessionAccountChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(45) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync(true);
    }

    public void Stop()
    {
        _timer?.Stop(); _timer = null;
        Svc.Session.AccountChanged -= OnSessionAccountChanged;
        ++_generation; _again = false;
    }

    private void OnSessionAccountChanged(object? sender, EventArgs e) => OnAccountChanged();

    private void OnAccountChanged()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(OnAccountChanged); return; }
        ++_generation; _lastAttempt = 0;
        Apply(new(0, 0, 0, 0));
        _ = RefreshAsync(true);
    }

    public Task RefreshAsync(bool force = false)
    {
        if (!_dispatcher.CheckAccess()) return _dispatcher.InvokeAsync(() => RefreshAsync(force)).Task.Unwrap();
        if (_account().Length == 0) { Apply(new(0, 0, 0, 0)); return Task.CompletedTask; }
        if (_refresh != null) { _again |= force; return _refresh; }
        if (!force && Environment.TickCount64 - _lastAttempt < 15_000) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refresh = completion.Task;
        _ = PollAsync(completion);
        return completion.Task;
    }

    private async Task PollAsync(TaskCompletionSource completion)
    {
        try
        {
            do
            {
                _again = false;
                var generation = _generation; var account = _account();
                if (account.Length == 0) break;
                _lastAttempt = Environment.TickCount64;
                MessageUnreadSnapshot snapshot;
                try { snapshot = await _fetch().ConfigureAwait(false); }
                catch { snapshot = new(); }
                await _dispatcher.InvokeAsync(() =>
                {
                    if (generation == _generation && account == _account()) Apply(snapshot);
                });
            } while (_again && _account().Length > 0);
        }
        finally
        {
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                try { await _dispatcher.InvokeAsync(() => { _refresh = null; completion.TrySetResult(); }); }
                catch { _refresh = null; completion.TrySetResult(); }
            }
            else
            {
                _refresh = null;
                completion.TrySetResult();
            }
        }
    }

    private void Apply(MessageUnreadSnapshot snapshot)
    {
        var before = (_private, _reply, _at, _like);
        _private = Math.Max(0, snapshot.Private ?? _private);
        _reply = Math.Max(0, snapshot.Reply ?? _reply);
        _at = Math.Max(0, snapshot.At ?? _at);
        _like = Math.Max(0, snapshot.Like ?? _like);
        if (before == (_private, _reply, _at, _like)) return;
        foreach (var property in new[] { nameof(PrivateCount), nameof(ReplyCount), nameof(LikeCount), nameof(Total), nameof(HasUnread), nameof(BadgeText), nameof(PrivateTitle), nameof(ReplyTitle), nameof(LikeTitle) }) OnPropertyChanged(property);
    }
}
