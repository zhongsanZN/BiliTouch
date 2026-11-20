using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.Views;

/// <summary>
/// 平板单窗口内嵌播放舞台（双宿主托管：零重复、零反射契约破坏）与安卓竖屏沉浸评论抽屉。
/// </summary>
public partial class PlayerWindow
{
    private MainWindow? _embeddedHost;
    private bool _isEmbeddedInMain;
    private bool _tabletPortraitLayoutMounted;
    private bool _tabletPortraitVideoCompact;
    private bool _portraitDrawerOpen;

    internal bool CanEmbedInMainWindow => _vmemMode && !_closing;
    internal bool IsEmbeddedInMain => _isEmbeddedInMain;
    internal bool IsTabletPortraitLayout => _tabletPortraitLayoutMounted;

    internal bool IsTabletPortraitPostureActive =>
        _isEmbeddedInMain
            ? _embeddedHost?.IsTabletPortraitPosture == true
            : Responsive.GetPosture(RootGrid) == LayoutPosture.TabletPortrait;

    internal bool AttachToMainWindow(MainWindow host)
    {
        if (!CanEmbedInMainWindow || host == null) return false;
        if (_isEmbeddedInMain && ReferenceEquals(_embeddedHost, host))
        {
            host.MountEmbeddedPlayer(RootGrid);
            ApplyInfoPanelState();
            ApplyAlternateLayoutPreference(allowPreLoad: true);
            if (_tabletPortraitLayoutMounted) UpdateTabletPortraitMeasurements();
            return true;
        }
        if (_isEmbeddedInMain) DetachFromMainWindow(pausePlayback: false);

        PinVideoSurfaceSize();
        StopSeamlessMorph();
        if (ReferenceEquals(Content, RootGrid)) Content = null;

        _embeddedHost = host;
        _isEmbeddedInMain = true;
        // MainWindow.RootGrid 自身已参与 UiScale，避免 InfoPanel 叠加双倍缩放
        UiScale.Unregister(InfoPanel);
        if (BtnEmbeddedBack != null) BtnEmbeddedBack.Visibility = Visibility.Visible;

        host.MountEmbeddedPlayer(RootGrid);
        host.PreviewKeyDown -= OnEmbeddedHostKeyDown;
        host.PreviewKeyDown += OnEmbeddedHostKeyDown;
        host.PreviewKeyUp -= OnEmbeddedHostKeyUp;
        host.PreviewKeyUp += OnEmbeddedHostKeyUp;
        Responsive.PostureChanged -= OnEmbeddedPostureChanged;
        Responsive.PostureChanged += OnEmbeddedPostureChanged;

        if (TabIntro != null && TabComments?.IsChecked != true && IntroPanel != null) TabIntro.IsChecked = true;
        UpdateButtonState();
        UpdateFillWindowButton();
        UpdateListenButtonState();
        if (_isListenMode) ApplyListenMode(true);
        ApplyInfoPanelState();
        ApplyAlternateLayoutPreference(allowPreLoad: true);
        if (_tabletPortraitLayoutMounted) UpdateTabletPortraitMeasurements();
        QueueAlternateLayout();
        return true;
    }

    internal void DetachFromMainWindow(bool pausePlayback = true)
    {
        if (!_isEmbeddedInMain) return;
        CancelRightKeyFastForward();
        SetPortraitCommentDrawerOpen(false);
        if (_isFullscreen) ToggleFullscreen();

        if (pausePlayback && !_closing)
        {
            try { _mp?.SetPause(true); } catch { }
            StopDanmakuClock();
        }

        var host = _embeddedHost;
        _embeddedHost = null;
        _isEmbeddedInMain = false;

        if (host != null)
        {
            host.PreviewKeyDown -= OnEmbeddedHostKeyDown;
            host.PreviewKeyUp -= OnEmbeddedHostKeyUp;
            host.UnmountEmbeddedPlayer(RootGrid);
        }
        Responsive.PostureChanged -= OnEmbeddedPostureChanged;

        if (BtnEmbeddedBack != null) BtnEmbeddedBack.Visibility = Visibility.Collapsed;
        if (!ReferenceEquals(Content, RootGrid)) Content = RootGrid;
        UiScale.Register(InfoPanel);

        ApplyInfoPanelState();
        ApplyAlternateLayoutPreference(allowPreLoad: true);
        QueueAlternateLayout();
    }

    private void OnEmbeddedBackClick(object sender, RoutedEventArgs e) => HandleEmbeddedBack();

    internal bool HandleEmbeddedBack()
    {
        if (!_isEmbeddedInMain) return false;
        if (_portraitDrawerOpen)
        {
            SetPortraitCommentDrawerOpen(false);
            return true;
        }
        if (_isFullscreen)
        {
            ToggleFullscreen();
            return true;
        }
        if (AnyPlayerPopupOpen)
        {
            CloseVolumePopup();
            CloseDanmakuAreaPopup();
            if (QualityPopup != null) QualityPopup.IsOpen = false;
            if (SpeedPopup != null) SpeedPopup.IsOpen = false;
            if (SubtitlePopup != null) SubtitlePopup.IsOpen = false;
            if (CommentEmotePopup != null) CommentEmotePopup.IsOpen = false;
            return true;
        }
        if (IsCommentDetailOpen)
        {
            CloseCommentDetail();
            return true;
        }
        DetachFromMainWindow(pausePlayback: true);
        return true;
    }

