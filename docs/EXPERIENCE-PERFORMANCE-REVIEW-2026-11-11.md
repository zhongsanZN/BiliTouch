# 基础体验、播放与 CPU/GPU 审查 · 2026-11-11

> 后续状态：E01、E03、E05—E07 已在 0.3.1 修复，E02 已改用显卡颜色转换及共享纹理合成并复测。E04 按用户反馈保留现有搜索体验。修复、性能对照及验证边界见 [0.3.1 修复记录](PERFORMANCE-AND-FOUNDATION-FIXES-0.3.1-2026-11-11.md)。下文保留修复前证据。

审查对象为当前 0.3.0 工作树，基于 `41a2b96` 并包含既有未提交修改。本轮没有修改业务源码、设置真实账号、安装、打包或发布。新增隔离诊断及记录位于 `artifacts/experience-audit-2026-11-11/`（被 Git 忽略）。

结论：硬件解码在本机确实有效，H.264、HEVC、AV1 均使用 Intel D3D11VA。当前主要问题是界面线程上的同步等待、默认合成输出的读回/上传成本、最小化后继续上传，以及搜索结果缺少视口虚拟化。没有证据支持用强制独显、绑定 CPU 核心或提高进程优先级解决这些问题。

## 发现与优先级

| 编号 | 优先级 | 发现 | 证据 |
| --- | --- | --- | --- |
| E01 | P1 | 缺少设备指纹时，登录完成流程可能死锁界面 | 实际异步方法 + 本地延迟 HTTP 复现，同步等待期间任务不能完成，恢复界面消息循环后完成 |
| E02 | P2 | 默认合成播放的 4K60 成本明显高于原生输出，部分帧在上传前合并 | 完整 PlayerWindow 的同片源对照；H.264 两轮上传约 49.6/53.3 次每秒 |
| E03 | P2 | 最小化播放器仍持续进行全帧 WPF 上传 | 多种编码复现；最小化时 IsVisible 仍为 true，完全隐藏后上传才降至 0 |
| E04 | P2 | 搜索结果随页数增加而创建全部卡片 | 实际 SearchPage：40 条创建 40 张卡片，400 条创建 400 张；后一次追加和布局占界面线程约 1.66 秒 |
| E05 | P2 | 换片、切清晰度及部分恢复路径仍在界面线程同步 Stop | 实际本地换片方法阻塞约 99–255 ms；源码确认多处同步调用 |
| E06 | P2 | 画质增强按钮与默认合成输出的实际能力不一致 | 源码确认：GetVmem 不传原生增强选项；切换增强设置得到同一 core 后直接跳过重建 |
| E07 | P2 | 性能检查存在失效指标和异常中断，不能作为完整验收门槛 | 原有滚动统计输出 0.3 frames/s、NaN；动效检查异常退出；滚动边界检查两次失败 |

### E01：登录完成中的同步等待

位置：`BiliTouch/Views/Windows/LoginWindow.xaml.cs:649`、`:679`；`BiliTouch/Services/ApiClient.cs:297`、`:315`，底层请求的 await 位于 `:118` 起。

`FinishLogin` 在界面线程调用 `EnsureBuvidAsync().GetAwaiter().GetResult()`。当设备指纹尚未齐全、需要异步请求时，相关 await 捕获界面上下文，续体需要回到同一个界面线程；这个线程却正在同步等待续体完成。登录窗口启动时虽有预取，但不能保证预取已成功，因此缓存命中的正常体验不能排除此问题。

隔离复现使用真正的 `EnsureBuvidAsync`，注入延迟 100 ms 的本地模拟响应，设置 WPF 的 DispatcherSynchronizationContext。将同步等待限制为 1 秒以防诊断本身永久挂起，结果为 `finishedWhileUiBlocked=false`；恢复消息循环后 `completedAfterPump=true` 且指纹成功保存。未调用真实登录写入。

建议把完成流程改为异步 await，在等待期间保留明确的忙碌状态，等待完成后再关窗；不能用另一个无上限同步等待替代。WPF 的界面线程不能在被阻塞期间处理返回的工作，参见 [Microsoft 的线程模型说明](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model)。

### E02：硬解有效，但合成输出并非显卡直接呈现

位置：`BiliTouch/Views/Windows/PlayerWindow.xaml.cs:1901`；`BiliTouch/Services/VlcCore.cs:121`；`BiliTouch/Helpers/VmemVideoView.cs:106`、`:140`、`:168`、`:204`。

