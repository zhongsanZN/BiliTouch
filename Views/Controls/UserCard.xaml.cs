using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 搜索结果的用户卡。点整卡 = 进这个人/官方号的主页。
///
/// ★ 走 <see cref="NavigationDispatcher.OpenUserSpace"/>: 与播放器里点 UP 主头像、
///   视频卡下面点 UP 主名是同一条路(主窗口内容区盖一层带返回的页面), 不另开窗口。
/// </summary>
public partial class UserCard : UserControl
{
    public UserCard()
    {
        InitializeComponent();
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not UserItem u || u.Mid <= 0) return;
        Svc.Navigate.OpenUserSpace(u.Mid.ToString());
    }
}
