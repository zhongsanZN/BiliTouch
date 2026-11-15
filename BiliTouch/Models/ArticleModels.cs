using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliTouch.Models;

/// <summary>专栏 / 图文正文块类型</summary>
public enum ArticleBlockKind
{
    Paragraph,
    Heading,
    Image,
    Quote,
    Code,
    Divider,
    ListItem,
    VideoCard,
}

/// <summary>正文段落中的行内样式片段（加粗 / 斜体 / 链接 / BVID 引用）</summary>
public class ArticleTextRun
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public string Color { get; set; } = "";
    public string Url { get; set; } = "";
    public string ReferencedBvid { get; set; } = "";
    public bool IsInteractive => Url.Length > 0 || ReferencedBvid.Length > 0;
}

/// <summary>专栏 / Opus 结构化内容块</summary>
public class ArticleBlock
{
    public ArticleBlockKind Kind { get; set; } = ArticleBlockKind.Paragraph;
    public string Text { get; set; } = "";
    public int HeadingLevel { get; set; } = 2;
    public string CodeLanguage { get; set; } = "";
    public bool OrderedList { get; set; }
    public int ListIndex { get; set; }
    public List<ArticleTextRun> Runs { get; set; } = new();
    public bool HasRuns => Runs.Count > 0;

    public string ImageUrl { get; set; } = "";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public string ImageCaption { get; set; } = "";
    public int ImageIndex { get; set; } = -1;

    public VideoItem? Video { get; set; }
    public string LinkUrl { get; set; } = "";

    public double HeadingFontSize => HeadingLevel switch
    {
        1 => 21,
        2 => 18.5,
        _ => 16.5,
    };

    public string ListMarker => OrderedList && ListIndex > 0 ? $"{ListIndex}." : "•";
    public bool HasImageCaption => ImageCaption.Length > 0;
}

/// <summary>专栏列表摘要项（用于搜索专栏与 UP 主个人主页专栏列表）</summary>
public class ArticleSummary
{
    public string ArticleId { get; set; } = "";
    public string OpusId { get; set; } = "";
    public string DynamicId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Cover { get; set; } = "";
    public List<string> Covers { get; set; } = new();
    public string AuthorName { get; set; } = "";
    public long AuthorMid { get; set; }
    public string AuthorFace { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public long PubTs { get; set; }
    public string PubTimeText { get; set; } = "";
    public long ViewCount { get; set; }
    public long LikeCount { get; set; }
    public long ReplyCount { get; set; }
    public long FavoriteCount { get; set; }
    public long WordCount { get; set; }

    public bool HasCover => Cover.Length > 0;
    public bool HasCategory => CategoryName.Length > 0;
    public string ViewCountText => FormatNum(ViewCount);
    public string LikeCountText => FormatNum(LikeCount);
    public string ReplyCountText => FormatNum(ReplyCount);

    public string MetaLine
    {
        get
        {
            var parts = new List<string>();
            if (AuthorName.Length > 0) parts.Add(AuthorName);
            if (CategoryName.Length > 0) parts.Add(CategoryName);
            if (PubTimeText.Length > 0) parts.Add(PubTimeText);
            return string.Join(" · ", parts);
        }
    }

    public string StatLine
    {
        get
        {
            var parts = new List<string>();
            if (ViewCount > 0) parts.Add($"{ViewCountText}阅读");
            if (LikeCount > 0) parts.Add($"{LikeCountText}赞");
            if (ReplyCount > 0) parts.Add($"{ReplyCountText}评");
            return parts.Count > 0 ? string.Join(" · ", parts) : "专栏";
        }
    }

    private static string FormatNum(long n) => n switch
    {
        >= 100_000_000 => (n / 100_000_000.0).ToString("0.#") + "亿",
        >= 10_000 => (n / 10_000.0).ToString("0.#") + "万",
        _ => n.ToString(),
    };
}

/// <summary>专栏 / Opus 阅读页完整数据</summary>
public class ArticleDetail
{
    public string ArticleId { get; set; } = "";
    public string OpusId { get; set; } = "";
    public string DynamicId { get; set; } = "";
    public string CommentOid { get; set; } = "";
    public int CommentType { get; set; } = 12;

    public string Title { get; set; } = "";
    public string BannerUrl { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public long PubTs { get; set; }
    public string PubTimeText { get; set; } = "";

    public long AuthorMid { get; set; }
    public string AuthorName { get; set; } = "";
    public string AuthorFace { get; set; } = "";

    public long ViewCount { get; set; }
    public long LikeCount { get; set; }
    public long FavoriteCount { get; set; }
    public long CoinCount { get; set; }
    public long ShareCount { get; set; }
    public long ReplyCount { get; set; }
    public long WordCount { get; set; }

    public bool IsLiked { get; set; }
    public bool IsFavorited { get; set; }
    public bool IsOpusBacked { get; set; }

    public List<ArticleBlock> Blocks { get; set; } = new();
    public List<DynamicPicture> AllImages { get; set; } = new();

    public string CanonicalUrl =>
        ArticleId.Length > 0 ? $"https://www.bilibili.com/read/cv{ArticleId}" :
        OpusId.Length > 0 ? $"https://www.bilibili.com/opus/{OpusId}" : "";

    public bool CanFavorite => ArticleId.Length > 0;
}
