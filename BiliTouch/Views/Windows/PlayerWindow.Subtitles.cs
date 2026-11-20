using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using Microsoft.Win32;

namespace BiliTouch.Views;

/// <summary>
/// 播放器原生字幕子系统：
/// 支持 B 站 CC / AI 字幕轨道自动拉取、多轨切换、本地外挂 SRT/VTT 字幕、本地视频内嵌 SPU 轨道、
/// 字幕字号缩放以及随控制栏显隐自动避让底部高度。
/// </summary>
public partial class PlayerWindow
{
    private readonly List<SubtitleTrack> _subtitleTracks = new();
    private IReadOnlyList<SubtitleCue> _subtitleCues = Array.Empty<SubtitleCue>();
    private SubtitleTrack? _activeSubtitleTrack;
    private CancellationTokenSource? _subtitleLoadCts;
    private int _subtitleGen;
    private bool _fillingSubtitleMenu;
    private bool _syncingSubtitleSlider;
    private string _lastSubtitleRendered = "";
    private bool _subtitleEnabled = true;

    private void InitializeSubtitleUi()
    {
        _subtitleEnabled = Svc.Settings.SubtitleEnabled;
        if (SubtitlePopup != null && BtnSubtitle != null)
        {
            SubtitlePopup.PlacementTarget = BtnSubtitle;
            RightAlignPopup(SubtitlePopup, BtnSubtitle);
            HookPopupToggle(SubtitlePopup, BtnSubtitle);
            SubtitlePopup.Opened += (_, _) =>
            {
                _controlsHideTimer?.Stop();
                if (_isLocalPlayback) RefreshLocalEmbeddedSubtitles();
                FillSubtitleMenu();
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => RightAlignPopup(SubtitlePopup, BtnSubtitle)));
            };
            SubtitlePopup.Closed += (_, _) =>
            {
                if (!_closing) StartControlsHideCountdown();
            };
        }
        if (SubtitleFontSlider != null)
        {
            _syncingSubtitleSlider = true;
            SubtitleFontSlider.Value = Svc.Settings.SubtitleFontScalePercent;
            _syncingSubtitleSlider = false;
        }
        UpdateSubtitleFontAppearance();
        UpdateSubtitleButton();
    }

    private void ResetSubtitlesForNewMedia()
    {
        _subtitleGen++;
        try { _subtitleLoadCts?.Cancel(); _subtitleLoadCts?.Dispose(); } catch { }
        _subtitleLoadCts = null;
        _subtitleTracks.Clear();
        _subtitleCues = Array.Empty<SubtitleCue>();
        _activeSubtitleTrack = null;
        _lastSubtitleRendered = "";
        if (SubtitlePopup != null) SubtitlePopup.IsOpen = false;
        HideSubtitleOverlay();
        UpdateSubtitleButton();
        FillSubtitleMenu();
    }

    private async Task LoadSubtitlesAsync(string bvid, long cid, long aid)
    {
        if (_closing || _isLive || cid <= 0) return;
        var gen = ++_subtitleGen;
        try { _subtitleLoadCts?.Cancel(); _subtitleLoadCts?.Dispose(); } catch { }
        var cts = new CancellationTokenSource();
        _subtitleLoadCts = cts;

        try
        {
            var tracks = await Svc.Api.GetSubtitlesAsync(bvid, cid, aid);
            if (_closing || gen != _subtitleGen || cts.IsCancellationRequested) return;

            _subtitleTracks.Clear();
            _subtitleTracks.AddRange(tracks);
            WriteLibVLCDebug($"[Subtitle] bvid={bvid} cid={cid} tracks={tracks.Count}\n");

            if (_subtitleEnabled && _subtitleTracks.Count > 0)
            {
                var preferred = SubtitleParser.PickPreferredTrack(_subtitleTracks, Svc.Settings.PreferredSubtitleLang);
                if (preferred != null)
                    await ActivateSubtitleTrackAsync(preferred, gen, cts.Token, persistChoice: false);
            }
            if (_closing || gen != _subtitleGen) return;
            FillSubtitleMenu();
            UpdateSubtitleButton();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            WriteLibVLCDebug($"[Subtitle] 加载失败: {ex.Message}\n");
        }
    }

    private async Task ActivateSubtitleTrackAsync(SubtitleTrack track, int expectedGen, CancellationToken ct, bool persistChoice)
    {
        if (track.EmbeddedSpuId >= 0)
        {
            _activeSubtitleTrack = track;
            _subtitleCues = Array.Empty<SubtitleCue>();
            _subtitleEnabled = true;
            HideSubtitleOverlay();
            try { _mp?.SetSpu(track.EmbeddedSpuId); } catch { }
            if (persistChoice) Svc.Settings.UpdateSubtitleSettings(enabled: true);
            UpdateSubtitleButton();
            return;
        }

        // 切回 WPF 原生字幕渲染时关闭 LibVLC 内嵌 SPU，避免双字幕重叠
        try { if (_mp != null && _mp.Spu >= 0) _mp.SetSpu(-1); } catch { }

        IReadOnlyList<SubtitleCue> cues;
        if (track.IsLocalFile)
        {
            cues = await Task.Run(() =>
            {
                if (!File.Exists(track.SubtitleUrl)) return Array.Empty<SubtitleCue>();
                var text = File.ReadAllText(track.SubtitleUrl);
                return track.SubtitleUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? SubtitleParser.ParseBilibiliJson(text)
                    : SubtitleParser.ParseSrtOrVtt(text);
            }, ct);
        }
        else
        {
            cues = await Svc.Api.DownloadSubtitleCuesAsync(track.SubtitleUrl, ct);
        }

        if (_closing || expectedGen != _subtitleGen || ct.IsCancellationRequested) return;

        _activeSubtitleTrack = track;
        _subtitleCues = cues;
        _subtitleEnabled = true;
        _lastSubtitleRendered = "";
        if (persistChoice)
        {
            Svc.Settings.UpdateSubtitleSettings(
                enabled: true,
                preferredLang: track.IsLocalFile ? null : track.Lan);
        }
        UpdateSubtitleButton();
        UpdateSubtitleDisplay(_lastKnownMs / 1000.0);
    }

    private void DisableSubtitles(bool persistChoice)
    {
        _subtitleEnabled = false;
        _activeSubtitleTrack = null;
        _subtitleCues = Array.Empty<SubtitleCue>();
        _lastSubtitleRendered = "";
        try { if (_mp != null && _mp.Spu >= 0) _mp.SetSpu(-1); } catch { }
        HideSubtitleOverlay();
        if (persistChoice) Svc.Settings.UpdateSubtitleSettings(enabled: false);
        UpdateSubtitleButton();
        FillSubtitleMenu();
    }

    private void ToggleSubtitleShortcut()
    {
        if (_closing || _isLive) return;
        if (_subtitleEnabled && _activeSubtitleTrack != null)
        {
            DisableSubtitles(persistChoice: true);
            Svc.Toast.Show("字幕已关闭");
            return;
        }

        if (_isLocalPlayback) RefreshLocalEmbeddedSubtitles();
        if (_subtitleTracks.Count == 0)
        {
            Svc.Toast.Show("当前视频暂无可用字幕");
            return;
        }
        var pick = SubtitleParser.PickPreferredTrack(_subtitleTracks, Svc.Settings.PreferredSubtitleLang) ?? _subtitleTracks[0];
        var gen = _subtitleGen;
        _ = SelectSubtitleTrackFromUiAsync(pick, gen, toast: true);
    }

    private async Task SelectSubtitleTrackFromUiAsync(SubtitleTrack track, int gen, bool toast = false)
    {
        try
        {
            await ActivateSubtitleTrackAsync(track, gen, CancellationToken.None, persistChoice: true);
            if (_closing || gen != _subtitleGen) return;
            FillSubtitleMenu();
            if (toast) Svc.Toast.Show("已开启字幕：" + track.DisplayLabel);
        }
        catch { }
    }

    private void OnSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (_isLive) return;
        TogglePlayerPopup(SubtitlePopup);
    }

    private void OnSubtitleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSubtitleMenu || SubtitleList == null) return;
        if (SubtitleList.SelectedItem is not ListBoxItem li) return;
        if (SubtitlePopup != null) SubtitlePopup.IsOpen = false;

        if (li.Tag is string tag && tag == "off")
        {
            DisableSubtitles(persistChoice: true);
            return;
        }
        if (li.Tag is SubtitleTrack track)
        {
            var gen = _subtitleGen;
            _ = SelectSubtitleTrackFromUiAsync(track, gen);
        }
    }

    private void OnLoadLocalSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (SubtitlePopup != null) SubtitlePopup.IsOpen = false;
        var dlg = new OpenFileDialog
        {
            Title = "选择字幕文件",
            Filter = "字幕文件 (*.srt;*.vtt;*.json)|*.srt;*.vtt;*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dlg.FileName)) return;

        var track = new SubtitleTrack
        {
            Id = -Math.Abs(dlg.FileName.GetHashCode()),
            Lan = "local-" + Path.GetFileName(dlg.FileName),
            LanDoc = "本地 · " + Path.GetFileName(dlg.FileName),
            SubtitleUrl = dlg.FileName,
            IsLocalFile = true,
        };
        _subtitleTracks.RemoveAll(t => t.IsLocalFile && string.Equals(t.SubtitleUrl, track.SubtitleUrl, StringComparison.OrdinalIgnoreCase));
        _subtitleTracks.Insert(0, track);
        var gen = _subtitleGen;
        _ = SelectSubtitleTrackFromUiAsync(track, gen, toast: true);
    }

    private void TryAutoLoadSidecarSubtitle(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(videoPath);
            var stem = Path.GetFileNameWithoutExtension(videoPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return;
            foreach (var ext in new[] { ".srt", ".vtt", ".json" })
            {
                var candidate = Path.Combine(dir, stem + ext);
                if (!File.Exists(candidate)) continue;
                var track = new SubtitleTrack
                {
                    Id = -Math.Abs(candidate.GetHashCode()),
                    Lan = "local-" + Path.GetFileName(candidate),
                    LanDoc = "本地 · " + Path.GetFileName(candidate),
                    SubtitleUrl = candidate,
                    IsLocalFile = true,
                };
                _subtitleTracks.Add(track);
                if (_subtitleEnabled && _activeSubtitleTrack == null)
                {
                    var gen = _subtitleGen;
                    _ = ActivateSubtitleTrackAsync(track, gen, CancellationToken.None, persistChoice: false);
                }
                break;
            }
            FillSubtitleMenu();
            UpdateSubtitleButton();
        }
        catch { }
    }

    private void OnSubtitleFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSubtitleSlider || _closing) return;
        var scale = Math.Clamp((int)Math.Round(e.NewValue), 75, 200);
        Svc.Settings.UpdateSubtitleSettings(fontScalePercent: scale);
        UpdateSubtitleFontAppearance();
    }

    private void RefreshLocalEmbeddedSubtitles()
    {
        if (_mp == null) return;
        try
        {
            var desc = _mp.SpuDescription;
            if (desc == null || desc.Length == 0) return;
            _subtitleTracks.RemoveAll(t => t.EmbeddedSpuId >= 0);
            foreach (var d in desc)
            {
                if (d.Id < 0) continue;
                _subtitleTracks.Add(new SubtitleTrack
                {
                    Id = d.Id,
                    Lan = $"spu:{d.Id}",
                    LanDoc = string.IsNullOrWhiteSpace(d.Name) ? $"内嵌字幕 {d.Id}" : $"内嵌 · {d.Name}",
                    EmbeddedSpuId = d.Id,
                });
            }
        }
        catch { }
    }

    private void FillSubtitleMenu()
    {
        if (SubtitleList == null) return;
        _fillingSubtitleMenu = true;
        try
        {
            SubtitleList.Items.Clear();
            var offItem = new ListBoxItem { Content = "关闭字幕", Tag = "off" };
            SubtitleList.Items.Add(offItem);

            ListBoxItem? selected = (!_subtitleEnabled || _activeSubtitleTrack == null) ? offItem : null;
            if (_subtitleTracks.Count == 0)
            {
                SubtitleList.Items.Add(new ListBoxItem
                {
                    Content = _isLocalPlayback ? "暂无内嵌字幕（可下方加载外挂）" : "当前视频暂无在线字幕",
                    IsEnabled = false,
                    Opacity = 0.65,
                });
            }
            else
            {
                foreach (var track in _subtitleTracks)
                {
                    var item = new ListBoxItem
                    {
                        Content = track.DisplayLabel,
                        Tag = track,
                    };
                    SubtitleList.Items.Add(item);
                    if (_subtitleEnabled && _activeSubtitleTrack != null &&
                        string.Equals(track.Lan, _activeSubtitleTrack.Lan, StringComparison.Ordinal) &&
                        string.Equals(track.SubtitleUrl, _activeSubtitleTrack.SubtitleUrl, StringComparison.Ordinal))
                    {
                        selected = item;
                    }
                }
            }
            SubtitleList.SelectedItem = selected ?? offItem;
        }
        finally
        {
            _fillingSubtitleMenu = false;
        }
    }

    private void UpdateSubtitleButton()
    {
        if (BtnSubtitle == null || SubtitleBtnText == null) return;
        BtnSubtitle.Visibility = _isLive ? Visibility.Collapsed : Visibility.Visible;
        var active = _subtitleEnabled && _activeSubtitleTrack != null;
        SubtitleBtnText.Text = active ? "字幕" : "字幕";
        SubtitleBtnText.Foreground = active
            ? Brushes.White
            : new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
        BtnSubtitle.ToolTip = active
            ? $"字幕：{_activeSubtitleTrack!.DisplayLabel}（点击切换，快捷键 C）"
            : _subtitleTracks.Count > 0
                ? $"字幕已关闭（共 {_subtitleTracks.Count} 条轨道，快捷键 C）"
                : "字幕（当前无在线字幕，可加载本地 .srt/.vtt）";
    }

    internal void UpdateSubtitleDisplay(double timeSec)
    {
        if (_closing || SubtitleOverlay == null || SubtitleText == null) return;
        if (!_subtitleEnabled || _activeSubtitleTrack == null || _activeSubtitleTrack.EmbeddedSpuId >= 0 ||
            _subtitleCues.Count == 0 || _isListenMode || _isLive || _ended)
        {
            HideSubtitleOverlay();
            return;
        }

        var text = SubtitleParser.FindActiveText(_subtitleCues, timeSec);
        if (string.Equals(text, _lastSubtitleRendered, StringComparison.Ordinal)) return;
        _lastSubtitleRendered = text;

        if (text.Length == 0)
        {
            SubtitleOverlay.Visibility = Visibility.Collapsed;
            SubtitleText.Text = "";
        }
        else
        {
            SubtitleText.Text = text;
            SubtitleOverlay.Visibility = Visibility.Visible;
        }
    }

    private void HideSubtitleOverlay()
    {
        _lastSubtitleRendered = "";
        if (SubtitleOverlay != null) SubtitleOverlay.Visibility = Visibility.Collapsed;
        if (SubtitleText != null) SubtitleText.Text = "";
    }

    private void UpdateSubtitleLayoutPosition()
    {
        if (SubtitleOverlay == null) return;
        // 控制栏显示时把字幕抬高到控制栏上方，隐藏时自然沉到底部留白处
        var bottom = _controlsHidden ? 26 : 92;
        SubtitleOverlay.Margin = new Thickness(36, 0, 36, bottom);
        UpdateSubtitleFontAppearance();
    }

    private void UpdateSubtitleFontAppearance()
    {
        var scalePercent = Math.Clamp(Svc.Settings.SubtitleFontScalePercent, 75, 200);
        if (SubtitleFontValue != null) SubtitleFontValue.Text = scalePercent + "%";
        if (SubtitleText == null) return;
        var width = VideoAreaRoot?.ActualWidth > 0 ? VideoAreaRoot.ActualWidth : 960;
        var basePx = Math.Clamp(width / 46.0, 15.0, 26.0);
        var fontSize = Math.Round(basePx * (scalePercent / 100.0), 1);
        SubtitleText.FontSize = fontSize;
        SubtitleText.LineHeight = Math.Round(fontSize * 1.38, 1);
        if (SubtitleOverlay != null)
            SubtitleOverlay.MaxWidth = Math.Max(300, width - 72);
    }
}
