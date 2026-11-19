using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliTouch.Helpers;
using BiliTouch.Views.Controls;

internal static partial class ScrollAbTests
{
    private static BitmapScalingMode? _coverScaling;

    internal static void CoverRendering(string artifacts, Action<bool, string> check)
    {
        var saved = Save();
        Window? window = null;
        try
        {
            ApplyUserSettings();
            var items = RealCovers(artifacts, 240);
            foreach (var url in items.Select(i => i.Cover).Distinct())
                CoverLoader.LoadAsync(url, 1280).GetAwaiter().GetResult();
            var fixture = HomeFixture(items);
            window = fixture.Item1;
            var viewport = fixture.Item2;
            SetUiScale((FrameworkElement)window.Content, UserScale);
            _dpiScale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            Console.WriteLine($"COVER fixture dpi={_dpiScale:F2} uiScale={UserScale:F2}");
            foreach (var (mode, cache) in new[] {
                (BitmapScalingMode.Fant, CardCache.Xaml),
                (BitmapScalingMode.Linear, CardCache.Xaml),
                (BitmapScalingMode.Fant, CardCache.Off),
                (BitmapScalingMode.Fant, CardCache.Xaml) })
            {
                _coverScaling = mode;
                _cardCache = cache;
                foreach (var card in Descendants<VideoCard>(window)) ApplyCache(card);
                Measure(window, viewport, $"cover-{mode}-cache-{cache}");
            }
            check(true, "cover rendering comparison completed");
        }
        finally { window?.Close(); _coverScaling = null; Restore(saved); }
    }
}
