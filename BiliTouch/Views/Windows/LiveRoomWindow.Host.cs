using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.Views;

/// <summary>
/// 直播间平板竖屏上下堆叠布局与单窗口内嵌托管（与 PlayerWindow.Host 同构）。
/// </summary>
public partial class LiveRoomWindow
{
    private MainWindow? _embeddedHost;
    private bool _isEmbeddedInMain;
    private bool _tabletStackedLayout;

    internal bool IsEmbeddedInMain => _isEmbeddedInMain;
    internal bool IsTabletStackedLayout => _tabletStackedLayout;

    internal bool IsTabletPortraitPostureActive =>
        _isEmbeddedInMain
            ? _embeddedHost?.IsTabletPortraitPosture == true
            : Responsive.GetPosture(Root) == LayoutPosture.TabletPortrait;

    internal void SyncLiveTabletConstraints()
    {
        var allowNarrow = Svc.Settings.TabletMode != TabletMode.Off
                          || Responsive.GetPosture(Root) == LayoutPosture.TabletPortrait;
        var targetMin = allowNarrow ? 480 : 840;
        if (Math.Abs(MinWidth - targetMin) > 0.5) MinWidth = targetMin;
    }

    internal bool AttachToMainWindow(MainWindow host)
    {
        if (IsClosing || host == null) return false;
        if (_isEmbeddedInMain && ReferenceEquals(_embeddedHost, host))
        {
            host.MountEmbeddedPlayer(Root);
            ApplyChatLayout();
            return true;
        }
        if (_isEmbeddedInMain) DetachFromMainWindow(stopPlayback: false);

        if (ReferenceEquals(Content, Root)) Content = null;
        Root.DataContext = Model;
        _embeddedHost = host;
        _isEmbeddedInMain = true;
        UiScale.Unregister(ChatPanel);
        if (BtnLiveEmbeddedBack != null) BtnLiveEmbeddedBack.Visibility = Visibility.Visible;

        host.MountEmbeddedPlayer(Root);
        host.PreviewKeyDown -= OnLiveEmbeddedHostKeyDown;
        host.PreviewKeyDown += OnLiveEmbeddedHostKeyDown;
        host.SizeChanged -= OnLiveEmbeddedHostSizeChanged;
        host.SizeChanged += OnLiveEmbeddedHostSizeChanged;
        Responsive.PostureChanged -= OnLivePostureChanged;
        Responsive.PostureChanged += OnLivePostureChanged;

        Model.Start();
        _health.Start();
        SyncVolumeGlyph();
        EnsureVolumePopupAnchor();
        ApplyChatLayout();
        return true;
    }

    internal void DetachFromMainWindow(bool stopPlayback = true)
    {
        if (!_isEmbeddedInMain) return;
        if (_full) ToggleFullscreen();
        CloseVolumePopup();
        CloseLiveSettingsPopup();
        if (QualityPopup != null) QualityPopup.IsOpen = false;

        if (stopPlayback && !IsClosing)
        {
            _chat.Stop();
            _overlay.Clear();
            _ = StopPlaybackAsync();
        }

        var host = _embeddedHost;
        _embeddedHost = null;
        _isEmbeddedInMain = false;

        if (host != null)
        {
            host.PreviewKeyDown -= OnLiveEmbeddedHostKeyDown;
            host.SizeChanged -= OnLiveEmbeddedHostSizeChanged;
            host.UnmountEmbeddedPlayer(Root);
        }

        if (BtnLiveEmbeddedBack != null) BtnLiveEmbeddedBack.Visibility = Visibility.Collapsed;
        if (!ReferenceEquals(Content, Root)) Content = Root;
        UiScale.Register(ChatPanel);
        ApplyChatLayout();
    }

    private void OnLiveEmbeddedBackClick(object sender, RoutedEventArgs e) => HandleEmbeddedBack();

    internal bool HandleEmbeddedBack()
    {
        if (!_isEmbeddedInMain) return false;
        if (LiveSettingsPopup?.IsOpen == true) { CloseLiveSettingsPopup(); return true; }
        if (QualityPopup?.IsOpen == true) { QualityPopup.IsOpen = false; return true; }
        if (VolumePopup?.IsOpen == true) { CloseVolumePopup(); return true; }
        if (_full) { ToggleFullscreen(); return true; }
        DetachFromMainWindow(stopPlayback: true);
        return true;
    }

    private void OnLiveEmbeddedHostKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isEmbeddedInMain || IsClosing) return;
        if (Keyboard.FocusedElement is TextBox || e.OriginalSource is TextBox || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = HandleEmbeddedBack();
            return;
        }
        if (e.Key == Key.F)
        {
            e.Handled = true;
            ToggleFullscreen();
        }
        else if (e.Key == Key.Space)
        {
            e.Handled = true;
            TogglePause();
        }
    }

    private void OnLiveEmbeddedHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isEmbeddedInMain || IsClosing) return;
        ApplyChatLayout();
        if (VolumePopup.IsOpen) EnsureVolumePopupAnchor();
    }

    private void OnLivePostureChanged(FrameworkElement source, LayoutPosture posture)
    {
        if (IsClosing) return;
        var expected = _isEmbeddedInMain ? _embeddedHost?.FindName("RootGrid") : Root;
        if (!ReferenceEquals(source, expected)) return;
        SyncLiveTabletConstraints();
        ApplyChatLayout();
    }
}
