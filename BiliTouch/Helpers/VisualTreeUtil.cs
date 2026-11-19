using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace BiliTouch.Helpers;

/// <summary>
/// 统一的可视树 / 逻辑树查找工具（安全兼容 <see cref="ContentElement"/>，如 Run/Hyperlink/Span）。
/// </summary>
public static class VisualTreeUtil
{
    /// <summary>安全获取父节点：对 ContentElement 走内容/逻辑树，避免 VisualTreeHelper.GetParent 抛异常。</summary>
    public static DependencyObject? GetParent(DependencyObject? d)
    {
        if (d == null) return null;
        if (d is ContentElement ce)
            return ContentOperations.GetParent(ce) ?? (ce as FrameworkContentElement)?.Parent ?? LogicalTreeHelper.GetParent(ce);
        if (d is Visual or Visual3D)
            return VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return LogicalTreeHelper.GetParent(d);
    }

    /// <summary>
    /// 从 <paramref name="d"/> 开始往上找第一个 T 类型的祖先(含自身)。找不到返回 null。
    /// </summary>
    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = GetParent(d);
        }
        return null;
    }

    /// <summary>判断 <paramref name="child"/> 是否为 <paramref name="ancestor"/> 的后代（含自身）。</summary>
    public static bool IsDescendantOf(DependencyObject? child, DependencyObject? ancestor)
    {
        if (ancestor == null) return false;
        while (child != null)
        {
            if (ReferenceEquals(child, ancestor)) return true;
            child = GetParent(child);
        }
        return false;
    }

    /// <summary>在可视树中按类型深度优先查找首个后代（含自身）。</summary>
    public static T? FindDescendant<T>(DependencyObject? root) where T : class
    {
        if (root == null) return null;
        if (root is T match) return match;
        if (root is not (Visual or Visual3D)) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>在可视树中按名称查找首个后代（含自身）。</summary>
    public static FrameworkElement? FindDescendantByName(DependencyObject? root, string name)
    {
        if (root == null) return null;
        if (root is FrameworkElement fe && fe.Name == name) return fe;
        if (root is not (Visual or Visual3D)) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendantByName(VisualTreeHelper.GetChild(root, i), name);
            if (found != null) return found;
        }
        return null;
    }
}
