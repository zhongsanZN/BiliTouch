using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Views;
using LibVLCSharp.Shared;

internal static class WebRailTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Part<T>(PlayerWindow player, string name) => (T)player.FindName(name);
    private static T Field<T>(PlayerWindow player, string name) => (T)typeof(PlayerWindow).GetField(name, Hidden)!.GetValue(player)!;
    private static void Set(PlayerWindow player, string name, object value) => typeof(PlayerWindow).GetField(name, Hidden)!.SetValue(player, value);
    private static object? Call(PlayerWindow player, string name, params object[] args) => typeof(PlayerWindow).GetMethod(name, Hidden)!.Invoke(player, args);
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> ready)
    {
        var deadline = Environment.TickCount64 + 8000;
        while (!ready() && Environment.TickCount64 < deadline) Pump(20);
        if (!ready()) throw new TimeoutException("web rail did not settle");
    }
    private static void Wait(Task task)
    {
        Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); Pump(60);
    }
    private static double Top(FrameworkElement part, Visual ancestor) => part.TransformToAncestor(ancestor).Transform(new Point()).Y;
    private static void Save(Window window, string file)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); encoder.Save(stream);
    }
    public static void Run(Action<bool, string> check, string artifacts)
    {
        var oldWeb = Svc.Settings.UseWebPlayerLayout;
        var oldVmem = Svc.Settings.UseVmemRenderer;
        var oldScale = Svc.Settings.UiScalePercent;
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var httpField = typeof(ApiClient).GetField("_http", Hidden)!;
        var oldHttp = httpField.GetValue(Svc.Api);
        var fixture = new RecommendationsFixture(); using var http = new HttpClient(fixture);
        httpField.SetValue(Svc.Api, http);
        var output = Path.Combine(artifacts, "web-rail"); Directory.CreateDirectory(output);
        try
        {
            Svc.Settings.SetUiScalePercent(100);
            foreach (var vmem in new[] { false, true })
            {
                Svc.Settings.SetWebPlayerLayout(true); Svc.Settings.SetVmemRenderer(vmem);
                var mode = vmem ? "vmem" : "native";
                var player = new PlayerWindow { Width = 1320, Height = 860, Topmost = true, ShowActivated = false };
                try
                {
                    player.Show(); Pump(400);
                    player.PlayLocalFile(Path.GetFullPath("artifacts/dock-color.mp4"));
                    Until(() => Field<LibVLCSharp.Shared.MediaPlayer>(player, "_mp").IsPlaying);
                    var stage = Part<Grid>(player, "PlayerStage");
                    var surface = Part<Grid>(player, "VideoSurface");
                    var scroll = Part<ScrollViewer>(player, "WebLeftScroll");
                    var info = Part<ScrollViewer>(player, "InfoScroll");
                    var anchor = Part<Border>(player, "WebDockAnchor");
                    var header = Part<ContentControl>(player, "WebHeaderHost");
                    var intro = Part<StackPanel>(player, "IntroPanel");
                    var related = Part<StackPanel>(player, "RelatedPanel");
                    var recommendations = Part<StackPanel>(player, "WebRecommendationsPanel");
                    var tab = Part<RadioButton>(player, "TabRecommended");
                    var introTab = Part<RadioButton>(player, "TabIntro");
                    var classicTab = Part<RadioButton>(player, "TabComments");
                    var comments = Part<ItemsControl>(player, "CommentList");
                    var commentRows = Enumerable.Range(0, 40).Select(i => new CommentItem { Rpid = i + 1, UserName = "读者", Content = "视频下面的评论保持原有实例与阅读位置。" }).ToArray();
                    comments.ItemsSource = commentRows;
                    Part<TextBlock>(player, "DescText").Text = "简介与推荐在右侧分别查看。";
                    Part<TextBox>(player, "CommentDraft").Text = "保留评论草稿";
                    Pump(150);
                    check(tab.IsVisible && Equals(tab.Content, "推荐") && classicTab.Visibility == Visibility.Collapsed, mode + ": web rail exposes introduction/recommendations tabs");
                    check(ReferenceEquals(related.Parent, recommendations) && !intro.Children.Contains(related), mode + ": recommendations are removed from the introduction");
                    check(Part<StackPanel>(player, "CommentsPanel").IsVisible, mode + ": comments remain below the video");
                    tab.IsChecked = true; Pump(250);
                    check(recommendations.IsVisible && !intro.IsVisible && Part<TextBlock>(player, "WebRecommendationsHint").Text.Contains("本地"), mode + ": recommendation tab has a local-media empty state");
                    Set(player, "_isLocalPlayback", false);
                    fixture.Count = 40; fixture.Fail = false;
                    var requestCount = fixture.Requests;
                    Wait((Task)Call(player, "LoadRelatedAsync", "BVfixtureCurrent")!);
                    check(related.IsVisible && Part<ItemsControl>(player, "RelatedList").Items.Count >= 8, mode + ": recommendations load into their own tab");
                    info.ScrollToBottom(); Pump(200);
                    check(Field<int>(player, "_relatedShown") > 8 && fixture.Requests == requestCount + 1, mode + ": scrolling recommendations appends the cached next batch without refetching");
                    introTab.IsChecked = true; Pump(220); tab.IsChecked = true; Pump(220);
                    check(fixture.Requests == requestCount + 1 && ReferenceEquals(comments.ItemsSource, commentRows) && Part<TextBox>(player, "CommentDraft").Text == "保留评论草稿", mode + ": tab switches retain recommendation cache, comments and draft");

                    // Check arranged geometry at multiple progress points, in both directions.
                    var bodyTop = Top(info, stage); var headerTop = Top(header, stage);
                    var full = anchor.ActualWidth * 9 / 16 + 12;
                    var fixedWidth = surface.Width; var fixedHeight = surface.Height;
                    var costs = new List<double>();
                    Call(player, "PrepareWebDockSpacing");
                    foreach (var p in new[] { 0.0, .25, .5, .75, 1.0, .75, .5, .25, 0.0 })
                    {
                        var watch = Stopwatch.StartNew();
                        Call(player, "UpdateWebDockSpacing", p); player.UpdateLayout(); watch.Stop(); costs.Add(watch.Elapsed.TotalMilliseconds);
                        check(Math.Abs(Top(info, stage) - bodyTop - full * p) < 1 && Math.Abs(Top(header, stage) - headerTop) < .1, mode + $": right body yields continuously at {p:0.##}, header stays fixed");
                    }
                    Call(player, "PrepareWebDockAnchor", 0.0);
                    check(surface.Width == fixedWidth && surface.Height == fixedHeight, mode + ": right-side reflow does not resize the video surface");

                    var live = new List<(double Progress, double Gap)>();
                    EventHandler observe = (_, _) => live.Add((Field<double>(player, "_webDockProgress"), Top(info, stage) - bodyTop));
                    CompositionTarget.Rendering += observe;
                    try
                    {
                        scroll.ScrollToVerticalOffset(Part<Border>(player, "WebVideoPlaceholder").ActualHeight * .55);
                        Pump(70);
                        scroll.ScrollToTop(); // Reverse before settling.
                        Pump(50);
                        scroll.ScrollToVerticalOffset(Part<Border>(player, "WebVideoPlaceholder").ActualHeight + 80);
                        // The scroll request is applied by the next layout pass; vmem tracking can be idle
                        // until then, so wait for the docked endpoint instead of the first idle frame.
                        Pump(40);
                        Until(() => !Field<bool>(player, "_webRendering") && !Field<bool>(player, "_vmemTracking") && Field<double>(player, "_webDockProgress") > .999);
                    }
                    finally { CompositionTarget.Rendering -= observe; }
                    check(live.Any(s => s.Progress > .02 && s.Progress < .98 && s.Gap > 1 && s.Gap < full - 12), mode + ": actual interrupted animation renders intermediate right-side spacing");
                    check(Math.Abs(anchor.Height - anchor.ActualWidth * 9 / 16) < .1 && Top(info, stage) >= Top(anchor, stage) + anchor.ActualHeight - .1, mode + ": settled content stays below the small video");
                    check(Field<LibVLCSharp.Shared.MediaPlayer>(player, "_mp").IsPlaying, mode + ": playback survives the interrupted transition");
                    Save(player, Path.Combine(output, mode + "-recommendations-docked.png"));
                    info.ScrollToTop(); Pump(100);
                    Save(player, Path.Combine(output, mode + "-recommendations-top.png"));
                    File.WriteAllText(Path.Combine(output, mode + "-layout-cost.json"), JsonSerializer.Serialize(new { SamplesMs = costs, MedianMs = costs.Order().ElementAt(costs.Count / 2), Samples = live.Select(s => new { s.Progress, s.Gap }) }, new JsonSerializerOptions { WriteIndented = true }));

                    for (var i = 0; i < 5; i++) Call(player, "ShowMoreRelated");
                    Pump(150); info.ScrollToBottom(); Pump(100);
                    Call(player, "PrepareWebDockSpacing");
                    foreach (var p in new[] { 1.0, .75, .5, .25, 0.0 })
                    {
                        Call(player, "UpdateWebDockSpacing", p); player.UpdateLayout();
                        check(Math.Abs(Top(info, stage) - bodyTop - full * p) < 1, mode + $": a previously docked viewport opens continuously at {p:0.##}");
                    }
                    var last = (FrameworkElement)Part<ItemsControl>(player, "RelatedList").ItemContainerGenerator.ContainerFromIndex(39);
                    var beforeCommit = Top(last, stage);
                    Call(player, "PrepareWebDockAnchor", 0.0); player.UpdateLayout();
                    check(Math.Abs(Top(last, stage) - beforeCommit) < 1 && double.IsNaN(info.Height), mode + ": undocking at the end of recommendations commits without a scroll clamp jump");
                    Set(player, "_webDockProgress", 0.0); Set(player, "_webAnimationTo", 0.0);
                    Call(player, "StopWebVideoAnimation"); Pump(120);
                    var thread = new CommentItem { Rpid = 777, UserName = "读者", Content = "保留打开的评论详情" };
                    Call(player, "ShowCommentDetail", thread, thread); Pump(120);
                    introTab.IsChecked = true; Pump(120); tab.IsChecked = true; Pump(120);
                    check(ReferenceEquals(Part<ContentControl>(player, "CommentDetailCard").Content, thread) && Part<FrameworkElement>(player, "CommentDetailPanel").IsVisible && Part<TextBox>(player, "CommentDraft").Text == "保留评论草稿", mode + ": right-side tabs preserve the left comment thread and draft");

                    Call(player, "ToggleFullscreen"); Pump(250); Call(player, "ToggleFullscreen");
                    Until(() => Field<bool>(player, "_webLayoutMounted") && !Field<bool>(player, "_webRendering")); Pump(300);
                    check(tab.IsChecked == true && ReferenceEquals(related.Parent, recommendations) && recommendations.IsVisible && surface.ActualWidth > 1 && surface.ActualHeight > 1, mode + ": fullscreen restore keeps the recommendation tab and valid video geometry");
                    fixture.Count = 0;
                    Wait((Task)Call(player, "LoadRelatedAsync", "BVfixtureEmpty")!);
                    check(!related.IsVisible && Part<TextBlock>(player, "WebRecommendationsHint").IsVisible && Part<TextBlock>(player, "WebRecommendationsHint").Text == "暂无推荐视频", mode + ": an empty response leaves a visible empty state");
                    fixture.Fail = true;
                    Wait((Task)Call(player, "LoadRelatedAsync", "BVfixtureFailure")!);
                    check(Part<TextBlock>(player, "WebRecommendationsHint").Text.Contains("无法加载"), mode + ": a failed response leaves an explicit error state");
                    fixture.Fail = false;
                    Svc.Settings.SetWebPlayerLayout(false); Pump(350);
                    check(ReferenceEquals(related.Parent, intro) && related.Margin.Top == 16 && tab.Visibility == Visibility.Collapsed && classicTab.IsVisible, mode + ": classic layout restores recommendations under introduction and its original comment tab");
                    check(Part<StackPanel>(player, "CommentsPanel").Parent == Part<Grid>(player, "PageHost"), mode + ": classic layout gets its comment page back");
                    Svc.Settings.SetWebPlayerLayout(true); Pump(350);
                    check(tab.IsChecked == true && recommendations.Children.OfType<StackPanel>().Count(p => ReferenceEquals(p, related)) == 1, mode + ": repeated mounting retains tab choice without duplicating the recommendation tree");
                    Call(player, "ClearRelatedPanel");
                    check(Part<TextBlock>(player, "WebRecommendationsHint").Text == "暂无推荐视频", mode + ": changing media clears the previous recommendation error");
                    var dmCanvas = Part<Canvas>(player, "DanmakuCanvas");
                    var areaRoot = Part<Grid>(player, "VideoAreaRoot");
                    check(dmCanvas.ClipToBounds && areaRoot.ClipToBounds && surface.ClipToBounds, mode + ": danmaku canvas and video containers enforce ClipToBounds so rolling danmaku never overflows into the right rail");
                    introTab.IsChecked = true; Pump(100);
                    var infoPanel = Part<Grid>(player, "InfoPanel");
                    Call(player, "InfoDown", new Point(220, 260), infoPanel);
                    Call(player, "InfoMove", new Point(120, 260));
                    check(intro.Visibility == Visibility.Visible && recommendations.Visibility == Visibility.Visible, mode + ": web layout right rail touch drag displays both 简介 and 推荐 side-by-side in an intermediate state");
                    Call(player, "InfoUp"); Pump(350);
                    check(tab.IsChecked == true, mode + ": web layout right rail touch drag completes switch to 推荐");
                    Call(player, "SwitchInfoPageByWheelForTest", -1); Pump(350);
                    check(introTab.IsChecked == true, mode + ": web layout right rail wheel switches back to 简介");
                    tab.IsChecked = true; Pump(100);
                    Call(player, "PrepareWebDockSpacing");
                    Set(player, "_closing", true);
                    Call(player, "StopWebVideoAnimation");
                    check(!Field<bool>(player, "_webDockSpacingPrepared") && double.IsNaN(info.Height) && info.RenderTransform is TranslateTransform { Y: 0 }, mode + ": closing restores temporary rail geometry without retaining a motion clock");
                    Set(player, "_closing", false);
                }
                finally { player.Close(); Pump(350); }
            }
        }
        finally
        {
            httpField.SetValue(Svc.Api, oldHttp);
            Svc.Settings.SetWebPlayerLayout(oldWeb); Svc.Settings.SetVmemRenderer(oldVmem); Svc.Settings.SetUiScalePercent(oldScale);
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
    private sealed class RecommendationsFixture : HttpMessageHandler
    {
        public int Count = 40, Requests;
        public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;
            if (request.RequestUri!.AbsolutePath.Contains("archive/related"))
            {
                Requests++;
                body = Fail ? "{\"code\":-1,\"message\":\"fixture failure\"}" : JsonSerializer.Serialize(new { code = 0, data = Enumerable.Range(0, Count).Select(i => new { bvid = "BVfixture" + i, aid = i + 1, title = "推荐视频 " + i, duration = 120, pic = "", owner = new { mid = 1, name = "示例作者" }, stat = new { view = 100, danmaku = 10 } }) });
            }
            else body = "{\"code\":0,\"data\":{\"b_3\":\"fixture\",\"b_4\":\"fixture\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
