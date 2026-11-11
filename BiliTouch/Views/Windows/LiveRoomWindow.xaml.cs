using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using LibVLCSharp.Shared;
// ★ 必须写别名: 这个文件同时 using 了 System.Windows.Media(要用 VisualTreeHelper)
//   和 LibVLCSharp.Shared, 而两边都有 MediaPlayer —— 不限定的话就是 CS0104 二义性。
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliTouch.Views;

/// <summary>
/// 直播间: 自己负责播放、WebSocket、侧栏弹幕与 SC 生命周期, 没有进度轴与点播评论。
///
/// ★★★ 2026-11-05 与播放页对齐(用户报"直播间重做之后很多东西没有做到对齐"):
///   · 控制栏的播放/暂停、清晰度、画面弹幕、弹幕设置、侧栏、全屏全部沿用播放页的
///     样式(全局 PlayerRoundBtn / PlayerTextBtn / PlayerSlider)与图标(Helpers.PlayerGlyphs);
///   · 音量改成"悬停/点一下才向上展开", 控制栏常驻宽度不变;
///   · 顶栏右上角换成统一的 WindowButtons, 铺满窗口与播放器同位置同语义;
///   · 播放中控制栏自动隐藏(与播放器同一套延迟), 鼠标移动/点画面唤出。
/// </summary>
public partial class LiveRoomWindow : FluentWindow
{
    public LiveRoomViewModel Model { get; } = new();
    public bool IsClosing { get; private set; }
    protected override FrameworkElement? ScaleRoot => ChatPanel;
    private MediaPlayer _player;
    private LibVLC _liveCore;
    private readonly LiveDanmakuService _chat;
    private readonly LiveOverlayRenderer _overlay;
    private readonly SemaphoreSlim _mediaGate = new(1, 1);
    private readonly DispatcherTimer _health;
    private readonly DispatcherTimer _controlsTimer;
    private int _request, _streamRequest, _quality, _retryCount;
    private long _chatRoom;
    private bool _qualityUpdating, _changingStream, _chatVisible = true, _full, _closeReady, _paused, _muted, _following;
    private bool _followLatest = true, _scrollScheduled;
    private bool _syncingOverlay;
    private bool _fillWindow;
    private bool _controlsHidden;
    private bool _volumePinned, _volumeHover;
    private DispatcherTimer? _volumeCloseTimer;
    private DanmakuKeywordFilter? _keywordFilter;
    private int _unread;
    private DateTimeOffset _nextRetry, _startedAt, _metadataAt;
    private bool _refreshingMetadata;
    private WindowState _previousState;
    private ResizeMode _previousResize;
    private sealed record QualityOption(int Qn, string Label);

    /// <summary>控制栏自动隐藏延迟: 与播放器 ControlsHideDelay(2400ms) 对齐, 两个窗口手感一致</summary>
    private static readonly TimeSpan ControlsHideDelay = TimeSpan.FromMilliseconds(2400);

