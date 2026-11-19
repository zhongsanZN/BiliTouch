# BiliTouch 基础能力审查 · 2026-11-11

> 后续状态：F01—F08 已修复并通过回归检查，见 [修复与验证记录](FOUNDATION-FIXES-2026-11-11.md)。下文保留修复前的审查证据。

审查对象是当前工作区（AppVersion 为 0.3.0），包含已有未提交修改。本轮没有修改业务源码，没有运行发布、安装或真实账号删除操作。复现使用独立数据目录和本地模拟 HTTP 响应，没有读写实际用户资料或发送真实私信。

结论：主要缺陷集中在对象与账号边界、失败重试、设置保存读取，以及自动验证是否真正执行。下面的 P1 应优先处理；界面能够正常打开、工程能够编译，都不能覆盖这些基础正确性问题。

| 编号 | 优先级 | 问题 | 验证方式 |
| --- | --- | --- | --- |
| F01 | P1 | 清空收藏夹期间切换，会改变后续删除目标 | 源码路径确认；隔离验证 Busy 状态仍可切换目标，未执行真实删除 |
| F02 | P1 | 退出登录后保留私信、当前联系人和草稿，切号后可能用新账号发送旧草稿 | 本地模拟请求复现 |
| F03 | P2 | 账号变化不会作废部分在途响应，旧资料和历史可在退出后回填 | 延迟模拟响应复现 |
| F04 | P2 | 快速切换收藏夹会把不同夹子的响应混在一起 | 逆序完成模拟响应复现 |
| F05 | P2 | 历史分页失败仍推进页码，再次加载跳过失败页 | 模拟第 2 页失败复现 |
| F06 | P2 | 已签名参数再次签名时把旧签名算进去，重试签名错误 | 调用实际签名方法复现 |
| F07 | P2 | 音质偏好写入后没有读回，重启变为 Auto | 实际保存、实际读取复现 |
| F08 | P2 | 自动构建只编译测试而不执行，当前已有测试失败仍可通过构建 | 流程检查与现有测试实跑 |

## F01：冻结删除操作的收藏夹与账号

位置：`BiliTouch/ViewModels/FavoritesViewModel.cs:435`、`:455`；切换入口为 `:201`，界面入口为 `BiliTouch/Views/Pages/FavoritesPage.xaml:128` 附近。

用户确认清空 A 后，循环每次读取、删除都重新访问 `_currentFolderId`。与此同时，收藏夹切换命令没有 Busy 限制，选择条也没有在 Busy 时禁用。网络等待期间点击 B 会立即修改 `_currentFolderId`。

后果有两种：读取 A 后切到 B，会把 A 的资源 ID 向 B 提交移除请求；已经完成 A 的一批移除后切到 B，下一轮会直接读取并清空 B。B 从未出现在原确认对话框中。批量移出所选也存在循环中重读当前收藏夹的问题。

隔离复现结果：`Busy=true` 时 `SelectFolderCommand.CanExecute` 仍为 true，`SelectFolderAsync` 仍成功改变目标。删除链路由源码确认，没有对真实账号运行删除。

修复方向：在确认操作时冻结 folderId、标题、账号标识；整轮操作始终使用这份快照；账号变化停止后续批次。禁用切换可以作为额外保护，不能替代固定操作目标。

## F02：登出必须清空私信状态，切号不能复用草稿

位置：`BiliTouch/ViewModels/MessagesViewModel.cs:83`，关联 `:72`、`:225` 附近。

MessagesViewModel 没有在 Session.Changed 时清理状态。LoadAsync 未登录分支仅设置错误并返回，保留 Sessions、Messages、Current、Draft。消息页面也只是在上方显示错误，旧会话和旧正文仍然绑定在内容区。

复现：账号 100 的模拟页面含一条私信、联系人 999 和草稿；Clear 后再次 LoadAsync，以上状态全部保留，CanCompose、CanSend 仍为 true。SendAsync 在完全未登录时会阻止实际发送，但登录账号 200 后，如果新会话加载失败，原联系人及草稿仍可沿发送路径提交。模拟服务实际捕获到 sender=200、receiver=999、content=old draft。

修复方向：账号变化立即清空联系人、正文、草稿、分页状态和待加载会话，禁用编辑/发送，直到属于新账号的会话建立；发送时校验联系人和草稿所属账号。不要仅依据“当前存在登录态”决定旧编辑状态是否可发送。

## F03：在途响应必须属于当前账号

位置：`BiliTouch/ViewModels/HistoryViewModel.cs:233`；同类问题见 `BiliTouch/ViewModels/MainViewModel.cs:121`。

历史页的 generation 只随刷新和类别切换变化，不随账号变化。请求开始时有登录态检查，请求结束时却只比较页面代次。MainViewModel 刷新资料也没有在 await 后比较账号或代次。

复现：延迟账号 100 的历史响应，等待期间 Clear，响应完成后，HasLogin 已为 false，但 Entries 仍加入旧账号观看历史。资料请求的相同实验中，IsLoggedIn 为 false，UserName、UserFace 却再次被旧账号资料填入。

