# 代码审查与流畅度优化 · 2026-11-09

本轮从网页版视频详情页的大小窗切换入手，落实动画、视频帧队列和弹幕热路径的修复，并检查共用滚动、图片、虚拟化、主题和隐藏控件生命周期。审查基线为 `5be3e8f`，开工时工作树无未提交改动。

后续 0.2.15 已继续处理本文末尾第 3 项的弹幕后台重筛，并为第 5 项增加其他播放器存活时的重型回收保护；同时修复音量悬停、倍速浮层与不透明度调节。本文保留首轮 341 项验证的历史范围，后续以 [0.2.15 验证记录](RELEASE-0.2.15-2026-11-09.md) 为准。

## 审查范围

对应用层原有 229 份 C# / XAML 文件进行性能风险扫描，重点逐链路审读播放器的布局与动效、视频宿主与回调、弹幕派发、触控板滚动、图片加载与缓存、列表虚拟化、主题动画、设置保存和内存回收。新增 `SpringMotion.cs` 后应用源码为 230 份。重点链路运行 WPF 窗口、离线视频和压力夹具验证；其余部分属于静态检查，不能据此声明所有页面、显卡和使用场景均已验收。

动效以响应及时、位置与速度连续、同一表面连续变形为本轮目标。参考 [小米澎湃 OS 4 官方展示](https://os.mi.com/)，本项目的弹簧参数是自行设计与验证的参数，没有宣称复用了小米内部曲线。

## 已修复的问题

| 优先级 | 触发条件及原问题 | 本轮处理 | 主要文件 |
| --- | --- | --- | --- |
| P1 | 滚动打断自动收拢时，16ms 跟踪定时器与合成回调可能同时写停靠进度；每帧固定追赶 35% 也使手感依赖刷新率 | 跟手与收拢共用一个合成帧入口，按真实秒数积分；切换目标时保留位置和速度；重复 RenderingTime 只消费一次 | `PlayerWindow.WebLayout.cs`、`SpringMotion.cs` |
| P1 | 原实现从当前表面缩到停靠位；已经停靠时再次计算，终点等于起点，放大方向缺少正确的连续几何 | 固定内联、停靠两个端点，再相对当前布局矩形计算变换，正反方向与打断使用相同坐标系 | `PlayerWindow.WebLayout.cs` |
| P1 | 非 16:9 视频的 Image 会在布局槽内居中，直接缩放 Image 与单独缩放浮层会产生不同的变换起点 | vmem 模式变换整个 VideoSurface，视频、黑边、弹幕、亮度遮罩、加载层及命中区域共同移动 | `PlayerWindow.WebLayout.cs` |
| P1 | 视频宿主已 Dispose，但原生解码回调可能仍在写入；原固定 16MiB 兜底不足以承载 4K RV32 帧 | 每次格式协商独立持有缓冲及回调引用，直到对应原生 cleanup 才释放；旧格式不能释放新格式缓冲；按 32 字节对齐并检查整数溢出 | `VmemVideoView.cs` |
| P1 | 有待上传回调时直接丢弃新帧，导致 UI 忙碌期间保留最早一帧；位图 Lock 还可能阻塞界面线程 | 合并 UI 回调同时更新待显示的最新像素，使用立即返回的 TryLock；拒绝已过期格式的上传，并处理不同像素行跨度 | `VmemVideoView.cs` |
| P1 | 高密度弹幕在一个派发回调内遍历全部到期内容；泳道已满仍创建、测量、挂载再删除控件 | 单次最多处理 128 条或约 2ms；超额到期条目二分跳过，完整时间轴与未来内容保留；创建视觉对象前检查可用位置 | `PlayerWindow.xaml.cs` |
| P2 | 停靠锚点未变化时仍同步 UpdateLayout；动画落位后的淡出阶段重复提交边界和原生背景修复 | 仅锚点真实改变时同步布局，首次落位后等待淡出，不再每帧重做原生修复 | `PlayerWindow.WebLayout.cs` |
| P2 | 品牌反向缩放错误地放在卡片的 TransformGroup 内，抵消卡片自身缩放 | 反向缩放只作用于 BrandMark，卡片保留独立缩放和位移 | `PlayerWindow.WebLayout.cs` |
| P2 | 每帧重建圆角 Geometry；裁剪基于 Image 尺寸，缩放后圆角尺寸变化；放大时右栏留下黑色占位块 | 复用同一个裁剪几何，按变换前尺寸补偿圆角；随实际表面尺寸更新；vmem 停靠占位背景透明 | `PlayerWindow.WebLayout.cs` |
| P2 | 动画结束、布局退出、隐藏或最小化时，跟踪/合成时钟及变换状态缺少统一收尾 | 统一提交几何、复位变换、恢复控件、释放静态帧订阅；全屏/布局退出去掉小窗裁剪 | `PlayerWindow.WebLayout.cs` |
| P2 | 页头每次鼠标移动均重绘不读取鼠标坐标的装扮背景 | 保留进入/离开时的恢复重绘，移除同一区域内每次移动的冗余重绘 | `MainWindow.xaml.cs` |
| P2 | 主题等待合成帧的静态订阅未随覆盖层取消而释放；重复回调可能被当成不同帧 | 取消时退订等待回调，并按实际 RenderingTime 计帧 | `ThemeTransition.cs` |
| P2 | 点播、直播弹幕可能重复消费同一个合成时间戳 | 两条弹幕时钟都去重同一 RenderingTime，看门狗保留 | `PlayerWindow.xaml.cs`、`LiveOverlayRenderer.cs` |

## 原生回调兼容性

实际播放回归曾在重构中抓到一次 cleanup 参数不匹配的原生异常，已修复后重跑。

LibVLCSharp 3.8 的格式与 lock/display 包装器转发其托管 `_videoUserData`，但 cleanup 被直接交给原生，且签名为 `ref IntPtr`，不能把它当成格式协商时设置的同一个 opaque。为确保缓冲生命周期准确，本实现将整组回调按 VLC 3 的原生 ABI 注册，cleanup 使用 `void*` 对应的 `IntPtr`，并由格式持有的 GCHandle 保活回调。依据：[LibVLCSharp 3.8 MediaPlayer 源码](https://github.com/videolan/libvlcsharp/blob/3.8.0/src/LibVLCSharp/Shared/MediaPlayer.cs)、[VLC 3 vmem 输出源码](https://github.com/videolan/vlc-3.0/blob/master/modules/video_output/vmem.c)。

TryLock 的选择依据是 WPF 位图 Lock 可能等待渲染线程的行为，见 [Microsoft WriteableBitmap.Lock 文档](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.writeablebitmap.lock?view=windowsdesktop-10.0)。锁暂不可用时让出本次上传，下一解码帧继续刷新，不用界面线程等待锁。

## 验证与证据

Release 构建完成，0 错误。构建仍有既存的可空引用和测试诊断字段警告，详见 `artifacts/motion-review-build.log`。

| 检查 | 项数 |
| --- | ---: |
| 新增运动、视频缓冲、密集弹幕及主题时钟回归 `--motion-performance` | 21 |
| 真实播放器大小窗、反向打断、裁剪、缩放、最小化和布局退出 `--vmem-morph` | 20 |
| 实际 1080p60 解码及 WPF 位图上传 `--vmem-view --clip=../artifacts/vmem-probe/motion-1080.mp4 --source60` | 7 |
| 默认网页布局 `--web-player-layout` | 33 |
| 默认原生播放颜色/白闪采样 `--web-dock-flicker` | 7 |
| 自动收拢与停靠几何 `--web-morph-motion` | 11 |
| 真实 vmem 播放器挂载 `--vmem-player` | 7 |
| 弹幕设置恢复与移动 `--danmaku-recovery` | 13 |
| 直播回归 `--live-room` | 50 |
| 触控板滚动 `--trackpad-scroll` | 33 |
| 网页控制栏与未读状态 `--web-controls-unread` | 41 |
| 播放加载 `--player-video-load` | 1 |
| 首页封面复用 `--home-cover-reuse` | 13 |
| 列表实现、文字滚动与加载圆环生命周期 `--scroll-realization` | 11 |
| 滚动边界 `--scroll-boundaries` | 9 |
| 页头恢复 `--header-recovery` | 14 |
| 界面回归 `--ui-polish` | 50 |
| **合计** | **341** |

本机专项采样：

- 满泳道下直接尝试 60,000 条滚动弹幕：约 **20.0ms**，当前线程累计分配 **1,440,104 字节**。这是拒绝无位置内容的压力测试，不是普通播放帧时间，也没有用旧报告里的另一轮数据计算加速倍数。
- 真正经 `DispatchDanmaku` 处理 60,000 条同时间到期弹幕：本次约 **1.58ms**，索引直接推进到未来内容，原 60,001 条完整时间轴保留。
- 独立 1080p60 夹具在 1.5 秒窗口内交付约 **60.7 帧/秒**，WPF 位图上传约 **60.7 次/秒**，该窗口合并计数为 0。短窗口计数会有误差；这是解码交付及上传速率，不能等同于屏幕真实呈现帧率。
- 30/60/120/144/240Hz 下，相同 500ms 积分后的弹簧位置和速度一致；这是时间积分的确定性测试。
- 大小窗正反方向采样中，相对固定端点插值的最大布局偏差打印为 **0.000 DIP**；验收阈值为 1.5 DIP。没有把布局坐标测试当成所有显示设备上的零像素误差保证。
- 另测非对齐宽度的像素行拷贝、旧格式待上传时换分辨率、先释放视觉再结束 4K 缓冲生命周期。4K 项检查内存边界与生命周期，没有证明 4K60 性能。

原始日志：`artifacts/motion-review-*.log`。全部 17 组结果及最终程序集哈希：`artifacts/motion-review-summary.json`；默认链路批量结果：`artifacts/motion-review-results.json`。截图：`artifacts/touch-tests/vmem-morph/` 中的 `vmem-morph-docked.png`、`vmem-morph-return.png` 和 `vmem-morph-scaled.png`。截图已检查画面、右栏停靠、黑边及裁剪；自动化及截图仍不能替代物理触控板长时间操作的主观验收。

## 尚未解决的性能与设计限制

1. **默认原生播放还不能让视频表面参与 WPF 连续缩放。** 默认路径仍是播放保持可见、遮罩过渡、末帧提交原生几何；本轮修好了遮罩缩放、曲线和重复开销，但它与实时画面变形仍有区别。实时画面变形需开启「实验性：WPF 合成视频（vmem）」并重开播放器。HwndHost 的变换限制见 [Microsoft WPF/Win32 互操作文档](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-and-win32-interoperation)。
2. **vmem 的软件解码、像素转换、内存拷贝及上传成本仍存在。** 本轮保留实验性默认关闭；4K、高码率、不同显卡和长时间播放还需单独验证。若要将实时变形推广为默认，下一项架构工作应评估视频帧的 GPU 合成路径，再按真实上屏帧间隔、音画同步、画质增强兼容性验收。
3. **大弹幕库排序与规则重筛仍在 UI 接入路径。** `LoadDanmakuAsync` 的排序/过滤和 `RefilterDanmaku` 在复杂屏蔽规则、大列表或连续修改设置时仍可能占用界面线程。本轮限制的是播放时派发和视觉生成成本；下一步需做设置快照、后台筛选与过期结果隔离，保留修改规则即时生效的行为。
4. **主题切换的大图快照和全树资源刷新仍有一次性成本。** 已有像素预算与降级，并修复取消时的帧订阅；本轮没有用降低截图分辨率掩盖这项成本。
5. **关闭播放器后的全量 GC、LOH 压缩及工作集回收仍是进程级操作。** 即使由后台任务触发，也可能打断另一个正在播放的窗口或让紧接着打开的内容重新缺页。当前未改这条产品行为，应在多播放器与快速连续开关场景下单独 A/B。

图片加载已有下载/解码并发限制和缓存复用；列表已有虚拟化与视口附近图片队列；加载圆环已按可见/挂载状态释放动画；普通切页的内存回收已有压力门槛；设置已有延迟保存。本轮复查并运行相关回归，没有把这些既有机制重复列为本次新增收益。
