using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using Microsoft.Win32;

namespace BiliDesk.Views;

/// <summary>
/// 动态发布窗口(2026-11-04)。
///
/// code-behind 只做四件事: 选图、把选中路径交给 VM、Ctrl+Enter 快捷键、发布后回传结果。
/// 所有状态都在 <see cref="DynamicPublishViewModel"/> 里 —— 这样发布流程能被自动化测试直接驱动,
/// 不用去点窗口。
/// </summary>
public partial class DynamicPublishWindow : FluentWindow
{
    public DynamicPublishViewModel ViewModel { get; }

    /// <summary>发布成功后的动态 id(调用方据此把新动态插到时间线最前面)</summary>
    public string PublishedId => ViewModel.PublishedId;

    public DynamicPublishWindow()
    {
        InitializeComponent();
        // 昵称/头像取不到就留空, 界面不显示假数据
        ViewModel = new DynamicPublishViewModel();
        DataContext = ViewModel;
        Loaded += (_, _) => BodyBox.Focus();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>外部(如动态页的"发动态"按钮)可以预填一段草稿</summary>
    public DynamicPublishWindow(string draftText) : this()
    {
        ViewModel.Text = draftText ?? "";
        BodyBox.Text = ViewModel.Text;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            _ = PublishAsync();
            return;
        }
        if (e.Key == Key.Escape && !ViewModel.Publishing) { e.Handled = true; Close(); }
    }

    private void OnBodyChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // TextBox 是双向绑定的源; 这里同步回 VM(绑定了 UpdateSourceTrigger=PropertyChanged 也可以,
        // 但 XAML 里的 Style 带了它自己的绑定设置, 显式同步最稳)
        if (ViewModel.Text != BodyBox.Text) ViewModel.Text = BodyBox.Text;
    }

    private async void OnAddImages(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("发布动态需要先登录"); return; }
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|所有文件|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var remaining = 9 - ViewModel.Images.Count;
        foreach (var path in dialog.FileNames.Take(remaining))
            await ViewModel.AddImageAsync(path);
        if (dialog.FileNames.Length > remaining)
            Svc.Toast.Show($"最多 9 张，已忽略多余的 {dialog.FileNames.Length - remaining} 张");
    }

    private async void OnRetryUpload(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: PublishImage item })
            await ViewModel.RetryAsync(item);
    }

    private void OnRemoveImage(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: PublishImage item })
            ViewModel.Remove(item);
    }

    private async void OnPublish(object sender, RoutedEventArgs e) => await PublishAsync();

    private async System.Threading.Tasks.Task PublishAsync()
    {
        if (!ViewModel.CanPublish) return;
        var ok = await ViewModel.PublishAsync();
        if (!ok) return;
        Svc.Toast.Show("动态已发布");
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
