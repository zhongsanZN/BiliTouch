using System;
using System.Collections.Generic;

namespace BiliTouch.Models;

/// <summary>
/// 「动态」页左栏 UP 主列表的排序模式(2026-11-05)。
///
/// 落盘位置见 <see cref="AppSettings.FollowSortMode"/> / SettingsStore.FollowSortMode(存 int, 读不到就用默认值)。
/// 默认 <see cref="RecentUpdate"/>: 官方 PC 客户端的动态页头部也是"最近更新"优先,
/// 用户点进动态页最想知道的就是"谁刚发了新东西"。
/// </summary>
public enum FollowSortMode
{
    /// <summary>最近更新: 按每个 UP 在**已加载动态流**里最新一条的 PubTs 降序; 没抓到的排后面</summary>
    RecentUpdate = 0,

    /// <summary>未读优先: 有未读红点的 UP 提到最前, 组内仍按最近更新排</summary>
    UnreadFirst = 1,

    /// <summary>关注顺序: 跟随接口返回的原序(点关注的时间先后), 不随动态变化</summary>
    FollowOrder = 2,
}

/// <summary>
/// 左栏 UP 主排序 + 未读红点的**纯逻辑**(2026-11-05)。
///
/// ★ 为什么单独抽一个 static 类而不是全塞进 FollowViewModel:
///   排序与红点这两件事都是"给定数据算结果"的纯函数, 抽出来之后
///   tests/TouchTests/FollowSortTests.cs 可以**离线**断言三种排序与基准推进,
///   不需要登录态、不需要网络、也不用真的开窗口。
///   (FollowViewModel 里剩下的只有"把结果搬到 ObservableCollection"这一层。)
///
/// ★ 为什么排序必须是**全序**(每条比较都有确定结果):
///   List.Sort 用的是不排序稳定(introsort)。只要比较器把两个元素判成"相等",
///   它们的相对位置就由内部实现决定 —— 数据没变、排两次也可能换位置,
///   表现就是左栏在每次刷新时"抖一下"。所以这里的比较器最后一定落到
///   "关注序号 → 当前下标"这级兜底键, 保证同输入同输出。
/// </summary>
public static class FollowSorting
{
    /// <summary>默认排序模式(与 AppSettings.FollowSortMode 的默认值 0 一致)</summary>
    public const FollowSortMode DefaultMode = FollowSortMode.RecentUpdate;

    /// <summary>把落盘读回来的 int 归一到合法模式(越界值一律回落到默认, 不抛异常)</summary>
    public static FollowSortMode Normalize(int raw)
        => Enum.IsDefined(typeof(FollowSortMode), raw) ? (FollowSortMode)raw : DefaultMode;

    /// <summary>枚举值是否合法(手改配置可能塞进 99 / -1)</summary>
    public static bool IsDefined(FollowSortMode mode) => Enum.IsDefined(typeof(FollowSortMode), mode);

    /// <summary>chip 上的短标签(中文, 界面上就这三个词)</summary>
    public static string Label(FollowSortMode mode) => mode switch
    {
        FollowSortMode.UnreadFirst => "未读优先",
        FollowSortMode.FollowOrder => "关注顺序",
        _ => "最近更新",
    };

    /// <summary>chip 的悬浮说明 —— 排序规则要能被用户自己读明白, 不能靠猜</summary>
    public static string Description(FollowSortMode mode) => mode switch
    {
        FollowSortMode.UnreadFirst => "有未读红点的 UP 排最前，组内仍按最近更新时间排",
        FollowSortMode.FollowOrder => "按关注先后排（接口原序），不随动态变化",
        _ => "按每个 UP 最近一条动态的发布时间排，没抓到的排后面",
    };

    // ------------------------------------------------------------ 新鲜度

    /// <summary>
    /// 每个作者在**这批动态**里最新一条的 PubTs(毫秒/秒都无所谓, 只比大小)。
    /// 只收录 PubTs &gt; 0 的条目: 没有时间戳的条目没法参与排序,
    /// 硬塞 0 会把它当成"1970 年发布"排到最后, 那与"这条没有时间信息"是两回事。
    /// </summary>
    public static Dictionary<long, long> LatestTimestamps(IEnumerable<DynamicItem> items)
    {
        var map = new Dictionary<long, long>();
        if (items == null) return map;
        foreach (var it in items)
        {
            if (it == null || it.AuthorMid <= 0 || it.PubTs <= 0) continue;
            if (!map.TryGetValue(it.AuthorMid, out var ts) || it.PubTs > ts) map[it.AuthorMid] = it.PubTs;
        }
        return map;
    }

