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
    private int _lastTabIndex = -1;
    private readonly Dictionary<int, double> _tabScrollOffsets = new();
    private System.Windows.Threading.DispatcherTimer? _retainedPreparation;

    public SettingsPage()
    {
        InitializeComponent();
        ModuleGestures = new TabNavigationGestures(this, ModuleContent, ModuleTabs, CreatePeerPreview, SyncPeerScrollOffset);
        ModuleContent.ScrollChanged += (_, _) =>
        {
            if (_vm != null && _lastTabIndex == _vm.TabIndex && !ModuleGestures.IsInteracting)
                _tabScrollOffsets[_lastTabIndex] = ModuleContent.VerticalOffset;
        };
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
        _lastTabIndex = _vm.TabIndex;
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
            // 保存离开页签的滚动偏移，并立即切到目标页签各自的记忆位置（未滚过则回到顶部），
            // 彻底消除"在 A 页签滚到中间切 B 页签时先预览顶部、松手瞬间跳到中间"的错位。
            if (_lastTabIndex >= 0 && _lastTabIndex != _vm.TabIndex)
                _tabScrollOffsets[_lastTabIndex] = ModuleContent.VerticalOffset;
            _lastTabIndex = _vm.TabIndex;
            var targetOffset = _tabScrollOffsets.GetValueOrDefault(_lastTabIndex, 0.0);
            ModuleContent.ScrollToVerticalOffset(targetOffset);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (_vm != null && _vm.TabIndex == _lastTabIndex)
                        ModuleContent.ScrollToVerticalOffset(_tabScrollOffsets.GetValueOrDefault(_lastTabIndex, 0.0));
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

    private void SyncPeerScrollOffset(int targetIndex, UIElement peer)
    {
        if (peer is ScrollViewer sv)
        {
            var offset = _tabScrollOffsets.GetValueOrDefault(targetIndex, 0.0);
            sv.ScrollToVerticalOffset(offset);
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
        var tablet = Responsive.GetPosture(this) == LayoutPosture.TabletPortrait;
        var sv = new ScrollViewer
        {
            // 与真实 ModuleContent 保持完全一致的 Auto 滚动条与页边距，杜绝落位后滚动条突然弹出挤窄重排
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsHitTestVisible = false,
            Focusable = false,
            Content = new StackPanel
            {
                Margin = tablet ? new Thickness(14, 10, 14, 24) : new Thickness(36, 12, 36, 32),
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
        sv.ScrollToVerticalOffset(_tabScrollOffsets.GetValueOrDefault(targetIndex, 0.0));
        return sv;
    }

    private (Border? box, TextBlock? fallback) FindAvatarElements()
        => (VisualTreeUtil.FindDescendantByName(this, "AvatarBox") as Border,
            VisualTreeUtil.FindDescendantByName(this, "AvatarFallback") as TextBlock);
}
