using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using LibVLCSharp.Shared;

namespace BiliDesk.Views;

/// <summary>Live room owns its playback, websocket, chat and SC lifecycle. No VOD timeline or comments.</summary>
public partial class LiveRoomWindow : FluentWindow
{
    public LiveRoomViewModel Model { get; } = new();
    public bool IsClosing { get; private set; }
    protected override FrameworkElement? ScaleRoot => ChatPanel;
    private readonly MediaPlayer _player;
    private readonly LiveDanmakuService _chat;
    private readonly LiveOverlayRenderer _overlay;
    private readonly SemaphoreSlim _mediaGate = new(1, 1);
    private readonly DispatcherTimer _health;
    private int _request, _streamRequest, _quality, _retryCount;
    private long _chatRoom;
    private bool _qualityUpdating, _changingStream, _chatVisible = true, _full, _closeReady, _paused, _muted, _following;
    private bool _followLatest = true, _scrollScheduled;
    private bool _syncingOverlay;
    private DanmakuKeywordFilter? _keywordFilter;
    private int _unread;
    private DateTimeOffset _nextRetry, _startedAt, _metadataAt;
    private bool _refreshingMetadata;
    private WindowState _previousState;
    private ResizeMode _previousResize;
    private sealed record QualityOption(int Qn, string Label);

