using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.Views.Controls;

/// <summary>
/// 直播间侧栏单条实时弹幕的轻量直绘控件。
/// 替代原先每行 10 个 WPF 可视节点（Border + StackPanel + DockPanel + 4 TextBlock + 6 Binding + 4 DynamicResource + DataTrigger）的模板：
/// 仅用单个 FrameworkElement 直接通过 DirectWrite FormattedText 测量与绘制，
/// 在高密度弹幕刷新时将单行布局与重绑开销降低一个数量级以上。
/// </summary>
public sealed class LiveChatRow : FrameworkElement
{
    private const double MarginX = 2;
    private const double MarginY = 2;
    private const double PadX = 12;
    private const double PadY = 7;
    private const double CornerRadius = 8;
    private const double BodyFontSize = 13;
    private const double MetaFontSize = 10;
    private const double TimeLeftGap = 8;
    private const double BodyTopMargin = 3;

    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(
        nameof(Entry),
        typeof(LiveChatEntry),
        typeof(LiveChatRow),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnEntryChanged));

    private static int _styleVersion;
    private static event Action? StyleCacheRefreshed;
    private static Brush _textPrimary = Brushes.White;
    private static Brush _textTertiary = Brushes.Gray;
    private static Brush _accentText = Brushes.DeepPink;
    private static Brush _scFill = Brushes.Transparent;
    private static FontFamily? _cachedFamily;
    private static Typeface? _normalTypeface;
    private static Typeface? _semiBoldTypeface;
    private static bool _hooksInstalled;
    private static string? _cachedTimeKey;
    private static double _cachedTimeDpi;
    private static int _cachedTimeStyleVersion = -1;
    private static FormattedText? _cachedTimeFt;

    private int _renderedStyleVersion = -1;
    private double _lastMeasureWidth = -1;
    private double _lastDpi = -1;
    private double _cachedHeight;
    private double _headerHeight;
    private double _identityY;
    private double _bodyY;
    private FormattedText? _timeFt;
    private FormattedText? _userFt;
    private FormattedText? _identityFt;
    private FormattedText? _bodyFt;

    public LiveChatEntry? Entry
    {
        get => (LiveChatEntry?)GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    public LiveChatRow()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        EnsureStyleHooks();
        Loaded += (_, _) =>
        {
            StyleCacheRefreshed -= OnStyleCacheRefreshed;
            StyleCacheRefreshed += OnStyleCacheRefreshed;
            if (_renderedStyleVersion != _styleVersion)
            {
                InvalidateMeasure();
                InvalidateVisual();
            }
        };
        Unloaded += (_, _) => StyleCacheRefreshed -= OnStyleCacheRefreshed;
    }

    private void OnStyleCacheRefreshed()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static void EnsureStyleHooks()
    {
        if (_hooksInstalled) return;
        _hooksInstalled = true;
        RefreshStyleCache();
        Svc.Theme.ThemeChanged += OnGlobalStyleChanged;
        AppTypography.Changed += OnGlobalStyleChanged;
    }

    private static void OnGlobalStyleChanged()
    {
        RefreshStyleCache();
        StyleCacheRefreshed?.Invoke();
    }

    private static void RefreshStyleCache()
    {
        var app = Application.Current;
        if (app != null)
        {
            if (app.TryFindResource("TextPrimaryBrush") is Brush tp) _textPrimary = tp;
            if (app.TryFindResource("TextTertiaryBrush") is Brush tt) _textTertiary = tt;
            if (app.TryFindResource("AccentTextOnCardBrush") is Brush at) _accentText = at;
            if (app.TryFindResource("AccentSoftFillStrongBrush") is Brush sc) _scFill = sc;
        }
        // 系统默认字体优先走 Microsoft YaHei UI（同时完整包含中英文字形），避免 Segoe UI Variable Text 对每个中文弹幕字符触发 DirectWrite 回退查找
        FontFamily family;
        if (AppTypography.Normalize(Svc.Settings.FontId) == "System")
            family = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI");
        else
            family = app?.TryFindResource(AppTypography.ResourceKey) as FontFamily ?? SystemFonts.MessageFontFamily;

        if (!Equals(_cachedFamily, family))
        {
            _cachedFamily = family;
            _normalTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _semiBoldTypeface = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        }
        _styleVersion++;
    }

    private static FormattedText GetCachedTimeText(string timeText, Typeface normal, double dpi)
    {
        if (_cachedTimeFt != null &&
            _cachedTimeStyleVersion == _styleVersion &&
            Math.Abs(_cachedTimeDpi - dpi) < 0.01 &&
            string.Equals(_cachedTimeKey, timeText, StringComparison.Ordinal))
        {
            return _cachedTimeFt;
        }
        _cachedTimeKey = timeText;
        _cachedTimeDpi = dpi;
        _cachedTimeStyleVersion = _styleVersion;
        _cachedTimeFt = new FormattedText(timeText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, normal, MetaFontSize, _textTertiary, dpi);
        return _cachedTimeFt;
    }

    private static void OnEntryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiveChatRow row)
        {
            row._lastMeasureWidth = -1;
            row._timeFt = row._userFt = row._identityFt = row._bodyFt = null;
        }
    }

    private void RebuildText(double contentWidth, double dpi)
    {
        var entry = Entry;
        if (entry == null || contentWidth <= 1)
        {
            _timeFt = _userFt = _identityFt = _bodyFt = null;
            _cachedHeight = 0;
            _lastMeasureWidth = contentWidth;
            _lastDpi = dpi;
            _renderedStyleVersion = _styleVersion;
            return;
        }

        if (_normalTypeface == null || _semiBoldTypeface == null)
            RefreshStyleCache();

        var normal = _normalTypeface!;
        var semiBold = _semiBoldTypeface!;
        var culture = CultureInfo.CurrentUICulture;

        var timeText = entry.TimeText;
        _timeFt = GetCachedTimeText(timeText, normal, dpi);

        var maxUserWidth = Math.Max(1, contentWidth - _timeFt.Width - TimeLeftGap);
        _userFt = new FormattedText(entry.User, culture, FlowDirection.LeftToRight, semiBold, BodyFontSize, _accentText, dpi)
        {
            MaxTextWidth = maxUserWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };

        _headerHeight = Math.Max(_userFt.Height, _timeFt.Height);
        var y = _headerHeight;

        var identity = entry.Identity;
        if (identity.Length > 0)
        {
            _identityY = y;
            _identityFt = new FormattedText(identity, culture, FlowDirection.LeftToRight, normal, MetaFontSize, _textTertiary, dpi)
            {
                MaxTextWidth = contentWidth,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            y += _identityFt.Height;
        }
        else
        {
            _identityFt = null;
        }

        _bodyY = y + BodyTopMargin;
        _bodyFt = new FormattedText(entry.Text, culture, FlowDirection.LeftToRight, normal, BodyFontSize, _textPrimary, dpi)
        {
            MaxTextWidth = contentWidth
        };

        _cachedHeight = Math.Ceiling(_bodyY + _bodyFt.Height + (PadY + MarginY) * 2);
        _lastMeasureWidth = contentWidth;
        _lastDpi = dpi;
        _renderedStyleVersion = _styleVersion;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 320;
        var contentWidth = Math.Max(1, width - (MarginX + PadX) * 2);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi <= 0) dpi = 1.0;

        if (_renderedStyleVersion != _styleVersion ||
            Math.Abs(_lastMeasureWidth - contentWidth) > 0.5 ||
            Math.Abs(_lastDpi - dpi) > 0.01)
        {
            RebuildText(contentWidth, dpi);
        }

        return new Size(width, _cachedHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var entry = Entry;
        if (entry == null) return;

        var width = ActualWidth > 0 ? ActualWidth : 320;
        var contentWidth = Math.Max(1, width - (MarginX + PadX) * 2);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi <= 0) dpi = 1.0;

        if (_renderedStyleVersion != _styleVersion ||
            Math.Abs(_lastMeasureWidth - contentWidth) > 0.5 ||
            Math.Abs(_lastDpi - dpi) > 0.01 ||
            _bodyFt == null)
        {
            RebuildText(contentWidth, dpi);
        }

        if (entry.Kind == LiveChatKind.SuperChat)
        {
            var rect = new Rect(MarginX, MarginY, Math.Max(0, width - MarginX * 2), Math.Max(0, ActualHeight - MarginY * 2));
            dc.DrawRoundedRectangle(_scFill, null, rect, CornerRadius, CornerRadius);
        }

        var originX = MarginX + PadX;
        var originY = MarginY + PadY;

        if (_userFt != null)
            dc.DrawText(_userFt, new Point(originX, originY));

        if (_timeFt != null)
            dc.DrawText(_timeFt, new Point(originX + Math.Max(0, contentWidth - _timeFt.Width), originY));

        if (_identityFt != null)
            dc.DrawText(_identityFt, new Point(originX, originY + _identityY));

        if (_bodyFt != null)
            dc.DrawText(_bodyFt, new Point(originX, originY + _bodyY));
    }
}
