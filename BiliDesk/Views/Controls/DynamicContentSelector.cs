using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 动态卡内容区的模板选择器(2026-11-04)。
///
/// 按 <see cref="DynamicItem.Kind"/> 选模板: 视频/专栏/直播走"左图右文"的附加卡,
/// 图文走九宫格, 转发走引用框, 纯文字没有内容区。
/// ★ 纯文字返回 null → ContentControl 不渲染任何东西, 正文已经在卡片主体里了。
/// </summary>
public class DynamicContentSelector : DataTemplateSelector
{
    public DataTemplate? VideoTemplate { get; set; }
    public DataTemplate? PictureTemplate { get; set; }
    public DataTemplate? ForwardTemplate { get; set; }
    public DataTemplate? ArticleTemplate { get; set; }
    public DataTemplate? LiveTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not DynamicItem d) return null;
        return d.Kind switch
        {
            DynamicKind.Video => VideoTemplate,
            DynamicKind.ImageText => PictureTemplate,
            DynamicKind.Forward => ForwardTemplate,
            DynamicKind.Article => ArticleTemplate,
            DynamicKind.Live => LiveTemplate,
            // 纯文字/其它: 正文已在主体里, 内容区留空
            _ => null,
        };
    }
}

/// <summary>
/// 把一条动态里的图片列表转成九宫格布局(2026-11-04)。
///
/// 官方手机端的规则:
///   · 1 张   → 单张大图(按原图比例, 最大宽度受限)
///   · 2/4 张 → 两列
///   · 3/5~9 张 → 三列
/// 这里把规则做成 ItemsPanel + UniformGrid, 列数由 <see cref="Columns"/> 决定。
/// </summary>
public class DynamicPicturePanel : System.Windows.Controls.Primitives.UniformGrid
{
    /// <summary>
    /// 图片格子的高度(2026-11-04)。
    /// 单图给 180 —— 官方单图是"大图预览", 96 的高度根本看不清内容;
    /// 多图给 96 —— 九宫格要控制整卡高度, 否则一屏放不下两条动态。
    /// 由 DynamicFeedCard 的 XAML 通过 ElementName 绑定到每个 tile 的 Height。
    /// </summary>
    public double TileHeight { get; private set; } = 96;

    public DynamicPicturePanel()
    {
        // 行数自动: UniformGrid 在 Rows=0 时按列数推
        Rows = 0;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        ApplyShape();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        ApplyShape();
        return base.MeasureOverride(constraint);
    }

    private void ApplyShape()
    {
        var count = Children.Count;
        Columns = count switch
        {
            1 => 1,
            2 or 4 => 2,
            _ => 3,
        };
        TileHeight = count == 1 ? 180 : 96;
    }
}

/// <summary>
/// 动态卡的交互(点击作者/视频/专栏/直播)。放在代码里而不是每个模板各写一遍。
/// </summary>
public static class DynamicActions
{
    public static void OpenAuthor(DynamicItem d)
    {
        if (d.AuthorMid <= 0) return;
        Svc.Navigate.OpenUserSpace(d.AuthorMid.ToString());
    }

    public static void OpenVideo(DynamicItem d)
    {
        if (d.Video == null) return;
        if (d.Video.IsLive) Svc.Player.PlayLive(d.Video.RoomId, d.Video.Title);
        else if (!string.IsNullOrEmpty(d.Video.Bvid)) Svc.Player.PlayVideo(d.Video.Bvid, d.Video.Title);
    }

    public static void OpenArticle(DynamicItem d)
    {
        if (string.IsNullOrEmpty(d.ArticleId)) return;
        Svc.Navigate.OpenWebViewWindow($"https://www.bilibili.com/read/cv{d.ArticleId}", "专栏");
    }

    public static void OpenLive(DynamicItem d)
    {
        if (d.LiveRoomId <= 0) return;
        Svc.Player.PlayLive(d.LiveRoomId, d.Text);
    }

    /// <summary>
    /// 打开动态详情页(2026-11-04)。
    ///
    /// ★ 官方 PC 客户端里"点一下整条动态"会进详情页看完整内容 + 全部评论。
    ///   我们这边用内嵌 WebView 打开 t.bilibili.com 的那条动态 ——
    ///   这样评论、图片查看、转发链、投票全都能用, 不用自己再实现一遍。
    /// ★ 没有 id(极少见)时退回作者主页, 总比点了没反应好。
    /// </summary>
    public static void OpenDetail(DynamicItem d)
    {
        if (!string.IsNullOrEmpty(d.Id))
        {
            Svc.Navigate.OpenWebViewWindow($"https://t.bilibili.com/{d.Id}", "动态");
            return;
        }
        OpenAuthor(d);
    }

    /// <summary>
    /// 查看大图(2026-11-04)。
    /// ★ 官方是点图片进一个可左右翻的看图器。这里用内嵌窗口打开原图地址 ——
    ///   图片本身是 CDN 直链, WebView 能缩放/保存, 对"我就想看清楚"这个需求够了。
    /// </summary>
    public static void OpenPicture(DynamicPicture pic)
    {
        if (pic == null || string.IsNullOrEmpty(pic.Url)) return;
        Svc.Navigate.OpenWebViewWindow(pic.Url, "查看图片");
    }
}
