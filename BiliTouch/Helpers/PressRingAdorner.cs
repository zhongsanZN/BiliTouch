using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BiliTouch.Helpers;

/// <summary>
/// 围绕元素外框的一圈进度描边（顺时针）。
///
/// 使用 Adorner 而不是放在按钮 Content 里的 Rectangle：
/// Adorner 不参与被装饰元素的布局，绘制在按钮外框上不会改变 DesiredSize、不会撑大按钮或挤走右侧控件。
/// </summary>
public sealed class PressRingAdorner : Adorner
{
    public PressRingAdorner(UIElement adorned) : base(adorned)
    {
        IsHitTestVisible = false;
    }

    /// <summary>0~1 的进度（顺时针从左上角开始）。</summary>
    public double Progress { get; set; }

    /// <summary>描边画刷（点赞红 / 投币金 / 收藏蓝）。</summary>
    public Brush Stroke { get; set; } = Brushes.Transparent;

    /// <summary>描边线宽。</summary>
    public double Thickness { get; set; } = 2;

    /// <summary>圆角半径（与 PlayerActionBtn 的 Tint 圆角一致）。</summary>
    public double Radius { get; set; } = 8;

    protected override void OnRender(DrawingContext dc)
    {
        var progress = Math.Clamp(Progress, 0, 1);
        if (progress <= 0) return;

        var w = AdornedElement.RenderSize.Width;
        var h = AdornedElement.RenderSize.Height;
        if (w <= Thickness || h <= Thickness) return;

        // 描边居中：向内缩半个线宽，使外沿紧贴按钮边框，不压到相邻按钮。
        var inset = Thickness / 2;
        var rect = new Rect(inset, inset, w - Thickness, h - Thickness);
        var geo = new RectangleGeometry(rect, Radius, Radius);
        var len = RingLength(rect.Width, rect.Height, Radius);
        if (len <= 0) return;

        // DashStyle 的长度单位是线宽倍数。
        var units = len / Thickness;
        var pen = new Pen(Stroke, Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            DashStyle = new DashStyle(
                new[] { progress * units, units }, 0)
        };
        dc.DrawGeometry(null, pen, geo);
    }

    /// <summary>计算圆角矩形周长（供绘制 DashStyle 与单元测试使用）。</summary>
    internal static double RingLength(double w, double h, double r)
    {
        if (w <= 0 || h <= 0) return 0;
        var rr = Math.Clamp(r, 0, Math.Min(w, h) / 2);
        return 2 * (w - 2 * rr) + 2 * (h - 2 * rr) + 2 * Math.PI * rr;
    }
}
