using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.Views.Controls;

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
    /// <summary>投票(2026-11-04): major.vote, 与正文/图片并存</summary>
    public DataTemplate? VoteTemplate { get; set; }
    /// <summary>附加卡片(2026-11-04): major.common(活动/商品/番剧/课程)</summary>
    public DataTemplate? CommonTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not DynamicItem d) return null;
        // ★ 优先级(2026-11-04): 投票与附加卡片**先于** Kind 判断。
        //   理由: 它们挂在 module_dynamic 上, 与动态类型正交 —— 一条 DYNAMIC_TYPE_ARTICLE
        //   的专栏动态也可能带投票(实测样本就是这样), 若先按 Kind 走 ArticleTemplate
        //   就永远看不到投票。官方客户端的顺序也是"正文 → 主内容 → 附加控件"。
        //   注意判据是 Vote/Common **节点存在**, 不是"选项已加载" —— 卡片节点不带 options。
        if (d.HasVote) return VoteTemplate;
        if (d.HasCommon) return CommonTemplate;
        return d.Kind switch
        {
            DynamicKind.Video => VideoTemplate,
            DynamicKind.ImageText => PictureTemplate,
            DynamicKind.Forward => ForwardTemplate,
            DynamicKind.Article => d.HasPictures ? PictureTemplate : ArticleTemplate,
            DynamicKind.Live => LiveTemplate,
            // 纯文字/其它: 正文已在主体里, 内容区留空
            _ => d.HasPictures ? PictureTemplate : null,
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
public sealed class DynamicPicturePanel : Panel
{
    private int _columns=1;
    private double _tileHeight=180;
    protected override Size MeasureOverride(Size constraint)
    {
        var count=InternalChildren.Count;
        _columns=count is 2 or 4?2:count<=1?1:3;
        var width=double.IsFinite(constraint.Width)?Math.Max(1,constraint.Width):Math.Max(1,ActualWidth>0?ActualWidth:300);
        var picture=count==1?ItemsControl.GetItemsOwner(this)?.Items.Cast<DynamicPicture>().FirstOrDefault():null;
        var ratio=picture is {Width:>0,Height:>0}?(double)picture.Height/picture.Width:0.75;
        _tileHeight=count==1?Math.Clamp(width*ratio,140,450):Math.Clamp(width/_columns-4,96,300);
        foreach(UIElement child in InternalChildren)child.Measure(new Size(width/_columns,_tileHeight));
        return new Size(width,Math.Ceiling((double)count/_columns)*_tileHeight);
    }
    protected override Size ArrangeOverride(Size size)
    {
        for(int i=0;i<InternalChildren.Count;i++)
            InternalChildren[i].Arrange(new Rect(i%_columns*size.Width/_columns,i/_columns*_tileHeight,size.Width/_columns,_tileHeight));
        return size;
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
        Svc.Navigate?.OpenUserSpace(d.AuthorMid.ToString());
    }

    public static void OpenVideo(DynamicItem d)
    {
        if (d.Video == null) return;
        if (d.Video.IsLive) Svc.Player.PlayLive(d.Video.RoomId, d.Video.Title);
        else if (!string.IsNullOrEmpty(d.Video.Bvid)) Svc.Player.PlayVideo(d.Video.Bvid, d.Video.Title);
    }

    public static void OpenArticle(DynamicItem d)
    {
        OpenDetail(d);
    }

    public static void OpenLive(DynamicItem d)
    {
        if (d.LiveRoomId <= 0) return;
        Svc.Player.PlayLive(d.LiveRoomId, d.Text);
    }

    /// <summary>在主窗口导航栈内打开原生动态详情，返回时保留原列表位置。</summary>
    public static void OpenDetail(DynamicItem d)
    {
        Svc.Navigate?.OpenDynamic(d);
    }

    /// <summary>使用原生图片查看器显示单张图片。</summary>
    public static void OpenPicture(DynamicPicture pic)
    {
        if (pic == null || string.IsNullOrEmpty(pic.Url)) return;
        BiliTouch.Helpers.MediaActions.ShowImage(pic.Url, "动态图片");
    }
}

/// <summary>
/// 按固定宽高比量高/排布的面板(2026-11-04)。
///
/// 为什么需要它: 动态卡的视频封面要"宽度撑满卡片、高度按比例"。
///   · 用 MultiBinding 把 ActualWidth 喂给 Converter 不行 —— 第一次布局时
///     ActualWidth 还是 0, 换算出的高度就是 0, 里面所有角标全被裁掉(实测就是这样);
///   · 用 Viewbox 又会把子元素缩放, 角标跟着变小。
///
/// 这个面板在 MeasureOverride 里直接拿 availableSize.Width, 除以比例得到高度,
/// 一轮布局就定下来, 不依赖任何"先量出来再回填"的反馈环。
/// ★ 这正是之前那次"封面卡死"教训的正解: 尺寸**只由输入(可用宽度 + 比例)**决定,
///   不由内容(位图)决定 —— 所以永远不会自旋。
/// </summary>
public sealed class AspectPanel : Panel
{
    public static readonly DependencyProperty AspectProperty = DependencyProperty.Register(
        nameof(Aspect), typeof(double), typeof(AspectPanel),
        new FrameworkPropertyMetadata(16.0 / 9.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>宽 / 高。16/9 = 横版, 0.5625 = 竖版</summary>
    public double Aspect
    {
        get => (double)GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 320;
        var aspect = double.IsFinite(Aspect) && Aspect > 0.05 ? Aspect : 16.0 / 9.0;
        var height = width / aspect;
        var slot = new Size(width, height);
        foreach (UIElement child in InternalChildren) child.Measure(slot);
        return slot;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }
}