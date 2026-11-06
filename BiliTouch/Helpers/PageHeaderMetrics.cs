using System.Windows;
namespace BiliTouch.Helpers;
public static class PageHeaderMetrics
{
    public static GridLength TitleRow => new(68);
    public static GridLength TabsRow => new(44);
    public static GridLength HeaderRow => new(112);
    public static Thickness TitleMargin => new(24,20,24,8);
    public static Thickness TabsMargin => new(24,0,24,0);
}
