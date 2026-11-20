using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Views;
using BiliTouch.Views.Controls;

/// <summary>
/// 评论区两项核心修复回归测试（--comment-fixes，已并入 --ci）：
/// 1) 评论区动图（GIF 评论配图、动态表情 gif_url、内联图片画廊展开、缩略图/表情逐帧播放、离线像素校验与隐藏自动停帧）；
/// 2) 评论区退出具体评论详情后不再回到最上层（并排布局、网页布局、详情页内二次点回复不覆盖锚点、详情页内切换排序重定位）。
/// </summary>
internal static class CommentFixesTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<bool, string> check)
    {
        TestGifCompositorAndDisposal(check);
        TestEmoteGifParsing(check);
        TestRealWindowCommentGifAndDetailReturn(check);
    }

    private static void TestGifCompositorAndDisposal(Action<bool, string> check)
    {
        var gifBytes = BuildThreeFrameTestGif();
        check(AnimatedImageDecoder.IsGifHeader(gifBytes) && AnimatedImageDecoder.IsPotentiallyAnimated(gifBytes),
            "GIF89a header is detected as potentially animated");

        var anim = AnimatedImageDecoder.TryDecodeBytes(gifBytes, 0);
        check(anim != null && anim.IsAnimated && anim.Frames.Length == 3,
            $"multi-frame GIF decodes all 3 frames (actual={anim?.Frames.Length ?? 0})");
        if (anim == null) return;

        check(anim.PixelWidth == 32 && anim.PixelHeight == 32,
            $"GIF logical screen size is preserved ({anim.PixelWidth}x{anim.PixelHeight})");
        check(anim.DelaysMs[0] == 50 && anim.DelaysMs[1] == 50 && anim.DelaysMs[2] == 50,
            $"GIF per-frame delays are parsed accurately ({string.Join(",", anim.DelaysMs)}ms)");

        // Frame 0: 全图红色 (255,0,0,255)
        var f0Center = ReadBgra(anim.Frames[0], 16, 16);
        check(f0Center.R > 240 && f0Center.G < 10 && f0Center.B < 10 && f0Center.A == 255,
            $"GIF frame 0 center is solid red (rgba={f0Center.R},{f0Center.G},{f0Center.B},{f0Center.A})");

        // Frame 1: (8,8,16,16) 为绿色，角落 (2,2) 因 Disposal=1 仍保持红色
        var f1Center = ReadBgra(anim.Frames[1], 16, 16);
        var f1Corner = ReadBgra(anim.Frames[1], 2, 2);
        check(f1Center.G > 240 && f1Center.R < 10 && f1Center.A == 255 && f1Corner.R > 240 && f1Corner.A == 255,
            "GIF frame 1 composites green sub-rect onto red canvas without wiping outside pixels");

        // Frame 2: Frame 1 的 Disposal=2 清空 (8,8,16,16) 为透明；(0,0,8,8) 绘蓝；(28,28) 仍红
        var f2Cleared = ReadBgra(anim.Frames[2], 16, 16);
        var f2Blue = ReadBgra(anim.Frames[2], 2, 2);
        var f2Outer = ReadBgra(anim.Frames[2], 28, 28);
        check(f2Cleared.A == 0 && f2Blue.B > 240 && f2Blue.A == 255 && f2Outer.R > 240 && f2Outer.A == 255,
            $"GIF frame 2 respects Disposal=2 transparency clear and composites blue sub-rect (clearedA={f2Cleared.A}, blueB={f2Blue.B}, outerR={f2Outer.R})");

        // 下采样 + 缓存命中验证
        Directory.CreateDirectory(AppPaths.CacheDir);
        var url = "https://example.invalid/comment-anim-downsample-test.gif";
        File.WriteAllBytes(Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(url) + ".jpg"), gifBytes);
        var media = CoverLoader.LoadMediaAsync(url, 16).GetAwaiter().GetResult();
        check(media.IsAnimated && media.Animated != null && media.Animated.Frames.Length == 3,
            "CoverLoader.LoadMediaAsync decodes cached GIF as AnimatedBitmap");
        check(CoverLoader.TryGetCachedMedia(url, 16, out var cached) && cached.IsAnimated && ReferenceEquals(cached.Animated, media.Animated),
            "CoverLoader.TryGetCachedMedia returns the cached AnimatedBitmap synchronously");
    }

    private static void TestEmoteGifParsing(Action<bool, string> check)
    {
        // 1) ParseEmotePanelJson 保留 gif_url 至 CommentEmote 字典
        var panelJson = """
        {
          "packages": [
            {
              "id": 1001,
              "text": "动态测试包",
              "type": 3,
              "emote": [
                {
                  "id": 2001,
                  "text": "[动图打call]",
                  "url": "http://i0.hdslb.com/bfs/emote/static.png",
                  "gif_url": "http://i0.hdslb.com/bfs/emote/anim.gif",
                  "meta": { "size": 2 }
                }
              ]
            }
          ]
        }
        """;
        using (var doc = JsonDocument.Parse(panelJson))
        {
            var (_, lookup) = ApiClient.ParseEmotePanelJson(doc.RootElement);
            check(lookup.TryGetValue("[动图打call]", out var em)
                  && em.GifUrl == "https://i0.hdslb.com/bfs/emote/anim.gif"
                  && em.PreferredUrl == "https://i0.hdslb.com/bfs/emote/anim.gif"
                  && em.Url == "https://i0.hdslb.com/bfs/emote/static.png",
                "ParseEmotePanelJson retains gif_url and prefers animated URL in CommentEmote");
        }

        // 2) ParseComment 读取 content.emote 中的 gif_url，且 url 缺失时可回落 gif_url
        var commentJson = """
        {
          "rpid": 99001,
          "mid": 12345,
          "like": 7,
          "ctime": 1700000000,
          "member": { "uname": "动图测试员", "avatar": "" },
          "content": {
            "message": "看这个[动图A]还有[仅动图B]",
            "emote": {
              "[动图A]": {
                "url": "http://i0.hdslb.com/bfs/emote/a.png",
                "gif_url": "http://i0.hdslb.com/bfs/emote/a.gif",
                "meta": { "size": 2 }
              },
              "[仅动图B]": {
                "url": "",
                "gif_url": "http://i0.hdslb.com/bfs/emote/b.gif",
                "meta": { "size": 1 }
              }
            }
          }
        }
        """;
        var parseMethod = typeof(ApiClient).GetMethod("ParseComment", BindingFlags.Static | BindingFlags.NonPublic);
        check(parseMethod != null, "ApiClient.ParseComment is accessible");
        if (parseMethod != null)
        {
            using var doc = JsonDocument.Parse(commentJson);
            var parsed = (CommentItem)parseMethod.Invoke(null, new object[] { doc.RootElement, false, 0L })!;
            check(parsed.Emotes.TryGetValue("[动图A]", out var a)
                  && a.GifUrl == "https://i0.hdslb.com/bfs/emote/a.gif"
                  && a.PreferredUrl == "https://i0.hdslb.com/bfs/emote/a.gif",
                "ParseComment parses gif_url for animated comment emotes");
            check(parsed.Emotes.TryGetValue("[仅动图B]", out var b)
                  && b.Url == "https://i0.hdslb.com/bfs/emote/b.gif"
                  && b.PreferredUrl == "https://i0.hdslb.com/bfs/emote/b.gif",
                "ParseComment falls back to gif_url when static url is empty");
        }
    }

    private static void TestRealWindowCommentGifAndDetailReturn(Action<bool, string> check)
    {
        var oldWeb = Svc.Settings.UseWebPlayerLayout;
        var bindingErrors = new List<string>();
        var listener = new BindingErrorCapture(bindingErrors);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);

        PlayerWindow? player = null;
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDir);
            var gifBytes = BuildThreeFrameTestGif();
            var picUrl = "https://example.invalid/comment-live-pic.gif";
            var emoteUrl = "https://example.invalid/comment-live-emote.gif";
            File.WriteAllBytes(Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(picUrl) + ".jpg"), gifBytes);
            File.WriteAllBytes(Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(emoteUrl) + ".jpg"), gifBytes);

            Svc.Settings.SetWebPlayerLayout(false);
            player = new PlayerWindow { Width = 1280, Height = 820, ShowInTaskbar = false };
            player.Show();
            Pump(220);

            var commentList = (ItemsControl)player.FindName("CommentList");
            var introPanel = (StackPanel)player.FindName("IntroPanel");
            var commentsPanel = (StackPanel)player.FindName("CommentsPanel");
            var infoScroll = (ScrollViewer)player.FindName("InfoScroll");

            // 构造 80 条评论，第 0 条带 GIF 配图与 GIF 表情，每条带楼中楼回复
            var comments = new ObservableCollection<CommentItem>();
            for (int i = 0; i < 80; i++)
            {
                var item = new CommentItem
                {
                    Rpid = 10000 + i,
                    UserName = $"评论用户 {i}",
                    Content = i == 0
                        ? "首条带动态表情[动图表情]和动态配图"
                        : $"第 {i} 条评论正文，用于验证退出评论详情后不会回到评论区最上层。",
                    LikeCount = 100 - i,
                    Ctime = 1700000000 + i * 60,
                    ReplyCount = 3
                };
                if (i == 0)
                {
                    item.Emotes["[动图表情]"] = new CommentEmote { Url = emoteUrl, GifUrl = emoteUrl, Size = 2 };
                    item.Pictures.Add(new CommentPicture { Url = picUrl, Width = 160, Height = 160 });
                }
                for (int r = 0; r < 3; r++)
                {
                    item.Replies.Add(new CommentItem
                    {
                        Rpid = (10000 + i) * 100 + r,
                        ParentRpid = item.Rpid,
                        UserName = $"回复者 {r}",
                        Content = $"给第 {i} 楼的回复 #{r}",
                        Ctime = 1700000000 + r * 30
                    });
                }
                item.SyncPreviewReplies();
                comments.Add(item);
            }

            commentList.ItemsSource = comments;
            introPanel.Visibility = Visibility.Collapsed;
            commentsPanel.Visibility = Visibility.Visible;
            Pump(320);

            // --- 1. 评论区 GIF 配图缩略图 + 动态表情实际切帧与非空像素验证 ---
            var picBorder = FindDescendants<Border>(commentsPanel)
                .FirstOrDefault(b => ReferenceEquals(b.DataContext, comments[0].Pictures[0]));
            WaitUntil(() => picBorder?.Background is ImageBrush, 1500);
            check(picBorder != null && picBorder.Background is ImageBrush,
                "GIF comment picture thumbnail renders with an ImageBrush");

            if (picBorder?.Background is ImageBrush thumbBrush)
            {
                var seenThumb = ObserveDistinctFrames(() => thumbBrush.ImageSource, targetCount: 3, timeoutMs: 800);
                check(seenThumb >= 2,
                    $"GIF comment picture thumbnail advances across distinct frames (seen={seenThumb})");

                // 离屏 RenderTargetBitmap 非空像素探针
                var nonEmpty = HasNonEmptyPixels(picBorder);
                check(nonEmpty, "GIF comment picture thumbnail produces non-empty rendered pixels");

                // 点开内联评论图片画廊，验证大图也能逐帧播放
                InvokePrivate(player, "OpenCommentPicture", picBorder, comments[0].Pictures[0]);
                Pump(80);
                var gallery = (InlineImageGallery?)typeof(PlayerWindow).GetField("_commentGallery", Private)!.GetValue(player);
                var galleryImg = gallery != null ? FindDescendants<Image>(gallery).FirstOrDefault() : null;
                WaitUntil(() => galleryImg?.Source != null, 1500);
                var seenGallery = ObserveDistinctFrames(() => galleryImg?.Source, targetCount: 3, timeoutMs: 800);
                check(seenGallery >= 2,
                    $"expanded InlineImageGallery plays GIF frames over time (seen={seenGallery})");
                InvokePrivate(player, "CloseCommentPicture");
                Pump(80);
            }

            // 验证内联动态表情 Image 逐帧播放
            var commentText = FindDescendants<CommentText>(commentsPanel)
                .FirstOrDefault(ct => ReferenceEquals(ct.Comment, comments[0]));
            var emoteImg = commentText != null ? FindInlineImages(commentText).FirstOrDefault() : null;
            WaitUntil(() => emoteImg?.Source != null, 1500);
            check(emoteImg != null && emoteImg.Source != null, "animated comment emote Image is rendered inline");
            if (emoteImg != null)
            {
                var seenEmote = ObserveDistinctFrames(() => emoteImg.Source, targetCount: 3, timeoutMs: 800);
                check(seenEmote >= 2,
                    $"animated comment emote advances across distinct frames (seen={seenEmote})");
            }

            // --- 2. 评论区退出具体评论后不回到最上层（并排布局） ---
            infoScroll.ScrollToVerticalOffset(3400);
            Pump(180);
            var targetSplit = PickVisibleComment(commentList, infoScroll, comments, minIndex: 5);
            check(targetSplit != null && infoScroll.VerticalOffset > 1500,
                $"scrolled deep into split comments before opening detail (offset={infoScroll.VerticalOffset:F0}, targetRpid={targetSplit?.Rpid})");

            if (targetSplit != null)
            {
                var beforeSplitOffset = infoScroll.VerticalOffset;
                var beforeSplitTop = ViewportTopOf(commentList, targetSplit, infoScroll);
                InvokePrivate(player, "ShowCommentDetail", targetSplit, targetSplit);
                Pump(180);
                var detailPanel = (Grid)player.FindName("CommentDetailPanel");
                check(detailPanel.Visibility == Visibility.Visible && infoScroll.VerticalOffset < beforeSplitOffset - 400,
                    $"entering comment detail shows detail panel near top (detailOffset={infoScroll.VerticalOffset:F0}, saved={beforeSplitOffset:F0})");

                // 模拟在详情页内对楼中楼再次点击“回复”触发第二次 ShowCommentDetail，不应覆盖原滚动锚点
                InvokePrivate(player, "ShowCommentDetail", targetSplit, targetSplit);
                Pump(80);

                InvokePrivate(player, "CloseCommentDetail");
                Pump(180);

                var afterSplitOffset = infoScroll.VerticalOffset;
                var restoredContainer = commentList.ItemContainerGenerator.ContainerFromItem(targetSplit) as FrameworkElement;
                var afterSplitTop = ViewportTopOf(commentList, targetSplit, infoScroll);
                check(detailPanel.Visibility == Visibility.Collapsed
                      && afterSplitOffset > 1500
                      && Math.Abs(afterSplitTop - Math.Max(0, beforeSplitTop)) < 48,
                    $"exiting comment detail in split layout restores target comment position instead of jumping to top (offset {beforeSplitOffset:F0}->{afterSplitOffset:F0}, top {beforeSplitTop:F0}->{afterSplitTop:F0})");
                check(restoredContainer != null && IsInViewport(restoredContainer, infoScroll),
                    "exiting comment detail in split layout realizes and keeps the original comment inside the viewport");

                // --- 3. 详情页内切换排序导致条目换位后退出详情，仍应滚到该评论 ---
                InvokePrivate(player, "ShowCommentDetail", targetSplit, targetSplit);
                Pump(120);
                var reversed = new ObservableCollection<CommentItem>(comments.Reverse());
                commentList.ItemsSource = reversed;
                typeof(PlayerWindow).GetField("_effectiveCommentSort", Private)!.SetValue(player, 2);
                InvokePrivate(player, "CloseCommentDetail");
                Pump(200);
                var resortedContainer = commentList.ItemContainerGenerator.ContainerFromItem(targetSplit) as FrameworkElement;
                check(resortedContainer != null && IsInViewport(resortedContainer, infoScroll),
                    "exiting comment detail after sort change still scrolls to and realizes the target comment");

                // 还原顺序
                commentList.ItemsSource = comments;
                typeof(PlayerWindow).GetField("_effectiveCommentSort", Private)!.SetValue(player, 3);
                Pump(120);
            }

            // --- 4. 网页布局下退出具体评论也不回到最上层 ---
            Svc.Settings.SetWebPlayerLayout(true);
            Pump(360);
            var webScroll = (ScrollViewer)player.FindName("WebLeftScroll");
            webScroll.ScrollToVerticalOffset(4200);
            Pump(220);
            var targetWeb = PickVisibleComment(commentList, webScroll, comments, minIndex: 5);
            check(targetWeb != null && webScroll.VerticalOffset > 1500,
                $"scrolled deep into web-layout comments before opening detail (offset={webScroll.VerticalOffset:F0}, targetRpid={targetWeb?.Rpid})");

            if (targetWeb != null)
            {
                var beforeWebOffset = webScroll.VerticalOffset;
                var beforeWebTop = ViewportTopOf(commentList, targetWeb, webScroll);
                InvokePrivate(player, "ShowCommentDetail", targetWeb, targetWeb);
                Pump(180);
                InvokePrivate(player, "CloseCommentDetail");
                Pump(220);
                var afterWebOffset = webScroll.VerticalOffset;
                var restoredWeb = commentList.ItemContainerGenerator.ContainerFromItem(targetWeb) as FrameworkElement;
                var afterWebTop = ViewportTopOf(commentList, targetWeb, webScroll);
                check(afterWebOffset > 1500 && Math.Abs(afterWebTop - Math.Max(0, beforeWebTop)) < 48,
                    $"exiting comment detail in web layout restores target comment position (offset {beforeWebOffset:F0}->{afterWebOffset:F0}, top {beforeWebTop:F0}->{afterWebTop:F0})");
                check(restoredWeb != null && IsInViewport(restoredWeb, webScroll),
                    "exiting comment detail in web layout realizes and keeps the original comment inside the viewport");
            }

            // --- 5. 隐藏评论面板后动图调度器应自动清零（无后台定时器空转） ---
            commentsPanel.Visibility = Visibility.Collapsed;
            Pump(100);
            check(AnimatedImagePlayer.ActiveCount == 0,
                $"hiding CommentsPanel pauses all comment GIF animations (active={AnimatedImagePlayer.ActiveCount})");

            // --- 6. 高密弹幕性能优化：对象池复用、BitmapCache 纹理缓存、防追尾判定与描边零矢量细分开销 ---
            Svc.Settings.SetWebPlayerLayout(false);
            Pump(120);
            var dmCanvas = (Canvas)player.FindName("DanmakuCanvas");
            InvokePrivate(player, "ResetLanes");
            InvokePrivate(player, "CalibrateDanmakuClock", 20.0);
            InvokePrivate(player, "SetDanmakuClockRunning", false);

            InvokePrivate(player, "AddDanmaku", "对象池首波弹幕", 1, 20.0, 0xFFFFFFu);
            check(dmCanvas.Children.Count == 1, "first danmaku wave spawns 1 element on DanmakuCanvas");
            var firstBorder = (Border)dmCanvas.Children[0];
            check(firstBorder.CacheMode is BitmapCache || !Svc.Settings.HardwareAccelerationEnabled,
                "danmaku Border attaches shared BitmapCache for single-pass GPU texture translation");

            // ResetLanes 将在飞弹幕回收进对象池；再次发射时应直接复用同一个 Border 实例
            InvokePrivate(player, "ResetLanes");
            check(dmCanvas.Children.Count == 0, "ResetLanes recycles active danmaku elements off canvas");
            InvokePrivate(player, "AddDanmaku", "对象池次波复用", 1, 21.0, 0x00FFAAu);
            check(dmCanvas.Children.Count == 1 && ReferenceEquals(dmCanvas.Children[0], firstBorder),
                "subsequent danmaku reuses pooled Border and child visual without allocating a new container");

            // 防追尾判定：先在全泳道放短弹幕，0.35s 后短弹幕刚离开右缘一点点，此时超长快速弹幕因会追尾应被拒绝同道
            InvokePrivate(player, "ResetLanes");
            InvokePrivate(player, "EnsureLanes");
            var laneArr = (double[]?)typeof(PlayerWindow).GetField("_laneFreeAt", Private)!.GetValue(player);
            int totalLanes = laneArr?.Length ?? 1;
            InvokePrivate(player, "CalibrateDanmakuClock", 30.0);
            InvokePrivate(player, "SetDanmakuClockRunning", false);
            for (int l = 0; l < totalLanes; l++)
                InvokePrivate(player, "AddDanmaku", "短", 1, 30.0, 0xFFFFFFu);
            var countAfterShort = dmCanvas.Children.Count;
            // 推进 0.45 秒：短弹幕右尾刚离开右侧约 30px，但超长弹幕速度远快于短弹幕，途中必追尾，故不应被放入
            InvokePrivate(player, "CalibrateDanmakuClock", 30.45);
            InvokePrivate(player, "SetDanmakuClockRunning", false);
            InvokePrivate(player, "AddDanmaku", "这是一条非常长的快速弹幕如果放进同一条泳道中途一定会追尾前面的短弹幕", 1, 30.45, 0xFFFFFFu);
            check(dmCanvas.Children.Count == countAfterShort,
                "anti-rear-end lane allocation rejects faster long danmaku that would overtake a slow short danmaku");

            // OutlinedDanmakuText 原地复用与非空像素渲染验证
            var outlined = new OutlinedDanmakuText("描边性能测试", Brushes.White, 24);
            outlined.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            outlined.Arrange(new Rect(0, 0, outlined.DesiredSize.Width, outlined.DesiredSize.Height));
            var initialW = outlined.DesiredSize.Width;
            check(initialW > 30 && HasNonEmptyPixels(outlined),
                "OutlinedDanmakuText renders non-empty outlined glyph pixels without PathGeometry stroke tessellation");
            outlined.SetContent("更长的描边复用文本验证", Brushes.Yellow, 24);
            outlined.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            check(outlined.DesiredSize.Width > initialW,
                "OutlinedDanmakuText.SetContent updates text and re-measures in place");

            // LiveOverlayRenderer 去除 DropShadowEffect + 对象池复用验证
            var liveCanvas = new Canvas { Width = 800, Height = 450 };
            liveCanvas.Measure(new Size(800, 450));
            liveCanvas.Arrange(new Rect(0, 0, 800, 450));
            using (var liveRenderer = new LiveOverlayRenderer(liveCanvas))
            {
                liveRenderer.Configure(20, 0.35, 0.85, 2, outline: false);
                liveRenderer.Add("直播无阴影特效弹幕", 0xFFFFFF);
                check(liveCanvas.Children.Count == 1, "LiveOverlayRenderer emits live danmaku onto canvas");
                var liveElem = (FrameworkElement)liveCanvas.Children[0];
                check(liveElem.Effect == null, "LiveOverlayRenderer no longer attaches per-element DropShadowEffect");
                liveRenderer.Clear();
                liveRenderer.Add("直播对象池复用弹幕", 0xFF6699);
                check(liveCanvas.Children.Count == 1 && ReferenceEquals(liveCanvas.Children[0], liveElem),
                    "LiveOverlayRenderer reuses pooled text element across waves");
            }

            check(bindingErrors.Count == 0,
                $"comment GIF & detail return flow produces zero WPF data-binding errors (count={bindingErrors.Count})");

            player?.Close();
            player = null;
            Pump(200);
            TestLiveRoomHighDensityDanmaku(check, bindingErrors);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            player?.Close();
            Svc.Settings.SetWebPlayerLayout(oldWeb);
            Pump(80);
        }
    }

    private static CommentItem? PickVisibleComment(
        ItemsControl commentList, ScrollViewer scroll, IList<CommentItem> items, int minIndex)
    {
        for (int i = minIndex; i < items.Count; i++)
        {
            if (commentList.ItemContainerGenerator.ContainerFromItem(items[i]) is FrameworkElement fe
                && IsInViewport(fe, scroll)
                && ViewportTopOf(commentList, items[i], scroll) >= 0)
            {
                return items[i];
            }
        }
        return null;
    }

    private static double ViewportTopOf(ItemsControl commentList, CommentItem item, ScrollViewer scroll)
    {
        if (commentList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement fe
            || !fe.IsLoaded || !fe.IsVisible)
            return double.NaN;
        return fe.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
    }

    private static bool IsInViewport(FrameworkElement fe, ScrollViewer scroll)
    {
        if (!fe.IsLoaded || !fe.IsVisible || scroll.ViewportHeight <= 0) return false;
        var bounds = fe.TransformToAncestor(scroll)
            .TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
        return bounds.Bottom > 0 && bounds.Top < scroll.ViewportHeight;
    }

    private static bool HasNonEmptyPixels(FrameworkElement element)
    {
        int w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        int h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var buf = new byte[w * h * 4];
        rtb.CopyPixels(buf, w * 4, 0);
        for (int i = 3; i < buf.Length; i += 4)
        {
            if (buf[i] > 0 && (buf[i - 1] > 0 || buf[i - 2] > 0 || buf[i - 3] > 0))
                return true;
        }
        return false;
    }

    private static IEnumerable<Image> FindInlineImages(CommentText text)
    {
        foreach (var block in text.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            foreach (var inline in p.Inlines)
            {
                if (inline is InlineUIContainer { Child: Image img })
                    yield return img;
            }
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child))
                yield return nested;
        }
    }

    private static void InvokePrivate(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethods(Private)
            .First(m => m.Name == methodName && m.GetParameters().Length == args.Length);
        method.Invoke(target, args);
    }

    private static (byte R, byte G, byte B, byte A) ReadBgra(BitmapSource src, int x, int y)
    {
        var px = new byte[4];
        src.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0], px[3]);
    }

    private static void WaitUntil(Func<bool> predicate, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate() && sw.ElapsedMilliseconds < timeoutMs)
            Pump(30);
    }

    private static int ObserveDistinctFrames(Func<ImageSource?> readSource, int targetCount, int timeoutMs)
    {
        var seen = new List<ImageSource>();
        var sw = Stopwatch.StartNew();
        while (seen.Count < targetCount && sw.ElapsedMilliseconds < timeoutMs)
        {
            var cur = readSource();
            if (cur != null && !seen.Any(s => ReferenceEquals(s, cur)))
                seen.Add(cur);
            if (seen.Count < targetCount) Pump(30);
        }
        return seen.Count;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 构造标准 3 帧 32x32 GIF89a：
    /// Frame 0: (0,0,32,32) 纯红，delay=50ms，Disposal=1(保留)
    /// Frame 1: (8,8,16,16) 纯绿，delay=50ms，Disposal=2(恢复透明背景)
    /// Frame 2: (0,0,8,8)   纯蓝，delay=50ms，Disposal=1(保留)
    /// </summary>
    private static byte[] BuildThreeFrameTestGif()
    {
        using var ms = new MemoryStream();
        // Header: GIF89a
        ms.Write("GIF89a"u8);
        // Logical Screen Descriptor: 32x32, GCT flag=1, color res=1, size=1 (4 colors)
        WriteU16(ms, 32);
        WriteU16(ms, 32);
        ms.WriteByte(0x91);
        ms.WriteByte(0x00); // bg index
        ms.WriteByte(0x00); // aspect
        // Global Color Table (4 entries: 0=transparent, 1=red, 2=green, 3=blue)
        ms.Write(new byte[]
        {
            0, 0, 0,
            255, 0, 0,
            0, 255, 0,
            0, 0, 255
        });
        // NETSCAPE2.0 infinite loop extension
        ms.Write(new byte[] { 0x21, 0xFF, 0x0B });
        ms.Write("NETSCAPE2.0"u8);
        ms.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });

        WriteGifFrame(ms, 0, 0, 32, 32, colorIndex: 1, delayCs: 5, disposal: 1);
        WriteGifFrame(ms, 8, 8, 16, 16, colorIndex: 2, delayCs: 5, disposal: 2);
        WriteGifFrame(ms, 0, 0, 8, 8, colorIndex: 3, delayCs: 5, disposal: 1);

        ms.WriteByte(0x3B); // Trailer
        return ms.ToArray();
    }

    private static void WriteGifFrame(Stream s, int left, int top, int width, int height, int colorIndex, int delayCs, int disposal)
    {
        // Graphic Control Extension
        s.WriteByte(0x21);
        s.WriteByte(0xF9);
        s.WriteByte(0x04);
        s.WriteByte((byte)((disposal << 2) | 0x01)); // Transparent color flag = 1, index = 0
        WriteU16(s, delayCs);
        s.WriteByte(0x00); // transparent index
        s.WriteByte(0x00); // terminator

        // Image Descriptor
        s.WriteByte(0x2C);
        WriteU16(s, left);
        WriteU16(s, top);
        WriteU16(s, width);
        WriteU16(s, height);
        s.WriteByte(0x00); // no local color table

        // LZW Image Data (minCodeSize = 2 -> initial code width = 3 bits, clear=4, eoi=5)
        // 每输出 2 个像素即发一个 Clear 码，保证码表永不超过 3-bit 上限，编码最简且 100% 符合规范
        s.WriteByte(0x02);
        var packed = new List<byte>();
        int bitBuf = 0;
        int bitCount = 0;
        void WriteCode(int code)
        {
            bitBuf |= (code & 0x07) << bitCount;
            bitCount += 3;
            while (bitCount >= 8)
            {
                packed.Add((byte)(bitBuf & 0xFF));
                bitBuf >>= 8;
                bitCount -= 8;
            }
        }

        WriteCode(4); // Clear
        int totalPixels = width * height;
        for (int p = 0; p < totalPixels; p++)
        {
            WriteCode(colorIndex);
            if ((p & 1) == 1 && p + 1 < totalPixels)
                WriteCode(4); // Reset dictionary before code size would grow to 4 bits
        }
        WriteCode(5); // EOI
        if (bitCount > 0) packed.Add((byte)(bitBuf & 0xFF));

        int offset = 0;
        while (offset < packed.Count)
        {
            int len = Math.Min(255, packed.Count - offset);
            s.WriteByte((byte)len);
            for (int k = 0; k < len; k++) s.WriteByte(packed[offset + k]);
            offset += len;
        }
        s.WriteByte(0x00); // Block terminator
    }

    private static void WriteU16(Stream s, int value)
    {
        s.WriteByte((byte)(value & 0xFF));
        s.WriteByte((byte)((value >> 8) & 0xFF));
    }

    private static void TestLiveRoomHighDensityDanmaku(Action<bool, string> check, List<string> bindingErrors)
    {
        bindingErrors.Clear();
        var live = new LiveRoomWindow { Width = 1360, Height = 840, WindowState = WindowState.Maximized };
        try
        {
            live.Show();
            Pump(250);
            check(bindingErrors.Count == 0,
                $"LiveRoomWindow produces zero WPF data-binding errors (count={bindingErrors.Count})");

            var vmemMode = (bool)typeof(LiveRoomWindow).GetField("_vmemMode", Private)!.GetValue(live)!;
            check(vmemMode, "LiveRoomWindow mounts VmemVideoView in main hardware-accelerated window without layered ForegroundWindow");

            var chatList = (ListBox)live.FindName("ChatList");
            var liveCanvas = (Canvas)live.FindName("LiveCanvas");
            var overlay = (LiveOverlayRenderer)typeof(LiveRoomWindow).GetField("_overlay", Private)!.GetValue(live)!;

            // 1. 满 500 条上限时连续 10 批 40 条弹幕的侧栏刷新 + 布局耗时验证（修复前 ~25-48ms/批）
            live.Model.OverlayEnabled = false;
            for (int i = 0; i < 500; i++)
            {
                live.Model.Enqueue(new LiveChatEntry
                {
                    User = "观众_" + i,
                    Text = "这是一条直播间的测试弹幕内容，用来验证侧边栏高密度刷新性能 " + i,
                    Medal = "粉丝牌 20",
                    Badge = "舰长",
                    Color = 0x00FFCC
                });
            }
            for (int i = 0; i < 15; i++) live.Model.Flush();
            Pump(150);

            var rows = FindDescendants<LiveChatRow>(chatList).ToList();
            check(rows.Count > 0 && HasNonEmptyPixels(rows[0]),
                $"LiveChatRow renders non-empty chat pixels directly (realizedRows={rows.Count})");

            var beforeRows = FindDescendants<LiveChatRow>(chatList).ToHashSet();
            var sw = new Stopwatch();
            double totalFlushMs = 0, totalLayoutMs = 0;
            for (int b = 0; b < 10; b++)
            {
                for (int j = 0; j < 40; j++)
                {
                    live.Model.Enqueue(new LiveChatEntry
                    {
                        User = $"突发观众_{b}_{j}",
                        Text = $"突发高密度弹幕刷屏测试内容，测试满 500 条时的批量刷新与轻量直绘性能 {b}_{j}",
                        Medal = "真爱粉 30",
                        Badge = "房管",
                        Color = 0xFF6699
                    });
                }
                sw.Restart();
                live.Model.Flush();
                totalFlushMs += sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                chatList.UpdateLayout();
                totalLayoutMs += sw.Elapsed.TotalMilliseconds;
            }
            var afterRows = FindDescendants<LiveChatRow>(chatList).ToList();
            var reusedRowCount = afterRows.Count(r => beforeRows.Contains(r));
            var avgBatchMs = (totalFlushMs + totalLayoutMs) / 10.0;
            check(reusedRowCount > 0 && reusedRowCount == afterRows.Count,
                $"LiveRoom ChatList reuses existing LiveChatRow containers in-place across 400 steady-state messages ({reusedRowCount}/{afterRows.Count})");
            check(avgBatchMs < 20.0,
                $"LiveRoom ChatList 40-item flush+layout at 500 cap stays fast ({avgBatchMs:F2}ms/batch < 20ms)");

            // 2. 真实视频播放 + 画面弹幕 + 侧栏持续洪峰下的渲染帧率验证（修复前仅 ~6-8 FPS / 204ms 跳帧）
            var mp = (LibVLCSharp.Shared.MediaPlayer)typeof(LiveRoomWindow).GetField("_player", Private)!.GetValue(live)!;
            var core = (LibVLCSharp.Shared.LibVLC)typeof(LiveRoomWindow).GetField("_liveCore", Private)!.GetValue(live)!;
            mp.Volume = 0;
            var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/touch-sample-silent.mp4"));
            if (File.Exists(samplePath))
            {
                using var media = new LibVLCSharp.Shared.Media(core, samplePath, LibVLCSharp.Shared.FromType.FromPath);
                media.AddOption(":input-repeat=65535");
                mp.Play(media);
                WaitUntil(() => mp.IsPlaying, 5000);
            }

            live.Model.OverlayEnabled = true;
            overlay.Configure(24, 1.0, 0.85, 2, outline: true);
            for (int k = 0; k < 24; k++)
                overlay.Add($"直播画面滚动弹幕流畅度测试_{k}", (uint)(0x808080 + (k * 11111 % 0x7F7F7F)));

            var deltas = new List<double>();
            long prev = 0;
            EventHandler onRender = (_, _) =>
            {
                long now = Stopwatch.GetTimestamp();
                if (prev != 0) deltas.Add(Stopwatch.GetElapsedTime(prev, now).TotalMilliseconds);
                prev = now;
            };
            int floodSeq = 0;
            var floodTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
            floodTimer.Tick += (_, _) =>
            {
                for (int k = 0; k < 12; k++)
                {
                    floodSeq++;
                    live.Model.Enqueue(new LiveChatEntry
                    {
                        User = "洪峰观众_" + floodSeq,
                        Text = "高并发直播间持续刷屏弹幕内容 " + floodSeq,
                        Medal = "真爱粉 20",
                        Color = 0xFFFFFF
                    });
                }
            };
            CompositionTarget.Rendering += onRender;
            floodTimer.Start();
            Pump(1200);
            floodTimer.Stop();
            CompositionTarget.Rendering -= onRender;
            deltas.Sort();
            double p50 = deltas.Count > 0 ? deltas[deltas.Count / 2] : 999;
            double p95 = deltas.Count > 0 ? deltas[(int)(deltas.Count * 0.95)] : 999;
            double fps = deltas.Count / 1.2;
            Console.WriteLine($"[LIVE-AFTER-RENDER] Video+Danmaku+ChatFlood 1.2s: frames={deltas.Count} ({fps:F1} fps), p50={p50:F2}ms, p95={p95:F2}ms, canvasChildren={liveCanvas.Children.Count}");
            check(fps >= 25.0 && p50 < 40.0,
                $"LiveRoom stays smooth under video + outlined danmaku + chat flood ({fps:F1} fps, p50={p50:F2}ms, p95={p95:F2}ms)");

            // 3. LiveDanmakuService 噪音包零分配预筛验证（2000 个非弹幕大包跳过 JsonDocument.Parse）
            using var svc = new LiveDanmakuService(Svc.Api, Dispatcher.CurrentDispatcher);
            var noiseBytes = System.Text.Encoding.UTF8.GetBytes("{\"cmd\":\"STOP_LIVE_ROOM_LIST\",\"data\":{\"room_id_list\":[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20]}}");
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart();
            for (int i = 0; i < 2000; i++) svc.HandlePacket(5, noiseBytes);
            sw.Stop();
            var noiseAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Console.WriteLine($"[LIVE-AFTER-NOISE] 2000 noise packets filtered in {sw.Elapsed.TotalMilliseconds:F2}ms, alloc={noiseAlloc} bytes");
            check(noiseAlloc < 4096 && sw.Elapsed.TotalMilliseconds < 50,
                $"LiveDanmakuService pre-filters 2,000 noise packets with near-zero allocation ({noiseAlloc} B, {sw.Elapsed.TotalMilliseconds:F2}ms)");
        }
        finally
        {
            live.Close();
            Pump(500);
        }
    }

    private sealed class BindingErrorCapture : TraceListener
    {
        private readonly List<string> _errors;
        public BindingErrorCapture(List<string> errors) => _errors = errors;
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _errors.Add(message);
        }
    }
}
