using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BiliTouch.Models;

namespace BiliTouch.Views.Controls;

/// <summary>
/// 动态正文的富文本渲染(2026-11-04)。
///
/// 为什么不用普通 TextBlock + Text 绑定:
///   接口的 desc.text 是**纯文本** —— 话题两侧的 # 会原样显示、@某人 只是一串字、
///   小表情退化成 [doge] 这种方括号。官方客户端里这三样都是可点/可图的。
///   desc.rich_text_nodes[] 才带类型, 所以正文必须按节点拼 Inlines。
///
/// 为什么做成 TextBlock 的子类而不是 UserControl:
///   TextBlock 支持 Inlines, 拼 Hyperlink / Run / InlineUIContainer 都是一行;
///   换成 UserControl 要多套一层 RichTextBox(评论那边用过, 重且要处理选择态),
///   而动态卡是虚拟化列表里的高频元素, 越轻越好。
///
/// ★ 兜底策略: 节点为空(老接口/纯文字动态)时直接显示 <see cref="Text"/>,
///   绝不留白 —— 正文消失比样式不完美严重得多。
/// </summary>
public class DynamicRichText : TextBlock
{
    public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(
        nameof(Nodes), typeof(IEnumerable<DynamicRichNode>), typeof(DynamicRichText),
        new PropertyMetadata(null, OnContentChanged));

    public IEnumerable<DynamicRichNode>? Nodes
    {
        get => (IEnumerable<DynamicRichNode>?)GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    /// <summary>节点为空时的纯文本兜底(绑 DynamicItem.Text)。</summary>
    public static readonly DependencyProperty FallbackProperty = DependencyProperty.Register(
        nameof(Fallback), typeof(string), typeof(DynamicRichText),
        new PropertyMetadata("", OnContentChanged));

    public string Fallback
    {
        get => (string)GetValue(FallbackProperty);
        set => SetValue(FallbackProperty, value);
    }

    /// <summary>话题 / @ / 链接的高亮色。XAML 传 DynamicResource, 主题切换时跟着变。</summary>
    public static readonly DependencyProperty LinkForegroundProperty = DependencyProperty.Register(
        nameof(LinkForeground), typeof(Brush), typeof(DynamicRichText),
        new PropertyMetadata(null, OnContentChanged));

    public Brush? LinkForeground
    {
        get => (Brush?)GetValue(LinkForegroundProperty);
        set => SetValue(LinkForegroundProperty, value);
    }

    /// <summary>点了话题 / @ 某人。参数是那个节点的 Rid(话题 id 或被 @ 的 mid)。</summary>
    public event System.EventHandler<DynamicRichNode>? NodeActivated;

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DynamicRichText text) text.Rebuild();
    }

    private int _contentGeneration;
    private async System.Threading.Tasks.Task LoadEmojiAsync(Image image, string url, int generation)
    {
        var bitmap = await Helpers.CoverLoader.LoadAsync(url, Helpers.CoverLoader.DecodeWidthFor(image.Width, VisualTreeHelper.GetDpi(this).DpiScaleX));
        if (generation == _contentGeneration) image.Source = bitmap;
    }

    private void Rebuild()
    {
        ++_contentGeneration;
        Inlines.Clear();
        var nodes = Nodes;
        if (nodes == null)
        {
            if (!string.IsNullOrEmpty(Fallback)) Inlines.Add(new Run(Fallback));
            return;
        }

        var any = false;
        foreach (var node in nodes)
        {
            any = true;
            if (node.IsEmoji)
            {
                // 表情是图片: 高度跟着字号走, 不参与换行计算
                var image = new Image
                {
                    Source = CoverLoaderFacade.Load(node.Emoji),
                    Width = FontSize * 1.35,
                    Height = FontSize * 1.35,
                    Stretch = Stretch.Uniform,
                    ToolTip = node.Text.Length > 0 ? node.Text : null,
                };
                _ = LoadEmojiAsync(image, node.Emoji, _contentGeneration);
                Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
                continue;
            }

            if (node.IsTopic || node.IsMention || node.IsLink)
            {
                var link = new Hyperlink(new Run(node.Text)) { Cursor = Cursors.Hand };
                if (LinkForeground is { } brush) link.Foreground = brush;
                link.Click += (_, e) => { e.Handled = true; NodeActivated?.Invoke(this, node); };
                Inlines.Add(link);
                continue;
            }

            Inlines.Add(new Run(node.Text));
        }

        if (!any && !string.IsNullOrEmpty(Fallback)) Inlines.Add(new Run(Fallback));
    }

    /// <summary>
    /// 表情图走 CoverLoader 的同步缓存查询, **不**在这里发起下载。
    /// 理由: 卡片会被回收复用, 在这里挂异步下载会造成"回收后仍往旧卡片贴图"。
    /// 首屏没缓存就先不显示图(留一个空位), 下次滚动回来命中缓存即出图 ——
    /// 表情图极小, 代价可以接受; 比让每张卡片各起一个下载任务安全得多。
    /// </summary>
    private static class CoverLoaderFacade
    {
        public static ImageSource? Load(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            return Helpers.CoverLoader.TryGetCached(url, 64, out var image) ? image : null;
        }
    }
}
