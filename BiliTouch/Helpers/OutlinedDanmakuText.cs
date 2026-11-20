using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BiliTouch.Services;

namespace BiliTouch.Helpers;

/// <summary>
/// 高性能弹幕描边文本元素：
/// - 采用 DirectWrite 硬件字形图集缓存的 <see cref="FormattedText"/> 四向轮廓 + 中心填充，
///   配合外层容器的 <see cref="BitmapCache"/> 在首次上屏时一次性栅格化为 GPU 纹理，
///   彻底消除 <c>FormattedText.BuildGeometry()</c> 矢量路径提取与每帧数万三角形描边细分开销；
/// - 构造时直接采用主窗口真实 DPI，消除高分屏（125%~225% 缩放）下构造按 1.0、Measure 再重算一次的双倍构建；
/// - 支持 <see cref="SetContent"/> 原地复用，配合弹幕对象池实现稳态零控件分配。
/// </summary>
public sealed class OutlinedDanmakuText : FrameworkElement
{
    private const double StrokeThickness = 2.4;
    private const double StrokePad = StrokeThickness * 0.5;
    private const double OutlineOffset = 1.1;
    private static readonly Brush OutlineBrush = CreateFrozenOutlineBrush();

    private static readonly FontFamily SystemCjkFirstFamily = new("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI");
    private static FontFamily? s_cachedFamily;
    private static Typeface? s_cachedTypeface;

    private FormattedText? _outlineText;
    private FormattedText? _fillText;
    private Size _measured;
    private double _lastDpi = 1.0;

    public string Text { get; private set; } = "";
    public Brush Foreground { get; private set; } = Brushes.White;
    public double FontSize { get; private set; } = 24;

    public OutlinedDanmakuText(string text, Brush foreground, double fontSize)
    {
        IsHitTestVisible = false;
        Text = text ?? "";
        Foreground = foreground ?? Brushes.White;
        FontSize = Math.Max(1, fontSize);
        BuildVisualData(ResolveInitialDpi());
    }

    /// <summary>
    /// 对象池原地更新文本、颜色与字号，避免每次弹幕上屏都新建 <see cref="FrameworkElement"/>。
    /// </summary>
    public void SetContent(string text, Brush foreground, double fontSize)
    {
        var nextText = text ?? "";
        var nextBrush = foreground ?? Brushes.White;
        var nextSize = Math.Max(1, fontSize);

        if (string.Equals(Text, nextText, StringComparison.Ordinal)
            && Math.Abs(FontSize - nextSize) <= 0.01)
        {
            UpdateForeground(nextBrush);
            return;
        }

        Text = nextText;
        Foreground = nextBrush;
        FontSize = nextSize;
        BuildVisualData(GetCurrentDpi());
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static Typeface ResolveTypeface()
    {
        var family = AppTypography.Normalize(Svc.Settings.FontId) == "System"
            ? SystemCjkFirstFamily
            : Application.Current?.TryFindResource(AppTypography.ResourceKey) as FontFamily
              ?? SystemFonts.MessageFontFamily;
        if (s_cachedTypeface != null && ReferenceEquals(s_cachedFamily, family))
            return s_cachedTypeface;

        s_cachedFamily = family;
        s_cachedTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return s_cachedTypeface;
    }

    public void UpdateForeground(Brush foreground)
    {
        var next = foreground ?? Brushes.White;
        if (ReferenceEquals(Foreground, next)) return;
        Foreground = next;
        _fillText?.SetForegroundBrush(next);
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
        if (string.IsNullOrEmpty(Text) || _outlineText == null || _fillText == null) return;

        // 四角偏移绘制深色描边 + 中心绘制前景字形：全程走 DirectWrite 硬件字形缓存，无矢量路径细分
        dc.DrawText(_outlineText, new Point(StrokePad - OutlineOffset, StrokePad - OutlineOffset));
        dc.DrawText(_outlineText, new Point(StrokePad + OutlineOffset, StrokePad - OutlineOffset));
        dc.DrawText(_outlineText, new Point(StrokePad - OutlineOffset, StrokePad + OutlineOffset));
        dc.DrawText(_outlineText, new Point(StrokePad + OutlineOffset, StrokePad + OutlineOffset));
        dc.DrawText(_fillText, new Point(StrokePad, StrokePad));
    }

    private static double ResolveInitialDpi()
    {
        try
        {
            if (Application.Current?.MainWindow is { } win)
            {
                var dpi = VisualTreeHelper.GetDpi(win).PixelsPerDip;
                if (dpi > 0) return dpi;
            }
        }
        catch { }
        return 1.0;
    }

    private double GetCurrentDpi()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            return dpi > 0 ? dpi : _lastDpi;
        }
        catch
        {
            return _lastDpi;
        }
    }

    private void BuildVisualData(double dpi)
    {
        _lastDpi = dpi > 0 ? dpi : 1.0;
        if (string.IsNullOrEmpty(Text))
        {
            _outlineText = null;
            _fillText = null;
            _measured = new Size(0, 0);
            return;
        }

        var typeface = ResolveTypeface();
        var culture = CultureInfo.CurrentUICulture;
        _outlineText = new FormattedText(
            Text,
            culture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            OutlineBrush,
            _lastDpi);
        _fillText = new FormattedText(
            Text,
            culture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Foreground,
            _lastDpi);
        _measured = new Size(
            Math.Ceiling(_fillText.WidthIncludingTrailingWhitespace + StrokePad * 2),
            Math.Ceiling(_fillText.Height + StrokePad * 2));
    }

    private static Brush CreateFrozenOutlineBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xE6, 0x00, 0x00, 0x00));
        brush.Freeze();
        return brush;
    }
}
