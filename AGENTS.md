# BiliTouch 工程协同指南（AGENTS.md）

> 面向现代强模型与维护者的单一工作入口。**不堆砌繁琐禁令，只沉淀决定上限的架构边界、单一真理源与实证验证方式。**

## 1. 核心工程哲学（Pit of Success）

- **用结构消灭错误，而不是靠文档提醒**：发现一类 Bug 时，优先通过安全默认值、单一物理通道或类型约束让“错误用法写不出来”（例如：`SkinActionIcon` 默认 `Stretch=Uniform`、`ApiClient` 全族仅保留唯一发包口 `SendThrottledAsync`、`AtomicFile` 统一原子写盘）。
- **质疑历史妥协**：遇到带有“暂不支持 / 妥协 / 直接跳过”注释的旧代码时，先审视当初的约束是否已经消失，避免在旧 workaround 上继续打补丁。
- **高内聚体验 + 低耦合实现**：跨页面状态联动走弱引用事件总线 `Svc.UserState`（`UserStateBus`），禁止页面/窗口彼此硬引用；手势/主题/落盘统一走共享基元。
- **不破坏测试反射契约**：`tests/TouchTests` 中有对 `PlayerWindow` 与 `ApiClient` 私有成员的反射验证，重构时优先通过 `partial` 拆分职责，如需改名同步更新对应测试。

## 2. 架构速览（详见 `docs/ARCHITECTURE.md`）

| 层次 | 关键入口与边界 |
| :--- | :--- |
| **API 传输与业务域** | `BiliTouch/Services/ApiClient.cs`（核心传输 + 唯一出站口 `SendThrottledAsync` + 限速/WBI/风控）+ 按域拆分的 `ApiClient.{Feed,PlayUrl,Dynamic,Search,Comments,UserLibrary,Live,Message,Publish}.cs` |
| **全站状态联动** | `Services/UserStateBus.cs`（`Svc.UserState`，弱引用、UI 线程派发、支持 `AlreadyAppliedTo` 去重） |
| **播放器子系统** | `Views/Windows/PlayerWindow.xaml.cs`（生命周期与状态复位）+ `PlayerWindow.{Playback,SeekWatchdog,Quality,ListenMode,FullscreenChrome,InputAndPopups,Actions,Comments,Danmaku,DanmakuSend,Enhancement,Episodes,Recovery,SeamlessMorph,SponsorBlock,Touch,WebLayout,Wheel}.cs` |
| **输入与手势内核** | `Helpers/HorizontalSwipePhysics.cs`（统一阻尼/提交阈值/冻结缓动）、`Helpers/IHorizontalSwipeHost.cs`（统一横滑路由）、`Helpers/PrecisionTouchpadInput.cs` |
| **渲染与持久化基元** | `Helpers/AtomicFile.cs`（防损坏原子写盘）、`Helpers/VisualTreeUtil.cs`（兼容 `ContentElement` 的安全树遍历）、`Helpers/SkinActionIcon.cs`（默认 `Stretch=Uniform` + 皮肤失败自动回退原生矢量） |

## 3. 单一真理源与标准工作流

### 本地离线工具链（外层工作区自带 .NET 10 SDK、NuGet 与 Inno Setup）
```powershell
$env:DOTNET_ROOT = (Resolve-Path "..\.tools\dotnet").Path
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:NUGET_PACKAGES = (Resolve-Path "..\.tools\nuget").Path
```

### 构建与一键回归验证（务必在 `BiliTouch` 仓库根目录执行）
```powershell
# Debug / Release 均须保持 0 错误 0 警告（BiliTouch.sln 已同时纳管主工程与 TouchTests）
dotnet build BiliTouch.sln -c Debug --no-restore
dotnet build BiliTouch.sln -c Release --no-restore

# 一键跑全量离线核心回归（含运行期绑定错误监听与离屏像素探针）
.\tests\TouchTests\bin\Debug\net10.0-windows\TouchTests.exe --ci

# 涉及播放器/本地媒体/动态/触控板改动时，补跑对应真实窗口套件：
.\tests\TouchTests\bin\Debug\net10.0-windows\TouchTests.exe --player-lifecycle
.\tests\TouchTests\bin\Debug\net10.0-windows\TouchTests.exe --release033
.\tests\TouchTests\bin\Debug\net10.0-windows\TouchTests.exe --dynamic
.\tests\TouchTests\bin\Debug\net10.0-windows\TouchTests.exe --precision-touchpad
```

### 版本号与打包发布
- **版本单一真理源**：`BiliTouch/BiliTouch.csproj` 的 `<Version>`（`发布.ps1` 自动读取；发版时同步更新 `App.xaml.cs` `DefaultVersion`、`Publish/installer.iss`、`README.md`、`docs/RELEASES.md` 与 `docs/release-notes/vX.Y.Z.md`）。
- **本地打包（安装器 + 便携包）**：`powershell -ExecutionPolicy Bypass -File 发布.ps1`
- **GitHub Release 发布**（只上传单个 `setup.exe`，详见 `Publish/RELEASE_POLICY.md`）：
  ```powershell
  git tag -a vX.Y.Z -m "BiliTouch X.Y.Z"
  # 仅推送本次 Tag（勿用 --tags，本地含上游 BiliDesk 历史 Tag）
  git push origin main vX.Y.Z
  python Publish/github_release.py --version X.Y.Z --setup Publish/BiliTouch-X.Y.Z-setup.exe --notes docs/release-notes/vX.Y.Z.md
  ```

## 4. 验证红线

- **严禁口头验收**：所有修复必须经过编译与实测验证；无法真机实测的边界（如物理触控板手感、特定显卡驱动）须明确标注验证范围。
- **UI 改动查像素与绑定**：不要只凭 XAML 语法正确就认定可见，优先补充 `RenderTargetBitmap` 非空像素断言与 `PresentationTraceSources.DataBindingSource` 零绑定错误断言（见 `tests/TouchTests/Core/AuditFixesTests.cs`）。
- **文档保持精洁**：架构变更更新 `docs/ARCHITECTURE.md`，版本说明放 `docs/release-notes/vX.Y.Z.md`，不在 `docs/` 根目录平铺一次性排障流水账。
