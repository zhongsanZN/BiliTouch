using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BiliTouch.Views.Controls;

/// <summary>
/// 统一的列表页状态浮层（加载中 / 错误重试 / 空状态），按 Error -> Loading -> Empty 互斥显隐。
/// </summary>
public partial class FeedStatusOverlay : UserControl
{
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(FeedStatusOverlay),
        new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty ErrorTextProperty = DependencyProperty.Register(
        nameof(ErrorText), typeof(string), typeof(FeedStatusOverlay),
        new PropertyMetadata("", OnStateChanged));

    public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.Register(
        nameof(IsEmpty), typeof(bool), typeof(FeedStatusOverlay),
        new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty LoadingTextProperty = DependencyProperty.Register(
        nameof(LoadingText), typeof(string), typeof(FeedStatusOverlay),
        new PropertyMetadata("正在加载…", OnStateChanged));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(FeedStatusOverlay),
        new PropertyMetadata("暂无内容"));

    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(
        nameof(RetryCommand), typeof(ICommand), typeof(FeedStatusOverlay),
        new PropertyMetadata(null, OnStateChanged));

    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    public string? ErrorText { get => (string?)GetValue(ErrorTextProperty); set => SetValue(ErrorTextProperty, value); }
    public bool IsEmpty { get => (bool)GetValue(IsEmptyProperty); set => SetValue(IsEmptyProperty, value); }
    public string LoadingText { get => (string)GetValue(LoadingTextProperty); set => SetValue(LoadingTextProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public ICommand? RetryCommand { get => (ICommand?)GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }

    public FeedStatusOverlay()
    {
        InitializeComponent();
        UpdateVisualState();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FeedStatusOverlay)d).UpdateVisualState();

    private void UpdateVisualState()
    {
        var hasError = !string.IsNullOrWhiteSpace(ErrorText);
        var showError = hasError;
        var showLoading = !hasError && IsLoading;
        var showEmpty = !hasError && !IsLoading && IsEmpty;

        ErrorPanel.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = RetryCommand != null ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = showLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingLabel.Visibility = string.IsNullOrEmpty(LoadingText) ? Visibility.Collapsed : Visibility.Visible;
        EmptyLabel.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;

        var active = showError || showLoading || showEmpty;
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        IsHitTestVisible = showError && RetryCommand != null;
    }
}