    /// <summary>
    /// 每个作者最新的一条动态(**按 PubTs 取最大**, 不是"先遇到的那条")。
    ///
    /// ★ 这就是 ClearUnreadBadge 那个隐患的修法: 动态流是时间倒序拼的, 通常"第一条匹配"
    ///   就是最新; 但单 UP 视图里 Dynamics 可能混进别的作者的条目(合作投稿/转发),
    ///   一旦流里出现乱序, "取第一条"会把基准推到一条**较旧**的动态上 ——
    ///   于是下次刷新算出 1 个未读, 红点明明点过了却又亮回来。
    ///   PubTs 相同(同一秒发两条)时保留先遇到的那条, 结果仍然确定。
    /// </summary>
    public static DynamicItem? NewestFor(IEnumerable<DynamicItem> items, long mid)
    {
        DynamicItem? best = null;
        if (items == null || mid <= 0) return null;
        foreach (var it in items)
        {
            if (it == null || it.AuthorMid != mid || string.IsNullOrEmpty(it.Id)) continue;
            if (best == null || it.PubTs > best.PubTs) best = it;
        }
        return best;
    }

    /// <summary>同上, 但一次算完整个流(字典: mid → 最新一条)</summary>
    public static Dictionary<long, DynamicItem> NewestByAuthor(IEnumerable<DynamicItem> items)
    {
        var map = new Dictionary<long, DynamicItem>();
        if (items == null) return map;
        foreach (var it in items)
        {
            if (it == null || it.AuthorMid <= 0 || string.IsNullOrEmpty(it.Id)) continue;
            if (!map.TryGetValue(it.AuthorMid, out var cur) || it.PubTs > cur.PubTs) map[it.AuthorMid] = it;
        }
        return map;
    }

    // ------------------------------------------------------------ 排序

    /// <summary>
    /// 算出左栏应该呈现的顺序(返回新列表, **不改动**传进来的集合)。
    ///
    /// 三种模式的规则:
    ///   · <see cref="FollowSortMode.RecentUpdate"/>: 有 PubTs 的按降序在前; 没抓到的
    ///     (不在 <paramref name="latestTs"/> 里)统一排后面, 组内按关注序号 —— 也就是
    ///     "原来的相对顺序", 不会因为一次刷新就互相换位。
    ///   · <see cref="FollowSortMode.UnreadFirst"/>: 先按有没有未读分组(有红点的在前),
    ///     再套用最近更新的规则。
    ///   · <see cref="FollowSortMode.FollowOrder"/>: 只看关注序号, 完全不看动态。
    ///
    /// ★ 参数 <paramref name="followOrder"/> 是 mid → 关注序号(接口原序)。
    ///   它由 FollowViewModel 在第一次见到该 UP 时分配、之后永不改变 —— 这就是
    ///   "关注顺序"在多次刷新、翻页之后仍然稳定的依据。
    /// </summary>
    public static List<FollowUser> Arrange(
        IReadOnlyList<FollowUser> users,
        IReadOnlyDictionary<long, long> latestTs,
        IReadOnlyDictionary<long, int> followOrder,
        FollowSortMode mode)
    {
        var list = new List<FollowUser>();
        if (users == null) return list;
        foreach (var u in users) if (u != null) list.Add(u);
        if (list.Count < 2) return list;

        // 当前下标: 比较器的**最后一级**兜底键, 保证"关注序号也相同"时仍然确定。
        // FollowUser 没有重写 Equals/GetHashCode, 这个字典天然按引用比较;
        // 用 ReferenceEqualityComparer 显式写死, 免得将来谁给它加了值语义就悄悄改掉排序。
        var position = new Dictionary<FollowUser, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < list.Count; i++)
            if (!position.ContainsKey(list[i])) position[list[i]] = i;

        int Order(FollowUser u) => followOrder != null && followOrder.TryGetValue(u.Mid, out var o) ? o : int.MaxValue;
        int Pos(FollowUser u) => position.TryGetValue(u, out var p) ? p : int.MaxValue;

