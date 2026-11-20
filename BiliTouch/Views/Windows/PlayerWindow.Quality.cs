using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Views.Controls;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliTouch.Views;

public partial class PlayerWindow
{
    // ------------------------------------------------------------ 清晰度

    /// <summary>填充清晰度菜单并高亮当前项(以 B 站实际返回的清晰度为准)</summary>
    private void FillQualityMenu(int actualQn)
    {
        if (QualityList == null) return;
        _fillingQualityMenu = true;
        try
        {
            QualityList.Items.Clear();
            foreach (var (qn, label) in _qualities)
                QualityList.Items.Add(new ListBoxItem { Tag = qn, Content = label });

            // ---- 音质分区（杜比采用可选方案；普通 AAC 同样展示当前码率）----
            //   ① 存在杜比音轨时：将「默认最高音质（Hi-Res 或最高立体声 AAC）」与「杜比全景声」
            //      同时列为可选项，默认不强选杜比，让用户按需切换；
            //   ② 无杜比音轨时：纯信息展示当前在播音轨与码率（如 Hi-Res 1400 kbps / 高品质 192 kbps）。
            var activeTrack = ResolveActiveAudioTrack();
            if (_audioTracks.Count > 0)
            {
                var hiResTrack = _audioTracks.FirstOrDefault(a => a.Quality == Models.AudioQuality.HiRes);
                var dolbyTrack = _audioTracks.FirstOrDefault(a => a.Quality == Models.AudioQuality.Dolby);
                var bestAacTrack = _audioTracks
                    .Where(a => a.Quality is Models.AudioQuality.High or Models.AudioQuality.Standard)
                    .OrderByDescending(a => a.Bandwidth)
                    .ThenByDescending(a => a.TrackId)
                    .FirstOrDefault();

                QualityList.Items.Add(new ListBoxItem
                {
                    Content = "── 音质 ──",
                    IsEnabled = false,
                    Focusable = false,
                    Padding = new Thickness(6, 6, 6, 3),
                });

                if (dolbyTrack != null)
                {
                    var nonDolbyPrimary = hiResTrack ?? bestAacTrack;
                    if (nonDolbyPrimary != null)
                    {
                        var isSelected = _actualAudio != Models.AudioQuality.Dolby
                            && (nonDolbyPrimary.Quality == _actualAudio
                                || (nonDolbyPrimary.Quality is Models.AudioQuality.High or Models.AudioQuality.Standard
                                    && _actualAudio is Models.AudioQuality.High or Models.AudioQuality.Standard));
                        QualityList.Items.Add(new ListBoxItem
                        {
                            Tag = nonDolbyPrimary,
                            Content = (isSelected ? "✓ " : "") + nonDolbyPrimary.DisplayLabel,
                        });
                    }
                    QualityList.Items.Add(new ListBoxItem
                    {
                        Tag = dolbyTrack,
                        Content = (_actualAudio == Models.AudioQuality.Dolby ? "✓ " : "") + dolbyTrack.DisplayLabel,
                    });
                }
                else
                {
                    var cur = activeTrack ?? hiResTrack ?? bestAacTrack ?? _audioTracks[0];
                    QualityList.Items.Add(new ListBoxItem
                    {
                        Content = "正在使用：" + cur.DisplayLabel,
                        IsEnabled = false,
                        Focusable = false,
                        Padding = new Thickness(10, 6, 10, 6),
                    });
                }
            }
            if (actualQn > 0) _currentQn = actualQn;
            // ★★★ 按钮上的文字必须按"**接口实际给的**档位"或"用户的偏好"来取,
            //   **绝不能**对 _currentQn 直接调 QnToLabel(2026-10-04 修):
            //   「默认画质 = 自动」时 _currentQn 是发给接口的请求上界 127, 而 QnToLabel(127)
            //   是"8K"。于是本地文件/直播/起播失败(actualQn == 0)
            //   这些"没有实际档位可显示"的路径上, 按钮会写成一个用户根本没有的档位。
            //   这里只显示**确实在播的那一档**: 有实际档位就说它, 没有(本地/直播)就说用户的偏好,
            //   两者都不会把"请求用的内部值"当成画质名报给用户。
            var label2 = actualQn > 0
                ? ApiClient.QnToLabel(actualQn)
                : QualityPreference.LabelOf(Svc.Settings.PreferredQualityQn);
            if (QualityText != null) QualityText.Text = label2;

            // ★ 音质角标：Hi-Res / 杜比显示专属标识，其余在线 DASH 音轨显示当前码率（如 192 kbps）
            if (BtnAudioBadge != null && AudioBadgeText != null)
            {
                var isPremium = _actualAudio is Models.AudioQuality.HiRes or Models.AudioQuality.Dolby;
                var badge = _actualAudio switch
                {
                    Models.AudioQuality.HiRes => "Hi-Res",
                    Models.AudioQuality.Dolby => "杜比音轨",
                    _ => (!_isLive && !_isLocalPlayback) ? (activeTrack?.BitrateBadgeText ?? "") : "",
                };
                if (badge.Length > 0)
                {
                    AudioBadgeText.Text = badge;
                    if (isPremium)
                    {
                        BtnAudioBadge.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x6B, 0x9D));
                        AudioBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD9, 0xE6));
                    }
                    else
                    {
                        BtnAudioBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                        AudioBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
                    }
                    var bitrateSuffix = activeTrack != null && activeTrack.EffectiveBitrateKbps > 0
                        ? $"（{activeTrack.EffectiveBitrateKbps} kbps）"
                        : "";
                    BtnAudioBadge.ToolTip = _actualAudio switch
                    {
                        Models.AudioQuality.Dolby =>
                            $"已选择杜比音轨{bitrateSuffix}；Atmos 输出取决于系统空间音效、输出设备和解码链路，当前未验证。无声时可右键选择音频兼容重试（仅本次）。",
                        Models.AudioQuality.HiRes =>
                            $"已选择 Hi-Res 音轨{bitrateSuffix}。无声时可右键选择音频兼容重试（仅本次）。",
                        _ => _audioTracks.Any(a => a.Quality == Models.AudioQuality.Dolby)
                            ? $"当前音轨：{activeTrack?.DisplayLabel ?? badge}（清晰度菜单中可按需切换杜比音轨）"
                            : $"当前音轨：{activeTrack?.DisplayLabel ?? badge}",
                    };
                    BtnAudioBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    // 直播/本地文件/无码率信息时隐藏
                    BtnAudioBadge.Visibility = Visibility.Collapsed;
                }
            }
            // 高亮与实际清晰度匹配的项
            for (var i = 0; i < QualityList.Items.Count; i++)
            {
                if (QualityList.Items[i] is ListBoxItem li && li.Tag is int q && q == actualQn)
                {
                    QualityList.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _fillingQualityMenu = false;
        }
    }

    /// <summary>
    /// 清晰度 / 弹幕区域弹层是否**有任意一个开着**。
    ///
    /// ★ 为什么需要它(2026-10-13 用户报"点清晰度 → 点空白处收起 → 视频变 2 倍速"):
    ///   这两个 Popup 是 <c>StaysOpen="False"</c>, 而 Popup 是**独立 HWND**。
    ///   实测(探针 bd-probe-coin, 真实 Win32 鼠标点击, 时间戳):
    ///     <code>
    ///     12:14:38.955  VIDEO UP                ← 只有 Up, 没有 Down!
    ///     12:14:38.975  *** POPUP CLOSED ***    ← Closed 晚 20ms
    ///     </code>
    ///   ⇒ 三个"当前状态"信号在 Up 到达时**全部不可用**:
    ///     · <c>Popup.IsOpen</c> 已被 WPF 自己的收起逻辑置成 false;
    ///     · <c>Mouse.LeftButton</c> 已回报 Released;
    ///     · <c>Popup.Closed</c> 还没来。
    ///   ⇒ 第一版写在 Closed 里的时间窗是**死代码**(等它记上, Up 早跑完了),
    ///     第二版的 IsOpen 判据同样挡不住这一次 Up。
    ///
    ///   ★ 真正可靠的判据是**手势配对**: 关闭弹层那一次点击, 它的 Down 被弹层 HWND
    ///     吃掉, 只有 Up 漏进视频区 —— 也就是"没有配对 Down 的 Up"。
    ///     这种 Up 一律丢弃即可, 且**不依赖任何事件顺序**。
    /// </summary>
    private bool AnyPlayerPopupOpen
        => (QualityPopup != null && QualityPopup.IsOpen)
        || (SubtitlePopup != null && SubtitlePopup.IsOpen)
        || (DanmakuAreaPopup != null && DanmakuAreaPopup.IsOpen)
        || (SpeedPopup != null && SpeedPopup.IsOpen)
        || (VolumePopup != null && VolumePopup.IsOpen);

    /// <summary>
    /// 视频区手势是否已经由一次 **Down** 正式起步。
    ///
    /// ★ 唯一可靠的弹层余波判据(见 <see cref="AnyPlayerPopupOpen"/> 里的实测记录):
    ///   弹层开着时按下的那一下不算视频区手势(它属于浮层), 所以这里必须为 false;
    ///   否则随后漏进来的 Up 会被当成一次完整的"单击", 于是**收起清晰度列表会把视频暂停**,
    ///   更糟的是漏进来的 Down 会起长按计时器 → 500ms 后 SetRate(2.0) 无故快进。
    /// </summary>
    private bool _videoGestureActive;

    /// <summary>
    /// 视频区**右键**手势是否已由一次 Down 正式起步(右键那条线的 <see cref="_videoGestureActive"/>)。
    ///
    /// ★ 为什么右键要单独一份而不是复用上面那个: 两者是**并行的两条手势**,
    ///   用户完全可能左键按着拖、中途点右键; 共用一个标记会互相把对方的"配对"抹掉。
    /// </summary>
    private bool _rightGestureActive;

    private void OnQualityClick(object sender, RoutedEventArgs e)
    {
        TogglePlayerPopup(QualityPopup);
    }

    private readonly Dictionary<System.Windows.Controls.Primitives.Popup, long> _popupClosedTicks = new();
    private readonly List<(System.Windows.Controls.Primitives.Popup Popup, System.ComponentModel.DependencyPropertyDescriptor Descriptor, EventHandler Handler)> _popupToggleHooks = new();

    private void HookPopupToggle(System.Windows.Controls.Primitives.Popup popup, Button trigger)
    {
        if (popup == null || trigger == null) return;
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            System.Windows.Controls.Primitives.Popup.IsOpenProperty,
            typeof(System.Windows.Controls.Primitives.Popup));
        EventHandler onIsOpenChanged = (_, _) =>
        {
            if (popup.IsOpen) return;
            if (IsCursorOverElement(trigger)) _popupClosedTicks[popup] = Environment.TickCount64;
        };
        descriptor.AddValueChanged(popup, onIsOpenChanged);
        _popupToggleHooks.Add((popup, descriptor, onIsOpenChanged));

        trigger.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_closing || !popup.IsOpen) return;
            popup.IsOpen = false;
            e.Handled = true;
        };
    }

    private static bool IsCursorOverElement(FrameworkElement element)
    {
        try
        {
            if (!GetCursorPos(out var pt)) return false;
            var tl = element.PointToScreen(new Point(0, 0));
            var br = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return pt.X >= tl.X && pt.X <= br.X && pt.Y >= tl.Y && pt.Y <= br.Y;
        }
        catch { return false; }
    }

    private void TogglePlayerPopup(System.Windows.Controls.Primitives.Popup popup, Action? onOpen = null)
    {
        if (_closing || popup == null) return;
        long? closedAt = _popupClosedTicks.TryGetValue(popup, out var tick) ? tick : null;
        switch (Helpers.PopupToggle.Decide(popup.IsOpen, closedAt, Environment.TickCount64))
        {
            case Helpers.PopupToggle.Action.Close:
                popup.IsOpen = false;
                break;
            case Helpers.PopupToggle.Action.StayClosed:
                break;
            default:
                onOpen?.Invoke();
                popup.IsOpen = true;
                break;
        }
    }

    private void UnhookPopupToggles()
    {
        foreach (var (popup, descriptor, handler) in _popupToggleHooks)
        {
            try { descriptor.RemoveValueChanged(popup, handler); } catch { }
        }
        _popupToggleHooks.Clear();
        _popupClosedTicks.Clear();
    }

    private Models.AudioTrack? ResolveActiveAudioTrack()
    {
        if (_audioTracks.Count == 0) return null;
        if (!string.IsNullOrEmpty(_listenAudioUrl))
        {
            var byUrl = _audioTracks.FirstOrDefault(t =>
                t.Quality == _actualAudio && string.Equals(t.Url, _listenAudioUrl, StringComparison.Ordinal));
            if (byUrl != null) return byUrl;
        }
        var byQuality = _audioTracks
            .Where(t => t.Quality == _actualAudio)
            .OrderByDescending(t => t.Bandwidth)
            .ThenByDescending(t => t.TrackId)
            .FirstOrDefault();
        if (byQuality != null) return byQuality;
        if (_actualAudio == Models.AudioQuality.Auto)
        {
            return _audioTracks
                .Where(t => t.Quality is Models.AudioQuality.High or Models.AudioQuality.Standard)
                .OrderByDescending(t => t.Bandwidth)
                .ThenByDescending(t => t.TrackId)
                .FirstOrDefault();
        }
        return null;
    }

    private void OnQualitySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingQualityMenu || QualityPopup == null) return;
        if (QualityList.SelectedItem is not ListBoxItem li) return;
        // 音质项: Tag 是 AudioTrack
        if (li.Tag is Models.AudioTrack track)
        {
            QualityPopup.IsOpen = false;
            if (_switchingQuality) return;
            // 从杜比切回普通立体声 AAC 时，偏好落回 Auto（保留未来视频自动选 Hi-Res 的能力，同时不默认选杜比）
            var targetQuality = track.Quality is Models.AudioQuality.High or Models.AudioQuality.Standard
                ? Models.AudioQuality.Auto
                : track.Quality;
            _ = SwitchAudioQualityAsync(targetQuality);
            return;
        }
        if (li.Tag is not int qn) return;
        QualityPopup.IsOpen = false;
        if (qn == _currentQn || _switchingQuality) return;
        _ = SwitchQualityAsync(qn);
    }

    /// <summary>音质档位的中文名(提示文案用)</summary>
    private static string 音质名(Models.AudioQuality q) => q switch
    {
        Models.AudioQuality.HiRes => "Hi-Res 无损",
        Models.AudioQuality.Dolby => "杜比全景声",
        Models.AudioQuality.High => "高品质音质",
        Models.AudioQuality.Standard => "标准音质",
        _ => "最佳音质",
    };

    /// <summary>
    /// 切换音质(2026-11-04)。只换音轨, **不动画面、不 seek**。
    ///
    /// 为什么要单独一条而不是复用 SwitchQualityAsync:
    ///   换清晰度要重新取整个 playurl 并重建 input(画面会闪一下); 而换音质只要把
    ///   播放中的音轨换掉。但 libvlc 的 input-slave 是**建 input 时**定的, 运行中换不了,
    ///   所以实际上仍要重建 input —— 只是**保持同一个视频地址与进度**, 观感上只有极短的停顿。
    ///
    /// ★ 选不到用户要的档(没大会员/UP 没传 Hi-Res)时**不静默降级**:
    ///   弹一句说明, 并把偏好落回 Auto, 免得用户以为选了 Hi-Res 就真在听无损。
    /// </summary>
    private async void OnAudioCompatibilityRetry(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isLive || _isLocalPlayback || _currentCid <= 0) { Svc.Toast.Show("当前来源不支持音轨兼容重试"); return; }
        var track = _audioTracks.Where(t => t.Quality is Models.AudioQuality.High or Models.AudioQuality.Standard)
            .OrderByDescending(t => t.Bandwidth).FirstOrDefault();
        if (track == null) { Svc.Toast.Show("这条视频没有可用的 AAC 兼容音轨"); return; }
        // High keeps DASH and video resolution intact, even when only a lower AAC track exists.
        await SwitchAudioQualityAsync(Models.AudioQuality.High, remember: false);
    }

    private async Task SwitchAudioQualityAsync(Models.AudioQuality quality, bool remember = true)
    {
        if (_closing || _switchingQuality || _isLive || _currentCid <= 0) return;
        _switchingQuality = true;
        var generation = _loadGen;
        var bvid = _currentBvid; var cid = _currentCid; var qn = _currentQn;
        try
        {
            CancelRemuxPrefetch();
            var resumeMs = Math.Max(0, _mp.Time);
            ShowLoading("正在切换音质…");
            var (ok, err, main, backups, durMs, qualities, actualQn, audioUrl, remux,
                audioTracks, actualAudio) =
                await Svc.Api.GetPlayUrlAsync(bvid, cid, qn, quality);
            if (_closing || generation!=_loadGen) return;
            if (!ok || string.IsNullOrEmpty(main))
            {
                ShowFail(err ?? "切换音质失败");
                return;
            }
            if (!remember && actualAudio is not (Models.AudioQuality.High or Models.AudioQuality.Standard))
            {
                ShowFail("服务器未返回 AAC 兼容音轨，请重试或选择其他音质");
                return;
            }
            // ★★★ 2026-11-04 二次改: 用户要求「不应切回 aac」。
            //   原来这里在"服务端没给这一档"时会把偏好**退回 Auto**, 而 Auto 在
            //   "这条视频没有高级音质"时就是最高 AAC —— 等于悄悄把用户降回 AAC。
            //   现在:
            //     · 拿到了要的档 ⇒ 记住它;
            //     · 拿到的是**另一种高级音质**(杜比↔Hi-Res 互退) ⇒ 记住实际那档, 并说明;
            //     · 只有**连高级音质都没有**时才提示, 且偏好保持用户的原始选择
            //       (下次遇到有高级音质的视频仍然按他的意愿走)。
            if (!remember)
            {
                Svc.Toast.Show($"本次使用{音质名(actualAudio)}，原音质偏好已保留");
            }
            else if (quality == Models.AudioQuality.Auto)
            {
                Svc.Settings.PreferredAudioQuality = Models.AudioQuality.Auto;
                Svc.Toast.Show($"已切换至{音质名(actualAudio)}（杜比已改为按需选择）");
            }
            else if (actualAudio == quality)
            {
                Svc.Settings.PreferredAudioQuality = quality;
            }
            else if (actualAudio is Models.AudioQuality.HiRes or Models.AudioQuality.Dolby)
            {
                Svc.Settings.PreferredAudioQuality = actualAudio;
                Svc.Toast.Show($"这条视频没有{音质名(quality)}, 已改用{音质名(actualAudio)}");
            }
            else
            {
                Svc.Toast.Show($"这条视频没有{音质名(quality)}(需大会员且 UP 主上传)");
            }
            _audioTracks = audioTracks;
            _actualAudio = actualAudio;

            // 重建 input: 音轨是建 input 时挂上去的(input-slave), 运行中换不了。
            // ★ 用与首次加载**同一个** BuildMedia, 免得两处选项慢慢漂移
            //   (Referer/UA/缓存策略/音频挂载有一处不同就是"换完音质播不动")。
            _lastPlayUrl=main;_lastAudioUrl=audioUrl;_listenAudioUrl=audioUrl;
            RememberPlayUrlFreshness(main ?? audioUrl, backups);
            _resumeAfterSwitchMs=resumeMs>0?resumeMs:0;
            _resumeByStartTime=false;
            Svc.Settings.Save();
            await ReplaceNativeInputAsync(() =>
            {
                var mediaUrl = _isListenMode && !string.IsNullOrEmpty(audioUrl) ? audioUrl : main;
                if (string.IsNullOrEmpty(mediaUrl))
                {
                    ShowFail("切换音质失败");
                    return;
                }
                var media = _isListenMode && !string.IsNullOrEmpty(audioUrl)
                    ? BuildMedia(mediaUrl, null)
                    : BuildMedia(mediaUrl, audioUrl);
                if (!PlayMedia(media)) ShowFail("切换音质失败");
            });
            if (_closing || generation != _loadGen) return;
            FillQualityMenu(actualQn > 0 ? actualQn : _currentQn);
        }
        catch (Exception ex)
        {
            if (!_closing && generation == _loadGen) ShowFail("切换音质失败: " + ex.Message);
        }
        finally
        {
            if (generation == _loadGen) _switchingQuality = false;
        }
    }

    /// <summary>
    /// 切换直播清晰度: 重新取一路流, 不 seek(直播没有时间轴)。
    ///
    /// ★ 服务端可能降级(请求原画但账号没权限): 拿回来的 actualQn 才是真在播的档,
    ///   界面按它写, 不能按请求值写 —— 否则用户看到"原画"实际是 720p。
    /// </summary>
    private async Task SwitchLiveQualityAsync(int qn)
    {
        if (_closing || _switchingQuality || _liveRoomId <= 0) return;
        _switchingQuality = true;
        try
        {
            ShowLoading("正在切换直播清晰度…");
            var (ok, err, url, actualQn, options) =
                await Svc.Api.GetLivePlayUrlExAsync(_liveRoomId, qn);
            if (_closing) return;
            if (!ok || string.IsNullOrEmpty(url))
            {
                ShowFail(err ?? "切换直播清晰度失败");
                return;
            }
            _liveQn = qn;
            _liveActualQn = actualQn;
            if (options.Count > 0)
            {
                _qualities.Clear();
                foreach (var (q, label) in options) _qualities.Add((q, label));
            }
            FillQualityMenu(actualQn);
            if (QualityText != null && options.Count > 0)
            {
                var label = options.FirstOrDefault(o => o.qn == actualQn).label;
                if (!string.IsNullOrEmpty(label)) QualityText.Text = label;
            }
            _lastPlayUrl = url;

            var liveCaching = BufferPolicy.Resolve(BufferMediaKind.Live, url, shortVideo: false,
                Svc.Settings.NetworkCachingMs, Svc.Settings.AutoBufferPolicy);
            var liveOptions = new List<string>
            {
                ":no-video-title-show",
                ":http-referrer=https://live.bilibili.com/" + _liveRoomId,
                ":http-user-agent=" + PlayerUserAgent,
                ":network-caching=" + liveCaching,
                ":live-caching=" + liveCaching
            };
            var media = new Media(_libVLC, new Uri(url), liveOptions.ToArray());
            _isReliableSeekSource = true;
            if (!PlayMedia(media)) ShowFail("切换直播清晰度失败");
        }
        catch (Exception ex)
        {
            ShowFail("切换直播清晰度失败: " + ex.Message);
        }
        finally
        {
            _switchingQuality = false;
        }
    }

    /// <summary>
    /// 启动直播弹幕(2026-11-04)。
    ///
    /// 与点播弹幕完全两套: 点播是 HTTP 拉 XML 按时间轴播; 直播是 WebSocket 长连接,
    /// 服务端推一条就画一条(见 <see cref="LiveDanmakuService"/>)。
    ///
    /// ★ 弹幕开关跟着设置走: 用户关了弹幕就不连(省流量也省得白跑一个长连接)。
    /// ★ 服务自己管重连与心跳, 这里只负责起停。
    /// </summary>
    private void StartLiveDanmaku(long roomId)
    {
        StopLiveDanmaku();
        if (_closing || !Svc.Settings.DanmakuEnabled) return;
        try
        {
            _liveDanmaku = new LiveDanmakuService(Svc.Api, Dispatcher);
            _liveDanmaku.Received += OnLiveDanmakuReceived;
            _liveDanmaku.ConnectionChanged += _ => UpdateLiveDanmakuStatus();
            _liveDanmakuCount = 0;
            _liveDanmaku.Start(roomId);
            UpdateLiveDanmakuStatus();
        }
        catch (Exception ex)
        {
            // 弹幕连不上不能影响看直播: 记日志, 不弹窗
            App.ReportError(ex);
        }
    }

    private void StopLiveDanmaku()
    {
        if (_liveDanmaku == null) return;
        try
        {
            _liveDanmaku.Received -= OnLiveDanmakuReceived;
            _liveDanmaku.Dispose();
        }
        catch { }
        _liveDanmaku = null;
    }

    /// <summary>本次直播累计收到的弹幕条数(用于界面显示"弹幕已连接 · N 条")</summary>
    private int _liveDanmakuCount;

    private void OnLiveDanmakuReceived(LiveDanmakuService.Message m)
    {
        // ★★★ 2026-11-04 修"直播还是看不到弹幕":
        //   实测走过的坑都补在这里, 每条都有明确目的 ——
        //   ① **弹幕总开关关着**时直接丢弃(用户设置 DanmakuEnabled=false)。
        //      但直播的"看不到"往往是这条, 而不是连线失败 —— 所以下面要给出可见状态。
        //   ② `DanmakuCanvas` 尺寸为 0 时 AddDanmaku 会静默 return(直播刚进还没布局)。
        //      这里延迟重试到布局就绪。
        //   ③ 直播的弹幕时钟不走媒体时间轴(见 AddDanmaku 里 currentSeconds 的说明),
        //      所以泳道可能全挤在 0 秒。给一个自增的虚拟时间, 保证分散开。
        if (_closing) return;
        _liveDanmakuCount++;
        UpdateLiveDanmakuStatus();

        if (!Svc.Settings.DanmakuEnabled) return;
        // 系统消息(进场/礼物)只显示高亮的那些(礼物/醒目留言), 进场不刷屏
        if (m.IsSystem && !m.IsHighlight) return;
        var text = string.IsNullOrEmpty(m.User) ? m.Text : m.User + "：" + m.Text;

        // 布局还没就绪(刚进直播间)就等一帧再补, 否则这条弹幕会被静默丢掉
        if (DanmakuCanvas == null || DanmakuCanvas.ActualWidth <= 0 || DanmakuCanvas.ActualHeight <= 0)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_closing) AddDanmaku(text, 1, 0);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        // mode 1 = 滚动弹幕; 直播没有时间轴, 传 0 让 AddDanmaku 用当前时钟
        AddDanmaku(text, 1, 0);
    }

    /// <summary>
    /// 把直播弹幕的连接状态与计数写到界面上(2026-11-04)。
    ///
    /// ★ 为什么要可见: 用户看不到弹幕时, 无法判断是"没连上"、"连上但没人发"、
    ///   还是"总开关关着"。有了这行字, 一眼就能定位, 不用再猜。
    /// </summary>
    private void UpdateLiveDanmakuStatus()
    {
        if (LiveDanmakuStatus == null) return;
        if (!Svc.Settings.DanmakuEnabled)
        {
            LiveDanmakuStatus.Text = "弹幕已关闭";
            LiveDanmakuStatus.Visibility = Visibility.Visible;
            return;
        }
        var connected = _liveDanmaku?.IsConnected == true;
        LiveDanmakuStatus.Text = connected
            ? $"弹幕已连接 · {_liveDanmakuCount} 条"
            : $"弹幕连接中… · {_liveDanmakuCount} 条";
        LiveDanmakuStatus.Visibility = Visibility.Visible;
    }

    /// <summary>切换清晰度: 记住播放进度, 换流后自动恢复</summary>
    private async Task SwitchQualityAsync(int qn)
    {
        if (_closing || _switchingQuality) return;
        // ★ 直播分支(2026-11-04): 直播没有 cid, 走 getRoomPlayInfo 重新取流。
        //   之前这里 `_currentCid <= 0` 直接 return, 于是直播的清晰度菜单点了没反应。
        if (_isLive)
        {
            await SwitchLiveQualityAsync(qn);
            return;
        }
        if (_currentCid <= 0) return;
        _switchingQuality = true;
        var generation = _loadGen;
        var bvid = _currentBvid; var cid = _currentCid;
        try
        {
            var resumeMs = _mp.Time;
            ShowLoading("正在切换清晰度…");

            // 换清晰度 = 换成另一路流: 上一档的合流立刻作废(缓存键里带 qn, 不会互相污染)
            CancelRemuxPrefetch();

            var (_, playErr, main, backups, _, qualities, actualQn, audioUrl, remux,
                audioTracks, actualAudio) =
                await Svc.Api.GetPlayUrlAsync(bvid, cid, qn, Svc.Settings.PreferredAudioQuality);
            if (_closing || generation != _loadGen) return;

            // 和首次加载同一套: 这一档已缓存就播本地, 没缓存就下完再播(单文件源: 跳转快、结尾完整)
            var local = ShortVideoCache.TryGet(bvid, actualQn, actualAudio);
            if (local == null && ShortVideoCache.WorthCaching(remux))
                local = await PrepareShortVideoAsync(remux!, actualQn);
            if (_closing || generation != _loadGen) return;

            var url = local ?? main;
            if (string.IsNullOrEmpty(url) && backups.Count > 0) url = backups[0];
            if (string.IsNullOrEmpty(url))
            {
                // 以前这里只有一句写死的"该清晰度暂时不可用" —— 而真正的原因(大会员/付费专属 /
                // 网络失败 / 该档无源)在 API 层就被丢掉了, 用户只能反复换档试。
                Svc.Toast.Show(string.IsNullOrEmpty(playErr) ? "该清晰度暂时不可用" : playErr);
                HideLoading();
                return;
            }
            _qualities.Clear();
            _qualities.AddRange(qualities);
            _audioTracks = audioTracks;
            _actualAudio = actualAudio;
            _listenAudioUrl = audioUrl;
            FillQualityMenu(actualQn > 0 ? actualQn : qn);

            // 起播后(Playing 事件)自动恢复到切换前进度
            _resumeAfterSwitchMs = resumeMs > 2000 ? resumeMs : 0;
            _resumeByStartTime = false;
            // 新流重新计时"提前结束自救"
            _eofResumeCount = 0;
            _lastEofResumeMs = 0;
            // 记住换过去的这一路: 之后点「重播」要回到同一个源, 而不是换回上一个清晰度
            _lastPlayUrl = url;
            // 本地合流是单文件源: 不能带 input-slave, 否则等于给同一个文件再挂一条音轨
            _lastAudioUrl = local != null ? null : audioUrl;
            // 「听视频」的音轨地址跟着新档位更新
            _listenAudioUrl = audioUrl;
            RememberPlayUrlFreshness(!string.IsNullOrEmpty(main) ? main : audioUrl, backups);
            // 必须先 Stop: LibVLC 3 里已有 input 在播时 play() 会被**直接忽略**,
            // 不 Stop 的话新 Media 根本不会起播 —— 这就是"切换清晰度没反应"
            // ★ 聆听态下切清晰度: 仍然只播音轨(用户此刻要的是声音, 不该突然出画面再切回去)。
            //   没有可用音轨时退回整路播放, 至少不会什么都不播。
            await ReplaceNativeInputAsync(() =>
            {
                if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
                    PlayMedia(BuildMedia(_listenAudioUrl!, null));
                else
                    PlayMedia(BuildMedia(url!, _lastAudioUrl));
            });
            if (_closing || generation != _loadGen) return;
            UpdatePlayButton();
        }
        catch (Exception ex)
        {
            if (_closing || generation != _loadGen) return;
            Svc.Toast.Show("切换清晰度失败: " + ex.Message);
            HideLoading();
        }
        finally
        {
            if (generation == _loadGen) _switchingQuality = false;
        }
    }

}
