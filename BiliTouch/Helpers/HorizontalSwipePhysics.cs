using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace BiliTouch.Helpers;

/// <summary>
/// 全站横向跟手翻页的单一物理内核：
/// 统一首页、关注页、我的页与顶栏标签的死区、橡胶边阻、切页提交阈值与落位缓动曲线，
/// 避免四处各自硬编码漂移，实现全站一致的丝滑手感与单点调优。
/// </summary>
internal static class HorizontalSwipePhysics
{
    /// <summary>起手判定方向的最小位移死区（DIP），避免轻点微抖误锁横滑。</summary>
    internal const double Slop = 12.0;

    /// <summary>松手时位移需超过视口宽度的八分之三（37.5%）才提交翻页。</summary>
    internal const double CommitRatio = 3.0 / 8.0;

    /// <summary>边界越界时的最大橡胶弹性比例。</summary>
    internal const double EdgeResist = 0.35;

    /// <summary>翻页/回弹吸附动画时长（毫秒）。</summary>
    internal const int SnapDurationMs = 170;

    /// <summary>翻页/回弹吸附动画时长。</summary>
    internal static readonly TimeSpan SnapDuration = TimeSpan.FromMilliseconds(SnapDurationMs);

    /// <summary>全局冻结的缓出曲线，零重复分配。</summary>
    internal static readonly CubicEase SnapEase = CreateFrozenEase();

    private static CubicEase CreateFrozenEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    /// <summary>
    /// 边界非线性橡胶阻尼位移：小幅拖动线性跟手，拉得越远阻力越大并渐近收敛至 <c>width * EdgeResist</c>。
    /// </summary>
    internal static double RubberBand(double dx, double width)
    {
        if (dx == 0) return 0;
        var w = Math.Max(1.0, width);
        return Math.Sign(dx) * w * EdgeResist * (1.0 - Math.Exp(-Math.Abs(dx) / w));
    }

    /// <summary>当前位移是否已达到翻页提交阈值。</summary>
    internal static bool ShouldCommit(double dx, double width) =>
        width > 0 && Math.Abs(dx) >= width * CommitRatio;

    /// <summary>构造统一参数的横向落位动画。</summary>
    internal static DoubleAnimation CreateSnapAnimation(double from, double to) => new()
    {
        From = from,
        To = to,
        Duration = SnapDuration,
        EasingFunction = SnapEase
    };
}