修复方向：建立账号代次；账号切换时清理现有账号数据并使在途结果失效。每个 await 后、每次提交结果前验证账号代次，而不是仅在请求开始时判断是否登录。需要覆盖历史、收藏、资料、通知、发送结果等账号数据入口。

## F04：收藏夹响应缺少目标校验

位置：`BiliTouch/ViewModels/FavoritesViewModel.cs:240` 至追加列表处。

SelectFolderAsync 每次清空 Items 并启动 LoadPageAsync，但先发出的请求没有取消，也没有请求代次或 folderId 校验。A 请求较慢时，先选 A 再选 B，B 结果回来后 A 仍会追加进当前 Items。

复现结果：CurrentFolderTitle 为 B，Items 同时出现 B 与 A 的视频。旧响应还会改变 LoadingMore、HasMore，污染 B 的分页状态。

修复方向：请求捕获 folderId、page、generation、账号；返回时逐一校验。旧请求不得修改新请求的列表、错误、加载和分页状态。

## F05：分页失败不能消耗页码

位置：`BiliTouch/ViewModels/HistoryViewModel.cs:218`。

LoadAsync(false) 在发送前 `_pn++`。错误分支不回退，而 HasMore 仍保留，因此之后继续滚动会发出下一页，直接遗漏失败页。

复现：第 1 页成功、第 2 页失败、再次加载，请求序列为 `[1,2,3]`，显示 60 条内容，但中间整页缺失。

修复方向：以局部 nextPage 发起请求，成功接受响应之后再更新 `_pn`；失败时保留原分页位置，继续加载仍请求第 2 页。

## F06：签名重试会把旧 w_rid 纳入签名

位置：`BiliTouch/Services/ApiClient.cs:252`；重试入口为 `:153`、`:176` 附近。

GetJsonAsync 会原地给 ps 添加 wts、w_rid，并在 -403 刷新密钥、-412 退避时把同一个字典再次传入。SignParams 排序所有键，未移除旧 w_rid，因此第二次签名的输入包含前一次签名。

复现：同一份参数再次调用实际 SignParams 后，得到的签名与删除旧 w_rid 再签名的结果不同。这使恢复请求继续使用无效签名；搜索的部分回退流程同样复用原字典。

修复方向：签名应从不含 wts、w_rid 的业务参数副本生成；至少在计算前剔除旧签名。加入首次签名、换密钥重签、限流后重签的行为验证。

## F07：音质设置只写不读

位置：`BiliTouch/Services/SettingsStore.cs:618` 附近的 ApplySanitized；保存字段在 `:846`。

Snapshot 写入 PreferredAudioQuality，播放器切换音质也更新该偏好；ApplySanitized 只恢复 PreferredQualityQn，没有恢复 PreferredAudioQuality。新实例因此保持默认 Auto。

复现：SettingsStore 设置 High 并 SaveNow，新的 SettingsStore.Load 后得到 Auto。正常退出再次保存还可能把文件中原来的偏好覆盖为 Auto。

修复方向：加载时恢复并校验音质枚举；增加用户可保存设置的保存—读取回环验证。

## F08：测试能编译，但没有成为自动检查

位置：`.github/workflows/build.yml:21`。

自动构建的最后一步只有 `dotnet build tests/TouchTests/TouchTests.csproj`，没有执行 TouchTests。解决方案也只包含应用工程，普通解决方案构建不意味着测试已运行。

本轮运行现有 `--rate-limit`：前 9 个断言通过，随后失败于“every HTTP send in ApiClient awaits the rate limiter first”。实际未过闸的调用是 `BiliTouch/Services/ApiClient.cs:6004` 的搜索建议请求。无论如何处理这个端点的限流策略，都应让自动检查实际运行并报告失败，而非只编译验证代码。

修复方向：选定稳定、离线、覆盖账号/分页/设置/网络异常的基础检查组，在自动构建中执行并传播退出码、保存结果；需要桌面和真实网络的专项测试另行明确运行环境。

## 验证记录与边界

- 应用工程构建成功，0 错误；UserCard 的同一处空引用警告在 WPF 两阶段构建中报告两次。
- TouchTests 独立构建成功，0 错误，7 个警告。
- 现有 `--message-unread` 的 14 个断言通过，包括角标清理与旧账号响应隔离；这份实现可以作为其他账号数据入口的参考。
- 现有 `--rate-limit` 未通过，失败位置见 F08。
- 新建隔离复现程序位于 `artifacts/foundation-audit-2026-11-11/`，结果为该目录下的 `results.json`。该目录被 Git 忽略；不包含真实账号凭据。
- 附带发现：`UrlParser.ExtractUid("https://space.bilibili.com/123456")` 返回 null，原因是正则匹配 `/space/123456`；其统一 HandleUrl 入口当前声明为未接线，因此列为低优先级待清理项，未计入上面 8 项活动缺陷。
- 本轮未验证完整触屏体验、物理触控板惯性、长时播放性能、真实网络限流恢复或真实账号写入。构建通过不代表上述场景通过。

建议修复顺序为 F01、F02，随后统一处理 F03/F04 的请求归属，再修 F05—F07，并将相应验证接入 F08。不要只修具体界面上的异常表现；需要固定“请求属于哪个对象和账号”“失败是否改变游标”“保存是否能够还原”这几条基本规则。
