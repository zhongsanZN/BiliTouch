using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BiliTouch.Services;

/// <summary>窗口和托盘直接使用随程序集发布的品牌资源，避免依赖 Shell 的 EXE 图标缓存。</summary>
public static class AppBranding
{
    public static ImageSource WindowIcon { get; } = LoadWindowIcon();

    private static ImageSource LoadWindowIcon()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri("pack://application:,,,/BiliTouch;component/Assets/app.png", UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static System.Drawing.Icon CreateTrayIcon()
    {
        // Publishing also copies a versioned ICO for the installer. Prefer that file:
        // MSBuild Link metadata can change the WPF resource key in published builds.
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", $"BiliTouch-{App.AppVersion}.ico");
        if (System.IO.File.Exists(path))
        {
            using var fileIcon = new System.Drawing.Icon(path);
            return (System.Drawing.Icon)fileIcon.Clone();
        }
        var resource = Application.GetResourceStream(new Uri("/BiliTouch;component/Assets/app.ico", UriKind.Relative))
            ?? throw new InvalidOperationException("应用图标资源缺失");
        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)icon.Clone();
    }
}
