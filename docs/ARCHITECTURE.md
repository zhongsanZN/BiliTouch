# BiliTouch 系统架构总览

> 技术栈：**WPF · .NET 10 (`net10.0-windows` / x64) · LibVLC 3.0.24 · Direct3D11/D3D9 合成 · WebView2**

## 1. 顶层分层

```
BiliTouch/
├── Models/          领域模型、动态解析/排序纯逻辑（零 UI 依赖）
├── Services/        网络传输(ApiClient.*)、会话/设置/下载/播放调度、全站状态总线(UserStateBus)
├── Helpers/         输入与横滑物理、视觉树/原子写盘/主题与皮肤、GPU 呈现与混流(DashRemuxer)
├── ViewModels/      各页面 MVVM 状态与异步加载（通过 Svc.UserState 弱订阅联动）
├── Views/
│   ├── Windows/     PlayerWindow.* / LiveRoomWindow.* / BrowserWindow / CoinWindow
│   ├── Pages/       Home / Follow / Mine / Messages / Search / Settings / Article / DynamicDetail / StarParty
│   └── Controls/    VideoCard / DynamicFeedCard / EmotePickerPanel / CommentText
└── Themes/          Colors.{Light,Dark}.xaml / Icons.xaml(冻结矢量) / Styles.xaml
```

## 2. 核心架构不变量（Pit of Success）

### 2.1 网络与风控传输核（`Services/ApiClient.*.cs`）
- **单一发包物理通道**：所有 API 请求均经 `ApiClient.SendThrottledAsync(request, ct, expectedAccount)` 发出；全族仅保留此唯一 1 处底层 `_http.SendAsync` 调用。
- **并发与风控一致性**：全局 `ApiRateLimiter` 令牌桶在命中 `-412` 冷静期时暂停补充防解封踩踏；`_wbiGate` / `_buvidGate` / `_writeReadyGate` 串行化冷启动签名与指纹预热；`expectedAccount` 在限速排队后二次核验当前账号，彻底杜绝切换/退出账号期间的跨账号串写。
- **按域解耦**：`ApiClient.cs` 专注传输、签名与 JSON 基元，其余业务拆分为 `Feed` / `PlayUrl` / `Dynamic` / `Search` / `Comments` / `UserLibrary` / `Live` / `Message` / `Publish` 九个独立领域文件。

### 2.2 全站状态联动（`Services/UserStateBus.cs`）
- 通过 `Svc.UserState` 提供弱引用、UI 线程自动派发的领域事件通道：`FollowChanged`、`FavoriteChanged`、`WatchLaterChanged`、`HistoryRecorded`、`DynamicPublished`、`DynamicMutated`。
- 播放器、直播间、UP 主空间、首页/动态卡片只需发布事件，所有已缓存的页面与 ViewModel 自动同步状态或标脏刷新，消除页面间直接耦合与内存泄漏。

### 2.3 播放器架构（`Views/Windows/PlayerWindow.*.cs`）
- **职责拆分**：
  - `PlayerWindow.xaml.cs`：窗口生命周期、媒体状态契约（`PerMediaStateFields` / `PersistentFields` + Debug 自动覆盖度检查）、统一复位 `ResetForNewMedia()` 与资源清理 `OnClosed()`。
  - `PlayerWindow.Playback.cs`：在线起播链、离线缓存起播及后台无感在线增强（`LoadOfflineOnlineContextAsync`——联网时在后台静默加载评论与弹幕，绝不阻塞或干扰本地播放）、短视频本地合流、EOF 尾段纯视频单流收尾。
  - `PlayerWindow.SeekWatchdog.cs`：Seek 确认看门狗、尾部进度外推、音频自愈、Seek 静音与长按 2x 倍速。
  - `PlayerWindow.Quality.cs` / `ListenMode.cs` / `FullscreenChrome.cs` / `InputAndPopups.cs` / `Danmaku.cs` / `Comments.cs` / `Actions.cs` / `Recovery.cs` / `SeamlessMorph.cs` / `WebLayout.cs` / `Host.cs`。