        int Compare(FollowUser a, FollowUser b)
        {
            if (mode == FollowSortMode.UnreadFirst)
            {
                var au = a.NewCount > 0;
                var bu = b.NewCount > 0;
                if (au != bu) return au ? -1 : 1;
            }

            if (mode != FollowSortMode.FollowOrder)
            {
                // ★ ta/tb 必须**先给初值**: `latestTs != null && latestTs.TryGetValue(..., out var ta)`
                //   里 ta 只在后半段才被赋值, 编译器不做"hasA 为真 ⇒ ta 已赋值"的推理(CS0165)。
                var ta = 0L;
                var tb = 0L;
                var hasA = latestTs != null && latestTs.TryGetValue(a.Mid, out ta) && ta > 0;
                var hasB = latestTs != null && latestTs.TryGetValue(b.Mid, out tb) && tb > 0;
                if (hasA && hasB)
                {
                    if (ta != tb) return tb.CompareTo(ta);   // 新的在前
                }
                else if (hasA != hasB)
                {
                    return hasA ? -1 : 1;                    // 抓到的在前, 没抓到的排后面
                }
            }

            var oa = Order(a);
            var ob = Order(b);
            if (oa != ob) return oa.CompareTo(ob);           // 关注顺序兜底(稳定性的关键)

            var pa = Pos(a);
            var pb = Pos(b);
            return pa.CompareTo(pb);
        }

        list.Sort(Compare);
        return list;
    }

    // ------------------------------------------------------------ 未读红点

    /// <summary>
    /// 用**一页动态**刷新左栏各 UP 的未读红点(规则见 <see cref="BiliTouch.Services.DynamicSeenStore"/> 的类注释)。
    ///
    /// 语义边界(三条都是刻意的, 改之前先想清楚):
    ///   · **本页没出现的 UP 完全不动** —— 动态接口只给近期条目, 一个 UP 最近没投稿就无从判断,
    ///     这时把红点清掉等于凭空断言"没更新"。宁可留着旧红点, 也不给错的信息;
    ///   · **没有基准的 UP 只建基准、不亮红点** —— 否则第一次打开动态页会满屏红点,
    ///     那不是"有更新", 那只是"以前没记过";
    ///   · **基准 == 该 UP 最新一条** → 红点归零。基准不同 → NewCount = 本页里排在
    ///     基准那条之前的该 UP 条目数(基准已翻出本页时, 本页该 UP 的条目全算新的)。
    ///
    /// ★ 读/写基准走委托而不是直接调 DynamicSeenStore:
    ///   这样测试可以塞一个内存字典, 不碰磁盘、不受上一次测试残留的影响。
    ///   生产调用方传的就是 DynamicSeenStore.TryGet / Set(见 FollowViewModel.UpdateUnreadBadges)。
    /// ★ skipMid: 单 UP 视图里"正在看的那个 UP"要跳过 —— 用户正看着它却亮着红点很怪,
    ///   而且它的已读基准由 ClearUnreadBadge(点开时)单独推。
    /// </summary>
    public static void ApplyBadges(
        IReadOnlyList<FollowUser> users,
        IReadOnlyList<DynamicItem> items,
        Func<long, string?> readSeen,
        Action<long, string> writeSeen,
        long skipMid = 0)
    {
        if (users == null || items == null || items.Count == 0) return;
        if (readSeen == null || writeSeen == null) return;

        var newest = NewestByAuthor(items);
        if (newest.Count == 0) return;

        foreach (var u in users)
        {
            if (u == null || u.Mid <= 0) continue;
            if (skipMid > 0 && u.Mid == skipMid) continue;

            // 本页没有这个 UP → 不动(见上面的语义边界)
            if (!newest.TryGetValue(u.Mid, out var top)) continue;

            var seen = readSeen(u.Mid);
            if (string.IsNullOrEmpty(seen))
            {
                writeSeen(u.Mid, top.Id);
                u.NewCount = 0;
                continue;
            }
            if (seen == top.Id)
            {
                u.NewCount = 0;
                continue;
            }

            var n = 0;
            foreach (var it in items)
            {
                if (it == null || it.AuthorMid != u.Mid || string.IsNullOrEmpty(it.Id)) continue;
                if (it.Id == seen) break;   // 追到上次看过的那条为止
                n++;
            }
            u.NewCount = n;
        }
    }
}
