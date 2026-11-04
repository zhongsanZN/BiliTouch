# 动态页修复与验证（2026-11-04）

版本：1.5.4-touch.2。源码基于本目录 BiliDesk-touch，产物位于上级 dist/BiliDesk-1.5.4-touch.2。

## 本轮已修复

- 混合流从精简 feed/nav 改为 feed/all；全部动态、单 UP 继续使用混合时间线。
- 同时识别数字类型和 DYNAMIC_TYPE_* 字符串类型；补齐 opus 无图片时的正文和标题、视频作者和计数、直播推荐 JSON。
- 图片与转发引用不再被 HasAttachedCard 的可见性条件隐藏；被删除原动态显示删除提示；转发递归在解析前限制深度。
- 评论使用服务端 basic.comment_id_str / comment_type，不再把动态 ID 当视频、相簿评论区 ID；修正评论点赞数和 WPF 属性变更通知。
- 用选择代次隔离异步结果，快速切换 UP、刷新、退出登录后丢弃旧请求；分页尊重 has_more 和游标，失败保留当前游标可重试。
- 新动态检查按当前 UP 请求；轮询同时刷新未读，避免重复拉取同一首页；应用新动态时重新同步首页和游标，避免跨页断档。
- 未读计算从视频 bvid 改为动态 ID，覆盖图文、文字、转发；关注列表独立加载状态和终止条件。
- 作者点击阻止冒泡，避免同时打开作者和动态；图片使用现有原生图片预览窗。
- 互动栏加入点赞/取消点赞、评论与转发详情、复制链接；点赞仅在服务器返回成功后更新状态，失败显示原因。
- 时间线使用 ListBox + Recycling + 像素滚动，封面、头像、配图接入现有尺寸控制和缓存；图片格子高度使用可通知的依赖属性。
- 登录提示不再被主体覆盖，切换时保留左侧选择入口。

## 验证

- Release 构建及框架依赖发布通过。仍有原有 UserCard 空引用和 actualAudioOut 未使用警告；测试工程仍有原有重复编译类型警告。
- 动态专项 29 项通过：解析、评论参数与绑定、切换竞争、单 UP 轮询、服务端分页结束、WPF 渲染、虚拟化。
- 301 条固定样例中首屏创建 5 个卡片容器。这只证明容器虚拟化有效，不代表真实账号性能基准。
- 原有触控与本地 VLC 播放回归 74 项通过；VLC 日志仍有缩略图权限及时间戳警告，未宣称媒体链路所有问题已解决。
- 测试网络使用可控 HttpMessageHandler，未读写用户真实会话；没有完成真实登录账号下的动态接口、点赞、风控和长时间滚动验收。
- 安装器编译器在本环境启动失败，报告“无效的系统 DLL 重定位”。提供框架依赖目录和 ZIP，未生成新的 setup.exe；原 touch.1 安装包未覆盖。

## 仍有差距

评论、转发、投票、完整长文详情仍通过内嵌官方网页承载，未实现完整的原生评论编辑、回复、转发编辑、动态发布、表情与话题链接、连续图片浏览器。未知动态类型仍依赖详情入口；本轮没有将这些项目声明为完成。尚未开展全应用性能剖析或与官方客户端逐项视觉对比。

## 复现命令

使用项目上级 .tools/dotnet/dotnet.exe，并配置 DOTNET_CLI_HOME 为 .tools/cli，NUGET_PACKAGES 为 .tools/nuget。

```powershell
dotnet run --project tests/TouchTests/TouchTests.csproj -c Release -- --dynamic
dotnet run --project tests/TouchTests/TouchTests.csproj -c Release --no-build -- artifacts/touch-sample-silent.mp4
```

日志：artifacts/dynamic-tests.log、artifacts/touch-regression.log。UI 固定样例截图：artifacts/touch-tests/dynamic-card.png。
