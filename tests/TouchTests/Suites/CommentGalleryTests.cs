using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BiliTouch.Helpers;
using BiliTouch.Views.Controls;

/// <summary>
/// 锁死「点开评论图片必崩」这个 bug（0.2.9 引入，0.2.12 才发现）。
///
/// 崩溃栈：InlineImageGallery.Button() → Decorator.set_Child →
///   "指定的元素已经是另一个元素的逻辑子元素"
/// 根因：SkinGlyph 是 Decorator，把 button.Content 那个 TextBlock 直接塞进它的 Child；
///   而 TextBlock 的逻辑父级已经是 Button。构造 InlineImageGallery 时必抛，
///   于是所有评论图片都点不开。
/// </summary>
internal static class CommentGalleryTests
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<bool, string> check)
    {
        InlineImageGallery gallery;
        try
        {
            gallery = new InlineImageGallery();
        }
        catch (Exception ex)
        {
            check(false, "InlineImageGallery must construct without throwing: " + ex.GetType().Name + " " + ex.Message);
            return;
        }

        check(true, "InlineImageGallery constructs without the logical-parent exception");

        var prev = (Button)typeof(InlineImageGallery).GetField("_previous", Hidden)!.GetValue(gallery)!;
        var next = (Button)typeof(InlineImageGallery).GetField("_next", Hidden)!.GetValue(gallery)!;
        var collapse = (Button)typeof(InlineImageGallery).GetField("_collapse", Hidden)!.GetValue(gallery)!;
        var save = (Button)typeof(InlineImageGallery).GetField("_save", Hidden)!.GetValue(gallery)!;

        check(prev.Content is SkinGlyph && next.Content is SkinGlyph,
            "prev/next buttons keep their themed SkinGlyph wrapper");

        var glyph = (SkinGlyph)prev.Content;
        check(glyph.Child is TextBlock { Text: "‹" },
            "the glyph keeps the original TextBlock as its fallback child");
        check(glyph.Child is FrameworkElement fe && (ReferenceEquals(fe.Parent, glyph) || fe.Parent == null),
            "fallback TextBlock is no longer owned by the Button (single logical parent)");

        check(collapse.Content is not SkinGlyph && save.Content is not SkinGlyph,
            "text-only buttons (close / save) are untouched");

        // 二次构造：防止将来又引入"静态复用同一个元素"的写法
        try
        {
            var second = new InlineImageGallery();
            check(second is not null, "a second gallery can be constructed independently");
        }
        catch (Exception ex)
        {
            check(false, "second gallery construction failed: " + ex.GetType().Name);
        }

        try
        {
            gallery.SetInline(true);
            gallery.Cancel();
            check(true, "SetInline / Cancel stay safe on an unopened gallery");
        }
        catch (Exception ex)
        {
            check(false, "SetInline / Cancel threw: " + ex.GetType().Name);
        }
    }
}
