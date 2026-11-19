using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliTouch.Helpers;
using BiliTouch.ViewModels;

namespace BiliTouch.Views.Pages;

public partial class SettingsPage : UserControl
{
    internal TabNavigationGestures ModuleGestures { get; }
    private SettingsViewModel? _vm;
    private bool _subscribed;
    private System.Windows.Threading.DispatcherTimer? _retainedPreparation;

    public SettingsPage()
    {
        InitializeComponent();
        ModuleGestures = new TabNavigationGestures(this, ModuleContent, ModuleTabs, CreatePeerPreview);
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) PrepareRetainedTabs(); else _retainedPreparation?.Stop(); };
        SizeChanged += (_, _) => ApplyTabletLayout(Responsive.GetPosture(this));
        Unloaded += (_, _) =>
        {
            Responsive.PostureChanged -= OnSettingsPostureChanged;
            _retainedPreparation?.Stop();
            if (_subscribed && _vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _subscribed = false;
            }
        };
    }

    private void OnSettingsPostureChanged(FrameworkElement _, LayoutPosture posture) => ApplyTabletLayout(posture);

    internal void ApplyTabletLayout(LayoutPosture posture)
    {
        var tablet = posture == LayoutPosture.TabletPortrait;
        if (ShellPageTitle != null)
            ShellPageTitle.Margin = tablet ? new Thickness(16, 20, 16, 0) : PageHeaderMetrics.TitleMargin;
        if (ModuleTabs != null)
            ModuleTabs.Margin = tablet ? new Thickness(16, 0, 16, 0) : PageHeaderMetrics.TabsMargin;
        if (SettingsContentStack != null)
            SettingsContentStack.Margin = tablet ? new Thickness(14, 10, 14, 24) : new Thickness(36, 12, 36, 32);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Responsive.PostureChanged -= OnSettingsPostureChanged;
        Responsive.PostureChanged += OnSettingsPostureChanged;
        ApplyTabletLayout(Responsive.GetPosture(this));
        var nextVm = DataContext as SettingsViewModel;
        if (!ReferenceEquals(_vm, nextVm))
        {
            if (_subscribed && _vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = nextVm;
            _subscribed = false;
        }
        if (_vm == null) return;

        _vm.OnNavigated();
        PrepareRetainedTabs();

        if (!_subscribed)
        {
            _subscribed = true;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        _ = LoadAvatarAsync(_vm.UserFace);
        HookPalette();
    }

    private void PrepareRetainedTabs()
    {
        _retainedPreparation?.Stop();
        var keys = new Queue<string>(new[] { "GeneralTabTemplate", "PlaybackTabTemplate", "DataTabTemplate", "AboutTabTemplate" });
        _retainedPreparation = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(32) };
        _retainedPreparation.Tick += (_, _) =>
        {
            if (!IsVisible || !IsLoaded || keys.Count == 0) { _retainedPreparation.Stop(); return; }
            if (ModuleGestures.IsInteracting) return;
            if (SettingsTabContent.TryFindResource(keys.Dequeue()) is DataTemplate template)
                SettingsTabContent.PrepareTemplate(template);
        };
        _retainedPreparation.Start();
    }

    /// <summary>在当前可视树里找调色盘并接上拖动事件(找不到就是"通用"Tab 不在前, 静默跳过)。</summary>
    private void HookPalette()
    {
        if (_vm == null) return;
        if (VisualTreeUtil.FindDescendant<Controls.ColorPalette>(this) is { } palette)
        {
            palette.DragStarted -= _vm.BeginAccentDrag;
            palette.DragEnded -= _vm.EndAccentDrag;
            palette.DragStarted += _vm.BeginAccentDrag;
            palette.DragEnded += _vm.EndAccentDrag;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(SettingsViewModel.UserFace))
        {
            _ = LoadAvatarAsync(_vm.UserFace);
        }
        else if (e.PropertyName == nameof(SettingsViewModel.TabIndex))
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    HookPalette();
                    if (_vm != null) _ = LoadAvatarAsync(_vm.UserFace);
                }));
        }
    }

    private async System.Threading.Tasks.Task LoadAvatarAsync(string? faceUrl)
    {
        var (box, fallback) = FindAvatarElements();
        if (box == null) return;

        if (string.IsNullOrEmpty(faceUrl))
        {
            box.Background = (Brush)FindResource("AccentSoftFillBrush");
            if (fallback != null) fallback.Visibility = Visibility.Visible;
            return;
        }

        var img = await CoverLoader.LoadAsync(faceUrl, 128);
        // 校验 faceUrl 仍与当前账号一致，避免登出/切号期间旧头像异步回来覆盖占位态
        if (img == null || _vm == null || !string.Equals(_vm.UserFace, faceUrl, StringComparison.Ordinal)) return;

        var (box2, fallback2) = FindAvatarElements();
        if (box2 == null) return;

        var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        box2.Background = brush;
        if (fallback2 != null) fallback2.Visibility = Visibility.Collapsed;
    }

    private static void IsolatePeerRadioGroups(DependencyObject root, int targetIndex)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is RadioButton rb && !string.IsNullOrEmpty(rb.GroupName) && !rb.GroupName.StartsWith("__peer_", StringComparison.Ordinal))
                rb.GroupName = $"__peer_{targetIndex}_{rb.GroupName}";
            if (child is DependencyObject dep)
                IsolatePeerRadioGroups(dep, targetIndex);
        }
    }

    private UIElement? CreatePeerPreview(int targetIndex)
    {
        var key = targetIndex switch
        {
            1 => "PlaybackTabTemplate",
            2 => "DataTabTemplate",
            3 => "AboutTabTemplate",
            _ => "GeneralTabTemplate"
        };
        if (SettingsTabContent?.TryFindResource(key) is not DataTemplate template) return null;
        if (template.LoadContent() is not FrameworkElement body) return null;
        body.DataContext = DataContext;
        IsolatePeerRadioGroups(body, targetIndex);
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            IsHitTestVisible = false,
            Focusable = false,
            Content = new StackPanel
            {
                Margin = new Thickness(36, 12, 36, 32),
                Children =
                {
                    new ContentPresenter
                    {
                        Margin = new Thickness(0, 4, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Focusable = false,
                        Content = body
                    }
                }
            }
        };
    }

    private (Border? box, TextBlock? fallback) FindAvatarElements()
        => (VisualTreeUtil.FindDescendantByName(this, "AvatarBox") as Border,
            VisualTreeUtil.FindDescendantByName(this, "AvatarFallback") as TextBlock);
}
