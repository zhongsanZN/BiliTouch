using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.Views.Controls;

/// <summary>
/// 动态流里的一条视频动态卡: 顶部 [UP 头像 + 昵称 + 发布时间], 下方左侧封面(带时长角标)、
/// 右侧标题与播放/弹幕数据。版式对齐 B 站动态页。
///
/// 纯展示 + 两个点击入口, 没有入场/悬停动画 —— 动态流是**单列大卡**, 一屏只有几条,
/// 卡片墙那套"一屏上百张同时播入场动画"的负担在这里不存在, 也就没必要引入它那套
/// ConditionalWeakTable 去重逻辑(见 VideoCard)。
/// </summary>
public partial class DynamicCard : UserControl
{
    public DynamicCard() => InitializeComponent();

    /// <summary>当前卡片对应的条目(右键菜单的各个处理器都从它取数据)</summary>
    private VideoItem? Current => DataContext as VideoItem;

    /// <summary>点封面或标题 → 打开这条视频(与卡片墙上点封面是同一条路)</summary>
    private void OnOpenClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not VideoItem v) return;
        if (string.IsNullOrEmpty(v.Bvid)) return;
        Svc.Player.PlayVideo(v.Bvid, v.Title);
    }

    /// <summary>点头像或昵称 → 进 TA 的主页(复用播放器/卡片墙那条 NavigationDispatcher 路径)</summary>
    private void OnUpClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not VideoItem v) return;
        if (v.OwnerMid <= 0)
        {
            Svc.Toast.Show("没拿到 UP 主的 UID, 打不开主页");
            return;
        }
        if (Svc.Navigate != null) Svc.Navigate.OpenUserSpace(v.OwnerMid.ToString());
        else if (Window.GetWindow(this) is { } w) new NavigationDispatcher(w).OpenUserSpace(v.OwnerMid.ToString());
    }

    private async void OnDownloadCover(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null) return;
        try { await MediaActions.SaveImageAsync(v.Cover, v.Title); }
        catch (Exception ex) { Svc.Toast.Show("保存封面失败: " + ex.Message); }
    }

    /// <summary>预览封面大图(与 VideoCard 同款, 动态卡不能少功能)。</summary>
    private void OnPreviewCover(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null) return;
        MediaActions.ShowImage(v.Cover, v.Title, Window.GetWindow(this));
    }

    private async void OnDownloadVideo(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null || string.IsNullOrEmpty(v.Bvid)) return;
        try { await VideoDownloader.DownloadAsync(v.Bvid, v.Title, Window.GetWindow(this)); }
        catch (Exception ex) { Svc.Toast.Show("下载失败: " + ex.Message); }
    }

    /// <summary>一键屏蔽这个 UP 主(精确同名 + UID, 与 VideoCard 同款)</summary>
    private void OnBlockUp(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null) return;

        var author = (v.Author ?? "").Trim();
        if (author.Length == 0 && v.OwnerMid <= 0)
        {
            Svc.Toast.Show("这条没有 UP 主信息，屏蔽不了");
            return;
        }

        FilterService.Instance.BlockUp(author, v.OwnerMid);
        var label = author.Length > 0 ? author : $"UID {v.OwnerMid}";
        Svc.Toast.Show($"已屏蔽 UP 主「{label}」，可在「设置 → 数据」屏蔽面板撤销");
    }

    /// <summary>一键屏蔽这个标题(完全相同才隐藏, 与 VideoCard 同款)</summary>
    private void OnBlockTitle(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null) return;

        var title = (v.Title ?? "").Trim();
        if (title.Length == 0) return;

        FilterService.Instance.BlockTitle(title);
        Svc.Toast.Show("已屏蔽这条视频，可在「设置 → 数据」屏蔽面板撤销");
    }

    private async void OnAddWatchLater(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null || string.IsNullOrEmpty(v.Bvid)) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("稍后再看需要先登录");
            return;
        }
        try
        {
            var aid = v.Aid;
            if (aid <= 0)
            {
                var (_, _, vd) = await Svc.Api.GetVideoAsync(v.Bvid);
                aid = vd?.Aid ?? 0;
            }
            if (aid <= 0)
            {
                Svc.Toast.Show("没拿到这条视频的 avid, 加不了");
                return;
            }

            var (ok, err) = await Svc.Api.AddWatchLaterAsync(aid);
            if (ok) Svc.UserState.PublishWatchLater(aid, v.Bvid, true);
            Svc.Toast.Show(ok ? "已加入稍后再看" : "加入失败: " + (err ?? "未知错误"));
        }
        catch (Exception ex) { Svc.Toast.Show("加入失败: " + ex.Message); }
    }
}
