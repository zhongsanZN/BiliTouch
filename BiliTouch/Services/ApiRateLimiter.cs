using System;
using System.Threading;
using System.Threading.Tasks;

namespace BiliTouch.Services;

/// <summary>
/// 出站请求的**令牌桶** —— 给 B 站接口的调用装一道全局限速闸。
///
/// ★★★ 为什么必须有它（2026-11-08，用户报"刷动态刷一会就提示网络请求过于频繁"）：
///   限流码是 <c>-412 request was banned</c>（见 ApiClient.RiskRateLimitedCode），
///   它是**按 IP 算总请求密度**的，不区分你是哪个页面。而我们的调用密度在几个地方会失控：
///     · 动态页一次进入 ≈ 12 个请求（关注列表 + 动态首页 + 未读 portal/uplist + 直播看板…）；
///     · 未读 portal 带 up_list_more 时会**逐页翻完**整个关注列表；
///     · 动态详情的评论预取在滚动时按批(每批 2 页)连续打；
///     · 而且每个 -412 还会被重试 2 次 ⇒ **越被限流打越多**，正反馈。
///   之前唯一的应对是"命中 -412 就退避重试"，那是**事后**补救；这里是**事前**就不超速。
///
/// ★ 为什么是令牌桶而不是"固定间隔排队"：
///   固定间隔会让一次普通的页面加载（十几个请求）硬生生拖成好几秒，用户能感觉到卡。
///   令牌桶允许**短时突发**（进页面那一下可以连着走完），但把**持续速率**按住 ——
///   正是"限流看的是单位时间密度"这件事需要的形状。
///
/// ★ 桶容量与速率怎么定的（2026-11-08 实测调过一版）：
///   一开始取了 12 / 6 每秒，结果**正常操作也会明显变慢** —— 动态页首屏、连续刷新、
///   评论翻页都会撞上闸口，回归套件里 10 秒的等待窗口直接超时。
///   真正的病根不在"单次突发有多大"，而在几处**失控的循环**（未读 portal 翻完整个
///   关注列表、找新动态时往下翻 4 页、滚动事件每帧都发起翻页）。那些已经各自修掉了，
///   这里只需要当安全带，不需要当刹车片。
///   ⇒ 24 / 12每秒 = 突发够一次页面加载，持续上限 720 次/分钟。
///     正常浏览远低于这个量，而"几千次/分钟"那种失控仍然会被按住。
/// </summary>
internal sealed class ApiRateLimiter
{
    private readonly object _sync = new();
    private readonly double _capacity;
    private readonly double _refillPerMs;
    private double _tokens;
    private long _lastRefillTicks;

    /// <summary>
    /// 被服务端判定超速之后的"冷静期"截止时刻（<see cref="Environment.TickCount64"/>）。
    ///
    /// ★ 为什么需要它（2026-11-08）：光"事后退避重试"是**正反馈** ——
    ///   被 -412 之后每个受影响的请求都会再去敲两次重试，越限流打得越多，越打越多被限流。
    ///   所以收到 -412 时把整个桶关掉一段时间：这段时间里**所有**请求都在闸口排队，
    ///   一只都出不去，逼着客户端真正安静下来。这才是"限流"该有的反应。
    /// </summary>
    private long _penalizedUntilTicks;

    /// <summary>默认: 突发 24 个, 持续 12 个/秒。</summary>
    public ApiRateLimiter(double burst = 24, double perSecond = 12)
    {
        if (burst < 1) burst = 1;
        if (perSecond <= 0) perSecond = 1;
        _capacity = burst;
        _tokens = burst;
        _refillPerMs = perSecond / 1000.0;
        _lastRefillTicks = Environment.TickCount64;
    }

    /// <summary>
    /// 报告"刚刚被服务端限流了"，把桶关掉 <paramref name="cooldown"/> 那么久。
    /// 多次命中取**更晚**的那个截止时刻（不会因为后一次更短而提前解封）。
    /// </summary>
    public void Penalize(TimeSpan cooldown)
    {
        var until = Environment.TickCount64 + (long)cooldown.TotalMilliseconds;
        lock (_sync)
        {
            if (until > _penalizedUntilTicks) _penalizedUntilTicks = until;
            _tokens = 0;
            _lastRefillTicks = Environment.TickCount64;
        }
    }

    /// <summary>当前是否处在冷静期（诊断/测试用）。</summary>
    public bool IsPenalized
    {
        get { lock (_sync) return Environment.TickCount64 < _penalizedUntilTicks; }
    }

    /// <summary>
    /// 取一个令牌，取不到就等到能取到为止。
    ///
    /// ★ 用循环 + <see cref="Task.Delay(int)"/> 而不是 <c>SemaphoreSlim.WaitAsync(timeout)</c>：
    ///   后者要预先算一个等待时长，而桶的填充是连续的 —— 算早了会空转、算晚了会白等。
    ///   循环每次只睡"差的那一点"，且每轮都在锁内重新结算，多个等待者会自然排队。
    /// ★ 单次睡眠上限 500ms：真有几十个请求在排队时，也要能及时响应取消。
    /// </summary>
    public async Task WaitAsync(CancellationToken token = default)
    {
        while (true)
        {
            long waitMs;
            lock (_sync)
            {
                var now = Environment.TickCount64;

                // 冷静期内一律不出站：这是"被限流之后真的停下来"，见 _penalizedUntilTicks。
                if (now < _penalizedUntilTicks)
                {
                    waitMs = _penalizedUntilTicks - now;
                }
                else
                {
                    _tokens = Math.Min(_capacity, _tokens + (now - _lastRefillTicks) * _refillPerMs);
                    _lastRefillTicks = now;
                    if (_tokens >= 1)
                    {
                        _tokens -= 1;
                        return;
                    }
                    waitMs = (long)Math.Ceiling((1 - _tokens) / _refillPerMs);
                }
            }

            token.ThrowIfCancellationRequested();
            await Task.Delay((int)Math.Clamp(waitMs, 1, 500), token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 当前可用令牌数（诊断/测试用）。
    /// ★ 冷静期内恒为 0：那时候一个请求都不许出站，报真实桶量会让人误以为"还能打几个"。
    /// </summary>
    public double AvailableTokens
    {
        get
        {
            lock (_sync)
            {
                var now = Environment.TickCount64;
                if (now < _penalizedUntilTicks) return 0;
                _tokens = Math.Min(_capacity, _tokens + (now - _lastRefillTicks) * _refillPerMs);
                _lastRefillTicks = now;
                return _tokens;
            }
        }
    }
}
