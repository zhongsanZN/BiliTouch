using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BiliTouch.Helpers;

/// <summary>
/// 高性能弹幕描边文本元素: 构造时一次性构建并冻结矢量路径, 运行期只由 Retained DrawingContext
/// 绘制外轮廓 + 填充色, 完全不使用 DropShadowEffect(避免每条弹幕做一次全像素模糊合成)。
/// </summary>
public sealed class OutlinedDanmakuText : FrameworkElement
{
    private const double StrokeThickness = 2.4;
    private const double StrokePad = StrokeThickness * 0.5;
    private static readonly Typeface DefaultTypeface = new(
        SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Pen OutlinePen = CreateFrozenPen();

    private Geometry? _geometry;
    private FormattedText? _fallbackText;
    private Size _measured;
    private double _lastDpi = 1.0;

    public string Text { get; }
    public Brush Foreground { get; private set; }
    public double FontSize { get; }

    public OutlinedDanmakuText(string text, Brush foreground, double fontSize)
    {
        Text = text ?? "";
        Foreground = foreground ?? Brushes.White;
        FontSize = Math.Max(1, fontSize);
        IsHitTestVisible = false;
        BuildVisualData(1.0);
    }

    public void UpdateForeground(Brush foreground)
    {
        var next = foreground ?? Brushes.White;
        if (ReferenceEquals(Foreground, next)) return;
        Foreground = next;
        if (_fallbackText != null) _fallbackText.SetForegroundBrush(next);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var dpi = GetCurrentDpi();
        if (Math.Abs(dpi - _lastDpi) > 0.01) BuildVisualData(dpi);
        return _measured;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text)) return;
        if (_geometry != null && !_geometry.IsEmpty())
        {
            // 先画描边再画填充, 外扩轮廓不会侵蚀字形笔画本身
            dc.DrawGeometry(null, OutlinePen, _geometry);
            dc.DrawGeometry(Foreground, null, _geometry);
            return;
        }
        if (_fallbackText != null)
        {
            var shadow = new FormattedText(
                Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                DefaultTypeface, FontSize, OutlinePen.Brush, _lastDpi);
            dc.DrawText(shadow, new Point(StrokePad + 1, StrokePad + 1));
            dc.DrawText(_fallbackText, new Point(StrokePad, StrokePad));
        }
    }

    private double GetCurrentDpi()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            return dpi > 0 ? dpi : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private void BuildVisualData(double dpi)
    {
        _lastDpi = dpi;
        if (string.IsNullOrEmpty(Text))
        {
            _geometry = null;
            _fallbackText = null;
            _measured = new Size(0, 0);
            return;
        }
        var ft = new FormattedText(
            Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            DefaultTypeface,
            FontSize,
            Foreground,
            dpi);
        _measured = new Size(
            Math.Ceiling(ft.WidthIncludingTrailingWhitespace + StrokePad * 2),
            Math.Ceiling(ft.Height + StrokePad * 2));
        var geo = ft.BuildGeometry(new Point(StrokePad, StrokePad));
        if (geo != null && !geo.IsEmpty())
        {
            if (geo.CanFreeze) geo.Freeze();
            _geometry = geo;
            _fallbackText = null;
        }
        else
        {
            _geometry = null;
            _fallbackText = ft;
        }
    }

    private static Pen CreateFrozenPen()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xE6, 0x00, 0x00, 0x00));
        brush.Freeze();
        var pen = new Pen(brush, StrokeThickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }
}
