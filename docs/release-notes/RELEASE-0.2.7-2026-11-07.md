# 0.2.7 最新改动复核与修复（2026-11-07）

本轮将本地版本从 0.3.0 调整为 0.2.7，保留 0.2.6 的功能和性能改动。未执行 GitHub 发布。

## 复核结论

- 保留已有卡片墙按可见行生成、跨行才重新测量、滚动逐帧合并等改动。这些优化与现有外层 ScrollViewer 协作，有独立回归覆盖。
- 撤回首页四处 ListBox 替换。原模板使用未声明的 `headers` 前缀，构建成功后仍会在显示首页卡片时抛出 XamlParseException；即使补上前缀，CardWallPanel 也不是实际使用的 VirtualizingCardWallPanel。新增内层 ScrollViewer 还会改变面板的视口来源，不能仅靠 Recycling 标记证明虚拟化有效。
- 恢复 ItemsControl + VirtualizingCardWallPanel + 页面 ScrollViewer，保留现有 DeferredFill。此前声称 DeferredFill 已移除、FPS 提升 80%、内存降低 40% 的文案没有对应实现和本轮对照证据，已移除。
- 保留 WPF 硬件加速偏好。修正应用顺序：AppPaths.Init → Settings.Load → ApplyProcessRenderMode → 创建窗口。此前在 Load 前读取设置，实际总是使用默认 true，关闭并重启也不会启用软件渲染。
- GPU 开关只决定 Default / SoftwareOnly，不再强制修改图片缩放质量或边缘抗锯齿，也不声称能关闭 VLC/WebView2 的硬件使用或显存占用。默认允许硬件加速，不保证每台机器使用 GPU。
- 设置模型保留 bool?，缺失字段按 true 迁移，显式 false 能保留。

## 构建与命名

- AppVersion、项目 Version、安装器 AppVersion/FileVersion、发布脚本默认值及 README 同步为 0.2.7。
- 系统 PATH 中的 dotnet 只有运行时，没有 SDK。发布脚本现在优先使用项目配套的 `../.tools/dotnet` SDK，并在清理产物前检查 .NET 10 SDK。
- 测试工程移除重复编译 PlayerTouchSession 的条目，直接使用项目引用中的真实类型，消除 CS0436 类型冲突。
- 原记录中的不存在的 WPF 类型、枚举、属性等编译修复在接手时已存在；本轮没有将其冒认成新修复。

## 验证

新增 `--release027` 直接装载真实 HomePage.xaml，而不是仅拼装一个类似的测试面板。修复前在 Window.Show 时稳定复现未定义 headers 的异常；修复后验证 600 条推荐数据仅生成 25 个初始容器、中段滚动、宽度和版本一致性，以及设置迁移和启动顺序。

详细构建与回归日志位于 `artifacts/review-*`。这些结果验证功能与结构，不代表所有设备的帧率或实际触摸手感。

### 本轮最终结果

- 应用 Release 全量重建：0 错误，4 条既存警告（UserCard 空引用与播放器未等待异步调用，WPF 临时项目各重复报告一次）。测试编译亦为 0 错误，仍有既存警告及 NuGet 漏洞索引网络告警。
- 8 组回归全部通过，共 280 项：release027 9、dynamic 119、trackpad-scroll 33、web-player-layout 28、web-controls-unread 35、player-comments-scroll 12、release023 33、scroll-realization 11。
- 安装器与便携包重新生成；安装器版本资源为 0.2.7。分别解包到独立目录运行，版本、主题资源和静音本地视频解码/播放进度验证均通过。未实际安装到用户系统。
- 安装器：`Publish/BiliTouch-0.2.7-setup.exe`；便携包：`Publish/BiliTouch-0.2.7-portable.zip`；校验和：`Publish/BiliTouch-0.2.7-SHA256SUMS.txt`。
- 包运行结果：`artifacts/review-027-package-ab426335/setup-result.json`、`portable-result.json`。