当前路径为：硬件解码 → VLC 转换/内存回调 → 双缓冲交接 → 界面线程复制到 WriteableBitmap → WPF 合成。显卡参与了解码及渲染，中间仍有显存读回、像素格式处理和上传。缓冲交换已经省去视频线程的一次整帧复制，但没有消除界面线程上传，也没有建立共享显卡纹理的直接合成路径。LibVLC 官方对内存回调输出也明确列出了这些性能限制，见 [LibVLC 3 内存输出说明](https://github.com/videolan/vlc/blob/3.0.x/include/vlc/libvlc_media_player.h)。

以下为完整 PlayerWindow，1280×820 DIP、225% DPI、默认无皮肤设置、本地动态夹具。每次播放预热后采样约 4 秒。CPU 毫秒/秒为整个诊断进程所有线程的 CPU 时间之和；1000 等于一个逻辑核心持续工作。

| 片源 / 输出 | 硬解设备 | 回调交付/秒 | WPF 上传/秒 | CPU 毫秒/秒 | 界面线程 CPU 毫秒/秒 |
| --- | --- | ---: | ---: | ---: | ---: |
| 1080p60 H.264 / 合成 | Intel D3D11VA | 59.9 | 58.9 | 932 | 78 |
| 4K60 H.264 / 合成，第 1 轮 | Intel D3D11VA | 59.8 | 49.6 | 1267 | 137 |
| 4K60 H.264 / 合成，第 2 轮 | Intel D3D11VA | 60.0 | 53.3 | 1466 | 152 |
| 同一 4K60 H.264 / 原生诊断对照 | Intel D3D11VA | 不经过回调 | 不经过 WPF 上传 | 246 | 8 |
| 4K60 HEVC / 合成 | Intel D3D11VA | 60.0 | 58.0 | 1410 | 172 |
| 4K60 AV1 / 合成 | Intel D3D11VA | 60.1 | 56.8 | 1356 | 148 |

这些帧数是回调和上传计数，不等于显示器实际呈现率；原生对照没有使用 VmemView，因此它的回调计数 0 不能解释为播放帧率 0。HEVC、AV1 由同一动态源重新编码，仅用于验证解码路径，码率和编码复杂度并不完全一致。原生对照只用来定位开销，没有改变产品默认输出。

单独 VmemVideoView 的 H.264 4K60 探针能达到约 59.8 次上传/秒、1283 CPU 毫秒/秒；完整播放器两轮只有约 50–53 次上传/秒。不能用独立视频控件的通过代替完整播放器验收。

4K 的 BGR32 单帧约 31.6 MiB；60 次上传每秒仅应用层这次复制就涉及约 1.85 GiB/s 的有效载荷，尚未计入转换、显存传输和复制两端的内存流量。当前结果足以定位合成路径的额外开销，但不足以将全部损耗归因于某一个 CopyMemory 调用。

建议继续分解颜色转换、读回、TryLock 失败、上传和最终呈现的耗时。在保留圆角、动效及连续变形要求下，评估共享纹理合成的实现；需要覆盖真实完整窗口与动画期间的播放，不能仅替换为不能合成的输出并宣称问题消失。

### E03：最小化没有关闭上传工作

位置：`BiliTouch/Helpers/VmemVideoView.cs:170`；`BiliTouch/Views/Windows/PlayerWindow.WebLayout.cs:123`。

上传保护只检查控件 `IsVisible`。最小化后窗口和合成视频控件的 `IsVisible` 仍为 true。窗口状态事件只结束布局动效，没有停止视频位图上传。

H.264 两轮最小化状态仍上传约 48.0/52.8 帧每秒，CPU 为 1651/1691 毫秒每秒；完全 Hide 后上传为 0，CPU 约 597/601 毫秒每秒。HEVC、AV1 和 1080p 夹具同样在最小化后继续上传。各阶段持续约 1–1.6 秒，不能作为长时功耗测量，但上传继续发生这一事实已被直接观察到。

建议把窗口最小化状态纳入呈现开关；声音和播放时钟继续运行，跳过不可见窗口的位图上传，恢复时补最新帧。是否进一步暂停视频解码需要单独验证音画恢复和聆听模式，不能简单停止整个播放器。

### E04：搜索页没有视口虚拟化

位置：`BiliTouch/Views/Pages/SearchPage.xaml:96`、`:100`、`:104`；`BiliTouch/ViewModels/SearchViewModel.cs:404` 起。

结果区是 ScrollViewer → StackPanel → ItemsControl → CardWallPanel，搜索分页持续追加。该面板创建全部卡片，封面延迟加载不能省去卡片本身的视觉树、绑定和布局。

使用实际 SearchPage、同一窗口、无封面和无网络的本地数据，40 条结果实现 40 张 VideoCard，追加和布局约 150 ms、界面线程分配约 12.4 MiB；再追加 360 条达到 400 条时，实现 400 张卡片，追加和布局约 1659 ms，新增界面线程分配约 104.8 MiB。这是批量加载成本测量，不等于正常每次分页都会一次卡住 1.66 秒，但说明页面成本随已加载总量增长。

建议采用首页、收藏和历史已在使用的视口虚拟化方案，并验证结果页顶部覆盖区、滚动位置与自动加载下一页。可参考 [WPF 控件性能及虚拟化说明](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls)。

### E05：同步 Stop 导致换片时界面短暂停顿

位置：`BiliTouch/Views/Windows/PlayerWindow.xaml.cs:713`、`:954`、`:1093`、`:3366`、`:3382`；关闭窗口的后台拆解参考 `:7447` 起。

多个界面入口直接 `_mp.Stop()`，原生停止需要等待内部线程退出。实际调用完整 `PlayLocalFile` 替换正在播放的本地视频时，该同步方法耗时约 99–255 ms，期间界面线程无法处理其他输入。它包括停止、状态重置及新媒体配置，不能把全部耗时当成 Stop 的独立耗时。远端流或超时网络下的持续时间尚未实测。

关闭窗口和独立直播窗口已经存在后台 Stop 的实现；换片及恢复路径尚未统一。建议为同一个 MediaPlayer 建立串行切换流程，将可阻塞的原生停止放到后台，回到界面线程更新控件和挂载媒体，并用请求代次隔离快速连点。避免多个 Task.Run 同时停止、替换或释放同一播放器。

### E06：增强按钮不能代表默认合成路径已执行厂商超分

位置：`BiliTouch/Services/VlcCore.cs:125`、`:135`；`BiliTouch/Views/Windows/PlayerWindow.Enhancement.cs:20`、`:35`；`BiliTouch/Services/VideoEnhancement.cs:13`。

GetVmem 明确不加入 direct3d11、超分和 HDR 输出参数，实例也按单例返回；增强设置变化时默认合成路径仍拿到相同 core，直接 continue。按钮却根据设置值显示“画质增强开启”。源码足以确认当前默认路径没有接入这套原生增强处理，本轮未修改增强配置或 Windows 显卡偏好。

建议状态明确表达当前输出支持的能力；只有确认实际处理链路后再显示增强生效，保留驱动未确认或输出不支持的可理解状态。

### E07：性能指标和检查需要先恢复可信度

位置：`tests/TouchTests/PresentedScrollProbe.cs:141`、`:159`、`:194`、`:214`；`tests/TouchTests/MotionPerformanceTests.cs:174`、`:185`；`.github/workflows/build.yml`。

滚动探针 `_lastClockSeconds` 初始化为 -1 后没有更新，间隔数组始终不加入样本；`_watch` 未订阅，offsets 一直为 0。本轮输出 `frames/s=0.3`、`p95=NaN`，这些字段不能用于定位或验收。独立屏幕条带哈希的 PRESENT 路径没有使用这个数组，仍可提供带采样误差的画面变化频率。

`--motion-performance` 在 7 项断言通过后于 TestFrameOwnership 第 185 行空引用退出。该测试未把 view 挂到可见窗口，而实现已经在隐藏时跳过上传，因此 Source 为 null；其 Format.Send 还保留原来的固定解码指针，与当前每帧交换缓冲的协议不一致。后续密集弹幕、主题时钟及连续变形子检查没有执行，不能称整组通过。新的 `--vmem-buffers` 可见窗口及逐帧重新取缓冲检查已通过。

`--scroll-boundaries` 两次均未通过“new contact cancels scroll commands already waiting for layout”。一个单独简化的取消场景 5 轮均为 0 偏移，尚未证明这是用户可见漂移，也不能据此消掉原完整序列的失败。需要记录完整序列中取消前后的实际偏移，核对精密滚动平滑和断言容差，作为待复核问题保留。

当前自动工作流执行 foundation、rate-limit，尚未执行上述性能组；构建成功与两项基础组通过不意味着播放/动效/滚动检查通过。

## 首页、评论与调度观察

- 完整 MainWindow 的首页夹具：240 条数据，约 40 张已实现卡片，225% DPI、110% 应用缩放。未开跟踪的两轮屏幕条带变化频率为 64.7/64.0 次每秒，变化间隔 P95 为 16.7 ms、最长 33.3 ms。跟踪时两轮为 63.3 次每秒，出现过 75 ms 变化间隔。条带哈希、抓屏损耗及脚本输入节奏使它不等同于显示器 FPS，也不能与旧记录直接认定为优化收益。
- 首页热轮新图片加载为 0，约 3.6 秒内界面线程仍分配约 21.6 MiB；冷轮 12 次图片加载并有 17 次 Gen2 回收，热轮 Gen2 为 0。存在冷缓存/位图压力，尚未用分配栈证明这些回收的具体触发者，不能把全部首页问题归因于封面。
- `--player-comments-scroll` 的分栏、网页评论合成输入与布局检查通过；不播放高负载视频时，WPF 渲染回调约 119/120 次每秒，P95 约 9.4/9.5 ms。这是评论夹具的回调频率，不是边播放 4K 边滚动的实际呈现证据。
- WPF 允许硬件渲染，诊断获得 RenderTier 2；不是全局 SoftwareOnly。GPU Engine 计数器按本次诊断进程筛选，H.264 合成稳定段 3 个有效样本：Intel VideoDecode 约 27.2%、3D 约 10.1%。各编码均有同一 Intel LUID 的解码活动，未见本次进程使用 RTX 3080 解码。
- 16 逻辑处理器上，1466 CPU 毫秒/秒约等于 1.47 个逻辑核心或全机算力口径的 9.2%。总 CPU/GPU 百分比不高，仍可能受单条呈现链路、内存传输和同步约束；本轮不能归因为“CPU/GPU 没调度满”。未发现产品主动固定处理器亲和性或提升进程优先级。
- 显卡设置写的是 Windows 应用级节能/高性能偏好，不是解码器的任意物理显卡绑定。实际设备应继续以解码日志和 GPU 引擎归属确认。本轮没有更改系统显卡策略。
- 线程跟踪包含等待中的线程、启动、预热和两轮滚动，不是精确 CPU 函数占比。已保存原始 trace 供进一步筛选，不把线程池等待样本当成 CPU 热点。

## 验证记录和范围

应用及 TouchTests 的 Release 构建通过：0 错误、0 警告；独立诊断工程最终构建同样通过。

| 检查 | 结果 |
| --- | --- |
| `--vmem-buffers` | 12 项通过，包含帧合并、隐藏恢复、缓冲清理和释放 |
| `--player-lifecycle` | 4 项通过，包含两个播放器及关闭后的存活窗口 |
| `--player-comments-scroll` | 12 项通过 |
| `--motion-performance` | 失败，7 项通过后测试代码空引用，见 E07 |
| `--scroll-boundaries` | 失败，两次相同断言未通过，见 E07 |

本机为 Core Ultra X7 358H、16 逻辑处理器、Intel Arc B390 + RTX 3080，屏幕 3120×2080、120 Hz，225% DPI。采样使用本地 10 秒动态片源；未控制供电/温度和桌面其他进程，因此数据用于本机路径对照，不能外推为所有设备的保证。测量过程中发现有独立安装版进程运行，没有对其操作；其产品版本为 0.3.0，不能从版本号认定它包含当前全部工作树修改。

没有覆盖持续数小时播放、HDR/10-bit、真实 DASH 分离音轨的长时同步、真实网络断续、物理触控板/触屏延迟、边 4K 播放边高密度弹幕及评论滚动、各功耗模式和跨显卡呈现。播放开始的 IsPlaying 和帧上传计数也不能代替首帧可见时间或音画质量验收。

主要证据：

- `artifacts/experience-audit-2026-11-11/build.log`、`probe-build.log`
- `login-wait.jsonl`、`search-realization.jsonl`、`scroll-cancel.jsonl`
- `player-4k*.jsonl`、`player-1080.jsonl`、相应 GPU JSON 和 stderr
- `playback-summary.json`、`gpu-steady-summary.json`
- `home-window.log`、`home-trace.log`、`home.nettrace`、`home-stacks-100.txt`
- `checks.json`、`check-*.log`
- `probe/Program.cs`、`probe/ExperienceProbe.csproj`、`measure-gpu.ps1` 为可复跑诊断源码。

另观察到部分 H.264 停止/换片诊断的 stderr 中有 get_buffer/no frame 信息；当前未证明其造成持续播放失败，保留原日志，不将其隐藏或计入已验证解决的问题。

建议实施顺序：先消除 E01 的死锁，再处理 E03、E04、E05；修正 E06 和 E07 以恢复功能状态及验收可信度；随后以完整窗口为基准推进 E02 的合成链路优化，并补齐播放与滚动同时发生时的持续采样。
