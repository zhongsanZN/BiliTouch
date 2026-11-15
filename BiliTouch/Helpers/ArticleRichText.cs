using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.Helpers;

/// <summary>
/// 将 <see cref="ArticleBlock"/> 内的行内样式片段（加粗/斜体/链接/BV 引用）渲染为 WPF Inlines，
/// 若没有 Runs 则回退到纯文本。
/// </summary>
public static class ArticleRichText
{
    public static readonly DependencyProperty BlockProperty = DependencyProperty.RegisterAttached(
        "Block", typeof(ArticleBlock), typeof(ArticleRichText),
        new PropertyMetadata(null, OnBlockChanged));

    public static ArticleBlock? GetBlock(DependencyObject d) => (ArticleBlock?)d.GetValue(BlockProperty);
    public static void SetBlock(DependencyObject d, ArticleBlock? v) => d.SetValue(BlockProperty, v);

    private static void OnBlockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not ArticleBlock block) return;

        if (!block.HasRuns)
        {
            tb.Text = block.Text;
            return;
        }

        foreach (var r in block.Runs)
        {
            if (string.IsNullOrEmpty(r.Text)) continue;

            if (r.IsInteractive)
            {
                var link = new Hyperlink(new Run(r.Text))
                {
                    TextDecorations = null,
                    Cursor = Cursors.Hand,
                };
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextBrush");
                var bvid = r.ReferencedBvid;
                var url = r.Url;
                link.Click += (_, _) =>
                {
                    if (!string.IsNullOrEmpty(bvid))
                        Svc.Player.PlayVideo(bvid);
                    else if (!string.IsNullOrEmpty(url))
                        Svc.Navigate?.HandleUrl(url);
                };
                if (r.Bold) link.FontWeight = FontWeights.SemiBold;
                if (r.Italic) link.FontStyle = FontStyles.Italic;
                tb.Inlines.Add(link);
            }
            else
            {
                var run = new Run(r.Text);
                if (r.Bold) run.FontWeight = FontWeights.SemiBold;
                if (r.Italic) run.FontStyle = FontStyles.Italic;
                tb.Inlines.Add(run);
            }
        }
    }
}
