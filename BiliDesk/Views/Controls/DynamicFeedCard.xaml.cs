using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Models;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 动态混合流卡片的交互(2026-11-04)。
/// 点作者 → 个人主页; 点内容区 → 按类型去视频/专栏/直播。
/// </summary>
public partial class DynamicFeedCard : UserControl
{
    public DynamicFeedCard()
    {
        InitializeComponent();
    }

    private void OnAuthorClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not DynamicItem d) return;
        DynamicActions.OpenAuthor(d);
    }

    private void OnContentClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not DynamicItem d) return;
        switch (d.Kind)
        {
            case DynamicKind.Video: DynamicActions.OpenVideo(d); break;
            case DynamicKind.Article: DynamicActions.OpenArticle(d); break;
            case DynamicKind.Live: DynamicActions.OpenLive(d); break;
            case DynamicKind.Forward:
                // 点引用框: 原动态是视频就播, 是图就什么都不做(没有独立查看器)
                if (d.Original?.Video != null) DynamicActions.OpenVideo(d.Original);
                break;
        }
    }
}
