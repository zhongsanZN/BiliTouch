using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views.Controls;
using BiliTouch.Views.Pages;

/// <summary>
/// 「动态」页左栏排序 + 未读/已读机制的**离线**验证(2026-11-05)。
///
/// ★ 全程不联网、不登录: 只造 <see cref="DynamicItem"/> / <see cref="FollowUser"/> 假数据,
///   直接打 <see cref="FollowSorting"/> 的纯逻辑, 再用一个**内存版**基准字典代替 DynamicSeenStore
///   (所以不会污染 artifacts 里的 dynamic_seen.json, 也不会被上一次测试的残留影响)。
/// ★ 界面部分只做"绑定通了"的静态校验: 找到 chip / 徽标 / 标记按钮, 断言文案是中文、
///   选中态跟着 ViewModel 走。不做截图、不启动真实数据加载。
///
/// 运行: TouchTests.exe --follow-sort
/// </summary>
internal static class FollowSortTests
{
    public static void Run(Action<bool, string> check, string output)
    {
        Directory.CreateDirectory(output);

        // ==================== 1. 三种排序的结果顺序 ====================
        // 五个 UP, 关注顺序(接口原序)是 100,200,300,400,500。
        // 动态流里: 300 最新(ts=1000), 100 次新(900), 200 更早(800);
        //           400 / 500 一条都没抓到(模拟"翻页还没翻到 TA")。
        // 未读: 200 和 500 有红点(200 有新投稿, 500 的红点是上一轮留下的)。
        var users = new List<FollowUser>
        {
            NewUser(100, "UP-A"), NewUser(200, "UP-B"), NewUser(300, "UP-C"),
            NewUser(400, "UP-D"), NewUser(500, "UP-E"),
        };
        users[1].NewCount = 2;   // UP-B: 有未读
        users[4].NewCount = 1;   // UP-E: 有未读(但这一页根本没抓到它的动态)

        var followOrder = new Dictionary<long, int> { [100] = 0, [200] = 1, [300] = 2, [400] = 3, [500] = 4 };
        var latestTs = new Dictionary<long, long> { [300] = 1000, [100] = 900, [200] = 800 };

        // ---- 最近更新(默认) ----
        var recent = FollowSorting.Arrange(users, latestTs, followOrder, FollowSortMode.RecentUpdate);
        check(Ids(recent) == "300,100,200,400,500",
            $"最近更新: 抓到的按 PubTs 降序在前, 没抓到的保持关注顺序排后面 → {Ids(recent)}");
        check(FollowSorting.DefaultMode == FollowSortMode.RecentUpdate,
            "默认排序模式是「最近更新」(与 AppSettings.FollowSortMode 的默认值 0 一致)");
        check(FollowSorting.Normalize(7) == FollowSortMode.RecentUpdate && FollowSorting.Normalize(-3) == FollowSortMode.RecentUpdate,
            "越界的持久化值归一成默认模式(不抛异常)");
        check(FollowSorting.Label(FollowSortMode.RecentUpdate) == "最近更新"
              && FollowSorting.Label(FollowSortMode.UnreadFirst) == "未读优先"
              && FollowSorting.Label(FollowSortMode.FollowOrder) == "关注顺序",
            "三档排序标签都是中文");

        // 抖动防护: 同一份输入排两次必须一模一样 —— 这是"不要每次刷新就乱跳"的机器判据。
        var again = FollowSorting.Arrange(users, latestTs, followOrder, FollowSortMode.RecentUpdate);
        check(Ids(again) == Ids(recent), "同样的输入排两次结果完全一致(不抖动)");
        // 传进来的集合不该被改动: Arrange 必须返回新列表
        check(Ids(users) == "100,200,300,400,500", "排序不改动调用方的原集合");

        // ---- 未读优先 ----
        // 有红点的两个(200 有动态 ts=800 / 500 这一页没抓到)成一整组提到最前,
        // 组内仍按最近更新: 200 有时间戳在前, 500 没有时间戳排组内后;
        // 其余按最近更新: 300(1000) → 100(900) → 400(没抓到)。
        var unreadFirst = FollowSorting.Arrange(users, latestTs, followOrder, FollowSortMode.UnreadFirst);
        check(Ids(unreadFirst) == "200,500,300,100,400",
            $"未读优先: 有红点的先分一组(组内按最近更新) → {Ids(unreadFirst)}");
        check(unreadFirst[0].HasNew && unreadFirst[1].HasNew,
            "未读组永远排在最前面, 即使组内有的 UP 这一页没抓到动态");
        check(unreadFirst[2].Mid == 300,
            "无未读的 UP 跟在后面, 组内仍按 PubTs 降序(300 最新)");

        // ---- 关注顺序 ----
        var followOrderSorted = FollowSorting.Arrange(users, latestTs, followOrder, FollowSortMode.FollowOrder);
        check(Ids(followOrderSorted) == "100,200,300,400,500",
            $"关注顺序: 完全按接口原序, 不看时间也不看未读 → {Ids(followOrderSorted)}");
        // 关注顺序必须能还原"上一次看到的顺序", 即使 latestTs 完全不同
        var otherTs = new Dictionary<long, long> { [500] = 9999, [400] = 9998 };
        var followOrderAgain = FollowSorting.Arrange(users, otherTs, followOrder, FollowSortMode.FollowOrder);
        check(Ids(followOrderAgain) == "100,200,300,400,500",
            "关注顺序与动态时间无关(换一份时间轴顺序不变)");

        // ---- 没抓到时间的 UP 不许乱插队 ----
        var noTs = FollowSorting.Arrange(users, new Dictionary<long, long>(), followOrder, FollowSortMode.RecentUpdate);
        check(Ids(noTs) == "100,200,300,400,500",
            "一条动态都没有时, 最近更新退化成关注顺序(而不是随机顺序)");

        // ---- LatestTimestamps / NewestFor 的基本语义 ----
        var items = new List<DynamicItem>
        {
            Item("d300b", 300, 1000),
            Item("d100a", 100, 900),
            Item("d200a", 200, 800),
            Item("d300old", 300, 500),   // 同一个 UP 更早的一条
            Item("noTs", 300, 0),        // 没有时间戳: 不许覆盖有时间的
        };
        var ts = FollowSorting.LatestTimestamps(items);
        check(ts[300] == 1000 && ts[100] == 900 && ts[200] == 800,
            "LatestTimestamps 取每个 UP 的最大 PubTs, 忽略 0 时间戳");
        check(FollowSorting.NewestFor(items, 300)?.Id == "d300b",
            "NewestFor 按 PubTs 取最大, 而不是取流里第一条");

        // ==================== 2. ClearUnreadBadge 的隐患: 取"最新一条"而不是"第一条匹配" ====================
        // 这个夹具重现旧 bug: 混合流里 UP-B 的**旧**条目排在 UP-B 的**新**条目之前
        // (服务端置顶 / 翻页拼接 / 同秒发布的乱序都可能造成)。
        // 旧写法命中第一条就 break → 基准被推成旧 id → 下次刷新算出 1 条未读, 红点又亮回来。
        var mixed = new List<DynamicItem>
        {
            Item("b-old", 200, 300),   // ← 旧写法会选中这条
            Item("c-new", 300, 1000),
            Item("b-new", 200, 900),   // ← 正确的那条
            Item("a-new", 100, 800),
        };
        var picked = FollowSorting.NewestFor(mixed, 200);
        check(picked?.Id == "b-new", "ClearUnreadBadge 取到的是 PubTs 最大的那条(不是流里第一条匹配)");
        check(FollowSorting.NewestFor(mixed, 999) == null, "流里没有这个 UP 时返回 null(调用方据此退到最近见过的 id)");
        check(FollowSorting.NewestFor(mixed, 0) == null, "mid<=0 时返回 null, 不会误命中");

        // ==================== 3. 未读基准推进(Set 之后 NewCount 归零) ====================
        // 用内存字典替身, 不碰磁盘。
        var store = new Dictionary<long, string>();
        Func<long, string?> read = mid => store.TryGetValue(mid, out var v) ? v : null;
        Action<long, string> write = (mid, id) => store[mid] = id;

        var users2 = new List<FollowUser> { NewUser(100, "UP-A"), NewUser(200, "UP-B"), NewUser(300, "UP-C"), NewUser(400, "UP-D") };
        var page1 = new List<DynamicItem>
        {
            Item("c1", 300, 1000), Item("a1", 100, 900), Item("b1", 200, 800),
        };
        FollowSorting.ApplyBadges(users2, page1, read, write);
        check(users2.Where(u => u.Mid != 400).All(u => u.NewCount == 1), "首次发现动态应显示未读");
        check(store.Count == 0, "探查动态不能自动写入已读基准");
        foreach (var item in page1) write(item.AuthorMid, item.Id); // Simulate opening each author.
        check(!store.ContainsKey(400), "本页没出现的 UP 不建基准(也无从判断)");

        // 第二轮: 300 发了新动态, 200 的基准那条还在这一页里(没有新的)
        var page2 = new List<DynamicItem>
        {
            Item("c2", 300, 1100), Item("c1", 300, 1000), Item("a1", 100, 900), Item("b1", 200, 800),
        };
        users2[0].NewCount = 0; users2[1].NewCount = 0; users2[2].NewCount = 0;
        FollowSorting.ApplyBadges(users2, page2, read, write);
        var c = users2.First(u => u.Mid == 300);
        check(c.NewCount == 1, $"有新动态的 UP 亮红点, 条数=排在基准之前的条目数 → {c.NewCount}");
        check(users2.First(u => u.Mid == 200).NewCount == 0, "基准那条还在本页 → 红点消失");
        check(users2.First(u => u.Mid == 400).NewCount == 0, "本页没出现的 UP 不动(不会凭空亮)");

        // 点开 300 = 已读: 基准推到最新 c2 → NewCount 归零, 下一轮不再亮
        var newestC = FollowSorting.NewestFor(page2, 300)!;
        check(newestC.Id == "c2", "点开时取到该 UP 的最新一条");
        store[300] = newestC.Id;
        users2.First(u => u.Mid == 300).NewCount = 0;
        FollowSorting.ApplyBadges(users2, page2, read, write);
        check(users2.First(u => u.Mid == 300).NewCount == 0,
            "Set 之后同一页再算一遍: NewCount 保持 0(红点不会又亮回来)");

        // 三连发: 基准那条已经完全翻出本页 → 本页该 UP 的条目全算新的
        var page3 = new List<DynamicItem>
        {
            Item("c5", 300, 1500), Item("c4", 300, 1400), Item("c3", 300, 1300),
        };
        FollowSorting.ApplyBadges(users2, page3, read, write);
        check(users2.First(u => u.Mid == 300).NewCount == 3,
            "基准已翻出本页时, 本页该 UP 的条目全部计为新动态");

        // skipMid: 单 UP 视图里"正在看的那个"不亮红点
        users2.First(u => u.Mid == 300).NewCount = 0;
        store[300] = "c3";
        FollowSorting.ApplyBadges(users2, page3, read, write, skipMid: 300);
        check(users2.First(u => u.Mid == 300).NewCount == 0 && store[300] == "c3",
            "skipMid 的 UP 既不被点亮, 也不被改写基准");

        // ==================== 4. ViewModel 层: 排序真的作用到 Users 上 ====================
        var vm = new FollowViewModel();
        vm.Users.Clear();
        foreach (var u in new[] { NewUser(100, "UP-A"), NewUser(200, "UP-B"), NewUser(300, "UP-C"), NewUser(400, "UP-D"), NewUser(500, "UP-E") })
            vm.Users.Add(u);

        // 生产链路: RefreshAsync 拿到关注列表后会 EnsureFollowOrder(分配关注序号)
        vm.EnsureFollowOrder(vm.Users);

        // 最近更新: 没有时间轴时退化成关注顺序
        check(Ids(vm.Users) == "100,200,300,400,500", "刚加载完(还没有任何动态)左栏就是接口原序");

        // 记下时间轴(生产侧由 UpdateUnreadBadges → RememberTimestamps 完成) 再排序
        vm.RememberTimestamps(new List<DynamicItem>
        {
            Item("c1", 300, 1000), Item("a1", 100, 900), Item("b1", 200, 800),
        });
        vm.SortUsers();
        check(Ids(vm.Users) == "300,100,200,400,500",
            $"「最近更新」: 抓到的按 PubTs 降序在前, 没抓到的按关注顺序排后面 → {Ids(vm.Users)}");

        // 排序必须**只是展示顺序**: Users 里的对象一个都没多、一个都没少
        check(vm.Users.Count == 5 && vm.Users.Select(u => u.Mid).Distinct().Count() == 5,
            "重排只是换位置, 没有重复/丢失 UP");

        // 再排一次必须原样(不抖动)
        var snapshot = Ids(vm.Users);
        vm.SortUsers();
        check(Ids(vm.Users) == snapshot, "同一份数据再排一次结果完全相同(刷新不抖动)");

        // 切模式 → 立即生效 + 落盘
        vm.FollowSortMode = FollowSortMode.FollowOrder;
        check(Ids(vm.Users) == "100,200,300,400,500",
            $"切到「关注顺序」立即回到接口原序 → {Ids(vm.Users)}");
        check(Svc.Settings.FollowSortMode == FollowSortMode.FollowOrder,
            "排序模式落盘到 Svc.Settings(与 FollowCardWall 同一类用户偏好)");

        vm.FollowSortMode = FollowSortMode.UnreadFirst;
        vm.Users.First(u => u.Mid == 200).NewCount = 2;
        check(Ids(vm.Users) == "200,300,100,400,500",
            $"「未读优先」: 有红点的 UP 立刻提到最前, 组内仍按最近更新 → {Ids(vm.Users)}");

        // 再起一个没抓到动态的 UP 有红点 → 它也在未读组里(组内靠后, 因为没时间戳)
        vm.Users.First(u => u.Mid == 500).NewCount = 1;
        check(Ids(vm.Users) == "200,500,300,100,400",
            $"未读组里有多个时按最近更新排 → {Ids(vm.Users)}");
        vm.Users.First(u => u.Mid == 500).NewCount = 0;

        // ★★★ 回归防护: 「未读优先」模式下**批量改 NewCount** 不能把遍历炸掉。
        //   机制: NewCount 变化 → OnUserPropertyChanged → SortUsers() → Users.Move,
        //   若发生在 foreach(Users) 内部就是"遍历中改集合", 集合枚举器会抛
        //   InvalidOperationException。生产上这条路径由定时轮询触发, 漏了就是
        //   "过两分钟动态页崩一次"。这里直接模拟最狠的情况: 全员都有未读, 逐个改。
        var crash = "";
        try
        {
            vm.FollowSortMode = FollowSortMode.UnreadFirst;
            foreach (var u in vm.Users.ToList()) u.NewCount = 2;
            // 再来一次「全部标记已读」链路(内部同样是批量改 NewCount + 重排)
            vm.MarkAllReadCommand.Execute(null);
            Pump(120);
        }
        catch (Exception ex) { crash = ex.GetType().Name + ": " + ex.Message; }
        check(crash.Length == 0, $"「未读优先」下批量改红点不会抛异常 → {(crash.Length == 0 ? "OK" : crash)}");
        check(vm.Users.All(u => u.NewCount == 0), "批量标记之后红点全清");

        // 点开这个 UP(清红点)之后, 它不该继续赖在最前面
        vm.Users.First(u => u.Mid == 200).NewCount = 0;
        check(Ids(vm.Users) == "300,100,200,400,500",
            $"红点清掉后「未读优先」下自动归位 → {Ids(vm.Users)}");

        // 汇总徽标跟着 NewCount 自动维护(订阅, 不靠调用点记得刷)
        check(vm.HasUnreadSummary == false, "没有未读时汇总徽标隐藏");        vm.Users.First(u => u.Mid == 300).NewCount = 3;
        check(vm.HasUnreadSummary && vm.UnreadSummary == "1 个 UP 有更新",
            $"汇总徽标文案 → {vm.UnreadSummary}");
        vm.Users.First(u => u.Mid == 500).NewCount = 1;
        check(vm.UnreadSummary == "2 个 UP 有更新", $"汇总徽标跟着变成 2 个 → {vm.UnreadSummary}");
        vm.Users.First(u => u.Mid == 300).NewCount = 0;
        vm.Users.First(u => u.Mid == 500).NewCount = 0;
        check(!vm.HasUnreadSummary, "红点全清后汇总徽标消失(不会永久残留)");

        vm.FollowSortMode = FollowSortMode.RecentUpdate;

        // ==================== 5. 界面上真的挂上了(绑定/复用校验) ====================
        var page = new FollowPage { DataContext = vm };
        var window = new Window { Content = page, Width = 1150, Height = 800, ShowInTaskbar = false };
        window.Show();
        Pump();

        var recentChip = FindByName<Button>(page, "SortRecentChip");
        var unreadChip = FindByName<Button>(page, "SortUnreadChip");
        var orderChip = FindByName<Button>(page, "SortOrderChip");
        var markButton = FindByName<Button>(page, "MarkAllReadButton");
        check(recentChip == null && unreadChip == null && orderChip == null, "三枚多余排序按钮已删除");
        check(markButton != null, "全部标记已读仍可用");
        // 左栏 XAML 真的绑了 HasNew(未读红点的可见性依据)
        var rows = Descendants<ContactListRow>(page).ToList();
        check(rows.Count > 0, "左栏渲染出了 UP 行");
        check(rows.All(r => r.DataContext is FollowUser u && r.HasUnread == u.HasNew),
            "每一行的未读红点绑定跟着 FollowUser.HasNew 走");
        var firstRow = rows.First();
        var firstUser = (FollowUser)firstRow.DataContext;
        firstUser.NewCount = 1;
        Pump(60);
        check(firstRow.HasUnread, "NewCount 改动后红点立刻亮(PropertyChanged → 绑定刷新)");
        firstUser.NewCount = 0;
        Pump(60);
        check(!firstRow.HasUnread, "NewCount 归零后红点立刻灭");

        // 「全部标记已读」: 点完所有红点清零 + 文案短暂变「已标记」
        vm.Users[0].NewCount = 2;
        vm.Users[1].NewCount = 1;
        check(vm.UnreadSummary == "2 个 UP 有更新", $"点之前徽标是 2 个 → {vm.UnreadSummary}");
        markButton!.Command.Execute(null);
        Pump(150);
        check(vm.Users.All(u => u.NewCount == 0), "「全部标记已读」把所有红点清零");
        check(vm.MarkAllReadText == "已标记", $"点完按钮文案短暂变「已标记」→ {vm.MarkAllReadText}");
        check(!vm.HasUnreadSummary, "清零后汇总徽标立刻消失");

        // 界面快照留档(与其它页面一致, 存 artifacts); 失败只提示不判失败 ——
        // 这条测的是功能, 截图是给人工看观感的。
        try
        {
            // ★ 测试里没有登录态, 左栏整块被 ShowContent 折叠着(视觉树在, 但渲染出来是空的)。
            //   截图前把承载左栏的那个 Grid 本地置为 Visible 覆盖绑定 —— 只为出图,
            //   紧接着就关窗, 不影响上面的任何断言。
            vm.FollowSortMode = FollowSortMode.UnreadFirst;
            vm.Users.First(u => u.Mid == 300).NewCount = 2;
            vm.Users.First(u => u.Mid == 500).NewCount = 1;
            var contacts = FindByName<ScrollViewer>(page, "ContactsScroll");
            if (contacts != null)
            {
                DependencyObject? node = contacts;
                while (node != null && node is not Grid) node = VisualTreeHelper.GetParent(node);
                if (node is Grid host) host.Visibility = Visibility.Visible;
            }
            page.UpdateLayout();
            Pump(120);
            page.UpdateLayout();
            var w = (int)Math.Max(1, page.ActualWidth);
            var h = (int)Math.Max(1, page.ActualHeight);
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(page);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, "follow-sort-unread.png"));
            encoder.Save(stream);
            check(File.Exists(Path.Combine(output, "follow-sort-unread.png")), "左栏排序/未读界面快照已保存");
        }
        catch (Exception ex)
        {
            Console.WriteLine("SKIP snapshot: " + ex.Message);
        }

        window.Close();
        Pump(80);

        // 收尾: 把设置恢复成默认, 免得这次测试影响后面跑的用例
        Svc.Settings.SetFollowSortMode(FollowSortMode.RecentUpdate);
    }

    // ------------------------------------------------------------ 夹具

    private static FollowUser NewUser(long mid, string name) => new() { Mid = mid, Name = name };

    private static DynamicItem Item(string id, long mid, long pubTs) => new()
    {
        Id = id,
        AuthorMid = mid,
        AuthorName = "UP" + mid,
        PubTs = pubTs,
        Kind = DynamicKind.Text,
        Text = "测试动态 " + id,
    };

    private static string Ids(IEnumerable<FollowUser> users) => string.Join(",", users.Select(u => u.Mid));

    private static string? TextOf(Button button)
        => Descendants<TextBlock>(button).Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));

    private static T? FindByName<T>(FrameworkElement root, string name) where T : class
        => root.FindName(name) as T ?? FindDescendantByName(root, name) as T;

    private static FrameworkElement? FindDescendantByName(FrameworkElement root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe)
            {
                if (fe.Name == name) return fe;
                var nested = FindDescendantByName(fe, name);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Pump(int ms = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
