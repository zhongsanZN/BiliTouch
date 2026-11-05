# 动态页：UP 主排序与未读/已读机制（2026-11-05）

面向问题（用户原话）：“没看懂动态的 up 主排序怎么做的，未读已读机制好像也没做好”。

这份文档要回答三件事：**左栏到底按什么排**、**什么算已读**、**红点为什么不会永久残留、也不会凭空消失**。

---

## 1. 左栏排序

### 1.1 三种模式

| 模式 | 界面上叫 | 规则 |
|---|---|---|
| `FollowSortMode.RecentUpdate`（0，默认） | 最近更新 | 每个 UP 取它在**已加载动态流**里最新一条的 `DynamicItem.PubTs`，按降序排；**没有任何动态的 UP 排在后面**，组内保持关注顺序 |
| `FollowSortMode.UnreadFirst`（1） | 未读优先 | 先按“有没有未读红点”分两组（有红点的在前），**组内仍按最近更新排** |
| `FollowSortMode.FollowOrder`（2） | 关注顺序 | 只看关注序号（接口返回的原序），**完全不看动态时间、也不看未读** |

代码：`BiliTouch/Models/FollowSorting.cs`（纯逻辑） + `FollowViewModel.SortUsers()`（把结果搬到 `ObservableCollection`）。

### 1.2 “关注顺序”从哪里来

`FollowViewModel._followOrder`：`mid → 序号`，**第一次见到该 UP 时分配、之后永不改变**（`EnsureFollowOrder`）。
关注列表第一页 30 个、翻页再补 30 个，序号只增不改 —— 这是“关注顺序”在多次刷新、翻页之后仍然稳定的依据。
切换账号（`_selfMid` 变化）时整张表清空重来（`ResetSortCaches`），否则新账号会沿用上一个人的序号，顺序看起来彻底是乱的。

### 1.3 为什么不会在每次刷新时抖动

三条独立保证：

1. **全序比较器**：`FollowSorting.Arrange` 的比较链是
   `未读组 → PubTs 降序 → 关注序号 → 当前下标`。
   最后两级保证任意两个元素都**不会判成“相等”**。`List.Sort` 是不稳定排序，
   一旦比较器返回 0，相对位置就由内部实现决定 —— 那是“数据没变、排两次却换位置”的根因。
2. **时间轴只增不减**：`_latestTs`（`mid → 最大 PubTs`）跨页面累计，不跟着当前这一页重算。
   切到单 UP 视图再切回来、翻页再翻回去，顺序都不变。
   “这一页没出现”不等于“从没发过”，所以它不会因此被踢到最后。
3. **原地重排 + 短路**：`SortUsers` 用 `ObservableCollection.Move` 逐个归位，
   已经在对的位置就一个动作都不发 —— 不 `Clear()` 重填，滚动位置和选中项都不会丢。

### 1.4 排序不做什么

- **不重新请求接口**。排序只作用在已经拿到的数据上。
- **不改动右栏**。右边是动态时间线，顺序永远服务端给的时间倒序。
- **不改动集合内容**。只是换位置，不新增、不删除、不去重。

---

## 2. 未读 / 已读

### 2.1 基准是什么

`Services/DynamicSeenStore.cs` 里存 `mid → 上次看过的最新一条动态 id`。
文件位置：`%LocalAppData%\BiliTouch\dynamic_seen.json`（与其它数据文件同一处）。

判断全程**本地比对**，不新增任何接口。

### 2.2 什么算已读（语义边界）

| 动作 | 算不算已读 | 说明 |
|---|---|---|
| 进入动态页 / 停在「全部动态」 | **不算** | 否则一进页面红点就被自己清掉，等于没有红点 |
| 下拉刷新、定时轮询 | **不算** | 只是重新计算红点亮不亮 |
| **点开某个 UP（右栏只看 TA）** | **算** | 这是唯一会推进基准的用户动作（`ClearUnreadBadge`） |
| 点「全部标记已读」 | 算（一次性全部） | 基准推到每个 UP 各自最新一条 |
| 点「N 条新动态」提示条 | 不算 | 那是把新内容插到列表最前，不等于逐个看过 |

