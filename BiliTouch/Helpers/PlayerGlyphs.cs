using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BiliTouch.Helpers;

/// <summary>
/// 播放器与直播间共用的控制栏图标(2026-11-05)。
///
/// ★★★ 为什么要有这个文件: 用户原话是"直播间重做之后很多东西没有做到对齐,
///   比如播放暂停按钮、声音等等"。两边的播放/暂停原来一个是矢量 Path、
///   另一个是字体字符"Ⅱ"/"▶" —— 字号、粗细、垂直居中全都对不上。
///   现在两个窗口都从这一份工厂拿图标, 只有一条路径能画出暂停键。
///
/// ★ 用 Path 而不是字体图标: 字体缺失时会变成方块(项目里踩过很多次),
///   而这两颗按钮是最高频的操作, 不能有"看不见"的可能。
/// </summary>
public static class PlayerGlyphs
{
    private const string PlayData = "M 7,4 L 18,11 L 7,18 Z";
    private const string PauseData = "M 5,4 L 9,4 L 9,18 L 5,18 Z M 12,4 L 16,4 L 16,18 L 12,18 Z";

    /// <summary>播放三角(暂停态显示它: 点一下继续播)</summary>
    public static Path Play(double size = 15) => Build(PlayData, size);

    /// <summary>暂停双竖条(播放中显示它: 点一下暂停)</summary>
    public static Path Pause(double size = 15) => Build(PauseData, size);

    /// <summary>
    /// 全屏图标码位。**三个地方共用这一个值**: 播放器控制栏(BtnFullscreen2)、
    /// 网页布局小窗(WebMiniControls)、直播间控制栏。
    /// ★ 2026-11-05 之前播放器用 E92D、小窗与直播间用 E740 —— 同一个动作三种字形的
    ///   一部分, 用户点名说"直播间是自己乱做一套"。现在只有这一个常量, 想换一起换。
    /// </summary>
    public static string FullscreenGlyph => "\uE92D";

    /// <summary>"铺满窗口"进入态 = 虚线框; 已铺满 = 向内的双箭头</summary>
    public static string FillWindowGlyph(bool filled) => filled ? "\uE73F" : "\uE9A6";

    /// <summary>音量图标码位: 有声 E767 / 静音 E74F</summary>
    public static string VolumeGlyph(bool muted) => muted ? "\uE74F" : "\uE767";

    private static Path Build(string data, double size)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return new Path
        {
            Data = geometry,
            Fill = Brushes.White,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false
        };
    }
}
