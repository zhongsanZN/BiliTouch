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
                // 点引用框: 原动态是视频就播, 其它进详情页
                if (d.Original?.Video != null) DynamicActions.OpenVideo(d.Original);
                else DynamicActions.OpenDetail(d);
                break;
            default:
                // 图文/纯文字/未知: 进详情页看完整内容与全部评论
                DynamicActions.OpenDetail(d);
                break;
        }
        e.Handled = true;
    }

    /// <summary>
    /// 点图片 → 看大图(2026-11-04)。
    /// ★ 官方是点图进可左右翻的看图器; 这里打开原图地址, 能缩放/保存。
    /// ★ 必须 e.Handled = true: 否则会继续冒泡到整卡的点击, 变成"点图进了动态详情"。
    /// </summary>
    private void OnPictureClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is FrameworkElement { DataContext: DynamicPicture pic })
        {
            DynamicActions.OpenPicture(pic);
            e.Handled = true;   // 别再冒泡到整卡
        }
    }

    /// <summary>
    /// 点整条动态(头像/正文/空白处) → 进详情页(2026-11-04)。
    /// ★ 排除已经被子元素处理过的事件: WPF 里子元素 Handler 设了 Handled 就不会到这里,
    ///   所以视频卡/图片/按钮仍然走它们自己的动作。
    /// </summary>
    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.Handled) return;
        if (DataContext is not DynamicItem d) return;
        // 作者名/头像有自己的点击(去个人主页), 那些事件已被子元素 Handled, 到不了这里
        DynamicActions.OpenDetail(d);
        e.Handled = true;
    }
}