### 2.3 红点怎么算

`FollowSorting.ApplyBadges`（由 `FollowViewModel.UpdateUnreadBadges` 调用）：

- **没有基准的 UP** → 只把当前最新一条记成基准，`NewCount = 0`（**不亮红点**）。
  第一次打开动态页不该满屏红点 —— 那不是“有更新”，那只是“以前没记过”。
- **基准 == 该 UP 最新一条** → `NewCount = 0`（红点灭）。
- **基准不同** → `NewCount` = 本页里**排在基准那条之前**的该 UP 条目数。
  基准那条已经翻出本页时，本页该 UP 的条目全算新的。
- **本页没出现的 UP 完全不动**。

### 2.4 三个“不会”是怎么保证的

**为什么不会永久残留**

- 点开 UP → 基准推到该 UP 最新一条 → 下一轮 `ApplyBadges` 算出 `seen == top` → 红点灭。
- 不想逐个点 → 「全部标记已读」把所有人的基准推到各自最新一条，一次性清零。
- 基准是**按 mid** 存的，不跟着页面实例走：切页、重启、进程退出再进都还是那份基准。

**为什么不会凭空消失**

- 「全部动态」里看一眼**不动**基准，红点不会因为“我路过了一下”而消失。
- 本页没出现的 UP **不参与判定**（不清、不亮）。动态接口只给近期条目，
  一个 UP 最近没投稿就无从判断，这时清红点等于凭空断言“没更新”。

**为什么不会“点过了又亮回来”（本次修的真实隐患）**

`ClearUnreadBadge` 旧写法遍历 `Dynamics`，命中第一个 `AuthorMid` 就 `Set` 并 `break`，
注释假设“FeedItems 是时间倒序，第一条就是最新”。但**单 UP 视图里的 `Dynamics` 可能混进别的作者**
（合作投稿 / 转发 / 空间动态流尾部的推荐），一旦流里出现乱序（服务端置顶、同秒发布、翻页拼接），
“第一条匹配”就会把基准推到一条**较旧**的动态上 —— 下次刷新算出还有 1 条未读，红点又亮回来，
表现就是“怎么点都消不掉”。

现在改成 `FollowSorting.NewestFor(Dynamics, mid)`：显式按 `AuthorMid` 过滤，**取 `PubTs` 最大的那条**
（同 `PubTs` 时保留先遇到的那条，结果仍确定）。当前视图一条都没有时，退到“最近见过的 id”
（`_latestDynamicId`），保证“点开”这个动作不会因为时序（首屏还没回来）而白点。

### 2.5 什么时候会刷新红点

| 触发点 | 代码路径 |
|---|---|
| 「全部动态」首屏/翻页加载完 | `LoadSelectionAsync` → `UpdateUnreadBadges()` |
| 左栏翻出新 UP | `LoadMoreAsync` → `UpdateUnreadBadges()`（仅在「全部动态」下） |
| 定时轮询 / 从别的页面切回来 | `CheckNewDynamicsAsync` → `UpdateUnreadBadges(result.items)` ← **2026-11-05 补上** |
| 点开某 UP | `ClearUnreadBadge` |
| 点「全部标记已读」 | `MarkAllRead` |

以前只有第一条会刷红点，于是“两分钟前刚投稿”的 UP 要等到下一次整页刷新才亮红点。

`UpdateUnreadBadges` 里的 `skipMid = SelectedUp?.Mid`：单 UP 视图里跳过正在看的那个 ——
用户正看着它却顶着红点很怪，它的基准由 `ClearUnreadBadge`（点开那一刻）单独推。

### 2.6 汇总徽标与订阅

左栏顶部显示「N 个 UP 有更新」（`UnreadSummary` / `HasUnreadSummary`）。
它和“未读优先”的排序都依赖 `FollowUser.NewCount`，而 `NewCount` 会被**四个地方**改
（算红点 / 点开清红点 / 全部标记已读 / 取消关注后重算）。

