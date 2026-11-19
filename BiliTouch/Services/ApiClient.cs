using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliTouch.Helpers;
using BiliTouch.Models;

namespace BiliTouch.Services;

/// <summary>
/// 哔哩哔哩 API 客户端: 带浏览器 UA、Cookie、WBI 签名与风控降级逻辑
/// </summary>
public partial class ApiClient
{
    /// <summary>
    /// 请求头里的 UA。
    ///
    /// **不要**在结尾加上 "Edg/126.0.0.0" 这类浏览器品牌后缀 —— 实测(2026-09-24, 冷启动单请求)
    /// 同一个评论接口: 纯 Chrome UA 返回 20 条, 带 Edg 后缀直接 `-352`(风控)。
    /// 这类后缀会被 B 站的风控当成"非真实浏览器会话", 表现是接口时好时坏、返回被降级的短列表。
    /// 字符串本体收敛在 HttpDefaults 一份, 全项目所有出站请求共用同一份。
    /// </summary>
    private const string Ua = HttpDefaults.UserAgent;

    // bilibili 官方 JS 中的 mixinKey 置换表
    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52
    };

    private readonly HttpClient _http;

    /// <summary>
    /// 全局出站限速闸（见 <see cref="ApiRateLimiter"/>）。
    ///
    /// ★★★ 为什么它是**进程级单例**（2026-11-08）：
    ///   B 站的 -412 是**按 IP 算总请求密度**的，不区分来源页面/实例。所以限速必须跨
    ///   所有 ApiClient 实例共享 —— 每个实例一个桶的话，多开几个页面又各自满速，
    ///   等于没限。这里用 static 就是为了"全进程只有一个桶"。
    /// </summary>
    private static readonly ApiRateLimiter RateLimiter = new();

    /// <summary>
    /// 每一次真正出站之前都要 await 它（见 <see cref="ApiRateLimiter"/> 里为什么需要）。
    /// 单独抽成方法是为了让调用点长得一样，方便日后统一改。
    /// </summary>
    private static Task ThrottleAsync(CancellationToken token = default) => RateLimiter.WaitAsync(token);

    /// <summary>
    /// 全站 API 唯一出站发包口：所有 HTTP 请求在结构上必经限速闸，并支持在排队后二次核验账号快照；
    /// 即使等待限速期间切换/退出账号，也会在真正发出前拦截，物理上杜绝绕过限速与跨账号串写。
    /// </summary>
    private async Task<HttpResponseMessage?> SendThrottledAsync(
        HttpRequestMessage request,
        CancellationToken ct = default,
        SessionAccount? expectedAccount = null)
    {
        await ThrottleAsync(ct).ConfigureAwait(false);
        if (expectedAccount is { IsCurrent: false }) return null;
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>当前可用令牌数（诊断/测试用）</summary>
    internal static double RateTokensAvailable => RateLimiter.AvailableTokens;

    private (string img, string sub)? _wbiKeys;
    private readonly SemaphoreSlim _wbiGate = new(1, 1);
    private readonly SemaphoreSlim _writeReadyGate = new(1, 1);
    private string? _buvid3;
    private string? _buvid4;

    /// <summary>
    /// 设备指纹领取的串行闸(借鉴上游 6a6e650): 进直播间并行拉流与连弹幕等并发路径不会双发 spi 互相覆盖。
    /// </summary>
    private readonly SemaphoreSlim _buvidGate = new(1, 1);

    internal const string DynamicFeatures = "itemOpusStyle,listOnlyfans,opusBigCover,onlyfansVote,forwardListHidden,decorationCard";

    private readonly object _emoteCacheGate = new();
    private List<EmotePackage>? _cachedEmotePackages;
    private Dictionary<string, CommentEmote> _cachedEmoteLookup = new(StringComparer.Ordinal);
    private string? _emoteCacheAccountKey;

    /// <summary>上一次 GetJsonAsync 解析 JSON 失败时的原始响应体(见 -2 分支)。</summary>
    private string? _lastNonJsonBody;

    public ApiClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Ua);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
    }

    // ---------------------------------------------------------------- 基础请求

    private async Task<(int code, string? msg, JsonElement? data)> GetJsonAsync(
        string url, Dictionary<string, string>? ps, bool sign = false, bool allowRetry = true,
        string? referer = null, bool fingerprint = true, string payloadKey = "data",
        int riskRetries = RiskRetryMax, SessionAccount? requestAccount = null)
    {
        var account = requestAccount ?? SessionManager.Instance.CaptureAccount();
        if (!account.IsCurrent) return (-101, "账号已切换，请重试", null);
        ps = ps != null ? new Dictionary<string, string>(ps) : new Dictionary<string, string>();
        if (sign)
        {
            var mixin = await GetMixinKeyAsync();
            if (!account.IsCurrent) return (-101, "账号已切换，请重试", null);
            if (mixin == null) return (-400, "WBI 密钥获取失败", null);
            SignParams(ps, mixin);
        }

        var fullUrl = url + "?" + string.Join("&",
            ps.Select(kv => $"{kv.Key}={Enc(kv.Value)}"));

        // 在 try 外声明: JSON 解析失败时 catch 里要靠它判断是不是 HTML 风控页
        string text = "";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            // fingerprint=false 时**故意不带**设备指纹 Cookie(见 GetCommentsAsync 的说明)
            if (fingerprint)
            {
                var cookie = AppendFingerprint(SessionManager.Instance.BuildCookieHeader());
                if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
            // Referer 逐请求设置: 部分接口(如空间)校验 Referer 域名
            req.Headers.TryAddWithoutValidation("Referer", referer ?? "https://www.bilibili.com/");

            using var resp = await SendThrottledAsync(req, expectedAccount: account);
            if (resp == null) return (-101, "账号已切换，请重试", null);
            text = await resp.Content.ReadAsStringAsync();
            if (!account.IsCurrent) return (-101, "账号已切换，请重试", null);
            int code;
            string? msg;
            JsonElement? data;
            using (var doc = JsonDocument.Parse(text))
            {
                var root = doc.RootElement;
                code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -999;
                msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;
                // Clone: 脱离 JsonDocument 生命周期, 否则返回的是悬空引用
                //
                // ★ payloadKey: 绝大多数接口把载荷放在根级 "data" 下, 但 **PGC 那套不是** ——
                //   pgc/view/web/season 直接把内容放在根级 "result" 下, 压根没有 "data"。
                //   对它照常取 "data" 会拿到 null, 而接口明明返回 code=0 / message=success,
                //   表现就是"明明调通了却说什么都没拿到", 极难往接口本身头上想。
                //   调用方按接口的形状传 "result" 即可(见 GetBangumiDetailAsync)。
                data = root.TryGetProperty(payloadKey, out var d) &&
                       d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
            }

            // WBI 签名过期(-403)时刷新密钥重试一次
            if (sign && allowRetry && code == -403)
            {
                _wbiKeys = null;
                // ★ fingerprint 必须原样透传(以前漏了, 会退回默认 true):
                //   故意不带指纹的调用方只有一个 —— 未登录评论(GetCommentsAsync, 匿名读评论
                //   不该留下设备痕迹)。它一旦撞上 -403, 重试就会突然**带上**指纹, 把"匿名访问"
                //   变成"带设备指纹访问", 正好与该调用点的用意相反。参数原样传下去才不会走样。
                return await GetJsonAsync(url, ps, sign: true, allowRetry: false,
                    referer: referer, fingerprint: fingerprint, payloadKey: payloadKey,
                    requestAccount: account);
            }

            // ★★★ -412「request was banned」= **IP 级限流**, 等一下重试即可(2026-10-17 修)
            //
            //   实测结论(探针 bd-probe-412 / cookie / cookie2 / conn, 四轮对照互相印证):
            //   ① 同一个 URL, 上一秒 code=0、这一秒 -412, **完全不带 cookie 也一样**;
            //   ② 换一组全新 buvid 仍被拦, 而同一时刻另一组又能过;
            //   ③ 完全相同的请求连打 12 次, **code=0 与 -412 交替出现**;
            //   ④ 等 45~180 秒后完全恢复(10 次连打全 code=0)。
            //   ⇒ 它既不是"免登录 1080P"那组参数的问题(playurl 根本还没走到,
            //     未登录取**详情**就已经被拦), 也不是本地指纹被标记 —— **是 IP 被限流**。
            //   ⇒ 唯一有效的处理是**退避后重试**; 换指纹只会白白多打一次请求。
            //
            //   重试策略: **退避重试两次**(1.5s → 3s)。实测(探针 bd-probe-conn)这个限流是
            //   "隔一阵就随机过、隔一阵又随机不过": 同一 IP、同一个 URL、完全不带 cookie,
            //   连打 12 次里出现过 code=0 与 -412 **交替**。⇒ 一次退避不一定够,
            //   而多试几次也不会把请求堆上去(总共只多 2 次, 且都带等待)。
            //
            //   ★★★ 2026-11-08 补一环: 命中 -412 时**顺手把全局限速桶关一段时间**。
            //     光靠"这一次退避重试"是正反馈 —— 每个被拦的请求都多打两次, 越限流越密。
            //     现在全局桶也一起冷静, 这段时间内所有出站请求都在闸口排队,
            //     客户端真的安静下来, 才可能等到服务端放宽(实测 45~180 秒恢复)。
            if (code == RiskRateLimitedCode)
            {
                // 退避递增: 第 1 次 1.5s，第 2 次 3s（原式 RiskBackoffMs * riskRetries 反向递减），
                // 且即便 riskRetries==0 也通知全局限速桶进入冷静期，避免其他并发请求继续撞 -412。
                var attempt = Math.Max(1, RiskRetryMax - riskRetries + 1);
                var backoffMs = RiskBackoffMs * attempt;
                RateLimiter.Penalize(TimeSpan.FromMilliseconds(backoffMs * 2));
                if (riskRetries > 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Api] {url} 命中 -412(IP 限流), 退避 {backoffMs}ms 后重试");
                    await Task.Delay(backoffMs).ConfigureAwait(false);
                    return await GetJsonAsync(url, ps, sign: sign, allowRetry: allowRetry,
                        referer: referer, fingerprint: fingerprint, payloadKey: payloadKey,
                        riskRetries: riskRetries - 1, requestAccount: account).ConfigureAwait(false);
                }
                return (code, "当前网络请求过于频繁(B站限流), 请稍候再试", data);
            }

            return (code, msg, data);
        }
        catch (TaskCanceledException)
        {
            return (-1, "网络请求超时", null);
        }
        catch (HttpRequestException ex)
        {
            return (-1, "网络请求失败: " + ex.Message, null);
        }
        catch (JsonException)
        {
            // ★ 留下原始 body: 偶尔服务端回的不是 JSON 而是 HTML 风控页(HTTP 200),
            //   调用方需要看它长什么样才能区分"接口坏了"和"被风控了"(见 IsHtmlRiskPage)。
            _lastNonJsonBody = text;
            return (-2, "响应解析失败", null);
        }
    }

    // ---------------------------------------------------------------- WBI 签名

    private async Task<string?> GetMixinKeyAsync()
    {
        var keys = _wbiKeys;
        if (keys == null)
        {
            await RefreshWbiKeysAsync().ConfigureAwait(false);
            keys = _wbiKeys;
        }
        if (keys == null) return null;

        var combined = keys.Value.img + keys.Value.sub;
        var mixin = new StringBuilder(32);
        foreach (var i in MixinKeyEncTab)
        {
            if ((uint)i < (uint)combined.Length) mixin.Append(combined[i]);
        }
        return mixin.Length > 0 ? mixin.ToString() : null;
    }

    private async Task RefreshWbiKeysAsync()
    {
        await _wbiGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_wbiKeys != null) return;
            // 注意: nav 接口未登录时返回 -101, 但 data 里仍带有 wbi 密钥, 不能判 code==0
            var (_, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/nav", null).ConfigureAwait(false);
            if (data == null) return;

            if (data.Value.TryGetProperty("wbi_img", out var wi) &&
                wi.TryGetProperty("img_url", out var iu) &&
                wi.TryGetProperty("sub_url", out var su) &&
                iu.ValueKind == JsonValueKind.String &&
                su.ValueKind == JsonValueKind.String)
            {
                var img = NameOnly(iu.GetString()!);
                var sub = NameOnly(su.GetString()!);
                if (img.Length > 0 && sub.Length > 0) _wbiKeys = (img, sub);
            }
        }
        catch
        {
            // 忽略
        }
        finally
        {
            _wbiGate.Release();
        }
    }

    private static string NameOnly(string url)
    {
        var idx = url.LastIndexOf('/');
        var file = idx >= 0 ? url[(idx + 1)..] : url;
        var dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    private static void SignParams(Dictionary<string, string> ps, string mixin)
    {
        ps.Remove("w_rid");
        ps["wts"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        var query = string.Join("&",
            ps.OrderBy(kv => kv.Key, StringComparer.Ordinal)
              .Select(kv => $"{kv.Key}={Enc(kv.Value)}"));
        ps["w_rid"] = Hashing.Md5Hex(query + mixin);
    }

    /// <summary>与 bilibili 前端一致的参数编码(等价 urllib.urlencode + 过滤!'()*)</summary>
    private static string Enc(string v)
    {
        var s = Uri.EscapeDataString(v)
            .Replace("%21", "!").Replace("%27", "'").Replace("%28", "(")
            .Replace("%29", ")").Replace("%2A", "*");
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '!' or '\'' or '(' or ')' or '*') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 设备指纹

    /// <summary>
    /// 获取设备指纹 Cookie(buvid3/buvid4, 降低风控 -352 触发概率的关键)。
    ///
    /// ★★ 这里有一个**直接影响推荐准确度**的要点: 优先复用已有那一对, 别每次都要新的。
    ///
    /// 为什么: 推荐系统靠 (buvid3 + buvid4) 认"这是同一台设备"。而
    /// `x/frontend/finger/spi` **每次调用都发一对全新的** —— 实测把已有的 buvid3/buvid4
    /// 带上再去调它, 它照样回吐两个新的, 完全不做 round-trip。所以每次启动都换一对,
    /// 等于每次开机都变成一台"从未见过的设备": 推荐模型每次从零冷启动,
    /// 用户看到的就是"推的东西东一榔头西一棒槌"。
    ///
    /// 现在的顺序: 内存 → 本地已存(session.json) → 两边都没有才真去请求 spi,
    /// 并把拿到的那一对**落盘**(见 SessionManager.SetBuvid4)。
    /// 于是正常使用下, 一台设备的身份是稳定的。
    /// </summary>
    public async Task EnsureBuvidAsync()
    {
        // 内存里这一对齐全, 直接用
        if (!string.IsNullOrEmpty(_buvid3) && !string.IsNullOrEmpty(_buvid4)) return;

        await _buvidGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_buvid3) && !string.IsNullOrEmpty(_buvid4)) return;

            // 不全就先把本地存的补进来(buvid3 / buvid4 都可能只缺一个)
            var sess = SessionManager.Instance.Current;
            _buvid3 ??= sess.Buvid3;
            _buvid4 ??= sess.Buvid4;

            // 补齐后完整了 —— 不需要再打网络请求
            if (!string.IsNullOrEmpty(_buvid3) && !string.IsNullOrEmpty(_buvid4)) return;

            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/frontend/finger/spi", null).ConfigureAwait(false);
            if (code == 0 && data != null)
            {
                // ★ 只补**缺的那一半**, 已有的绝不覆盖。
                if (string.IsNullOrEmpty(_buvid3) &&
                    data.Value.TryGetProperty("b_3", out var b3) &&
                    b3.ValueKind == JsonValueKind.String)
                {
                    _buvid3 = b3.GetString();
                    if (!string.IsNullOrEmpty(_buvid3)) SessionManager.Instance.SetBuvid3(_buvid3);
                }
                if (string.IsNullOrEmpty(_buvid4) &&
                    data.Value.TryGetProperty("b_4", out var b4) &&
                    b4.ValueKind == JsonValueKind.String)
                {
                    _buvid4 = b4.GetString();
                    // ★ 必须落盘: 不存的话下次启动又变成一台新设备
                    if (!string.IsNullOrEmpty(_buvid4)) SessionManager.Instance.SetBuvid4(_buvid4);
                }
            }
        }
        catch
        {
            // 忽略
        }
        finally
        {
            _buvidGate.Release();
        }
    }

    /// <summary>在 Cookie 基础上附加设备指纹(buvid3/buvid4)</summary>
    private string AppendFingerprint(string cookie)
    {
        if (!string.IsNullOrEmpty(_buvid3) && !cookie.Contains("buvid3", StringComparison.Ordinal))
            cookie = cookie.Length > 0 ? cookie + "; buvid3=" + _buvid3 : "buvid3=" + _buvid3;
        if (!string.IsNullOrEmpty(_buvid4) && !cookie.Contains("buvid4", StringComparison.Ordinal))
            cookie = cookie.Length > 0 ? cookie + "; buvid4=" + _buvid4 : "buvid4=" + _buvid4;
        return cookie;
    }

    // ---------------------------------------------------------------- 写操作风控预热

    /// <summary>
    /// 从 Referer 推导 Origin。web 端每个 POST 都带它, 且必须与 Referer 同源。
    /// Referer 是页面地址(带路径), Origin 只要 origin 部分。
    /// </summary>
    private static string OriginFor(string? referer)
    {
        if (string.IsNullOrEmpty(referer)) return "https://www.bilibili.com";
        var i = referer.IndexOf("://", StringComparison.Ordinal);
        if (i < 0) return "https://www.bilibili.com";
        var rest = referer[(i + 3)..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? referer : referer[..(i + 3 + slash)];
    }

    /// <summary>写操作预热是否已经做过(每进程一次, 失败不重试以免拖慢用户点击)</summary>
    private bool _writeReady;

    /// <summary>
    /// B 站的 IP 级限流码: <c>-412 request was banned</c>(2026-10-17 实测钉死)
    ///
    /// ★ 它**不是**账号/设备被封: 实测同一 IP 上"这一秒 -412、下一秒 code=0",
    ///   且**完全不带 cookie** 也照样被拦 ⇒ 与 buvid、登录态、参数都无关。
    ///   表现形式是短时间内请求过密, 退避一下就好(见 GetJsonAsync 里那处重试)。
    /// </summary>
    private const int RiskRateLimitedCode = -412;

    /// <summary>命中 -412 后的退避基数(毫秒)。第 n 次重试等待 n × 该值, 形成 1.5s / 3s。</summary>
    private const int RiskBackoffMs = 1500;

    /// <summary>-412 最多重试几次(见 GetJsonAsync 里那处的实测依据)</summary>
    private const int RiskRetryMax = 2;

    /// <summary>
    /// 清掉内存里的设备指纹缓存(下次 <see cref="EnsureBuvidAsync"/> 会重新领并落盘)。
    ///
    /// ★★ 为什么需要它(2026-10-12 16:33 实测, 用户报"退出登录再登进去风控又出现"):
    ///   <see cref="EnsureBuvidAsync"/> 第一句是"内存里这一对齐全就直接返回" ——
    ///   这是为了"一台设备的身份稳定"(见该方法注释)。但退出登录会删掉 session.json,
    ///   而这个单例的 `_buvid3/_buvid4` **不会**跟着清 ⇒ 重新登录后它直接短路,
    ///   既不重新领也不落盘 ⇒ 磁盘上 buvid 是空的, 写接口报风控。
    ///   由 <see cref="SessionManager.Clear"/> 调用。
    /// </summary>
    public void ResetBuvidCache()
    {
        _buvid3 = null;
        _buvid4 = null;
    }

    /// <summary>
    /// 供启动时调用的写操作预热(见 <see cref="EnsureWriteReadyAsync"/>)。
    ///
    /// ★ 为什么要单独暴露: 预热放在"用户第一次点赞"那一刻的话, 那一次点击要多等
    ///   一个网络往返, 体感就是"点了没反应"。启动 1.5 秒后静默做掉, 用户无感。
    /// </summary>
    public Task WarmUpWriteAsync() => EnsureWriteReadyAsync();

    /// <summary>
    /// 当前屏幕分辨率(上报设备指纹用)。取不到就给一个常见的桌面值 ——
    /// 这个字段服务端只校验存在性, 不影响任何判断, 所以失败不需要重试。
    /// </summary>
    private static string ScreenRes()
    {
        try
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
            return bounds is { } b ? $"{b.Width}x{b.Height}" : "1920x1080";
        }
        catch
        {
            return "1920x1080";
        }
    }

    /// <summary>
    /// 写操作(点赞/投币/收藏/三连)发出前的最后一道风控预热。
    ///
    /// ★★ 为什么需要它(2026-10-12 用户报"冷门视频点赞报 -352 风控现在依然存在"):
    ///   B 站的写接口有**三层**设备身份校验, 缺任何一层都可能 -352:
    ///     1) buvid3/buvid4 —— 光"有"不够, 还必须 POST 一次
    ///        `/x/internal/gaia-gateway/ExClimbWuzhi` 才算"激活";
    ///        未激活的指纹服务端视为无效, 读接口照过、写接口判风控。
    ///     2) b_nut / sid / buvid_fp / b_lsid —— 登录会话特征(2026-11-12 已补到 Cookie 头)。
    ///     3) Origin 头 —— web 端 POST 都带, 缺了会被判成非浏览器客户端。
    ///
    /// ★ 实测基线(探针 bd-probe-like2/3/4/5, 全程未改用户点赞状态):
    ///   · 对本人冷门投稿(播放 7762、赞 0)打 like=1 → **成功**(65006/0),
    ///     说明"当前账号 + 当前 Cookie + 当前代码路径"本身是通的;
    ///   · 补 b_nut/b_lsid/buvid_fp/dm_img/Origin 与不补, 返回码完全一样
    ///     ⇒ 这几项在**探针环境**下不是必要条件;
    ///   · 评论点赞 /x/v2/reply/action 同样全放行(不存在的 rpid 也返 0)。
    ///
    /// ★★ 当时据此推断"根因是冷启动未激活指纹"——**那个推断是错的**, 别沿用。
    ///   真根因(2026-10-12 用户对照实验定位): "退出登录 → 重新扫码登录"这条路上
    ///   `EnsureBuvidAsync` 的内存短路导致 buvid 从未落盘(session.json 里是空的),
    ///   见 <see cref="ResetBuvidCache"/> 与 SessionManager.Clear。
    ///   ⇒ 探针查不出问题**不等于**代码没问题: 探针的 ApiClient 是全新实例(内存为空),
    ///     而用户跑的是长驻进程(退出登录不清内存)。**"探针复现不了"本身就是一条线索** ——
    ///     要找"探针与真实进程的差异", 而不是回头怀疑用户或账号。
    ///   预热本身仍有价值(激活未激活的指纹是已知风控面), 保留。
    ///
    /// ★ 实测(2026-10-12, 探针 bd-probe-wuzhi, 只读):
    ///   这个接口返回 `{"code":130212,"message":"130212","data":null}` —— **不是错误**。
    ///   社区里同样如此(bilibili-api issue #691 有人专门问过这个码),
    ///   它是"已收到但不需要响应"的意思, 之后写接口仍可正常放行。
    ///   所以**不要**因为看到非 0 码就以为激活失败而反复重试。
    ///
    /// ★ 每进程只做一次且不阻塞失败: 预热失败就照常发写请求(退回预热前的行为),
    ///   绝不让"防风控"本身变成"不能点赞"。
    /// </summary>
    private async Task EnsureWriteReadyAsync()
    {
        if (_writeReady) return;
        await _writeReadyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writeReady) return;

            // 第一层: 设备指纹必须存在(读接口已经在用, 通常已经是现成的)
            await EnsureBuvidAsync().ConfigureAwait(false);

            // 第二层: 激活 buvid。**光拿到 buvid3/buvid4 不算激活** —— 还必须 POST 一次
            //   `/x/internal/gaia-gateway/ExClimbWuzhi` 把设备指纹上报, 服务端才认这个设备。
            //   未激活的指纹: 读接口照过、写接口判风控(这正是"能看不能赞"的根因之一)。
            //
            // ★ 请求形态按社区实测文档(SocialSisterYi/bilibili-API-collect issue #933):
            //     POST https://api.bilibili.com/x/internal/gaia-gateway/ExClimbWuzhi
            //     Content-Type: application/x-www-form-urlencoded
            //     表单字段 payload = 一串 JSON(里面是 WebGL/分辨率/时间戳等设备特征)
            //     Cookie 需要 buvid3 / buvid4 / b_nut
            //   payload 里的编号字段服务端只校验存在性, 用公开常量即可。
            // ★ 不需要登录态, 失败/无响应都不影响主流程。
            try
            {
                if (string.IsNullOrEmpty(_buvid3)) return;

                var cookie = "buvid3=" + _buvid3;
                if (!string.IsNullOrEmpty(_buvid4)) cookie += "; buvid4=" + _buvid4;
                var bNut = SessionManager.Instance.Current.BNut;
                if (!string.IsNullOrEmpty(bNut)) cookie += "; b_nut=" + bNut;

                // payload: 设备特征。分辨率用真实屏幕, 其余用公开常量(服务端只验存在性)。
                //   ★ 用 WinForms 的 Screen 而不是 WPF 的 SystemParameters: ApiClient 不依赖 WPF,
                //     而这是一个可能在非 UI 线程上跑的 Service(探针/后台任务都直接调它)。
                var res = ScreenRes();
                var payload = "{\"3064\":1,\"5062\":\"" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    + "\",\"03bf\":\"https%3A%2F%2Fwww.bilibili.com%2F\",\"39c8\":\"333.1007.fp.risk\""
                    + ",\"34f1\":\"\",\"d402\":\"\",\"654a\":\"\",\"6e7c\":\"" + res
                    + "\",\"3c43\":{\"2673\":1,\"5766\":24,\"6527\":0,\"7003\":1,\"807e\":1}}";

                using var req = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.bilibili.com/x/internal/gaia-gateway/ExClimbWuzhi");
                req.Headers.TryAddWithoutValidation("Cookie", cookie);
                req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
                req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["payload"] = payload
                });
                using var _ = await SendThrottledAsync(req).ConfigureAwait(false);
            }
            catch
            {
                // 预热失败不阻塞: 见方法注释
            }
            finally
            {
                // 完成一次尝试后再置位: 既保证并发写入等待首次激活完成, 又避免失败时反复重试
                _writeReady = true;
            }
        }
        finally
        {
            _writeReadyGate.Release();
        }
    }


    /// <summary>POST 表单 + 自动加 CSRF / Cookie / Referer / 错误码解析</summary>
    private async Task<(bool ok, string? err)> PostFormAsync(string url, Dictionary<string, string> ps, string? referer = null)
    {
        var (code, msg) = await PostFormCodeAsync(url, ps, referer);
        return code == 0 ? (true, null) : (false, msg);
    }

    /// <summary>
    /// 与 PostFormAsync 相同, 但把业务码原样返回。
    /// 少数接口需要区分具体错误码才能判断"算不算成功" —— 例如收藏的
    /// 11201(已经收藏过了) / 11202(已经取消收藏了)。
    /// </summary>
    private async Task<(int code, string? msg)> PostFormCodeAsync(
        string url, Dictionary<string, string> ps, string? referer = null)
    {
        var account = SessionManager.Instance.CaptureAccount();
        var csrf = SessionManager.Instance.Current.BiliJct;
        if (string.IsNullOrEmpty(csrf)) return (-101, "未登录");
        ps["csrf"] = csrf;
        try
        {
            // ★ 写操作(点赞/投币/收藏/三连)的风控门比读接口严, 实测要点见下面 EnsureWriteReadyAsync。
            //   在真正发写请求前把设备身份预热一次, 不做的话新登录用户第一次点赞极易撞 -352。
            await EnsureWriteReadyAsync();
            if (!account.IsCurrent) return (-101, "账号已切换，操作已停止");

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Cookie",
                AppendFingerprint(SessionManager.Instance.BuildCookieHeader()));
            if (!string.IsNullOrEmpty(referer))
                req.Headers.TryAddWithoutValidation("Referer", referer);
            // ★ Origin 必须与 Referer 同源: B 站 web 端所有 POST 都带这个头, 缺了
            //   读接口照样过, 写接口却会被当成"非浏览器客户端"直接判风控。
            req.Headers.TryAddWithoutValidation("Origin", OriginFor(referer));
            req.Content = new FormUrlEncodedContent(ps);
            using var resp = await SendThrottledAsync(req, expectedAccount: account);
            if (resp == null) return (-101, "账号已切换，操作已停止");
            var text = await resp.Content.ReadAsStringAsync();
            if (!account.IsCurrent) return (-101, "账号已切换，操作已停止");
            using var doc = JsonDocument.Parse(text);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : -999;
            if (code != 0)
            {
                if (code == RiskRateLimitedCode)
                    RateLimiter.Penalize(TimeSpan.FromMilliseconds(RiskBackoffMs * 2));
                var errMsg = "code " + code;
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    errMsg = m.GetString() ?? errMsg;
                return (code, errMsg);
            }
            return (0, null);
        }
        catch (Exception ex) { return (-999, ex.Message); }
    }


    // ---------------------------------------------------------------- 工具

    /// <summary>
    /// 同一档清晰度下, 各编码的优先级(数字越大越优先)。
    ///
    /// hevc 排最前是有实测依据的: 同一档 qn 下 hevc 的码率约为 avc 的一半
    /// (4K 档 7.03 vs 12.60 Mbps), 而 VLC 的硬解对两者一视同仁 —— 同样的画质少搬一半数据,
    /// 起播与跳转的缓冲自然短。
    /// av01 放最后: 体积更小, 但硬解支持面窄(软解 4K AV1 基本跑不动), 不主动选。
    /// </summary>
    private static int CodecRank(string codec)
    {
        if (codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase)) return 3;   // hev1 / hvc1
        if (codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase)) return 2;   // avc1
        if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return 1;  // av01
        return 0;
    }

    /// <summary>
    /// 线路选择已迁到 <see cref="CdnService"/>(2026-10-03)。
    ///
    /// 以前这里是两个静态方法(IsPcdnHost / PickFastestHost): 只按"域名里有没有 mcdn"
    /// 换一下顺序。现在换成 CdnService, 因为它要额外负责:
    ///   · 自动测速挑最快(各家 CDN 实测吞吐能差 4 倍)、手动指定、跟随服务端三种策略;
    ///   · 认出**伪装域名**的 PCDN(实测 `mv0bz14m.edge.mountaintoys.cn` 带 os=mcdn,
    ///     只看域名会漏 —— 旧规则的漏洞);
    ///   · 失败回退与测速缓存。
    /// 逻辑集中在一处, 设置页改设置 -> 下次取流生效, 不需要动这里。
    /// </summary>

    private static string GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) &&
           (v.ValueKind == JsonValueKind.String || v.ValueKind == JsonValueKind.Number)
            ? v.ToString() : "";

    private static int GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : 0;

    /// <summary>
    /// 浮点也能取的 int/long(2026-10-31 修"部分视频评论区整页解析失败")。
    ///
    /// ★ 某些评论字段(实测 like)会被服务端下发成 **小数**(如 12.0):
    ///   GetInt/GetLong 对浮点调 GetInt32()/GetInt64() 会抛
    ///   "One of the identified items was in an invalid format",
    ///   而 ParseComment 外层只有一个大 try —— 一条评论抛异常, **整页评论全部丢掉**。
    ///   数值字段一律走这里, 按"先整后浮再截断"取值。
    /// </summary>
    private static int GetIntSafe(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt32(out var i)) return i;
        return v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
    }

    private static long GetLongSafe(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt64(out var l)) return l;
        return v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
    }

    /// <summary>
    /// 数字**或数字字符串**都能取(动态接口把 mid/rid 当字符串下发: "mid": "1255510247")。
    ///
    /// ★ 为什么必须单独有一个: 动态接口里 `author.mid` 实测是**字符串**,
    ///   走 GetLong 会得 0 —— 表现为"点头像进不了主页""左栏未读角标永远不亮"(都按 mid 匹配)。
    ///   只给确认会用字符串下发的字段用它, 别的字段继续用 GetLong, 免得把"脏字符串当数字"
    ///   这种本该暴露的问题也一并咽下去。
    /// </summary>
    private static long GetLongLoose(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        if (v.ValueKind == JsonValueKind.String &&
            long.TryParse(v.GetString(), out var n)) return n;
        return 0;
    }

}

/// <summary>字符串工具扩展</summary>
public static class StringExtensions
{
    /// <summary>去除搜索标题里的高亮标签</summary>
    public static string StripHtml(this string s)
        => string.IsNullOrEmpty(s)
            ? s
            : s.Replace("<em class=\"keyword\">", "").Replace("</em>", "");
}
