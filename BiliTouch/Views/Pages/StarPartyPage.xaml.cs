using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Services;
using LibVLCSharp.Shared;

namespace BiliTouch.Views.Pages;

public partial class StarPartyPage : UserControl
{
    private XteamSiteData? _data;
    private XteamProjectDetail? _currentDetail;
    private string _pickFilter = "all";
    private bool _loadedOnce;

    // 原生 OST 音频播放器（基于 LibVLC，支持 .ogg / .mp3）
    private LibVLCSharp.Shared.MediaPlayer? _audioPlayer;
    private Media? _currentMedia;
    private DispatcherTimer? _audioTimer;
    private bool _audioPlaying;
    private bool _audioScrubbing;

    public StarPartyPage()
    {
        InitializeComponent();
        ScreenshotGallery.CloseRequested += () => ScreenshotGallery.Visibility = Visibility.Collapsed;
        AudioProgressSlider.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, _) => _audioScrubbing = true), true);
        Loaded += async (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await LoadAsync(forceRefresh: false);
        };
        Unloaded += (_, _) => DisposeAudioPlayer();
    }

    private async Task LoadAsync(bool forceRefresh)
    {
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        OverviewView.Visibility = Visibility.Collapsed;
        WorksView.Visibility = Visibility.Collapsed;
        PicksView.Visibility = Visibility.Collapsed;
        OstView.Visibility = Visibility.Collapsed;
        LinksView.Visibility = Visibility.Collapsed;
        BtnRefresh.IsEnabled = false;

        try
        {
            var (ok, err, data) = await XteamService.Instance.LoadSiteAsync(forceRefresh);
            if (!ok || data == null)
            {
                ErrorText.Text = string.IsNullOrWhiteSpace(err) ? "未能获取到星创组站点数据" : err;
                ErrorPanel.Visibility = Visibility.Visible;
                return;
            }

            _data = data;
            BindSiteData(data);
            UpdateVisibleSection();
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            BtnRefresh.IsEnabled = true;
        }
    }

    private void BindSiteData(XteamSiteData data)
    {
        HeroBadgeText.Text = data.HeroBadge;
        HeroTitleText.Text = data.HeroTitle;
        HeroSubtitleTextFallback(data);
        StatsList.ItemsSource = data.Stats;

        OverviewWorksList.ItemsSource = data.Works.Select(BuildProjectCard).ToList();
        OverviewPicksList.ItemsSource = data.Picks.Take(6).Select(BuildProjectCard).ToList();
        WorksList.ItemsSource = data.Works.Select(BuildProjectCard).ToList();
        ApplyPicksFilter();

        OstPacksList.ItemsSource = data.AudioPacks;
        LinksList.ItemsSource = data.Links;
        AboutList.ItemsSource = data.AboutSections;
    }

    private void HeroSubtitleTextFallback(XteamSiteData data)
    {
        HeroSubText.Text = data.HeroSubtitle;
    }

    private void ApplyPicksFilter()
    {
        if (_data == null) return;
        IEnumerable<XteamProjectItem> items = _data.Picks;
        if (_pickFilter == "game")
            items = items.Where(i => string.Equals(i.SubCategory, "game", StringComparison.OrdinalIgnoreCase));
        else if (_pickFilter == "mod")
            items = items.Where(i => string.Equals(i.SubCategory, "mod", StringComparison.OrdinalIgnoreCase));
        PicksList.ItemsSource = items.Select(BuildProjectCard).ToList();
    }

    private void UpdateVisibleSection()
    {
        if (_data == null) return;
        OverviewView.Visibility = TabOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        WorksView.Visibility = TabWorks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PicksView.Visibility = TabPicks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OstView.Visibility = TabOst.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LinksView.Visibility = TabLinks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MainScroll.ScrollToTop();
    }

    private void OnSectionChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        DetailOverlay.Visibility = Visibility.Collapsed;
        UpdateVisibleSection();
    }

    private void OnPickFilterChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _pickFilter = sender == FilterPickGame ? "game"
            : sender == FilterPickMod ? "mod"
            : "all";
        ApplyPicksFilter();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = LoadAsync(forceRefresh: true);

    private void OnOpenOriginalSiteClick(object sender, RoutedEventArgs e) =>
        Svc.Navigate?.OpenWebViewWindow(XteamService.BaseUrl, "星创组 · 吉星派对同人站");

    private void OnJumpWorksClick(object sender, RoutedEventArgs e) => TabWorks.IsChecked = true;
    private void OnJumpPicksClick(object sender, RoutedEventArgs e) => TabPicks.IsChecked = true;
    private void OnJumpOstClick(object sender, RoutedEventArgs e) => TabOst.IsChecked = true;

    private Border BuildProjectCard(XteamProjectItem item)
    {
        var card = new Border
        {
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左侧封面
        var cover = new Border
        {
            Width = 104,
            Height = 104,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Clip = new RectangleGeometry(new Rect(0, 0, 104, 104), 8, 8)
        };
        cover.SetResourceReference(Border.BackgroundProperty, "PlaceholderBrush");
        if (!string.IsNullOrEmpty(item.CoverUrl))
            Cover.SetSource(cover, item.CoverUrl);
        root.Children.Add(cover);

        // 右侧信息
        var info = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(info, 1);

        var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (item.IsPinned)
            badges.Children.Add(MakePill("置顶推荐", isAccent: true));
        if (!string.IsNullOrEmpty(item.StatusText))
            badges.Children.Add(MakePill(item.StatusText, isAccent: !item.IsPinned));
        if (!string.IsNullOrEmpty(item.IpTag))
            badges.Children.Add(MakePill(item.IpTag, isAccent: false));
        if (badges.Children.Count > 0)
            info.Children.Add(badges);

        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        info.Children.Add(title);

        if (!string.IsNullOrEmpty(item.SubTitle))
        {
            var sub = new TextBlock
            {
                Text = item.SubTitle,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            info.Children.Add(sub);
        }

        var summaryText = !string.IsNullOrEmpty(item.Description) ? item.Description : item.Tagline;
        if (!string.IsNullOrEmpty(summaryText))
        {
            var summary = new TextBlock
            {
                Text = summaryText,
                FontSize = 13,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19
            };
            summary.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            info.Children.Add(summary);
        }

        if (item.Tags.Count > 0)
        {
            var tagWrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var tag in item.Tags.Take(6))
                tagWrap.Children.Add(MakePill(tag, isAccent: false));
            info.Children.Add(tagWrap);
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (item.HasPrimaryAction && !string.IsNullOrEmpty(item.PrimaryActionUrl))
        {
            var primaryBtn = new Button
            {
                Content = string.IsNullOrEmpty(item.PrimaryActionLabel) ? "打开体验" : item.PrimaryActionLabel,
                Margin = new Thickness(0, 0, 8, 4),
                MinHeight = 32,
                Padding = new Thickness(12, 4, 12, 4)
            };
            primaryBtn.SetResourceReference(StyleProperty, "AccentButtonStyle");
            primaryBtn.Click += (_, e) =>
            {
                e.Handled = true;
                HandleUrlAction(item.PrimaryActionUrl, item.Title, item);
            };
            actions.Children.Add(primaryBtn);
        }

        if (!string.IsNullOrEmpty(item.DetailUrl))
        {
            var detailBtn = new Button
            {
                Content = "作品详情",
                Margin = new Thickness(0, 0, 8, 4),
                MinHeight = 32,
                Padding = new Thickness(12, 4, 12, 4)
            };
            detailBtn.SetResourceReference(StyleProperty,
                item.HasPrimaryAction ? "SubtleButtonStyle" : "AccentButtonStyle");
            detailBtn.Click += (_, e) =>
            {
                e.Handled = true;
                _ = ShowDetailAsync(item.DetailUrl, item);
            };
            actions.Children.Add(detailBtn);
        }

        if (item.HasAuthor)
        {
            var authorBtn = new Button
            {
                Content = item.HasBiliAuthor ? $"UP: {item.AuthorName} ↗" : $"作者: {item.AuthorName}",
                Margin = new Thickness(0, 0, 8, 4),
                MinHeight = 32,
                Padding = new Thickness(10, 4, 10, 4)
            };
            authorBtn.SetResourceReference(StyleProperty, "SubtleButtonStyle");
            authorBtn.Click += (_, e) =>
            {
                e.Handled = true;
                OpenAuthor(item.AuthorUid, item.AuthorUrl, item.AuthorName);
            };
            actions.Children.Add(authorBtn);
        }

        if (item.HasScreenshots)
        {
            var shotsBtn = new Button
            {
                Content = $"预览截图 ({item.Screenshots.Count})",
                Margin = new Thickness(0, 0, 8, 4),
                MinHeight = 32,
                Padding = new Thickness(10, 4, 10, 4)
            };
            shotsBtn.SetResourceReference(StyleProperty, "SubtleButtonStyle");
            shotsBtn.Click += (_, e) =>
            {
                e.Handled = true;
                OpenGallery(item.Screenshots, 0);
            };
            actions.Children.Add(shotsBtn);
        }

        if (actions.Children.Count > 0)
            info.Children.Add(actions);

        root.Children.Add(info);
        card.Child = root;
        return card;
    }

    private static Border MakePill(string text, bool isAccent)
    {
        var pill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 4)
        };
        pill.SetResourceReference(Border.BackgroundProperty, isAccent ? "AccentSoftFillBrush" : "SubtleFillBrush");
        var label = new TextBlock { Text = text, FontSize = 11 };
        if (isAccent) label.FontWeight = FontWeights.SemiBold;
        label.SetResourceReference(TextBlock.ForegroundProperty, isAccent ? "AccentTextBrush" : "TextSecondaryBrush");
        pill.Child = label;
        return pill;
    }

    private void HandleUrlAction(string url, string title, XteamProjectItem? sourceItem = null)
    {
        if (url.StartsWith(XteamService.BaseUrl + "/works/", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(XteamService.BaseUrl + "/picks/", StringComparison.OrdinalIgnoreCase))
        {
            _ = ShowDetailAsync(url, sourceItem);
            return;
        }
        Svc.Navigate?.OpenWebViewWindow(url, title);
    }

    private void OpenAuthor(string uid, string url, string name)
    {
        if (!string.IsNullOrEmpty(uid))
        {
            Svc.Navigate?.OpenUserSpace(uid);
            return;
        }
        if (!string.IsNullOrEmpty(url))
            Svc.Navigate?.OpenWebViewWindow(url, string.IsNullOrEmpty(name) ? "创作者主页" : name);
    }

    private async Task ShowDetailAsync(string url, XteamProjectItem? fallbackItem)
    {
        DetailOverlay.Visibility = Visibility.Visible;
        DetailLoading.Visibility = Visibility.Visible;
        DetailCard.Visibility = Visibility.Collapsed;
        _currentDetail = null;

        var (ok, err, detail) = await XteamService.Instance.LoadDetailAsync(url);
        DetailLoading.Visibility = Visibility.Collapsed;

        if (!ok || detail == null)
        {
            // Fallback to opening original page if native archive fails to parse
            DetailOverlay.Visibility = Visibility.Collapsed;
            Svc.Toast.Show(string.IsNullOrWhiteSpace(err) ? "详情解析失败，已改用网页打开" : err);
            Svc.Navigate?.OpenWebViewWindow(url, fallbackItem?.Title ?? "星创组详情");
            return;
        }

        _currentDetail = detail;
        DetailStatusText.Text = string.IsNullOrEmpty(detail.StatusText)
            ? (string.IsNullOrEmpty(detail.BreadcrumbCategory) ? "星创组档案" : detail.BreadcrumbCategory)
            : detail.StatusText;
        DetailDateText.Text = detail.DateText;
        DetailTitleText.Text = string.IsNullOrEmpty(detail.Title) ? (fallbackItem?.Title ?? "") : detail.Title;
        DetailSubtitleText.Text = string.IsNullOrEmpty(detail.Subtitle) ? (fallbackItem?.SubTitle ?? "") : detail.Subtitle;
        DetailSubtitleText.Visibility = string.IsNullOrEmpty(DetailSubtitleText.Text) ? Visibility.Collapsed : Visibility.Visible;

        if (!string.IsNullOrEmpty(detail.AuthorName))
        {
            BtnDetailAuthor.Visibility = Visibility.Visible;
            BtnDetailAuthor.Content = detail.HasBiliAuthor ? $"UP: {detail.AuthorName} ↗" : $"作者: {detail.AuthorName}";
        }
        else
        {
            BtnDetailAuthor.Visibility = Visibility.Collapsed;
        }

        DetailCoverImage.Source = null;
        var coverUrl = !string.IsNullOrEmpty(detail.CoverUrl) ? detail.CoverUrl : (fallbackItem?.CoverUrl ?? "");
        if (!string.IsNullOrEmpty(coverUrl))
            DetailCoverImage.Source = await CoverLoader.LoadAsync(coverUrl, 360);

        DetailActionsPanel.Children.Clear();
        foreach (var act in detail.Actions)
        {
            var btn = new Button
            {
                Content = act.Label,
                Margin = new Thickness(0, 0, 8, 6),
                MinHeight = 32,
                Padding = new Thickness(12, 4, 12, 4)
            };
            btn.SetResourceReference(StyleProperty, act.IsPrimary ? "AccentButtonStyle" : "SubtleButtonStyle");
            var actUrl = act.Url;
            var actLabel = act.Label;
            btn.Click += (_, _) => HandleUrlAction(actUrl, $"{DetailTitleText.Text} - {actLabel}");
            DetailActionsPanel.Children.Add(btn);
        }

        var originBtn = new Button
        {
            Content = "网页查看 ↗",
            Margin = new Thickness(0, 0, 8, 6),
            MinHeight = 32,
            Padding = new Thickness(12, 4, 12, 4)
        };
        originBtn.SetResourceReference(StyleProperty, "SubtleButtonStyle");
        originBtn.Click += (_, _) => Svc.Navigate?.OpenWebViewWindow(detail.Url, DetailTitleText.Text);
        DetailActionsPanel.Children.Add(originBtn);

        var paragraphs = detail.Paragraphs.Count > 0
            ? detail.Paragraphs
            : (!string.IsNullOrEmpty(fallbackItem?.Description) ? new List<string> { fallbackItem.Description } : new List<string>());
        DetailParagraphsList.ItemsSource = paragraphs;

        var shots = detail.Screenshots.Count > 0 ? detail.Screenshots : (fallbackItem?.Screenshots ?? new List<string>());
        DetailGalleryHeader.Visibility = shots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailScreenshotsList.ItemsSource = shots;
        DetailCard.Visibility = Visibility.Visible;
    }

    private void OnCloseDetailClick(object sender, RoutedEventArgs e) =>
        DetailOverlay.Visibility = Visibility.Collapsed;

    private void OnDetailAuthorClick(object sender, RoutedEventArgs e)
    {
        if (_currentDetail == null) return;
        OpenAuthor(_currentDetail.AuthorUid, _currentDetail.AuthorUrl, _currentDetail.AuthorName);
    }

    private void OnDetailScreenshotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url }) return;
        var shots = DetailScreenshotsList.ItemsSource as IReadOnlyList<string> ?? Array.Empty<string>();
        var idx = 0;
        for (int i = 0; i < shots.Count; i++)
        {
            if (string.Equals(shots[i], url, StringComparison.Ordinal)) { idx = i; break; }
        }
        OpenGallery(shots, idx);
    }

    private void OpenGallery(IReadOnlyList<string> urls, int index)
    {
        if (urls.Count == 0) return;
        ScreenshotGallery.Visibility = Visibility.Visible;
        _ = ScreenshotGallery.OpenAsync(urls, index);
    }

    private void OnLinkCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: XteamLinkItem link }) return;
        if (!string.IsNullOrEmpty(link.BiliUid))
            Svc.Navigate?.OpenUserSpace(link.BiliUid);
        else if (!string.IsNullOrEmpty(link.Url))
            Svc.Navigate?.OpenWebViewWindow(link.Url, link.Name);
    }

    // ---------- 原生 OST 音频试听 ----------

    private void EnsureAudioPlayer()
    {
        if (_audioPlayer != null) return;
        _audioPlayer = new LibVLCSharp.Shared.MediaPlayer(VlcCore.Get());
        VlcCore.RegisterPlayer();
        _audioPlayer.EndReached += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            _audioPlaying = false;
            BtnAudioToggle.Content = "▶";
            AudioProgressSlider.Value = 100;
        });

        _audioTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _audioTimer.Tick += (_, _) => UpdateAudioUi();
    }

    private void OnTrackClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: XteamAudioTrack track } || string.IsNullOrEmpty(track.Url))
            return;

        try
        {
            EnsureAudioPlayer();
            if (_audioPlayer == null) return;

            _audioPlayer.Stop();
            _currentMedia?.Dispose();
            _currentMedia = new Media(VlcCore.Get(), new Uri(track.Url));
            _audioPlayer.Play(_currentMedia);
            _audioPlaying = true;

            AudioTrackTitle.Text = track.Title;
            AudioPackName.Text = track.Pack;
            AudioTimeText.Text = "00:00 / 00:00";
            AudioProgressSlider.Value = 0;
            BtnAudioToggle.Content = "❚❚";
            AudioPlayerBar.Visibility = Visibility.Visible;
            _audioTimer?.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StarPartyPage] Audio play failed: {ex.Message}");
            Svc.Toast.Show("音频播放失败：" + ex.Message);
        }
    }

    private void OnAudioToggleClick(object sender, RoutedEventArgs e)
    {
        if (_audioPlayer == null) return;
        if (_audioPlaying)
        {
            _audioPlayer.Pause();
            _audioPlaying = false;
            BtnAudioToggle.Content = "▶";
        }
        else
        {
            _audioPlayer.Play();
            _audioPlaying = true;
            BtnAudioToggle.Content = "❚❚";
        }
    }

    private void OnAudioSeekEnd(object sender, MouseButtonEventArgs e)
    {
        _audioScrubbing = false;
        if (_audioPlayer == null || _audioPlayer.Length <= 0) return;
        var ratio = Math.Clamp(AudioProgressSlider.Value / 100.0, 0, 1);
        _audioPlayer.Time = (long)(_audioPlayer.Length * ratio);
    }

    private void OnAudioStopClick(object sender, RoutedEventArgs e)
    {
        StopAudioPlayback();
        AudioPlayerBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateAudioUi()
    {
        if (_audioPlayer == null || _audioScrubbing) return;
        var len = _audioPlayer.Length;
        var cur = _audioPlayer.Time;
        if (len > 0 && cur >= 0)
        {
            AudioProgressSlider.Value = Math.Clamp(cur * 100.0 / len, 0, 100);
            var curTs = TimeSpan.FromMilliseconds(cur);
            var lenTs = TimeSpan.FromMilliseconds(len);
            AudioTimeText.Text = $"{curTs:mm\\:ss} / {lenTs:mm\\:ss}";
        }
    }

    private void StopAudioPlayback()
    {
        _audioTimer?.Stop();
        _audioPlaying = false;
        try { _audioPlayer?.Stop(); } catch { }
        _currentMedia?.Dispose();
        _currentMedia = null;
    }

    private void DisposeAudioPlayer()
    {
        StopAudioPlayback();
        if (_audioPlayer != null)
        {
            try { _audioPlayer.Dispose(); } catch { }
            _audioPlayer = null;
            VlcCore.UnregisterPlayer();
        }
    }
}
