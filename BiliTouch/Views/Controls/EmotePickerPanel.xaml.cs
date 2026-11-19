using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.Views.Controls;

public partial class EmotePickerPanel : UserControl
{
    private List<EmotePackage> _allPackages = new();
    private EmotePackage? _selectedPackage;
    private string _category = "all";
    private bool _loading;
    private bool _loadedOnce;

    private SessionAccount _loadedAccount;

    public event Action<EmoteEntry>? EmoteSelected;

    public EmotePickerPanel()
    {
        InitializeComponent();
        WeakEventManager<SessionManager, EventArgs>.AddHandler(
            Svc.Session, nameof(SessionManager.AccountChanged), OnAccountChanged);
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        _loadedOnce = false;
        _allPackages.Clear();
        _selectedPackage = null;
        PackageStrip.Children.Clear();
        EmoteWrap.Children.Clear();
    }

    public async Task EnsureLoadedAsync(bool forceRefresh = false)
    {
        if (_loading) return;
        if (_loadedOnce && !forceRefresh && _loadedAccount.IsCurrent && _allPackages.Count > 0) return;

        var account = Svc.Session.CaptureAccount();
        _loading = true;
        BtnRefresh.IsEnabled = false;
        StatusText.Text = "正在加载表情与收藏集…";
        StatusText.Visibility = Visibility.Visible;
        EmoteWrap.Children.Clear();
        try
        {
            var (ok, err, packages) = await Svc.Api.GetReplyEmotePanelAsync("reply", forceRefresh);
            if (!account.IsCurrent) return;
            _loadedOnce = true;
            _loadedAccount = account;
            _allPackages = packages ?? new List<EmotePackage>();
            if (!ok || _allPackages.Count == 0)
            {
                StatusText.Text = string.IsNullOrWhiteSpace(err) ? "暂无可用表情" : err;
                StatusText.Visibility = Visibility.Visible;
                PackageStrip.Children.Clear();
                PackageTitleText.Text = "";
                FooterHint.Text = "";
                return;
            }

            RebuildPackageStrip();
        }
        finally
        {
            _loading = false;
            BtnRefresh.IsEnabled = true;
        }
    }

    private IEnumerable<EmotePackage> FilteredPackages() => _category switch
    {
        "suite" => _allPackages.Where(p => p.Type == 3),
        "basic" => _allPackages.Where(p => p.Type == 1 || p.Type == 2),
        "kaomoji" => _allPackages.Where(p => p.Type == 4),
        _ => _allPackages
    };

    private void RebuildPackageStrip()
    {
        PackageStrip.Children.Clear();
        var visible = FilteredPackages().ToList();
        if (visible.Count == 0)
        {
            EmoteWrap.Children.Clear();
            StatusText.Text = _category == "suite"
                ? (Svc.Session.HasLogin ? "当前账号暂未解锁收藏集表情包" : "登录后可查看已购买的收藏集表情")
                : "该分类下暂无表情";
            StatusText.Visibility = Visibility.Visible;
            PackageTitleText.Text = "";
            FooterHint.Text = "";
            return;
        }

        if (_selectedPackage == null || !visible.Contains(_selectedPackage))
            _selectedPackage = visible[0];

        foreach (var pkg in visible)
        {
            var isSelected = ReferenceEquals(pkg, _selectedPackage);
            var btn = new Button
            {
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(8, 4, 8, 4),
                MinHeight = 32,
                ToolTip = pkg.Text + (pkg.IsCollection ? "（收藏集）" : "") + (pkg.Unlocked ? "" : "（未解锁）")
            };
            btn.SetResourceReference(StyleProperty, isSelected ? "AccentButtonStyle" : "SubtleButtonStyle");

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(pkg.Url))
            {
                var icon = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 5, 0) };
                _ = LoadImageAsync(icon, pkg.Url, 48);
                row.Children.Add(icon);
            }
            var label = new TextBlock
            {
                Text = pkg.IsCollection ? $"✦ {pkg.Text}" : pkg.Text,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 88,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            row.Children.Add(label);
            btn.Content = row;

            var captured = pkg;
            btn.Click += (_, e) =>
            {
                e.Handled = true;
                _selectedPackage = captured;
                RebuildPackageStrip();
            };
            PackageStrip.Children.Add(btn);
        }

        RenderSelectedPackage();
    }

    private void RenderSelectedPackage()
    {
        EmoteWrap.Children.Clear();
        var pkg = _selectedPackage;
        if (pkg == null) return;

        StatusText.Visibility = Visibility.Collapsed;
        PackageTitleText.Text = pkg.IsCollection ? $"收藏集 · {pkg.Text}" : pkg.Text;
        FooterHint.Text = pkg.Unlocked ? $"{pkg.Emotes.Count} 个表情" : "未解锁（需拥有该装扮）";

        foreach (var entry in pkg.Emotes)
        {
            var tile = new Button
            {
                Margin = new Thickness(2),
                Padding = entry.IsTextOnly ? new Thickness(8, 5, 8, 5) : new Thickness(4),
                MinWidth = entry.IsTextOnly ? 60 : (entry.Size >= 2 ? 50 : 38),
                MinHeight = entry.IsTextOnly ? 30 : (entry.Size >= 2 ? 50 : 38),
                ToolTip = entry.Text,
                Opacity = entry.Unlocked ? 1.0 : 0.45
            };
            tile.SetResourceReference(StyleProperty, "SubtleButtonStyle");

            if (entry.IsTextOnly)
            {
                tile.Content = new TextBlock
                {
                    Text = entry.Text,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else
            {
                var dim = entry.Size >= 2 ? 38 : 26;
                var img = new Image { Width = dim, Height = dim, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                _ = LoadImageAsync(img, entry.Url, 96);
                tile.Content = img;
            }

            var captured = entry;
            tile.Click += (_, e) =>
            {
                e.Handled = true;
                if (!captured.Unlocked)
                {
                    Svc.Toast.Show($"需要先在 B 站解锁收藏集「{pkg.Text}」");
                    return;
                }
                EmoteSelected?.Invoke(captured);
            };
            EmoteWrap.Children.Add(tile);
        }
    }

    private static async Task LoadImageAsync(Image target, string url, int decodeWidth)
    {
        var bmp = await CoverLoader.LoadAsync(url, decodeWidth);
        if (bmp != null)
            target.Source = bmp;
    }

    private void OnCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _category = sender == CatSuite ? "suite"
            : sender == CatBasic ? "basic"
            : sender == CatKaomoji ? "kaomoji"
            : "all";
        RebuildPackageStrip();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = EnsureLoadedAsync(forceRefresh: true);
}
