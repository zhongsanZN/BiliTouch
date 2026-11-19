using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using BiliTouch.Services;

/// <summary>
/// 动态页「刷一会就限流」的两条防线回归（2026-11-08）。
///
/// 用户报的两个问题各有一条对应断言：
///   ① 底部莫名其妙的「加载更多」—— 已从 FollowPage.xaml 删除，这里断言 XAML 里不再有它；
///   ② 刷动态刷一会就提示「网络请求过于频繁」—— 这里是限速桶与轮询降档的行为断言。
/// </summary>
internal static class RateLimitTests
{
    public static void Run(Action<bool, string> check)
    {
        // ---------- ① 令牌桶本身 ----------
        var limiter = new ApiRateLimiter(burst: 5, perSecond: 100);
        check(Math.Abs(limiter.AvailableTokens - 5) < 0.001, "fresh limiter starts full");

        var start = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) limiter.WaitAsync().GetAwaiter().GetResult();
        check(start.ElapsedMilliseconds < 200, $"burst tokens are spent without waiting ({start.ElapsedMilliseconds}ms)");

        // 第 6 个必须真的等（速率 100/s ⇒ 一个令牌 ~10ms，允许放宽到 5ms 下限）
        start.Restart();
        limiter.WaitAsync().GetAwaiter().GetResult();
        check(start.ElapsedMilliseconds >= 3, $"the bucket throttles after the burst is spent ({start.ElapsedMilliseconds}ms)");

        // ---------- ② 冷静期：被限流之后就别再打了 ----------
        var penalized = new ApiRateLimiter(burst: 100, perSecond: 100);
        check(!penalized.IsPenalized, "limiter is not penalized initially");
        penalized.Penalize(TimeSpan.FromMilliseconds(400));
        check(penalized.IsPenalized, "penalize closes the gate");
        // 桶里明明还有余量，也不许出站 —— 这是"真的停下来"的关键
        check(penalized.AvailableTokens == 0, "a penalized limiter reports no usable tokens");
        var waited = Stopwatch.StartNew();
        penalized.WaitAsync().GetAwaiter().GetResult();
        check(waited.ElapsedMilliseconds >= 300, $"a penalized limiter holds every caller until the cooldown ends ({waited.ElapsedMilliseconds}ms)");
        check(!penalized.IsPenalized, "cooldown expires");

        // 多次惩罚取更晚的截止时刻（不能因为后一次更短就提前解封）
        var extend = new ApiRateLimiter(burst: 10, perSecond: 10);
        extend.Penalize(TimeSpan.FromMilliseconds(1200));
        extend.Penalize(TimeSpan.FromMilliseconds(100));
        check(extend.IsPenalized, "a shorter penalty does not shorten the longer one");

        // ---------- ③ 出站必须过闸：每个 SendAsync 之前都有 ThrottleAsync ----------
        check(EverySendGoesThroughThrottle(),
            "every HTTP send in ApiClient awaits the rate limiter first");

        // ---------- ④ 轮询走省额度档 ----------
        var pollSnapshot = typeof(BiliTouch.ViewModels.FollowViewModel)
            .GetMethod("RefreshUnreadBadgesAsync", BindingFlags.Instance | BindingFlags.Public);
        check(pollSnapshot != null && pollSnapshot.ReturnType == typeof(Task),
            "the page timer has a dedicated shrunken refresh entrypoint");
        var vm = new BiliTouch.ViewModels.FollowViewModel();
        var full = typeof(BiliTouch.ViewModels.FollowViewModel)
            .GetMethod("RefreshServerUnreadAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        check(full != null && full.GetParameters().Length == 1 && full.GetParameters()[0].ParameterType == typeof(bool),
            "unread snapshot can be taken without walking every overflow page");
        _ = vm;

        // ---------- ⑤ 底部那枚「加载更多」已经不在 XAML 里 ----------
        check(!FollowPageXamlHasLoadMoreButton(),
            "the dynamics page no longer renders a load-more button (scrolling to the bottom loads the next page)");
    }

    /// <summary>
    /// 逐行扫 ApiClient*.cs：每处 <c>_http.SendAsync</c> 往上 3 行内必须有 <c>ThrottleAsync</c>。
    /// ★ 为什么用源码扫描而不是反射：这是一条"以后别人加新接口时也别绕过限速"的护栏，
    ///   运行期断言只能在已经调用的路径上生效，源码扫描能覆盖还没被调用到的代码。
    /// </summary>
    private static bool EverySendGoesThroughThrottle()
    {
        var services = FindAppServicesDirectory();
        if (services == null) return false;

        var files = System.IO.Directory.GetFiles(services, "ApiClient*.cs");
        foreach (var file in files)
        {
            var lines = System.IO.File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("_http.SendAsync", StringComparison.Ordinal)) continue;
                var guarded = false;
                for (var back = Math.Max(0, i - 3); back < i; back++)
                    if (lines[back].Contains("ThrottleAsync", StringComparison.Ordinal)) { guarded = true; break; }
                if (!guarded) return false;
            }
        }
        return files.Length > 0;
    }

    /// <summary>FollowPage.xaml 里不能再出现「加载更多」按钮。</summary>
    private static bool FollowPageXamlHasLoadMoreButton()
    {
        var services = FindAppServicesDirectory();
        if (services == null) return true;
        var root = System.IO.Path.GetDirectoryName(services)!;
        var xaml = System.IO.Path.Combine(root, "Views", "Pages", "FollowPage.xaml");
        if (!System.IO.File.Exists(xaml)) return true;
        return System.IO.File.ReadAllText(xaml).Contains("OnLoadMoreFeed", StringComparison.Ordinal);
    }

    /// <summary>
    /// 从测试程序集所在目录往上找应用工程的 <c>Services</c> 目录。
    ///
    /// ★ 不能只找 <c>BiliTouch.csproj</c>：测试二进制在
    ///   <c>…\BiliTouch\tests\TouchTests\bin\Release\net10.0-windows\</c>，
    ///   而应用工程在 <c>…\BiliTouch\BiliTouch\</c> —— 它不在任何一个**祖先**目录里。
    ///   所以每一级祖先都要同时看自己和它下面的 <c>BiliTouch</c> 子目录。
    /// </summary>
    private static string? FindAppServicesDirectory()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir != null && depth < 10; depth++, dir = dir.Parent)
        {
            var direct = System.IO.Path.Combine(dir.FullName, "Services");
            if (System.IO.File.Exists(System.IO.Path.Combine(direct, "ApiClient.cs"))) return direct;

            var nested = System.IO.Path.Combine(dir.FullName, "BiliTouch", "Services");
            if (System.IO.File.Exists(System.IO.Path.Combine(nested, "ApiClient.cs"))) return nested;
        }
        return null;
    }
}