- **双呈现路径与单窗口双宿主内嵌**：支持传统原生 `HwndHost` 与低开销 `vmem`（`GpuVideoPresenter` D3D11→D3D9 共享纹理零拷贝合成）；支持按显卡偏好选择硬件解码与厂商超分；桌面竖屏视频提供可选并排优化（左视频 + 右评论 + 最右信息栏）；平板竖屏态（`Responsive.Posture == TabletPortrait`）通过单树双宿主将 `PlayerWindow.RootGrid` 与 `LiveRoomWindow.Root` 直接挂载到 `MainWindow.EmbeddedPlayerHost` 实现单窗口安卓式三段播放与全屏半屏评论抽屉。

### 2.4 输入、横滑、响应式姿态与渲染统一基元
- **触屏与触控笔**：在 WPF 初始化前启用 `WM_POINTER` 输入栈（`EnablePointerSupport=true`）。手势坐标统一读取系统已完成屏幕旋转与 DPI 变换的 `GetTouchPoint` / `GetPosition`（禁止对 `PointerStylusDevice` 调用未旋转原始 HID 坐标的 `GetStylusPoints`）；手指触屏仅走 `PreviewTouch*` 路由，触笔处理入口显式跳过 `TabletDeviceType.Touch`，标准控件仍可接收框架生成的兼容点击。
- **响应式宽度与平板姿态**：`Helpers/Responsive.cs` 维护窗口级 `WidthClass` 与正交 `LayoutPosture`（`Desktop` / `TabletPortrait`，默认关闭，带宽高比与宽度迟滞防抖，`DeviceProfile` 识别 Surface 一次性引导），驱动 `MainWindow` 底部导航壳、两列卡片墙及各页面竖屏重排；`FluentWindow` 监听 `DisplaySettingsChanged` 响应平板物理旋转。
- **横滑统一内核**：`Helpers/IHorizontalSwipeHost.cs` 统一主窗口触控板/横向滚轮路由；`Helpers/HorizontalSwipePhysics.cs` 提供全站唯一的非线性橡胶阻尼、3/8 切页阈值与冻结 `CubicEase` 曲线。
- **安全与可靠性基元**：
  - `Helpers/AtomicFile.cs`：唯一临时文件 + 原子覆盖重命名，用于会话、设置、封面缓存、混流与离线下载。
  - `Helpers/VisualTreeUtil.cs`：统一兼容 `Visual` 与 `ContentElement`（`Run`/`Hyperlink`）的树遍历。
  - `Helpers/SkinActionIcon.cs`：静态构造覆写 `Stretch` 默认值为 `Stretch.Uniform`，全局 `Icons.xaml` 矢量统一 `po:Freeze="True"`，皮肤资源异常时自动回退原生矢量。
  - `Views/FluentWindow.cs`：无边框窗口圆角、云母/亚克力统一调度、最大化安全边距及按目标显示器真实 DPI 裁剪初始尺寸。

## 3. 测试与自检体系（`tests/TouchTests`）

- **分层目录**（已纳入 `BiliTouch.sln`，一次构建同时校验主工程与测试工程）：
  - `Core/`：核心架构不变量、账号/风控隔离与离线 CI 回归套件；
  - `Suites/`：窗口/布局/手势/动效/主题/播放体验专项回归套件；
  - `Releases/`：历史发版定向防回退套件；
  - `Probes/`：诊断与抓帧探针工具（非默认 CI）。
- `--ci`：一键运行全部核心离线回归套件（基础账号隔离、限速单通道、全仓审计与运行时健康探针、未读同步、解码与缓冲、弹幕过滤与恢复、更新回退、VSR、上游稳定性同步、动态排序、版本契约）。
- **运行时自检探针（`Core/AuditFixesTests.cs`）**：
  - 唯一 `_http.SendAsync` 发包口结构断言；
  - `RenderTargetBitmap` 离屏渲染像素探针（防图标画偏/隐身）；
  - `PresentationTraceSources.DataBindingSource` 静默绑定错误零容忍监听。
