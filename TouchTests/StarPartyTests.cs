using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Controls;
using BiliTouch.Views.Pages;

internal static class StarPartyTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    static void Until(Func<bool> condition)
    {
        var start = Environment.TickCount64;
        while (!condition()) { if (Environment.TickCount64 - start > 5000) throw new TimeoutException("skin UI did not settle"); Pump(15); }
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T t) yield return t; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    static void Save(FrameworkElement root, string path, double scale = 1)
    {
        root.UpdateLayout(); var image = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(path); png.Save(output);
    }
    public static void Run(Action<bool, string> check, string artifacts)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var oldHttp = typeof(ApiClient).GetField("_http", Hidden)!.GetValue(Svc.Api);
        var oldSession = Svc.Session.Current; var oldSkin = Svc.Settings.SkinId; var oldAccent = Svc.Settings.AccentColor;
        var oldMode = Svc.Theme.Mode; var oldWeb = Svc.Settings.UseWebPlayerLayout;
        var oldEffects = Svc.Settings.SkinEffects; var oldUseAccent = Svc.Settings.SkinUseAccent;
        var handler = new Fixture(); using var http = new HttpClient(handler);
        typeof(ApiClient).GetField("_http", Hidden)!.SetValue(Svc.Api, http);
        typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session, new Session { SessData = "fixture", BiliJct = "fixture", DedeUserID = "42" });
        var windows = new List<Window>();
        try
        {
            Svc.Theme.SetMode(AppTheme.Light); Svc.Theme.SetAccent("#327CB0");
            var vm = new SettingsViewModel(); vm.SkinSelected = vm.SkinOptions[1];
            check(SkinService.Instance.IsEnabled && Svc.Settings.AccentColor == "#327CB0", "skin preserves the user's saved accent");
            check(((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color == Color.FromRgb(255, 38, 130), "skin recommendation supplies the effective accent");
            vm.SkinUseAccent = false;
            check(((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color == Color.FromRgb(50, 124, 176), "custom accent remains usable while wearing the skin");
            vm.SkinUseAccent = true;
            var snapshot = (AppSettings)Call(Svc.Settings, "Snapshot")!;
            var serialized = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(snapshot))!;
            Svc.Settings.SkinId = ""; Call(Svc.Settings, "ApplySanitized", serialized);
            check(Svc.Settings.SkinId == SkinService.StarParty && Svc.Settings.SkinEffects && Svc.Settings.SkinUseAccent, "skin settings survive save/load normalization");
            serialized.SkinId = "unknown"; Call(Svc.Settings, "ApplySanitized", serialized);
            check(Svc.Settings.SkinId == "", "unknown skin identifiers fall back to the default");
            vm.SkinSelected = vm.SkinOptions[1];
            check(SkinService.Instance.Image("head_myself_bg").IsFrozen, "local theme images are cached and frozen");

            var main = new MainWindow { Width = 1320, Height = 820 }; windows.Add(main); main.Show(); Pump(250);
            var home = Descendants<HomePage>(main).First();
            var header = (FrameworkElement)home.FindName("PageHeader"); var start = header.TransformToAncestor(main).Transform(new Point());
            Save(main, Path.Combine(artifacts, "star-party-home-light.png"));
            main.Width = 860; Pump(220);
            check(((Border)main.FindName("NavRail")).ActualWidth == 68, "skin preserves the compact navigation width");
            Save(main, Path.Combine(artifacts, "star-party-home-compact.png"));
            main.Width = 1320; Svc.Theme.SetMode(AppTheme.Dark); Pump(500);
            var headerAfter = header.TransformToAncestor(main).Transform(new Point());
            check((headerAfter - start).Length < 1, $"skin and color mode do not move the home header ({start} -> {headerAfter})");
            Save(main, Path.Combine(artifacts, "star-party-home-dark.png"));
            main.Hide(); Svc.Theme.SetMode(AppTheme.Light);

            // Render the actual profile page with deterministic data, without using a real account.
            var profile = new MineOverviewPage();
            var profileHost = new Window { Width = 1120, Height = 740, Content = profile }; windows.Add(profileHost); profileHost.Show(); Pump(150);
            profile.DataContext = new { IsLoggedIn = true, Name = "中三", Mid = 42, Face = "", Sign = "记录创作与生活 · 星引擎party闪亮启航", FollowingText = "1538", FollowersText = "1630", VideoCountText = "12", Error = "", ShowEmpty = false, Loading = false, Videos = Array.Empty<VideoItem>() };
            Pump(100); var frame = (SkinProfileLayout)profile.FindName("ProfileSkinLayout");
            check(Math.Abs(frame.Child!.TranslatePoint(new Point(), frame).Y - 26) < 1, "wide profile preserves text padding over edge-to-edge artwork");
            Save(profileHost, Path.Combine(artifacts, "star-party-profile-wide.png"));
            profileHost.Width = 700; Pump(150);
            check(frame.Child.TranslatePoint(new Point(), frame).Y >= 190, "compact profile puts both characters above the personal information");
            Save(profileHost, Path.Combine(artifacts, "star-party-profile-compact.png"), 1.5);
            profileHost.Close();

            var pattern = new SkinSurface { Kind = "Home", Width = 2400, Height = 160 };
            var patternHost = new Window { Width = 1100, Height = 260, Content = new ScrollViewer { Content = pattern, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto } }; windows.Add(patternHost); patternHost.Show(); Pump(100);
            Save(pattern, Path.Combine(artifacts, "star-party-pattern-2400.png"), 2); patternHost.Close();
            var settings = new SettingsPage { DataContext = vm }; var settingsHost = new Window { Width = 860, Height = 800, Content = settings }; windows.Add(settingsHost); settingsHost.Show(); Pump(150);
            check(Descendants<ComboBox>(settings).Any(c => ReferenceEquals(c.ItemsSource, vm.SkinOptions)), "General settings expose the live decoration picker");
            Save(settingsHost, Path.Combine(artifacts, "star-party-settings.png")); settingsHost.Close();

            Svc.Settings.SetWebPlayerLayout(false);
            var player = new PlayerWindow { Width = 1320, Height = 860 }; windows.Add(player); player.Show(); Pump(250);
            Set(player, "_currentBvid", "BVfixture1"); Set(player, "_aid", 1L);
            ((TextBlock)player.FindName("UpNameText")).Text = "示例 UP";
            ((TextBlock)player.FindName("VideoTitleText")).Text = "星引擎party · 互动效果预览";
            var reactions = (ThemeReactionHost)player.FindName("ThemeReactions");
            var button = (Button)player.FindName("BtnLike");
            var toasts = new List<string>();
            Action<string> recordToast = message => toasts.Add(message);
            Svc.Toast.Shown += recordToast;
            var pendingLike = new TaskCompletionSource<HttpResponseMessage>(); handler.Pending = pendingLike;
            // ★ 2026-11-05: 用"上一次动效开始时刻"判"这一下点赞有没有触发过动效", 而不是只看 IsPlaying。
            //   动效本身 740ms, 而"请求返回 + 按钮重新可用"这段在夹具里可能超过一秒 ——
            //   等断言跑到时动效已经自然收场, 于是出现"八次挂两次"的假失败(实测)。
            //   语义不变: 我们要证明的是"点赞成功会庆祝", 不是"庆祝还在屏幕上"。
            long likeReactionTick = reactions.LastStartedTick;
            Call(player, "OnLikeClick", button, new RoutedEventArgs()); Pump(40);
            check(!reactions.IsPlaying && !button.IsEnabled, "pending like neither celebrates nor allows overlapping requests");
            pendingLike.SetResult(Fixture.Response("{\"code\":0}")); Until(() => button.IsEnabled);
            check(reactions.LastStartedTick > likeReactionTick && Field<bool>(player, "_isLiked"), $"successful video like triggers a character beside the original button (liked={Field<bool>(player,"_isLiked")}, playing={reactions.IsPlaying}, host={reactions.RenderSize}, loaded={reactions.IsLoaded}, buttonVisible={button.IsVisible}, bounds={button.TransformToVisual(reactions).TransformBounds(new Rect(button.RenderSize))}, effects={SkinService.Instance.EffectsEnabled}, systemAnimation={SystemParameters.ClientAreaAnimation}, toasts={string.Join(';',toasts)})");
            check(!toasts.Contains("点赞成功"), "successful like has no duplicate text toast");
            check(!reactions.IsHitTestVisible && new Rect(reactions.RenderSize).Contains(reactions.LastSpriteBounds) && !reactions.LastSpriteBounds.IsEmpty, "reaction stays within the information panel and never captures input");
            Pump(80);
            Save(player, Path.Combine(artifacts, "star-party-player-like.png"));
            Pump(820); check(!reactions.IsPlaying, "reaction clock removes itself after the short animation");
            Call(player, "OnLikeClick", button, new RoutedEventArgs()); Pump(80);
            check(!reactions.IsPlaying && !Field<bool>(player, "_isLiked"), "canceling a like updates state without celebrating");
            handler.Fail = true; Call(player, "OnLikeClick", button, new RoutedEventArgs()); Pump(80);
            check(!reactions.IsPlaying && !Field<bool>(player, "_isLiked"), "failed like never celebrates"); handler.Fail = false;
            pendingLike = new TaskCompletionSource<HttpResponseMessage>(); handler.Pending = pendingLike; Call(player, "OnLikeClick", button, new RoutedEventArgs()); Pump(20);
            Set(player, "_currentBvid", "BVfixture2"); Set(player, "_aid", 2L);
            pendingLike.SetResult(Fixture.Response("{\"code\":0}")); Until(() => button.IsEnabled);
            check(!reactions.IsPlaying && !Field<bool>(player, "_isLiked"), "late success for a previous video cannot mutate or celebrate the new video");
            var coinTick = reactions.LastStartedTick;
            var coin = (Task)Call(player, "CoinAsync", 1, true)!; Until(() => coin.IsCompleted); coin.GetAwaiter().GetResult();
            // 同上: 判"触发过"而不是"还在播"(动效 740ms, 可能已经收场)。
            check(reactions.LastStartedTick > coinTick && Field<bool>(player, "_isCoined") && Field<bool>(player, "_isLiked"), "coin with bundled like emits one successful reaction"); Pump(950);
            check(!toasts.Any(t => t is "投币成功" or "已投 2 枚币"), "successful coin has no duplicate text toast");

            // Exercise the real favorite picker and real success path with local HTTP responses.
            var pickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            pickerTimer.Tick += (_, _) =>
            {
                var picker = Application.Current.Windows.OfType<FavFolderPickerWindow>().FirstOrDefault(); if (picker == null) return;
                pickerTimer.Stop(); var item = ((ItemsControl)picker.FindName("FolderList")).Items[0]; item.GetType().GetProperty("IsChecked")!.SetValue(item, true);
                Call(picker, "OnConfirmClick", picker, new RoutedEventArgs());
            };
            var favTick = reactions.LastStartedTick;
            pickerTimer.Start(); Call(player, "OnFavoriteClick", player.FindName("BtnFavorite"), new RoutedEventArgs()); Until(() => !Field<bool>(player, "_favBusy"));
            check(reactions.LastStartedTick > favTick && Field<bool>(player, "_isFavorited"), "adding a favorite celebrates only after the confirmed folder update succeeds");
            check(!toasts.Any(t => t.StartsWith("已收藏到")), "successful favorite has no duplicate text toast");
            Svc.Toast.Shown -= recordToast;
            Pump(130);
            Save(player, Path.Combine(artifacts, "star-party-player-favorite.png")); Pump(950);
            vm.SkinEffects = false;
            check(!reactions.Show(ThemeReaction.Like, button), "theme interaction switch suppresses characters"); vm.SkinEffects = true;
            check(reactions.Show(ThemeReaction.Coin, button), "character can start again after the cooldown");
            vm.SkinSelected = vm.SkinOptions[0];
            check(!reactions.IsPlaying && ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color == Color.FromRgb(50, 124, 176), "removing the skin clears live effects and restores the saved accent");
            vm.SkinSelected = vm.SkinOptions[1]; Pump(950);
            Svc.Settings.SetWebPlayerLayout(true); Pump(400);
            check(reactions.Show(ThemeReaction.Like, button), "web layout resolves the relocated operation button as its current anchor");
            Save(player, Path.Combine(artifacts, "star-party-player-web.png"));
            player.Width = 1000; Pump(150); check(!reactions.IsPlaying, "layout changes clear the previous reaction position");
            player.Close(); main.Close();
        }
        finally
        {
            foreach (var window in windows) { try { window.Close(); } catch { } }
            typeof(ApiClient).GetField("_http", Hidden)!.SetValue(Svc.Api, oldHttp);
            typeof(SessionManager).GetProperty(nameof(SessionManager.Current))!.SetValue(Svc.Session, oldSession);
            Svc.Settings.SetWebPlayerLayout(oldWeb); Svc.Theme.SetAccent(oldAccent); SkinService.Instance.Configure(oldSkin, oldEffects, oldUseAccent); Svc.Theme.SetMode(oldMode);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }
    sealed class Fixture : HttpMessageHandler
    {
        public TaskCompletionSource<HttpResponseMessage>? Pending;
        public bool Fail;
        public List<string> Paths = new();
        public static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/x/web-interface/archive/like" && Pending != null) { var pending = Pending; Pending = null; return pending.Task; }
            string json = path switch
            {
                "/x/web-interface/nav" => "{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":42,\"uname\":\"测试用户\",\"face\":\"\",\"wbi_img\":{\"img_url\":\"https://example.invalid/01234567890123456789012345678901.png\",\"sub_url\":\"https://example.invalid/01234567890123456789012345678901.png\"}}}",
                "/x/web-interface/archive/like" or "/x/web-interface/coin/add" or "/x/v3/fav/resource/deal" => Fail ? "{\"code\":-400,\"message\":\"fixture failure\"}" : "{\"code\":0}",
                "/x/v3/fav/folder/created/list-all" or "/x/v3/fav/folder/created/list" => "{\"code\":0,\"data\":{\"list\":[{\"id\":55,\"title\":\"星趴收藏\",\"fav_state\":0,\"media_count\":3}]}}",
                _ => "{\"code\":-400,\"message\":\"fixture: no data\"}"
            };
            return Task.FromResult(Response(json));
        }
    }
}