    private void OnEmbeddedHostKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isEmbeddedInMain || _closing) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = HandleEmbeddedBack();
            return;
        }
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false }) return;
        if (e.Key == Key.F)
        {
            e.Handled = true;
            ToggleFullscreen();
            return;
        }
        if (e.Key is Key.Right or Key.Left or Key.Space or Key.C or Key.D or Key.M)
            OnPreviewKeyDown(sender, e);
    }

    private void OnEmbeddedHostKeyUp(object sender, KeyEventArgs e)
    {
        if (!_isEmbeddedInMain || _closing) return;
        if (e.Key == Key.Right)
            OnPreviewKeyUp(sender, e);
    }

    private void OnEmbeddedPostureChanged(FrameworkElement source, LayoutPosture posture)
    {
        if (!_isEmbeddedInMain || _closing) return;
        if (_embeddedHost == null || !ReferenceEquals(source, _embeddedHost.FindName("RootGrid"))) return;
        // 旋转平板时保持单窗口播放不中断，仅即时重排内部为桌面并排/网页或安卓竖屏三段布局
        ApplyInfoPanelState();
        ApplyAlternateLayoutPreference(allowPreLoad: true);
        if (_tabletPortraitLayoutMounted) UpdateTabletPortraitMeasurements();
        else if (_webLayoutMounted) UpdateWebMeasurements();
        else if (_portraitLayoutMounted) UpdatePortraitMeasurements();
        UpdatePortraitImmersiveOverlay();
    }

    private void UpdatePortraitImmersiveOverlay()
    {
        if (BtnPortraitImmersiveComments == null) return;
        var show = _isFullscreen && IsPortraitVideoDetected && IsTabletPortraitPostureActive
                   && !_isLive && !_isListenMode;
        BtnPortraitImmersiveComments.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show && _portraitDrawerOpen) SetPortraitCommentDrawerOpen(false);
    }

    private void OnPortraitImmersiveCommentsClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetPortraitCommentDrawerOpen(!_portraitDrawerOpen);
    }

    private void OnPortraitDrawerCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetPortraitCommentDrawerOpen(false);
    }

    private void OnPortraitDrawerScrimClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SetPortraitCommentDrawerOpen(false);
    }

    internal void SetPortraitCommentDrawerOpen(bool open)
    {
        if (PortraitCommentDrawerOverlay == null || PortraitDrawerCommentsHost == null || PortraitDrawerChrome == null) return;
        if (open == _portraitDrawerOpen) return;
        if (open)
        {
            _portraitDrawerOpen = true;
            DetachWebElement(CommentsPanel);
            PortraitDrawerCommentsHost.Content = CommentsPanel;
            CommentsPanel.Visibility = Visibility.Visible;
            foreach (var control in new FrameworkElement[] { BtnBackToComments, CommentFab, CommentComposerPanel })
            {
                DetachWebElement(control);
                PortraitDrawerChrome.Children.Add(control);
            }
            var h = PlayerStage.ActualHeight > 0 ? PlayerStage.ActualHeight : ActualHeight;
            PortraitCommentDrawerSheet.Height = Math.Max(260, Math.Round(h * 0.65));
            if (ControlBar != null) ControlBar.Visibility = Visibility.Collapsed;
            if (BtnPortraitImmersiveComments != null) BtnPortraitImmersiveComments.Visibility = Visibility.Collapsed;
            PortraitCommentDrawerOverlay.Visibility = Visibility.Visible;
            UpdateCommentComposer();
            if (_aid > 0) _ = LoadCommentsAsync();
        }
        else
        {
            _portraitDrawerOpen = false;
            PortraitCommentDrawerOverlay.Visibility = Visibility.Collapsed;
            if (ControlBar != null) ControlBar.Visibility = Visibility.Visible;
            UpdatePortraitImmersiveOverlay();
            DetachWebElement(CommentsPanel);
            if (!UsesDetachedCommentsLayout)
            {
                if (!PageHost.Children.Contains(CommentsPanel)) PageHost.Children.Add(CommentsPanel);
                foreach (var control in new FrameworkElement[] { BtnBackToComments, CommentFab, CommentComposerPanel })
                {
                    DetachWebElement(control);
                    if (!InfoPanel.Children.Contains(control)) InfoPanel.Children.Add(control);
                }
            }
            else
            {
                WebCommentsHost.Content = CommentsPanel;
                foreach (var control in new FrameworkElement[] { BtnBackToComments, CommentFab, CommentComposerPanel })
                {
                    DetachWebElement(control);
                    if (!WebCommentChrome.Children.Contains(control)) WebCommentChrome.Children.Add(control);
                }
            }
            UpdateCommentComposer();
        }
    }
}
