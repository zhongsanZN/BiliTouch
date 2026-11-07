using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliTouch.Helpers;

internal static class ThemeSnapshotTests
{
    internal static void Run(Action<bool,string> check)
    {
        var root=new Grid {Width=900,Height=500,Background=Brushes.White};
        root.Children.Add(new TextBlock {Text="清晰文字 Theme",FontSize=16,Margin=new Thickness(24)});
        root.Measure(new Size(900,500));root.Arrange(new Rect(0,0,900,500));root.UpdateLayout();
        var capture=typeof(ThemeTransition).GetMethod("Capture",BindingFlags.Static|BindingFlags.NonPublic)!;
        var image=(Image)capture.Invoke(null,new object[]{root,900d,500d})!;
        var bitmap=(BitmapSource)image.Source;var dpi=VisualTreeHelper.GetDpi(root);
        check(bitmap.PixelWidth==(int)Math.Ceiling(900*dpi.DpiScaleX) &&
            bitmap.PixelHeight==(int)Math.Ceiling(500*dpi.DpiScaleY),
            "theme snapshot preserves native DPI pixels instead of shrinking the page");
        check(image.Stretch==Stretch.None,"theme snapshot is displayed without resampling");
        check(capture.Invoke(null,new object[]{root,4000d,2200d})==null,
            "oversized windows skip the snapshot instead of lowering resolution");
    }
}