    public LiveRoomWindow()
    {
        InitializeComponent(); DataContext = Model;
        _player = new MediaPlayer(VlcCore.Get()) { EnableHardwareDecoding = true };
        VlcCore.RegisterPlayer(); LiveVideo.MediaPlayer = _player;
        _player.EnableKeyInput = false; _player.EnableMouseInput = false;
        _chat = new LiveDanmakuService(Svc.Api, Dispatcher);
        _overlay = new LiveOverlayRenderer(LiveCanvas);
        _chat.ChatReceived += ReceiveChat;
        _chat.SuperChatDeleted += Model.DeleteSuperChats;
        _chat.ConnectionChanged += connected => Model.ConnectionText = connected ? "已连接 · 实时更新" : _chat.LastError.Length > 0 ? _chat.LastError + " · 自动重试" : "弹幕连接中 · 自动重试";
        _chat.RoomEvent += OnRoomEvent;
        Model.BatchApplied += OnChatBatch;
        ApplyOverlaySettings(); Svc.Settings.DanmakuSettingsChanged += ApplyOverlaySettings;
        _player.Playing += OnPlaying;
        _health = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _health.Tick += HealthTick;
        LiveVolume.Value = Svc.Settings.PlayerVolume;
        _player.Volume = (int)LiveVolume.Value;
        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) => { Model.Start(); _health.Start(); };
        SizeChanged += (_, _) => { LiveVolume.Visibility = VideoPanel.ActualWidth < 600 ? Visibility.Collapsed : Visibility.Visible; if (!_full && _chatVisible) ChatColumn.Width = new GridLength(ActualWidth < 1100 ? 300 : 360); };
    }

    public void OpenRoom(long roomId, string? title = null)
    {
        if (IsClosing || roomId <= 0) return;
        ++_request; ++_streamRequest; _chat.Stop(); _overlay.Clear();
        _chatRoom = 0;
        _ = StopPlaybackAsync();
        Model.Reset(roomId); if (!string.IsNullOrEmpty(title)) Model.Title = title;
        ChatInput.Text = ""; _followLatest = true; _unread = 0; LatestButton.Visibility = Visibility.Collapsed;
        _quality = 0; _retryCount = 0; _paused = false; _changingStream = false; _startedAt = default; _nextRetry = DateTimeOffset.MaxValue;
        _qualityUpdating = true; QualityBox.ItemsSource = null; _qualityUpdating = false;
        ShowPlaybackState("正在获取直播间…");
        _ = LoadRoomAsync(roomId, _request);
    }

    private async Task LoadRoomAsync(long roomId, int request)
    {
        try
        {
            // Metadata resolves short IDs before websocket authentication and history are requested.
            var result = await Svc.Api.GetLiveRoomDetailsAsync(roomId);
            if (!Current(request)) return;
            _metadataAt = DateTimeOffset.UtcNow;
            if (result.room is { } room)
            {
                Model.Apply(room); Title = room.Title + " - BiliTouch 直播";
                if (room.LiveStatus == 0) ShowPlaybackState("主播暂未开播");
            }
            else Model.Error = result.error ?? "部分房间信息暂时不可用";
            StartChat();
            _ = LoadRecentAsync(Model.RoomId, request);
            if (result.room is not { LiveStatus: 0 }) await LoadStreamAsync();
        }
        catch (Exception ex) { if (Current(request)) { StartChat(); Model.Error = ex.Message; ShowPlaybackState("连接失败，请重新连接"); _nextRetry = DateTimeOffset.UtcNow.AddSeconds(10); } }
    }
    private void StartChat() { _chatRoom = Model.RoomId; _chat.Start(_chatRoom); }
    private async Task LoadRecentAsync(long roomId, int request)
    {
        try
        {
            var recent = await Svc.Api.GetRecentLiveMessagesAsync(roomId);
            if (!Current(request)) return;
            // History is marked in the sidebar and never emitted onto the live video.
            foreach (var item in recent) Model.Enqueue(new LiveChatEntry { Id = item.Id, UserId = item.UserId, User = item.User, Text = item.Text, Badge = "最近发言", Kind = LiveChatKind.System });
        }
        catch { /* History failure must not block the realtime connection or video. */ }
    }
    private bool Current(int request) => !IsClosing && _request == request;

    private async Task LoadStreamAsync()
    {
        if (IsClosing || Model.RoomId <= 0) return;
        int roomRequest = _request, stream = ++_streamRequest;
        _nextRetry = DateTimeOffset.MaxValue; _changingStream = true;
        ShowPlaybackState("正在连接直播画面…");
        try
        {
            var result = await Svc.Api.GetLivePlayUrlExAsync(Model.RoomId, _quality);
            if (!Current(roomRequest) || stream != _streamRequest) return;
            if (!result.ok || string.IsNullOrEmpty(result.url))
            {
                ShowPlaybackState(Model.LiveState == 0 ? "主播暂未开播，开播后会自动连接" : "直播画面连接失败");
                Model.Error = result.err ?? "直播源暂时不可用";
                _nextRetry = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, 3 * Math.Pow(2, Math.Min(_retryCount++, 4))));
                return;
            }
            _qualityUpdating = true;
            var options = result.options.Select(q => new QualityOption(q.qn, q.label)).ToList();
            QualityBox.ItemsSource = options;
            QualityBox.SelectedItem = options.FirstOrDefault(q => q.Qn == result.actualQn);
            _qualityUpdating = false;
            await _mediaGate.WaitAsync();
            try
            {
                if (!Current(roomRequest) || stream != _streamRequest) return;
                await Task.Run(() => _player.Stop());
                if (!Current(roomRequest) || stream != _streamRequest) return;
                using var media = new Media(VlcCore.Get(), result.url, FromType.FromLocation);
                media.AddOption(":http-referrer=https://live.bilibili.com/");
                media.AddOption(":http-user-agent=Mozilla/5.0");
                media.AddOption(":network-caching=1000");
                _paused = false; PauseButton.Content = "Ⅱ";
                if (!_player.Play(media)) throw new InvalidOperationException("无法打开直播流");
                _player.Volume = _muted ? 0 : (int)LiveVolume.Value;
                _startedAt = DateTimeOffset.UtcNow; Model.Error = ""; Model.StatusText = ""; if (Model.LiveState != 2) Model.SetLiveState(1);
            }
            finally { _mediaGate.Release(); }
        }
        catch (Exception ex)
        {
            if (Current(roomRequest) && stream == _streamRequest) { Model.Error = ex.Message; ShowPlaybackState("直播连接中断"); _nextRetry = DateTimeOffset.UtcNow.AddSeconds(5); }
        }
        finally { if (stream == _streamRequest) _changingStream = false; }
    }
    private void OnPlaying(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() =>
    {
        if (IsClosing) return;
        PlaybackState.Visibility = Visibility.Collapsed; _retryCount = 0; _nextRetry = DateTimeOffset.MaxValue;
    });
    private void HealthTick(object? sender, EventArgs args)
    {
        if (IsClosing || Model.RoomId <= 0) return;
        if (!_refreshingMetadata && DateTimeOffset.UtcNow - _metadataAt > TimeSpan.FromSeconds(30)) _ = RefreshRoomAsync();
        if (_paused || _changingStream || Model.LiveState == 0) return;
        if (_nextRetry <= DateTimeOffset.UtcNow) { _ = LoadStreamAsync(); return; }
        if (_startedAt != default && DateTimeOffset.UtcNow - _startedAt > TimeSpan.FromSeconds(25) && _player.State is not (VLCState.Playing or VLCState.Paused))
        { ShowPlaybackState("直播信号中断，正在重新连接…"); _nextRetry = DateTimeOffset.UtcNow.AddSeconds(3); }
    }
    private async Task RefreshRoomAsync()
    {
        int request = _request; _refreshingMetadata = true; _metadataAt = DateTimeOffset.UtcNow;
        try
        {
            var result = await Svc.Api.GetLiveRoomDetailsAsync(Model.RoomId);
            if (!Current(request) || result.room == null) return;
            int previous = Model.LiveState; Model.Apply(result.room);
            if (_chatRoom != Model.RoomId) StartChat();
            if (previous <= 0 && Model.LiveState > 0) await LoadStreamAsync();
            else if (previous > 0 && Model.LiveState == 0) { ++_streamRequest; _overlay.Clear(); ShowPlaybackState("本场直播已结束"); await StopPlaybackAsync(); }
        }
        catch { }
        finally { _refreshingMetadata = false; }
    }
    private void ShowPlaybackState(string text) { PlaybackStateText.Text = text; PlaybackState.Visibility = Visibility.Visible; }

    private void OnRoomEvent(string command, JsonElement data)
    {
        if (IsClosing) return;
        switch (command)
        {
            case "LIVE": Model.SetLiveState(1); Model.StatusText = "主播已开播"; _ = LoadStreamAsync(); break;
            case "PREPARING": case "CUT_OFF": case "ROOM_LOCK":
                Model.SetLiveState(0); ++_streamRequest; _changingStream = false; _overlay.Clear();
                ShowPlaybackState(command == "PREPARING" ? "本场直播已结束" : "直播暂时中断");
                Model.StatusText = PlaybackStateText.Text; _ = StopPlaybackAsync(); break;
            case "ROOM_CHANGE":
                var title = LiveJson.Text(data, "title"); if (title.Length > 0) Model.Title = title;
                var area = LiveJson.Text(data, "area_name"); if (area.Length > 0) Model.Area = area; break;
            case "WATCHED_CHANGE": Model.Watched = LiveJson.Text(data, "text_large"); break;
            case "ROOM_SILENT_ON": Model.StatusText = "本直播间已开启禁言限制"; break;
            case "ROOM_SILENT_OFF": Model.StatusText = "本直播间已解除禁言限制"; break;
            case "WARNING": Model.StatusText = LiveJson.Text(data, "msg"); break;
        }
    }
    private async Task StopPlaybackAsync()
    {
        int request = _request, stream = _streamRequest;
        await _mediaGate.WaitAsync();
        try { if (IsClosing || (request == _request && stream == _streamRequest)) await Task.Run(() => { try { _player.Stop(); } catch { } }); }
        finally { _mediaGate.Release(); }
    }
    private void OnChatBatch(IReadOnlyList<LiveChatEntry> batch)
    {
        if (IsClosing) return;
        if (Model.OverlayEnabled)
            foreach (var item in batch.Where(m => m.Kind == LiveChatKind.Danmaku)) _overlay.Add(item.Text, Svc.Settings.DanmakuColorful ? item.Color : 0xFFFFFF);
        if (_followLatest) ScheduleLatest();
        else
        {
            _unread += batch.Count; LatestButton.Content = $"{Math.Min(_unread, 999)} 条新弹幕 · 回到最新"; LatestButton.Visibility = Visibility.Visible;
        }
    }
    private void ScheduleLatest()
    {
        if (_scrollScheduled) return; _scrollScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            _scrollScheduled = false;
            if (IsClosing || !_followLatest || Model.Messages.Count == 0) return;
            ChatList.ScrollIntoView(Model.Messages[^1]);
        });
    }
    private void ChatWheel(object sender, MouseWheelEventArgs e) { if (e.Delta > 0) _followLatest = false; }
    private void ChatTouchDown(object sender, TouchEventArgs e) => _followLatest = false;
    private void ChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scroll || e.VerticalChange == 0) return;
        if (scroll.ScrollableHeight - scroll.VerticalOffset < 4)
        { _followLatest = true; _unread = 0; LatestButton.Visibility = Visibility.Collapsed; }
        else if (e.ExtentHeightChange == 0 && e.VerticalChange < 0) _followLatest = false;
    }
    private void LatestClick(object sender, RoutedEventArgs e) { _followLatest = true; _unread = 0; LatestButton.Visibility = Visibility.Collapsed; ScheduleLatest(); }
    private void ClearChatClick(object sender, RoutedEventArgs e) { Model.ClearMessages(); _unread = 0; LatestButton.Visibility = Visibility.Collapsed; _overlay.Clear(); }

    private async Task SendMessageAsync()
    {
        if (Model.IsSending || IsClosing) return;
        string draft = ChatInput.Text.Trim(); if (draft.Length == 0) return;
        int request = _request; long room = Model.RoomId;
        Model.IsSending = true; Model.Error = "";
        try
        {
            var response = await Svc.Api.SendLiveMessageAsync(room, draft);
            if (!Current(request)) return;
            if (response.ok) { if (ChatInput.Text.Trim() == draft) ChatInput.Clear(); Model.StatusText = "已发送"; }
            else Model.Error = response.err ?? "弹幕发送失败";
        }
        catch (Exception ex) { if (Current(request)) Model.Error = ex.Message; }
        finally { Model.IsSending = false; }
    }
    private async void SendClick(object sender, RoutedEventArgs e) => await SendMessageAsync();
    private async void ChatInputKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SendMessageAsync(); } }
    private async void FollowClick(object sender, RoutedEventArgs e)
    {
        if (_following || Model.UserId <= 0 || IsClosing) return;
        if (!Svc.Session.HasLogin) { Model.Error = "请先登录后再关注主播"; return; }
        var request = _request; var follow = !Model.Followed; _following = true; FollowButton.IsEnabled = false;
        try { var response = await Svc.Api.FollowUpAsync(Model.UserId, follow); if (Current(request)) { if (response.ok) Model.Followed = follow; else Model.Error = response.err ?? "关注失败"; } }
        catch (Exception ex) { if (Current(request)) Model.Error = ex.Message; }
        finally { _following = false; FollowButton.IsEnabled = true; }
    }
    private void OpenBrowserClick(object sender, RoutedEventArgs e) { if (Model.RoomId > 0) Process.Start(new ProcessStartInfo($"https://live.bilibili.com/{Model.RoomId}") { UseShellExecute = true }); }
    private void QualityChanged(object sender, SelectionChangedEventArgs e) { if (_qualityUpdating || QualityBox.SelectedItem is not QualityOption q) return; _quality = q.Qn; _ = LoadStreamAsync(); }
    private void ReconnectClick(object sender, RoutedEventArgs e)
    {
        if (IsClosing) return;
        StartChat(); _ = LoadStreamAsync();
    }
    private void PauseClick(object sender, RoutedEventArgs e) => TogglePause();
    private void TogglePause()
    {
        if (_changingStream || IsClosing) return;
        if (_paused) { _paused = false; _ = LoadStreamAsync(); }
        else if (_player.IsPlaying) { _paused = true; _player.SetPause(true); PauseButton.Content = "▶"; Model.StatusText = "画面已暂停，继续播放将回到实时画面"; }
    }
    private void MuteClick(object sender, RoutedEventArgs e) { _muted = !_muted; ApplyVolume(); }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_player == null) return; if (e.NewValue > 0) _muted = false; ApplyVolume(); Svc.Settings.SetPlayerVolume((int)e.NewValue); }
    private void ApplyVolume() { _player.Volume = _muted ? 0 : (int)LiveVolume.Value; MuteButton.Content = _muted || LiveVolume.Value == 0 ? "静" : "♪"; }
    private void OverlayClick(object sender, RoutedEventArgs e)
    { var s = Svc.Settings; s.UpdateDanmakuSettings(!s.DanmakuEnabled, s.DanmakuAreaPercent, s.DanmakuSmartFilter); }
    private void ReceiveChat(LiveChatEntry entry)
    {
        if (entry.Kind == LiveChatKind.Danmaku && _keywordFilter?.IsBlocked(entry.Text) == true) return;
        Model.Enqueue(entry);
    }
    private void ApplyOverlaySettings()
    {
        if (IsClosing) return;
        var s = Svc.Settings; _syncingOverlay = true;
        AreaSetting.Value = s.DanmakuAreaPercent; OpacitySetting.Value = s.DanmakuOpacityPercent;
        FontSetting.Value = s.DanmakuFontScalePercent; SpeedSetting.Value = s.DanmakuSpeedLevel; ColorSetting.IsChecked = s.DanmakuColorful;
        Model.OverlayEnabled = s.DanmakuEnabled; OverlayOff.Visibility = Model.OverlayEnabled ? Visibility.Collapsed : Visibility.Visible;
        _overlay.Configure(DanmakuMotion.FontSizeFromPercent(s.DanmakuFontScalePercent, 20), s.DanmakuAreaPercent / 100d, s.DanmakuOpacityPercent / 100d, s.DanmakuSpeedLevel);
        _keywordFilter = DanmakuKeywordFilter.Build(s.DanmakuBlockKeywords); _syncingOverlay = false;
    }
    private void OverlaySettingsClick(object sender, RoutedEventArgs e) => LiveSettingsPopup.IsOpen = !LiveSettingsPopup.IsOpen;
    private void OverlaySettingChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SaveOverlaySettings();
    private void ColorSettingChanged(object sender, RoutedEventArgs e) => SaveOverlaySettings();
    private void SaveOverlaySettings()
    {
        if (_syncingOverlay || _overlay == null || AreaSetting == null || FontSetting == null || SpeedSetting == null || ColorSetting == null || OpacitySetting == null) return;
        var s = Svc.Settings;
        s.UpdateDanmakuSettings(s.DanmakuEnabled, (int)AreaSetting.Value, s.DanmakuSmartFilter,
            colorful: ColorSetting.IsChecked == true, opacityPercent: (int)OpacitySetting.Value, fontScalePercent: (int)FontSetting.Value, speedLevel: (int)SpeedSetting.Value);
    }
    private void ChatToggleClick(object sender, RoutedEventArgs e) { _chatVisible = !_chatVisible; ApplyChatLayout(); }
    private void ApplyChatLayout()
    {
        ChatPanel.Visibility = !_full && _chatVisible ? Visibility.Visible : Visibility.Collapsed;
        ChatColumn.Width = new GridLength(!_full && _chatVisible ? ActualWidth < 1100 ? 300 : 360 : 0);
    }
    private void VideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && e.OriginalSource is DependencyObject source && !LiveControls.IsAncestorOf(source))
        { ToggleFullscreen(); e.Handled = true; }
    }
    private void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (IsClosing) return;
        _full = !_full;
        if (_full) { _previousState = WindowState; _previousResize = ResizeMode; ResizeMode = ResizeMode.NoResize; MaximizeCoversTaskbar = true; SuppressAcrylicActivationSwap = true; DwmInterop.SuppressCaptionForFullscreen(this); }
        else { MaximizeCoversTaskbar = false; ResizeMode = _previousResize; SuppressAcrylicActivationSwap = false; DwmInterop.RestoreCaption(this); }
        TitleBar.Visibility = RoomHeader.Visibility = _full ? Visibility.Collapsed : Visibility.Visible;
        ChromeRow.Height = new GridLength(_full ? 0 : 44); HeaderRow.Height = new GridLength(_full ? 0 : 92);
        ApplyChatLayout();
        if (_full) { if (WindowState == WindowState.Maximized) RefreshMaximizedBounds(); else WindowState = WindowState.Maximized; }
        else if (_previousState == WindowState.Maximized) RefreshMaximizedBounds(); else WindowState = _previousState;
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || e.OriginalSource is TextBox || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.F: ToggleFullscreen(); break;
            case Key.Escape: if (_full) ToggleFullscreen(); else Close(); break;
            case Key.Space: TogglePause(); break;
            case Key.M: _muted = !_muted; ApplyVolume(); break;
            default: return;
        }
        e.Handled = true;
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeReady)
        {
            e.Cancel = true;
            if (!IsClosing)
            {
                IsClosing = true; ++_request; ++_streamRequest; _health.Stop(); LiveSettingsPopup.IsOpen = false; Svc.Settings.DanmakuSettingsChanged -= ApplyOverlaySettings; Model.Dispose(); _chat.Dispose(); _overlay.Dispose();
                _player.Playing -= OnPlaying; _player.Volume = 0; Hide(); _ = TeardownAsync();
            }
        }
        base.OnClosing(e);
    }
    private async Task TeardownAsync()
    {
        await StopPlaybackAsync();
        LiveVideo.MediaPlayer = null; LiveVideo.Content = null; LiveVideo.Dispose();
        await Task.Run(_player.Dispose); VlcCore.UnregisterPlayer();
        _closeReady = true; Close();
    }
}