    public LiveRoomWindow()
    {
        InitializeComponent(); DataContext = Model;
        _liveCore = VlcCore.Get();
        _player = new MediaPlayer(_liveCore) { EnableHardwareDecoding = VideoDecode.HardwareEnabled };
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
        _controlsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ControlsHideDelay };
        _controlsTimer.Tick += (_, _) => { _controlsTimer.Stop(); if (ShouldHideControls()) ApplyControlsHidden(true); };
        // 音量真源是 Svc.Settings.PlayerVolume, 与播放器共用同一个偏好:
        // 用户在播放器里调到 40, 进直播间也应该是 40(原来就是如此, 别改成独立音量)。
        _player.Volume = Svc.Settings.PlayerVolume;
        VolumeSliderSync(Svc.Settings.PlayerVolume, updateSettings: false);
        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) => { Model.Start(); _health.Start(); SyncVolumeGlyph(); EnsureVolumePopupAnchor(); };
        SizeChanged += (_, _) =>
        {
            if (!_full && _chatVisible && !_fillWindow) ChatColumn.Width = new GridLength(ActualWidth < 1100 ? 300 : 360);
            // 窗口变窄时把"弹幕设置"文字收成图标宽度, 避免挤掉全屏按钮(播放器用同样策略)
            LiveSettingsButton.MinWidth = RoomLayout.ActualWidth < 760 ? 40 : 56;
            if (VolumePopup.IsOpen) EnsureVolumePopupAnchor();
        };
        InitializeLiveTouch();
        InitializeLiveEnhancement();
        Loaded += (_, _) => { StartLiveBackdropGuard(); VideoOverlay.Focus(); };
        SizeChanged += (_, _) => StartLiveBackdropGuard();
        VolumePopup.Child.MouseEnter += VolumeHoverEnter;
        VolumePopup.Child.MouseLeave += VolumeHoverLeave;
        VolumePopup.Child.LostMouseCapture += (_, _) => ScheduleVolumeClose();
        VolumePopup.Closed += (_, _) =>
        {
            _volumeCloseTimer?.Stop();
            _volumeHover = _volumePinned = false;
            if (!IsClosing) StartControlsCountdown();
        };
        QualityPopup.PlacementTarget = QualityButton;
        QualityPopup.Opened += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RightAlignQualityPopup));
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
        _qualityUpdating = true; QualityList.ItemsSource = null; QualityList.SelectedItem = null; _qualityUpdating = false;
        SetPauseGlyph(playing: true);
        ShowPlaybackState("正在获取直播间…");
        _ = LoadRoomAsync(roomId, _request);
    }

    private async Task LoadRoomAsync(long roomId, int request)
    {
        try
        {
            // 短号要先解析成真实房间号, 之后 WebSocket 认证与历史弹幕才能对上。
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
            // ★ 历史弹幕与"取流"并行: 一进房间画面上就有最近几条弹幕(高权重预加载),
            //   不必等 WebSocket 推第一条。
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
            // 历史弹幕**两侧都给**: 侧栏标成"最近发言", 画面层预加载(等画布量出尺寸就铺上去)。
            foreach (var item in recent)
            {
                Model.Enqueue(new LiveChatEntry { Id = item.Id, UserId = item.UserId, User = item.User, Text = item.Text, Badge = "最近发言", Kind = LiveChatKind.System });
                if (Model.OverlayEnabled && item.Text.Length > 0 && _keywordFilter?.IsBlocked(item.Text) != true)
                    _overlay.Add(item.Text, Svc.Settings.DanmakuColorful ? item.Color : 0xFFFFFF);
            }
        }
        catch { /* 历史失败不能挡住实时连接与视频 */ }
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
            QualityList.ItemsSource = options;
            QualityList.SelectedItem = options.FirstOrDefault(q => q.Qn == result.actualQn);
            QualityText.Text = (options.FirstOrDefault(q => q.Qn == result.actualQn)?.Label) ?? "原画";
            _qualityUpdating = false;
            await _mediaGate.WaitAsync();
            try
            {
                if (!Current(roomRequest) || stream != _streamRequest) return;
                await Task.Run(() => _player.Stop());
                if (!Current(roomRequest) || stream != _streamRequest) return;
                using var media = new Media(_liveCore, result.url, FromType.FromLocation);
                media.AddOption(":http-referrer=https://live.bilibili.com/");
                media.AddOption(":http-user-agent=Mozilla/5.0");
                media.AddOption(":network-caching=1000");
                _paused = false; SetPauseGlyph(playing: true);
                if (!_player.Play(media)) throw new InvalidOperationException("无法打开直播流");
                _player.Volume = _muted ? 0 : Svc.Settings.PlayerVolume;
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
    private Window? _liveOverlayWindow;
    private DispatcherTimer? _liveBackdropGuard;
    private int _liveBackdropTicks;
    private void HookLiveOverlayInput()
    {
        var overlay = LiveVideo.GetType().GetProperty("ForegroundWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(LiveVideo) as Window;
        if (overlay == null || ReferenceEquals(overlay, _liveOverlayWindow)) return;
        if (_liveOverlayWindow != null) { UnhookLiveTrackpad(); _liveOverlayWindow.PreviewKeyDown -= OnKeyDown; }
        _liveOverlayWindow = overlay;
        overlay.ShowInTaskbar = false;
        overlay.PreviewKeyDown += OnKeyDown;
        HookLiveTrackpad(overlay);
        VideoOverlay.Focus();
    }
    private void StartLiveBackdropGuard()
    {
        if (IsClosing) return;
        RefreshLiveBackdrop();
        _liveBackdropTicks = 25;
        if (_liveBackdropGuard == null) {
            _liveBackdropGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _liveBackdropGuard.Tick += (_, _) => {
                if (IsClosing || --_liveBackdropTicks <= 0) { _liveBackdropGuard.Stop(); return; }
                RefreshLiveBackdrop();
            };
        }
        _liveBackdropGuard.Start();
    }
    private void RefreshLiveBackdrop()
    {
        try {
            HookLiveOverlayInput();
            var host = LiveVideo.GetType().GetField("_videoHwndHost", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(LiveVideo) as System.Windows.Interop.HwndHost;
            if (host != null && host.Handle != IntPtr.Zero) VideoBackdropInterop.EnsureBlackVideoHost(host.Handle);
        } catch { }
    }

    private void OnPlaying(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() =>
    {
        if (IsClosing) return;
        StartLiveBackdropGuard();
        PlaybackState.Visibility = Visibility.Collapsed; _retryCount = 0; _nextRetry = DateTimeOffset.MaxValue;
        StartControlsCountdown();
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
    private void ShowPlaybackState(string text) { PlaybackStateText.Text = text; PlaybackState.Visibility = Visibility.Visible; StartLiveBackdropGuard(); }

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
            foreach (var item in batch.Where(m => m.Kind == LiveChatKind.Danmaku))
                _overlay.Add(item.Text, Svc.Settings.DanmakuColorful ? item.Color : 0xFFFFFF);
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

    // ------------------------------------------------------------ 清晰度(与播放器同形态的按钮 + 弹层)

    /// <summary>回到实时画面 / 重新连接(控制栏第二颗)。原方法名叫 ReconnectClick, XAML 里两处引用它。</summary>
    private void ReconnectClick(object sender, RoutedEventArgs e)
    {
        if (IsClosing) return;
        StartChat(); _ = LoadStreamAsync();
    }

    private void QualityClick(object sender, RoutedEventArgs e)
    {
        if (QualityPopup.IsOpen) { QualityPopup.IsOpen = false; return; }
        QualityPopup.PlacementTarget = QualityButton;
        QualityPopup.IsOpen = true;
    }
    private void RightAlignQualityPopup()
    {
        // ★ Placement=Top 的语义是"以目标水平居中"。要右缘对齐, 只需把它往右推
        //   (按钮半宽 - 弹层半宽): 弹层比按钮宽时这个值是负数, 自然向左溢出。
        //   必须等 Popup 布局完成后再量 ActualWidth —— Opened 那一刻它还是 0(项目里踩过)。
        if (QualityPopup.Child is not FrameworkElement child || QualityButton == null) return;
        if (child.ActualWidth <= 0) return;
        QualityPopup.HorizontalOffset = (QualityButton.ActualWidth - child.ActualWidth) / 2;
    }
    private void QualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_qualityUpdating || QualityList.SelectedItem is not QualityOption q) return;
        QualityText.Text = q.Label;
        _quality = q.Qn; QualityPopup.IsOpen = false;
        _ = LoadStreamAsync();
    }

    // ------------------------------------------------------------ 音量(悬停/点击向上展开)

    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => VolumeSliderSync((int)e.NewValue, updateSettings: true);

    /// <summary>
    /// 音量的唯一入口。真人拖滑杆、滚轮、按键、程序回灌全走这里, 保证三个账本
    /// (原生音量 / 设置持久化 / 图标状态)不会漂移。
    /// </summary>
    private void VolumeSliderSync(int value, bool updateSettings)
    {
        if (_player == null) return;
        value = Math.Clamp(value, 0, 100);
        if (Math.Abs(LiveVolume.Value - value) > 0.5)
        {
            _syncingVolume = true;
            LiveVolume.Value = value;
            _syncingVolume = false;
        }
        if (value > 0) _muted = false;
        _player.Volume = _muted ? 0 : value;
        VolumePopupText.Text = _muted ? "静音" : value + "%";
        SyncVolumeGlyph();
        if (updateSettings && !_syncingVolume) Svc.Settings.SetPlayerVolume(value);
    }
    private bool _syncingVolume;

    private void SyncVolumeGlyph()
    {
        if (VolumeGlyphText == null) return;
        var silent = _muted || LiveVolume.Value <= 0;
        VolumeGlyphText.Text = PlayerGlyphs.VolumeGlyph(silent);
    }
    private void MuteClick(object sender, RoutedEventArgs e)
    {
        // 触屏没有 hover: 第一下点喇叭=展开音量条, 已经在展开态时再点=静音切换。
        if (!VolumePopup.IsOpen)
        {
            _volumePinned = true;
            EnsureVolumePopupAnchor();
            VolumePopup.IsOpen = true;
            return;
        }
        _muted = !_muted;
        VolumeSliderSync((int)LiveVolume.Value, updateSettings: false);
    }
    private void VolumeHoverEnter(object sender, MouseEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e)) return;   // 触屏由点击处理, 不要被合成鼠标事件抢
        _volumeHover = true;
        _volumeCloseTimer?.Stop();
        ShowControls();
        EnsureVolumePopupAnchor();
        VolumePopup.IsOpen = true;
    }
    private void VolumeHoverLeave(object sender, MouseEventArgs e)
    {
        _volumeHover = false;
        ScheduleVolumeClose();
    }
    private void ScheduleVolumeClose()
    {
        if (IsClosing || _volumePinned || _volumeHover) return;
        _volumeCloseTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _volumeCloseTimer.Tick -= VolumeCloseTick;
        _volumeCloseTimer.Tick += VolumeCloseTick;
        _volumeCloseTimer.Stop();
        _volumeCloseTimer.Start();
    }
    private void VolumeCloseTick(object? sender, EventArgs e)
    {
        _volumeCloseTimer?.Stop();
        if (IsClosing || _volumeHover || _volumePinned || MuteButton.IsMouseOver ||
            VolumePopup.Child is { IsMouseOver: true } or { IsMouseCaptureWithin: true }) return;
        VolumePopup.IsOpen = false;
    }

    private void EnsureVolumePopupAnchor()
    {
        // 面板必须锚在喇叭上: 窗口尺寸变化/全屏切换后 PlacementTarget 会失效, 重设一次。
        VolumePopup.PlacementTarget = MuteButton;
        VolumePopup.Placement = PlacementMode.Top;
    }
    private void CloseVolumePopup()
    {
        _volumeHover = false; _volumePinned = false;
        _volumeCloseTimer?.Stop();
        VolumePopup.IsOpen = false;
    }

    // ------------------------------------------------------------ 控制栏显隐(与播放器同一套规则)

    /// <summary>仅在"播放中 + 没有弹层/输入框/悬停"时才允许隐藏。暂停时控制栏常显。</summary>
    private bool ShouldHideControls()
    {
        if (IsClosing || _paused) return false;
        if (!_player.IsPlaying) return false;
        if (_changingStream || PlaybackState.Visibility == Visibility.Visible) return false;
        if (LiveSettingsPopup.IsOpen || QualityPopup.IsOpen || VolumePopup.IsOpen) return false;
        if (ChatInput.IsKeyboardFocusWithin) return false;
        if (LiveControls.IsMouseOver) return false;
        return true;
    }
    private void StartControlsCountdown()
    {
        if (!ShouldHideControls()) { _controlsTimer.Stop(); return; }
        _controlsTimer.Stop(); _controlsTimer.Start();
    }
    private void ApplyControlsHidden(bool hidden)
    {
        if (_controlsHidden == hidden || IsClosing) return;
        _controlsHidden = hidden;
        if (hidden) CloseVolumePopup();
        LiveControls.IsHitTestVisible = !hidden;
        VideoOverlay.Cursor = hidden ? Cursors.None : Cursors.Arrow;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(LiveControls.Opacity, hidden ? 0 : 1, TimeSpan.FromMilliseconds(hidden ? 180 : 140))
        { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
        LiveControls.BeginAnimation(OpacityProperty, fade);
        LiveControls.Opacity = hidden ? 0 : 1;
    }
    private void ShowControls()
    {
        _controlsTimer.Stop();
        ApplyControlsHidden(false);
    }
    private void VideoMouseMove(object sender, MouseEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e)) return;
        ShowControls();
        StartControlsCountdown();
    }
    private void VideoMouseLeave(object sender, MouseEventArgs e)
    {
        if (ShouldHideControls()) ApplyControlsHidden(true);
    }
    /// <summary>单击画面 = 切换控制栏显隐(与播放器 ToggleControlsByClick 完全一致)</summary>
    private void ToggleControlsByClick()
    {
        if (_controlsHidden) { ShowControls(); StartControlsCountdown(); }
        else { _controlsTimer.Stop(); ApplyControlsHidden(true); }
    }

    // ------------------------------------------------------------ 暂停 / 静音 / 画面弹幕

    private void PauseClick(object sender, RoutedEventArgs e) => TogglePause();
    private void TogglePause()
    {
        if (_changingStream || IsClosing) return;
        if (_paused) { _paused = false; SetPauseGlyph(playing: true); ShowControls(); _ = LoadStreamAsync(); }
        else if (_player.IsPlaying)
        {
            _paused = true; _player.SetPause(true); SetPauseGlyph(playing: false);
            Model.StatusText = "画面已暂停，继续播放将回到实时画面";
            ShowControls();   // 暂停时控制栏常显(用户多半要操作)
        }
    }
    /// <summary>播放/暂停图标: 与播放器 UpdatePlayButton 同一套矢量(Helpers.PlayerGlyphs)</summary>
    private void SetPauseGlyph(bool playing)
    {
        if (PauseGlyph == null) return;
        var glyph = playing ? PlayerGlyphs.Pause() : PlayerGlyphs.Play();
        PauseGlyph.Data = glyph.Data;
        PauseGlyph.Fill = glyph.Fill;
    }
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
        AreaValue.Text = s.DanmakuAreaPercent + "%"; OpacityValue.Text = s.DanmakuOpacityPercent + "%";
        FontValue.Text = s.DanmakuFontScalePercent + "%"; SpeedValue.Text = DanmakuMotion.SpeedLabel(s.DanmakuSpeedLevel);
        Model.OverlayEnabled = s.DanmakuEnabled; OverlayOff.Visibility = Model.OverlayEnabled ? Visibility.Collapsed : Visibility.Visible;
        _overlay.Configure(DanmakuMotion.FontSizeFromPercent(s.DanmakuFontScalePercent, 20), s.DanmakuAreaPercent / 100d, s.DanmakuOpacityPercent / 100d, s.DanmakuSpeedLevel);
        _keywordFilter = DanmakuKeywordFilter.Build(s.DanmakuBlockKeywords); _syncingOverlay = false;
    }
    private void OverlaySettingsClick(object sender, RoutedEventArgs e)
    {
        if (LiveSettingsPopup.IsOpen) { LiveSettingsPopup.IsOpen = false; return; }
        LiveSettingsPopup.PlacementTarget = LiveSettingsButton;
        LiveSettingsPopup.IsOpen = true;
    }
    private void OverlaySettingChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SaveOverlaySettings();
    private void ColorSettingChanged(object sender, RoutedEventArgs e) => SaveOverlaySettings();
    private void SaveOverlaySettings()
    {
        if (_syncingOverlay || _overlay == null || AreaSetting == null || FontSetting == null || SpeedSetting == null || ColorSetting == null || OpacitySetting == null) return;
        var s = Svc.Settings;
        s.UpdateDanmakuSettings(s.DanmakuEnabled, (int)AreaSetting.Value, s.DanmakuSmartFilter,
            colorful: ColorSetting.IsChecked == true, opacityPercent: (int)OpacitySetting.Value, fontScalePercent: (int)FontSetting.Value, speedLevel: (int)SpeedSetting.Value);
    }

    // ------------------------------------------------------------ 侧栏 / 铺满 / 全屏

    private void ChatToggleClick(object sender, RoutedEventArgs e) { _chatVisible = !_chatVisible; ApplyChatLayout(); }
    private void ApplyChatLayout()
    {
        var show = !_full && !_fillWindow && _chatVisible;
        ChatPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ChatColumn.Width = new GridLength(show ? (ActualWidth < 1100 ? 300 : 360) : 0);
    }

    /// <summary>
    /// 铺满窗口(2026-11-05, 与播放页 BtnFillWindow 同一语义):
    /// 收起右侧弹幕栏, 让画面吃掉整块窗口宽度 —— 窗口尺寸本身不变。
    /// ★ 与"全屏"是两件事: 全屏会收顶栏 + 最大化 + 盖任务栏。两者共用同一颗画面按钮的不同模式。
    /// </summary>
    private void FillWindowClick(object sender, RoutedEventArgs e) => ToggleFillWindow();
    private void ToggleFillWindow()
    {
        if (IsClosing || _full) return;
        _fillWindow = !_fillWindow;
        FillWindowGlyph.Text = PlayerGlyphs.FillWindowGlyph(_fillWindow);
        BtnFillWindow.ToolTip = _fillWindow ? "还原（退出铺满窗口）" : "铺满窗口（收起右侧弹幕栏）";
        ApplyChatLayout();
    }
    private void VideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit && !IsInsideControls(hit)) VideoOverlay.Focus();
        if (e.ClickCount == 2 && e.OriginalSource is DependencyObject source && !IsInsideControls(source))
        { ToggleFullscreen(); e.Handled = true; return; }
        // 单击(非双击)画面 = 控制栏显隐, 与播放器一致。
        if (e.ClickCount == 1 && e.OriginalSource is DependencyObject single && !IsInsideControls(single))
        { ToggleControlsByClick(); e.Handled = true; }
    }
    private bool IsInsideControls(DependencyObject source)
    {
        for (var d = source; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null)
            if (ReferenceEquals(d, LiveControls) || d is ButtonBase or Slider or TextBoxBase) return true;
        return false;
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
        StartControlsCountdown();
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || e.OriginalSource is TextBox || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.F: ToggleFullscreen(); break;
            case Key.Escape: if (_full) ToggleFullscreen(); else Close(); break;
            case Key.Space: TogglePause(); break;
            case Key.M: _muted = !_muted; VolumeSliderSync((int)LiveVolume.Value, updateSettings: false); break;
            case Key.Enter: ToggleFillWindow(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if(_liveEnhancementSwitching){e.Cancel=true;_closeAfterLiveEnhancement=true;return;}
        if (!_closeReady)
        {
            e.Cancel = true;
            if (!IsClosing)
            {
                IsClosing = true; ++_request; ++_streamRequest; _health.Stop(); _controlsTimer.Stop();
                _volumeCloseTimer?.Stop();
                _liveBackdropGuard?.Stop();
                UnhookLiveTrackpad();
                if (_liveOverlayWindow != null) { UnhookLiveTrackpad(); _liveOverlayWindow.PreviewKeyDown -= OnKeyDown; }
                LiveSettingsPopup.IsOpen = false; QualityPopup.IsOpen = false; VolumePopup.IsOpen = false;
                Svc.Settings.DanmakuSettingsChanged -= ApplyOverlaySettings; Model.Dispose(); _chat.Dispose(); _overlay.Dispose();
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