所以 `FollowViewModel` 订阅了 `Users.CollectionChanged` → 逐项挂 `PropertyChanged`
（`OnUsersChanged` / `OnUserPropertyChanged`）：谁改了 `NewCount` 都会自动重算徽标、
并在「未读优先」模式下重排。逐个调用点去记得刷迟早会漏一处，那处的表现就是
“红点灭了但徽标还写着 2 个”。

---

## 3. 落盘

| 键 | 位置 | 说明 |
|---|---|---|
| `FollowSortMode`（int） | `settings.json`（`%LocalAppData%\BiliTouch\settings.json`） | 0/1/2，见 `AppSettings.FollowSortMode` + `SettingsStore.FollowSortMode` |

- 与 `FollowCardWall` 同一类**用户偏好**：点一次就落盘，重启/切页回来还是他选的那档。
- **向后兼容**：老配置没有这个字段 → 反序列化拿到 `0` → 正好是默认的“最近更新”，不需要版本兼容代码。
- **越界归一**：`SettingsStore.ApplySanitized` 里走 `FollowSorting.Normalize`，
  手改配置塞进 `99` / `-1` 会回落默认，而不是在下游 `switch` 里默默走 `default`
  （那会表现成“三个 chip 全不亮”，看着像排序坏了）。

---

## 4. 界面

`Views/Pages/FollowPage.xaml`，左栏顶部、**「全部动态」入口之前**：

- 三枚 chip：最近更新 / 未读优先 / 关注顺序（样式 `FollowSortChipStyle`，与 `FeedLayoutChipStyle` 同一套触发器与配色）。
  选中态由 `FollowViewModel.SortTagRecent / SortTagUnread / SortTagOrder`（`"on"` / `"off"`）驱动，
  **只在一处决定**，不会出现“两枚都亮”或“一枚都不亮”。
- 汇总徽标：品牌红底白字「N 个 UP 有更新」，没有未读时整块隐藏。
- 「全部标记已读」：点完 1.6 秒内文案变「已标记」+ 粉底（`MarkAllReadFlash`），给一次明确的“点到了”反馈。
  没有未读时按钮仍保留（它是“我确认都看过了”的手动出口，不该忽隐忽现），但列表为空时禁用。

### 踩过的坑（已处理）

- **全局隐式 `TextBlock` 样式带 `Foreground` setter**，优先级高于 `ContentPresenter` 透传的
  `TextElement.Foreground`。所以 chip / 按钮里的文字必须写成**显式 `TextBlock`** 并显式给 `Foreground`
  （这里绑到 `RelativeSource AncestorType=Button` 的 `Foreground`），否则选中时的粉字会被吃成灰字。
- `DataTrigger` 的 `Binding` 必须写 `RelativeSource Self`（`ControlTemplate` 里不写解析不到，
  改 `TemplatedParent` 会静默失效）。
- 左栏只有 252 宽，三枚 chip 的 `Padding` 收到 `7,4`、`MinHeight` 28，才排得进一行。

---

## 5. 验证

`tests/TouchTests/FollowSortTests.cs`，注册为 `TouchTests.exe --follow-sort`。**全程离线**：
假 `DynamicItem` / `FollowUser`，基准用内存字典替身（不写 `dynamic_seen.json`）。覆盖：

1. 三种排序的结果顺序（含“没抓到的 UP 排后面、保持关注顺序”）；
2. 不抖动：同输入排两次结果一致；`Arrange` 不改动调用方的原集合；
3. `NewestFor` 按 `PubTs` 取最大（这正是 `ClearUnreadBadge` 旧 bug 的回归防护）；
4. 未读基准推进：首次只建基准不亮红点 → 有新动态亮红点 → Set 之后 `NewCount` 归零 → 基准翻出本页时全算新的 → `skipMid` 不动；
5. ViewModel 层：三种模式立即生效、落盘、原地重排不丢不重、汇总徽标跟着 `NewCount` 自动变；
6. 界面：三枚 chip 存在且文案是中文、`Foreground` 走绑定、选中态只亮一枚、红点绑定跟着 `HasNew`、
   「全部标记已读」清零且文案变「已标记」。

构建/测试命令见 task-1 说明（独立 `artifacts/obj-dyn` / `bin-dyn` 目录，避免与并行构建打架）。
