using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BiliDesk.Services;

/// <summary>窗口和托盘直接使用随程序集发布的品牌资源，避免依赖 Shell 的 EXE 图标缓存。</summary>
public static class AppBranding
{
    public static ImageSource WindowIcon { get; } = LoadWindowIcon();

    private static ImageSource LoadWindowIcon()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri("pack://application:,,,/BiliDesk;component/Assets/app.png", UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static System.Drawing.Icon CreateTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("/BiliDesk;component/Assets/app.ico", UriKind.Relative))
            ?? throw new InvalidOperationException("应用图标资源缺失");
        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)icon.Clone();
    }
}
