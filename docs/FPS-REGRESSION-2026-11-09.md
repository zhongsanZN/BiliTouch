# 帧率回归排查：当前版本 vs desk 版（2026-11-09）

## 用户现象

1. 卡顿掉帧比之前版本少了，但帧数要明显低于 desk 版本。
2. 不论什么视频，页面帧数低的感觉都很明显。
3. 网页版布局因有小窗动效，卡顿会更多；大体上仍是那个低帧数的感觉。

结论：**这不是视频解码/输出链路的回归，是 WPF 浮层被一个 30ms 的定时器钉住了刷新节奏。** 网页版布局再叠加一条「截图替身 + 隐藏原生窗口 + 两次 DwmFlush」的过渡链，造成额外瞬时冻结。

---

## P0（主因）：弹幕/浮层驱动时钟从 `CompositionTarget.Rendering` 降级为 16ms `DispatcherTimer`

### 代码差异

| | desk 版 | 当前版 |
|---|---|---|
| 挂钟 | `CompositionTarget.Rendering += OnDanmakuRendering` | `new DispatcherTimer(16ms, DispatcherPriority.Render, OnDanmakuRendering, Dispatcher)` |
| 位置 | `artifacts/bilidesk-171-src/BiliDesk.Views/PlayerWindow.xaml.cs:4010-4025` | `BiliTouch/Views/Windows/PlayerWindow.xaml.cs:5962-5976` |
| 逐帧体 | `:4028-4065` | `:5990-6031`（逻辑完全相同：本地 Stopwatch + `TranslateTransform`，无原生调用） |

直播间也用了同一个 16ms 定时器：`BiliTouch/Helpers/LiveOverlayRenderer.cs:47`。

### 引入原因

`docs/RELEASE-0.1.6-2026-11-05.md:22-26`、`docs/RELEASE-0.1.7-2026-11-06.md:12`：0.1.6 为修「窗口不可见 / 被遮挡时 `CompositionTarget.Rendering` 会停，弹幕停在画布右缘外 → 弹幕看不见」，把驱动改成了 `DispatcherTimer`。

**这个修复选错了层。** 要修的是「弹幕初始位置不能预置在画布外」和「窗口恢复可见时补一次推进」，而不是把整条浮层时钟从合成节奏降级成系统定时器。

### 机制：为什么标称 16ms 实际是 ~30ms

`DispatcherTimer` 走 Windows 定时器队列，默认时钟粒度约 15.6ms；标称 16ms 的间隔实际落在 30ms 一跳。而 `CompositionTarget.Rendering` 由 DWM 合成循环驱动，跟屏幕刷新率走。

浮层（`VideoView.Content`，`PlayerWindow.xaml:816-824`）是 LibVLCSharp.WPF 建立的**独立顶层 HWND**，弹幕、控制栏、遮罩全在里面（`:838`、`:1044`、`:853`）。WPF 按需渲染：只有视觉树变化才提交新帧。于是 30ms 一跳的弹幕位置更新，把**整个浮层窗口的呈现帧率一起钉在 ~33-55Hz**，即使视频 HWND 本身仍在正常出帧。

### 实测（本机，同一可视树，只切驱动时钟）

探针：`artifacts/render-clock-probe/`（40 条运动弹幕 + 一块每 tick 移动的大色块，保证每次更新都改变屏幕内容）。屏幕捕获用 `ffmpeg -f lavfi -i ddagrab=framerate=120 ... -f framemd5`，数真实不同画面数。

| 指标 | `timer`（当前版） | `rendering`（desk） | 倍数 |
|---|---:|---:|---:|
| 逻辑更新率 `updates/s` | 55.4 | 167.6 | 3.0× |
| 更新间隔 p50 | 30.15 ms | 8.35 ms | 3.6× |
| 更新间隔 p95 | 32.48 ms | 9.73 ms | 3.3× |
| **屏幕真实上屏变化/秒** | **36.8** | **83.4** | **2.3×** |
| WPF 合成回调 `composition/s` | 121.8 | 167.6 | — |

第一组纯计时对照（无捕获）：`timer` 39.2 updates/s、p50 30.17ms；`rendering` 119.8 updates/s、p50 8.34ms。

原始数据：`artifacts/render-clock-probe/present-timer.framemd5`、`present-rendering.framemd5`、`present-timer.txt`、`present-rendering.txt`、`timer.result.txt`、`rendering.result.txt`。

**关键读法**：`composition/s` 两版都在 120Hz 以上，说明 WPF 合成循环没被限住；差异全部来自「内容多久变一次」。当前版合成循环空转了一大半，用户看到的就是「页面帧数低」。

### 对应用户三句话

- **「卡顿掉帧比之前版本少了」**：0.1.6 之前的 `CompositionTarget.Rendering` 在窗口被遮挡、最小化、vout 重建时会不规则停摆，弹幕出现突发大间隔（正是 0.1.6 修的那个 bug）。改成均匀 30ms 定时器后，突发停顿没了 → 卡顿感减少。
- **「帧数明显低于 desk」**：从 83.4 上屏帧/秒掉到 36.8，差 2.3 倍。
- **「不论什么视频都很明显」**：与码率、分辨率、编码格式、CDN 完全无关，纯 UI 时钟，所以所有视频一视同仁。

---

## P1：网页版布局小窗动效的额外开销

### 播放中的过渡走的是「截图替身」路径，与文件头设计相反

`BiliTouch/Views/Windows/PlayerWindow.WebLayout.cs:27-33` 的设计是「过渡期间原生窗口一次都不动，由纯 WPF 遮罩承担动效，动画结束帧才一次性移动原生窗口」。但 `:280` 在 `_mp.IsPlaying` 时改走 `TryWebFrameTransitionAsync()`（`:424-455`），后者用 `Helpers/VideoFrameTransition.cs` 开一个**独立透明分层窗口播放静态截图**。

调用链：

```
XAML:792 ScrollChanged
→ WebLayout.cs:264-271 OnWebLeftScrollChanged
→ :255-260 UpdateDockForOffset（滞回阈值）
→ :277-282 SetWebDockTarget
→ :280 播放中 → TryWebFrameTransitionAsync (:424-455)
   → :429 VideoFrameTransition.CaptureAsync (:37-48)
        TakeSnapshot 写 1280px PNG + BitmapImage 解码 + 最多 180ms 轮询
   → :434 PrepareWebDockAnchor → :381 PlayerStage.UpdateLayout() 强制布局
   → VideoFrameTransition.RunAsync (:49-82)
        :56-57 新建 AllowsTransparency 分层 Window 并 Show（截图上屏）
        :58 WaitForPresentationAsync #1 → :23-35 等 2 个 RenderingTime + DwmFlush()
        :61 hideNative → WebLayout:439 VideoView.Visibility=Hidden
        :62-72 380ms 弹簧（每帧重采样 HighQuality 位图）
        :72 commitNative → WebLayout:440-451
             :443 ApplyWebVideoBounds → 一次原生 SetWindowPos + vout 交换链重建
             :446/:449 两次 VideoSurface.UpdateLayout()
             :448 VideoView.Visibility=Visible
             :450 StartVideoBackdropFix → xaml.cs:9844-9858 120ms×25 拍 = 3s 守护
        :75 SetWindowPos 重新置顶
        :76 Task.Delay(180)
        :77 WaitForPresentationAsync #2（又一次 DwmFlush）
        :83 Dispose 关分层窗口
→ :454 finally 恢复弹幕 / 控制栏
```

### 耗时与冻结窗口

| | 正常 | 最坏 |
|---|---|---|
| 截图写盘 + 解码 | 10-50ms（UI 线程） | 180ms 轮询上限后返回 null → 兜底 legacy 220ms + 140ms |
| 两次 `WaitForPresentationAsync` | ≈100ms（含 2 次 `DwmFlush()`） | 各 1s `WaitAsync` 上限 → 2s |
| 380ms 弹簧 | 380ms | 380ms |
| commit 强制布局 ×3 + vout resize | 10-50ms | 可尖峰 |
| `Task.Delay(180)` | 180ms | 180ms |
| 3s 守护定时器 | 3s 内 25 次原生调用 | 滚动频繁时反复重置，接近常驻 |
| **画面被静态截图替代** | **≈0.65-0.8s** | **≈3.1s+** |
| 原生 `VideoView` Hidden | ≈0.43s | — |

期间 `UpdateAlternateVideoControls`（`:469-482`）把 `DanmakuCanvas`、`ControlBar`、`WebMiniControls` 全部隐藏（`:477`、`:479`、`:480`），截图本身也不含弹幕（`TakeSnapshot` 取解码帧）。用户看到的是「画面定住 → 缩放 → 弹幕突然消失又回来」。

### 稳态开销（网页布局独有，普通布局完全没有）

- 每次 `WebLeftScroll.ScrollChanged` → `UpdateWebMeasurements`（`:240-253`）→ `:252 ApplyWebVideoBounds()` → `:362→:381 PlayerStage.UpdateLayout()` **强制同步布局**；即使 `Nearly`（`:417-422`）挡住 Width/Height 写入，强制布局仍然每次发生。
- `:346 BrandMark.LayoutTransform = new ScaleTransform(...)` 每帧重建 → 强制 measure/arrange（legacy 路径）。
- `:308` 逐帧 `UpdateAlternateVideoControls()` 反复写 `Visibility`。
- 浮层 HWND 内 `DanmakuCanvas` / `ControlBar` / `DockTransitionCover` 每帧独立合成。
- `:452` 若滚动期间目标被改过，`BeginInvoke` 再起一轮过渡 → 「一轮未平一轮又起」。

---

## 被实测否定的假设：画质增强 `super` / VSR 降低视频帧率

当前配置确实开启了增强：`%LocalAppData%\BiliTouch\settings.json:2-6` 为 `ExperimentalVideoEnhancement=true`、`VideoEnhancementActive=true`、自动厂商增强、选定 NVIDIA 适配器。日志 `%LocalAppData%\BiliTouch\video-enhancement.log:419-421` 明确记录 `True:super...` / `Using Super Resolution scaler` / `turning VSR ON` / NVIDIA D3D11VA，且 `:343-418` 反复出现 VSR ON/OFF 切换。

代码：`BiliTouch/Services/VideoEnhancement.cs:7-13` 在增强开启时追加 `--verbose=2 --vout=direct3d11 --d3d11-upscale-mode=super --d3d11-hdr-mode=auto`；`BiliTouch/Services/VlcCore.cs:99-107` 把它们塞进全局 LibVLC 参数。desk 版只有 `--intf=dummy --no-osd --network-caching`（`artifacts/bilidesk-171-src/BiliDesk.Services/VlcCore.cs:42-44`）。

**但 A/B 实测否定它作为帧率主因。** 探针 `artifacts/fps-ab-probe/`（读 LibVLC 自己的 `MediaStats.DisplayedPictures` / `LostPictures`，比抓屏更客观），同片源 640x360@60 放大到 1264x721，10 秒，四组串行：

| `--d3d11-upscale-mode` | displayed/10s | steady rate | lost | late/drop | d3d11 重建 |
|---|---:|---:|---:|---:|---:|
| `default` | 579 | 59.3 fps | 0 | 0 | 1 |
| `linear` | 579 | 59.3 fps | 0 | 0 | 1 |
| `processor` | 564 | 57.7 fps | 0 | 0 | 1 |
| `super` | 579 | 59.3 fps | 0 | 0 | 1 |

四组一致（processor 的 15 帧差异在起播阶段，稳态同为 59.9fps）。**视频解码与呈现链路没有被超分限帧**。

它仍然是需要处理的次要项，但不是「所有视频都低帧」的解释：

- VSR 反复 ON/OFF（日志 `:343-418`，1 秒级切换）会带来 GPU/驱动侧抖动。
- `--verbose=2` 让海量 LibVLC 日志进入托管回调 `VideoEnhancement.cs:24-37`，命中关键字时同步 `File.AppendAllText` 写盘并 `Changed?.Invoke()` → 播放器 UI 线程 `Dispatcher.BeginInvoke(UpdateEnhancementButton)`（`PlayerWindow.Enhancement.cs:16`）。

---

## 放大器与次要项（按优先级）

1. **强制最大化**：`BiliTouch/Services/PlayerService.cs:172-196`（复用路径）、`:229-234`（新建路径）无条件 `WindowState.Maximized`；desk `artifacts/bilidesk-171-src/BiliDesk.Services/PlayerService.cs:53-85` 只还原最小化、保留用户尺寸、新建直接 `Show()`。两版 XAML 初始都是 1280×760。更大交换链 / 浮层面积会放大上面所有成本，但单独不足以造成全局明显降帧。**A/B 必须固定窗口尺寸**，否则测不准。
2. **`VlcPlacePictureGuard`**：`BiliTouch/Services/VlcCore.cs:68-77` 无条件安装；`Helpers/VlcPlacePictureGuard.cs:359-395` 改写 D3D11 插件 IAT，正常调用也要经过托管 Shim（4 次 `Marshal.ReadInt32` + `Interlocked` + 转发）。vout 几何计算的热路径开销，量级小但可测。
3. **弹幕总量 6,000 → 60,000**：`docs/PERFORMANCE-AUDIT-2026-11-06.md:11-27` 已实测极端突发下 1.9-2.3s UI 同步耗时、480MiB 累计分配。不是稳态帧率主因，但会在热门视频起播 / 改屏蔽设置时造成一次大卡顿。
4. **`LoadingRing` 隐藏后仍无限旋转**：同上 `:29-42`，旧问题，空闲 CPU 占用。

## 已排除（避免走回头路）

- **原生库逐字节一致**：当前安装版 `C:\Program Files\BiliTouch\libvlc\win-x64` 与 `artifacts/bilidesk-171-app\libvlc\win-x64` 的 `libvlc.dll`、`libvlccore.dll`、`plugins\video_output\libdirect3d11_plugin.dll`、`plugins\codec\libavcodec_plugin.dll` SHA-256 全部相同；均为 LibVLC 3.0.24 + LibVLCSharp(.WPF) 3.8.0（`BiliTouch/BiliTouch.csproj:87-96`）。
- **硬解正常**：日志 `video-enhancement.log` 与 `artifacts/vsr-probe/o6-super.txt` 均为 `Using D3D11VA (NVIDIA GeForce RTX 3080 / Intel Arc B390)`。两版 VOD 初始构造都未显式设 `EnableHardwareDecoding`（desk `:2788-2791`、Touch `:1828-1836`），Touch 仅增强切换后设 true（`PlayerWindow.Enhancement.cs:42`）。
- **媒体主路一致**：两版都强制 `:demux=avformat`、`input-slave` 挂音轨、同一套 `BufferPolicy`（desk `:5979-6016`、Touch `PlayerWindow.xaml.cs:2030-2080`）。没有新增 deinterlace、fps filter、`clock-jitter` / `clock-synchro`、video-filter 或 `SetVideoCallbacks`。
- **视频宿主一致**：desk `PlayerWindow.xaml:435-436` 与 Touch `PlayerWindow.xaml:802-824` 都是 `LibVLCSharp.WPF VideoView` / `HwndHost`，不是新版改成 WPF 逐帧拷图。
- **DPI / 软件缩放一致**：两版 `app.manifest` 都 PerMonitorV2；`ScaleRoot` 都只返回 `InfoPanel`（desk `:627`、Touch `:49`），不缩视频。当前 `HardwareAccelerationEnabled=true` → `RenderMode.Default`（`Helpers/RenderPolicy.cs:9-15`），没有落到软件渲染。

---

## 修复方案（按优先级，带文件与行号）

### 1. 弹幕/浮层主时钟改回 `CompositionTarget.Rendering`，用正确方式修 0.1.6 的 bug

改动集中在 `BiliTouch/Views/Windows/PlayerWindow.xaml.cs:5962-5976`，两个方法：

```csharp
// 主时钟：跟 DWM 合成节奏，回到 desk 的做法
private void StartDanmakuClock()
{
    if (_danmakuClockAttached || _closing) return;
    _danmakuClockAttached = true;
    CompositionTarget.Rendering += OnDanmakuRendering;
    StartDanmakuWatchdog();          // 只兜底，不当主时钟
}

private void StopDanmakuClock()
{
    if (!_danmakuClockAttached) return;
    _danmakuClockAttached = false;
    CompositionTarget.Rendering -= OnDanmakuRendering;
    StopDanmakuWatchdog();
}
```

配套三处（这才是「弹幕看不见」的正解）：

- **`:5900-5903` `RenderRolling`**：不要预置 `new TranslateTransform(canvasWidth, 0)`。按 `DanmakuClock()` 与 `SpawnClock` 直接算出当前应处的 X：
  ```csharp
  // 新弹幕 elapsed=0，RollingX 直接给「刚从右缘入场」的精确位置；
  // 即便一帧 Rendering 都没来，弹幕也在正确位置，不会停在画布外看不见。
  var translate = new TranslateTransform(
      Helpers.DanmakuMotion.RollingX(canvasWidth, width, 0, _danmakuScrollSeconds), 0);
  ```
  即便一帧 Rendering 都没来，弹幕也在正确位置，不会「停在右缘外看不见」。
- **新增低频看门狗**（比如 250ms `DispatcherTimer`，优先级 `Background`）：仅当 `_flying.Count > 0` 且距上次 Rendering 超过阈值时补调一次 `OnDanmakuRendering`；窗口 `Loaded` / `ContentRendered` / `IsVisibleChanged` / `Activated` 时各补一次。它只覆盖「合成停摆」的窗口期，不承担逐帧推进。
- **`Helpers/LiveOverlayRenderer.cs:47`** 同样改：`CompositionTarget.Rendering` + 看门狗；`Emit`（`:106`）的初始 X 也按当前时间直接算，别预置在右缘外。

保留 0.1.6 已做对的「画布无尺寸时进 pending 队列」预加载（`:71-78`、`:119-123`），那部分与时钟无关。

**验收**：`artifacts/render-clock-probe` 的 `PRESENTED` 从 ~37/s 回到 ~83/s；真机检查最小化/遮挡恢复后弹幕不消失、不堆在右缘。

### 2. 网页版布局过渡：砍掉截图路径，回到纯 WPF 遮罩

按 `PlayerWindow.WebLayout.cs`：

- **`:280`** 删掉播放中走 `TryWebFrameTransitionAsync()` 的分支，无条件 `:281 / :283 StartLegacyWebDockTarget(target)`。整条 `TryWebFrameTransitionAsync`（`:424-455`）与 `Helpers/VideoFrameTransition.cs` 下线。
- **`:304-305`** 把 220ms 三次缓动换成 380ms 弹簧，保留观感：
  ```csharp
  var t = Math.Clamp(_webAnimationElapsed / 380, 0, 1);
  var eased = 1 - Math.Exp(-7 * t) * (Math.Cos(11 * t) + .42 * Math.Sin(11 * t));
  ```
- **`:346`** `BrandMark.LayoutTransform = new ScaleTransform(...)` 换成缓存的 `ScaleTransform` 就地改 `ScaleX/ScaleY`（在 `:91` 装进 `TransformGroup`），消除每帧强制布局。
- **`:308`** 删掉 `OnWebVideoFrame` 里逐帧 `UpdateAlternateVideoControls()`；`moving` 整段恒定，`:291` 起、`:466` 收各调一次即可。
- **`:252` / `:381`** `PlayerStage.UpdateLayout()` 只保留最终 commit 那一次；滚动稳态不要每事件强制布局。
- **`:450`** 不再调 `StartVideoBackdropFix()`（`xaml.cs:9879`，120ms×25 拍守护）；在 `StopWebVideoAnimation:463` 的 `ApplyWebVideoBounds()` 之后补一次 `ApplyHwndHostBlackBackground()`（`xaml.cs:9794`）即可覆盖那一次 resize 的白边。
- **`PlayerWindow.xaml` 不用改**；`VideoView` 全程 `Visible`，原生窗口只在动画结束帧做一次 `SetWindowPos`（`:315` / `:463`），与文件头 `:27-33` 设计一致。

**验收**：每轮过渡只剩 380ms 纯 WPF 矩阵动画 + 1 次原生 resize + 140ms settle；无 PNG 编解码、无分层窗口、无 `DwmFlush`、无 Hidden/Visible、无 3s 守护。

### 3. 画质增强收敛为显式选项

- `BiliTouch/Services/VideoEnhancement.cs:13` 去掉 `--verbose=2`（或只在诊断开关下加）。
- `VideoEnhancement.cs:24-37` 日志写盘改异步/批量，`Changed?.Invoke()` 加节流，避免 VSR ON/OFF 频繁时打 UI 线程。
- `BiliTouch/Services/SettingsStore.cs:37-38` 把 `ExperimentalVideoEnhancement` 默认改为 `false`，「自动厂商增强」不再作为开箱默认值；用户显式开启才走 `super`。
- 若要保留超分收益，至少把 `processor` 与 `super` 做成用户可选档，并在切换时提示需重启（当前 `VlcCore.Get()` 按 profile.Key 缓存多个 LibVLC 实例，`VlcCore.cs:29/55-57/107`，反复切换会累积实例）。

### 4. 次要项

- `BiliTouch/Services/PlayerService.cs:172-196, :229-234`：默认最大化保留产品语义，但在性能 A/B 与用户反馈里明确它是放大器；若允许，改为「记住用户上次的窗口状态」。
- `BiliTouch/Services/VlcCore.cs:68-77`：`VlcPlacePictureGuard.Install()` 收敛到只在 d3d11 `processor` / `super` 模式安装（它修的是这两种缩放模式下的 `0xC0000094`，`linear` 用不到）。
- `docs/PERFORMANCE-AUDIT-2026-11-06.md:11-27`：给 `DispatchDanmaku` 单次派发加条数/耗时预算，`AddDanmaku` 在创建视觉对象前先判泳道。

---

## 验证顺序（建议）

同一视频、同一窗口尺寸、同一清晰度，逐项 A/B，每次只动一个变量：

1. 关弹幕 → 确认「低帧感」是否随浮层动画一起消失（隔离 P0）。
2. 只改 `StartDanmakuClock` 回 `CompositionTarget.Rendering` → 用 `artifacts/render-clock-probe` 的 `PRESENTED` 复核。
3. 关「画质增强」→ 复核 VSR ON/OFF 抖动与日志开销（预期对稳态帧率影响很小）。
4. 关「网页版布局」→ 复核 `WebLeftScroll` 强迫布局与小窗过渡冻结。
5. 最后才看 `VlcPlacePictureGuard`、最大化、弹幕总量。

## 本轮新增探针与原始数据

| 路径 | 用途 |
|---|---|
| `artifacts/render-clock-probe/` | 两种驱动时钟的逻辑更新率 + `ddagrab`/`framemd5` 真实上屏帧数对照 |
| `artifacts/fps-ab-probe/` | 读 `MediaStats.DisplayedPictures` / `LostPictures`，对照 `default/linear/processor/super` 四种缩放 |
| `artifacts/vsr-probe/motion.mp4`、`motion-640.mp4` | 持续运动 60fps 夹具（此前静态夹具测不出帧差） |

---

## 修复实施（同日完成）

按上面 P0 / P1 / P3 落地，**没有改视频解码链路、没有改原生库、没有改 XAML 布局**。

### 改动文件

| 文件 | 改动 |
|---|---|
| `BiliTouch/Views/Windows/PlayerWindow.xaml.cs:5900-5903, 5962-6005` | 弹幕主时钟 `16ms DispatcherTimer` → `CompositionTarget.Rendering`；新增 250ms 低频看门狗（`Background` 优先级，仅在合成停摆 >200ms 时补一次）；`RenderRolling` 初始位置改用 `RollingX(..., 0, ...)` 并补注释 |
| `BiliTouch/Helpers/LiveOverlayRenderer.cs` | 直播间同样改主时钟 + 看门狗；`Tick` 改签名并更新时间戳；`EnsureClock/StopClock` 改挂/解绑 `Rendering`；文件头注释更正 |
| `BiliTouch/Views/Windows/PlayerWindow.WebLayout.cs` | 删除 `TryWebFrameTransitionAsync` 整条截图替身路径及相关 4 个字段；`SetWebDockTarget` 无条件走 `StartLegacyWebDockTarget`；动画 220ms 三次缓动 → 380ms 弹簧；删逐帧 `UpdateAlternateVideoControls()`；品牌块 `LayoutTransform` 改成缓存 `ScaleTransform` 实例就地改 `ScaleX/ScaleY`；`ApplyWebVideoBounds()` 后补一次 `ApplyHwndHostBlackBackground()` 盖 resize 白底 |
| `BiliTouch/Services/VideoEnhancement.cs` | `Options` 去掉 `--verbose=2`；日志写盘改 `ThreadPool` 异步；`Changed?.Invoke()` 只在状态字符串真变化时触发 |
| `tests/TouchTests/WebPlayerLayoutTests.cs`、`WebControlsUnreadTests.cs`、`WebDockFlickerTests.cs` | 断言跟随新实现：过渡状态由 `_webRendering` 判定；新增「原生 VideoView 全程可见」「过渡期零白闪」断言；稳态颜色改用正确率判定（屏幕级 `GetPixel` 采样有固定时序离群值） |

`Helpers/VideoFrameTransition.cs` 保留在仓库但已无任何引用，可从 git 历史取回做 A/B 对照。

### 验证

- 主项目 + 测试项目 `dotnet build -c Release -r win-x64`：**0 错误**（仅既有 CS8602 等警告）。
- 六项定向回归**全绿，144 项检查**：`--danmaku-recovery` 13、`--web-player-layout` 33、`--web-dock-flicker` 6、`--web-controls-unread` 41、`--live-room` 50、`--player-video-load` 1。
- `--player-video-load` 关键数字：**视频播放中评论滚动 117.6fps（median 8.5ms），无视频 116.4fps（median 8.6ms）** —— 视频在播不再拖低页面帧率。
- 便携版与安装器载荷各跑一次 `--verify-publish`（隔离配置、静音播本地 MP4）：两份 `passed: true`，首帧 400ms/151ms → 651ms 正常推进，D3D11VA 硬解正常。

### 产物（0.2.12，重打包）

- 安装器：`Publish/BiliTouch-0.2.12-setup.exe`（41.7 MB）
- 便携版：`Publish/BiliTouch-0.2.12-portable.zip`（115.2 MB，自包含单文件，解压即用）
- 校验记录：`Publish/verify-portable.json`、`Publish/verify-installer.json`

### 试机时建议的 A/B 顺序

1. 同一视频、同一窗口尺寸，先在设置里**关弹幕**看低帧感是否消失（隔离 P0）。
2. 开弹幕，对比弹幕/控制栏显隐的顺滑度（主时钟已回到合成节奏）。
3. 开「网页版布局」，滚动触发小窗停靠/还原，确认不再出现「画面定住 ~0.7s + 弹幕消失又回来」。
4. 「画质增强」可以保持开启——A/B 实测 `super` 不影响视频呈现帧率；若仍觉得有驱动抖动，再手动关掉。

### 已知取舍

- 网页版布局的小窗动效回到 0.1.6 之前的**纯 WPF 遮罩**观感（过渡期间 `DockTransitionCover` 会压暗画面并显示品牌块缩放），不再是「真实画面截图缩放」。换来的是零 PNG 编解码、零 `DwmFlush`、零 `VideoView` Hidden/Visible、零 3s 守护定时器，每轮过渡从 0.65~0.8s 冻结降到 380ms 纯矩阵动画 + 结束帧一次原生 resize。若嫌遮罩太实，调 `PlayerWindow.xaml:853` 的 `Background` 或 `WebLayout.cs:UpdateWebCoverAnimation` 里 `DockTransitionCover.Opacity` 即可。
- 屏幕级像素采样仍有 2/117 帧固定浅色离群值（非过渡态、同位置），是 `GetPixel` 时序噪声，已在断言里用正确率判定而非零容忍。

## 本轮新增探针与原始数据

| 路径 | 用途 |
|---|---|
| `artifacts/render-clock-probe/` | 两种驱动时钟的逻辑更新率 + `ddagrab`/`framemd5` 真实上屏帧数对照 |
| `artifacts/fps-ab-probe/` | 读 `MediaStats.DisplayedPictures` / `LostPictures`，对照 `default/linear/processor/super` 四种缩放 |
| `artifacts/vsr-probe/motion.mp4`、`motion-640.mp4` | 持续运动 60fps 夹具（此前静态夹具测不出帧差） |

排查阶段只加探针与本报告；修复阶段的改动全在上面「改动文件」表里。

---

## 第二轮（同日）：把小窗切换做成澎湃OS 那种连续变形

用户试机反馈：「确实好了很多，不过网页版小窗切换还是不够丝滑，很生硬，理想是澎湃OS那种动画」。

### 为什么仍然生硬（查到一个隐藏 bug + 一个架构缺口）

1. **隐藏 bug**：`PlayerWindow.xaml:853` 的 `DockTransitionCover.Child` 就是 `BrandMark`（同一个对象）。
   `InitializeAlternatePlayerLayout` 先给 `coverElement.RenderTransform` 挂了含 `_webCoverScale` /
   `_webCoverMove` 的 `TransformGroup`，紧接着 `brand.RenderTransform = _webBrandMove` 把它**整个覆盖**了。
   ⇒ 遮罩的等比缩放与位移从来没生效过，实际只有「一整块不透明深灰板盖住视频 + 图标放大 +
   结束帧视频瞬移」。内容完全没有连续性。
2. **架构缺口**：视频是 `HwndHost`，不参与 WPF 合成；逐帧改它的尺寸会让 libvlc d3d11 vout
   每帧重建交换链（0.1.6 的教训）。所以「让窗口本体连续变形」不能靠动 `VideoSurface`。
   静态 PNG 截图是伪连续（画面冻结），纯色遮罩则根本没有内容。

### 方案：DWM Thumbnail 实时镜像

先用 `artifacts/dwm-thumb-probe/` 验证前提：

| 源 HWND | `DwmRegisterThumbnail` |
|---|---|
| 子窗口 `Panel` / `VLC video output` | `0x80070057` E_INVALIDARG |
| **顶层窗口** | **`0x00000000` 成功** |

`DwmUpdateThumbnailProperties` 逐帧改 `rcDestination` / `rcSource` / `opacity` 全部返回 0；
截屏确认是**实时**视频画面（两帧内容不同），不是静态位图。

于是新增 `Helpers/LiveWindowTransition.cs`：

- 源 = `PlayerWindow` 的顶层 HWND，用 `rcSource` 裁到视频区；目标 = 一个普通（非 layered）顶层
  owned window —— DWM Thumbnail 在 `AllowsTransparency` 分层窗口上注册会失败。
- 逐帧只改**目标窗口**的屏幕矩形（`SetWindowPos`）、圆角 region（`SetWindowRgn` + `CreateRoundRectRgn`）、
  以及缩略图属性。**源窗口尺寸不变 ⇒ vout 一次都不重建**。
- 位置 / 尺寸 / 圆角同步插值，曲线换成**临界阻尼偏软 spring**（ζ=0.86, ω=14，约 430ms），
  不再是 `1-exp(-7t)*(cos(11t)+.42sin(11t))` 那种明显过冲的欠阻尼曲线。
- 起点位置用 `DockTransitionCover` 压成不透明背景，避免镜像移走后露出原生 vout 造成「两个画面」重影；
  `BrandMark` 过渡期间收起。
- 动画结束帧才调 `commit()` 让真正的 `VideoSurface` 一次性落位；此刻镜像仍盖在上面，
  等 vout 出新帧（~190ms）再用 110ms 淡出 ⇒ 全程看不到黑帧/白边/瞬移。
- **任何 P/Invoke 失败都会抛，`RunLiveTransitionAsync` catch 后自动回退**到纯 WPF 遮罩路径，不会更差。

### 验证

`--web-dock-flicker` 现在直接反射读 `LiveWindowTransition.CurrentRect`，采镜像窗口中心的屏幕颜色：

| 指标 | 结果 |
|---|---|
| 镜像窗口几何连续变形 | **186 帧、68 个不同 x 位置**（不是硬切） |
| 过渡期白闪 | 0 |
| 稳态帧颜色正确率 | **0/77 离群**（上一轮还有 2/117） |
| 六项回归 | **145 项全绿** |
| 视频播放中评论滚动 | 113.4fps vs 无视频 116.3fps（仍不掉帧） |

### 已知取舍 / 下一步

- 过渡仍是「滚动跨滞回阈值 → 触发一次 430ms 自动动画」，**还没有做到手指跟手**。
  澎湃OS 那种「拖多少走多少、松手再收敛」需要把 `_webDockProgress` 直接绑到
  `WebLeftScroll.VerticalOffset` 并让镜像接受外部 progress，改动会牵动滚动映射与现有测试；
  本轮先把「内容连续变形」这个从无到有的质变做实。
- 目标窗口用系统原生无边框窗口 + `SetWindowRgn` 圆角，**没有澎湃OS 那种柔和投影**。
  若要补，可在 `VideoAreaRoot` 里加一层跟随的半透明圆角 `Border`（纯 WPF，Z 序在镜像窗口之下）。

## 本轮新增探针与原始数据（第二轮补充）

| 路径 | 用途 |
|---|---|
| `artifacts/dwm-thumb-probe/` | 验证 DWM Thumbnail 在本机 LibVLC vout 上的可用性、源 HWND 层级要求、逐帧 `rcDestination`/`opacity` 更新；截图在 `shots/thumb-srcForm-*.png` |
| `BiliTouch/Helpers/LiveWindowTransition.cs` | 实时镜像 + 连续变形 + 临界阻尼 spring + 无缝交接（产品代码） |

---

## 第三轮（同日）：真机「问题很大」——撤销 DWM 镜像，止损并修根因

用户试机反馈「继续磨，问题很大」。这次不靠猜，先在**用户同款配置**下把过渡画面逐帧截下来看。

### 复现环境（从 `%LocalAppData%\BiliTouch\settings.json` 读到的真实值）

- `UseWebPlayerLayout = true`、`UiScalePercent = 110`、画质增强开着（VSR 每 1~2 秒 ON/OFF）
- **系统 DPI = 225%**（`VisualTreeHelper.GetDpi` 返回 2.250）

新增探针 `--web-morph-capture`（`tests/TouchTests/WebMorphCapture.cs`）：用纯 P/Invoke
`BitBlt/GetDIBits` + WPF `BitmapSource` 截整窗口物理像素，复刻上述配置、播带图案片源，
每 55ms 存一帧到 `artifacts/touch-tests/web-morph/`。

### 抓到的三个真问题（都有截图/日志证据）

1. **物理像素 / DIP 双重缩放 → 镜像窗口飞出屏幕**
   `fromPx = 21,172,2061,1159`，而 WPF `Window.Width/Left` 是 **DIP**。把物理像素直接赋进去，
   WPF 又乘 2.25 ⇒ 窗口放大 2.25 倍；`mirror` 最终 `x=2152` 远超屏幕。
   截图 f00 是一个巨大的、被裁到屏外的彩色画面盖住整个播放器，连窗口标题栏都被镜像进去。
2. **DWM `rcSource` 裁切在 WPF `WindowChrome` 源上不可靠**
   `DwmRegisterThumbnail` 成功、`DwmUpdateThumbnailProperties` 返回 `0x00000000`，
   但画面是**纯黑方块**；去掉 `RECTSOURCE` 后变成「整个播放器窗口缩小版」（含标题栏和右栏）；
   补 `DWM_TNP_SOURCECLIENTAREAONLY` 标志位又变回黑。
   ⇒ 结论：这套 WPF 窗口 + 225% DPI + WindowChrome 组合下拿不到稳定的「只镜像视频区」。
3. **首帧延迟 505ms → 整段连续变形被跳过**
   日志 `registered 03:33:06.488` → `firstFrame 03:33:06.993`，而动画时长只有 430ms。
   第一次 `CompositionTarget.Rendering` 回调时 `t` 已经 = 1，用户只看到终点一闪。
   根因是 `ScrollChanged` 那一串同步布局占住了 Dispatcher。

另外之前所有回归都跑在 **100% DPI + UiScale 100 + 纯色夹具**下，纯色画面看不出裁切偏移，
所以这三个问题一个都没被测出来。

### 决策：撤销 DWM 镜像，回到稳妥路径，但把隐藏 bug 修掉

`LiveWindowTransition.cs` 已删除，`SetWebDockTarget` 回到 `StartLegacyWebDockTarget`。

| 改动 | 文件 |
|---|---|
| 修隐藏 bug：`coverElement` 就是 `BrandMark`，旧代码挂好 `TransformGroup` 后又被 `brand.RenderTransform` 覆盖 ⇒ `_webCoverScale/_webCoverMove` **从来没生效**。现在三个变换并进一个 `RenderTransform` group（纯渲染变换，不触发布局） | `PlayerWindow.WebLayout.cs` |
| 遮罩从 `Background="#17171C"` 不透明深灰板改成 `#26000000` 半透明 + 圆角 + 白色描边 ⇒ **视频全程可见，不冻结、不重影** | `PlayerWindow.xaml:853` |
| 过渡期间隐藏 `BrandMark`（原来正好压在视频中心，测试里是灰色方块） | `WebLayout.cs` |
| 动画时长改用**绝对墙钟**，不再「每帧累加且 clamp 50ms」——后者在 Dispatcher 被占住时把 380ms 拖到 1.5 秒（捕获实测 1541ms，改后 ~794ms） | `WebLayout.cs` |
| 收尾复位 `_webCoverScale/_webCoverMove/_webBrandLayoutScale` | `WebLayout.cs` |

### 验证（用户同款配置）

| 指标 | 结果 |
|---|---|
| 七项回归 | **145 项全绿** |
| 遮罩缩放真的生效（回归保护） | **39 个不同 scale 值** |
| 稳态颜色离群 | **0/137** |
| 视频播放中评论滚动 | 111.5fps vs 无视频 117.1fps |
| 逐帧截图 | `web-morph/f00..f19`：无黑方块、无错位窗口、无深灰板；f06 最终态小窗视频/控制栏/评论全部正常 |

### 仍未解决 / 下一轮

- commit 那一瞬（`f03`）左侧会露出一块纯黑（原生 `VideoSurface` 移到右栏后原位底色），
  属既有布局表现，非致命，但视觉上仍不够干净。
- **跟手**（滚动直接驱动 progress）与**柔和投影**仍未做；「内容本体连续变形」在
  `HwndHost` 不参与 WPF 合成、DWM 镜像又不可靠的前提下，需要换一条完全不同的路
  （例如让 vout 只平移不 resize 的几何实验），不能靠继续调 `rcSource` 赌。
- VSR 每 1~2 秒 ON/OFF 是用户开着「自动厂商增强」时 vout 反复 resize 触发的驱动重协商，
  与本次小窗动效独立，可单独评估。

---

## 第四轮（同日）：摸清 HwndHost 架构边界，别再往原生窗口上动刀

目标「继续磨，问题很大」。这轮不猜参数，先做定量实验，再把每条路都跑到真机截图为止。

### 实验一：只平移 vs 改尺寸（`artifacts/pan-ab-probe/`）

裸 LibVLC + WinForms，`SetWindowPos` 各跑 3 秒，挂 `--verbose=2` 数 `VoutDisplayEvent 'resize'`，
读 `MediaStats`：

| | 只改位置（SWP_NOSIZE） | 每帧改尺寸（SWP_NOMOVE） |
|---|---:|---:|
| 循环/秒 | **142.3** | 115.0 |
| `resize` 事件 | **0** | 46 |
| d3d11 重建 | **0** | 0 |
| lost pictures | **0** | 2 |

**结论**：0.1.6 记的「每秒重建 60 次」只在**改尺寸**时发生；只改位置完全不碰 vout。
而且每帧改尺寸在当前 LibVLC 3.0.24 + D3D11VA 上代价也没旧笔记那么灾难（115 loops/s、lost=2）。

### 实验二：把结论搬进产品（三条路全部真机截图验证，全部撤销）

| 尝试 | 真机结果（`--web-morph-capture`） |
|---|---|
| 动画期间 `VideoSurface` 每帧改 Margin + 尺寸分档写 | 屏幕采样抓到 **27 帧纯白 (255,255,255)**，连稳态都在；每次 resize 后刷黑宿主类背景也盖不住异步白底 |
| 动画期间**只平移** Margin（尺寸留到结束帧） | `f02` 里 `VideoView` 的 WPF 黑底与 vout HWND 分离，露出大片黑边 + 白边，控制栏/画面错位 |
| 滚动跟手（progress 直接由滚动偏移驱动，纯 WPF 遮罩连续变形） | 破坏了「跨滞回阈值启动 spring」的模型：progress 常直接等于 target，`StartLegacyWebDockTarget` 短路落位，`_webRendering` 不再为 true，`--web-player-layout` / `--web-dock-flicker` 挂了 |

**架构边界（本轮最重要的产出）**：`LibVLCSharp.WPF` 的 `VideoView` 是 `HwndHost`，
WPF 元素与原生 vout 是两套合成树。在过渡期间动原生窗口——无论是 DWM 镜像、
平移、还是分档 resize——都会产生黑边/白边/白帧/错位。当前可靠的上限就是
**过渡期间原生窗口一律不动，只有纯 WPF 遮罩连续变形，结束帧一次性落位**。
再往「内容本体连续变形」走，需要换渲染架构（自定义 vout / DirectComposition），
不是调参数能解决的。

### 实验三：`--player-video-load` 掉到 8fps 的误判澄清

某轮回归里 `BENCH` 从 116fps 掉到 8fps，`flicker` 采样数从 129 骤降到 22。
用独立小程序 `artifacts/render-clock-probe/` 测到当前会话 `composition/s = 6.8`
（历史值 119.8）——**DWM 合成被限到 ~7Hz，是环境**。

进一步做同环境 A/B：`git stash` 到 HEAD（`ef74f6c`）后跑同一批测试，
`BENCH` 同样是 **8.0 / 9.1 / 9.1 fps**，`flicker` 同样因采样数不足失败。
⇒ 与代码改动无关，不是性能回退。

### 本轮保留下来的改进

| 改动 | 文件 |
|---|---|
| `WebDockFlickerTests` 断言改成按**离群比例**判定（`idleTotal >= 8`、比例 ≤15%），并对 `motion >= 10` / `distinctScales >= 5` 放宽下限 —— 低帧率会话不再把环境慢当成产品 bug | `tests/TouchTests/WebDockFlickerTests.cs` |
| 把「窗口落位后布局尚未刷新的 1~2 帧，采样点仍钉在原位读到的纯黑底」识别为合法过渡底色；撤销一次错误的 `TransformToAncestor` + `Margin` 双重偏移（那会把采样点推到窗口外读成假白闪） | 同上 |
| `OnWebVideoFrame` 注释记录上述架构边界，防止后来人再往原生窗口上动刀 | `BiliTouch/Views/Windows/PlayerWindow.WebLayout.cs` |
| 新增 `artifacts/pan-ab-probe/`、`artifacts/render-clock-probe/` 两组定量探针 | artifacts |

### 验证

七项回归 **145 项全绿**；`--web-dock-flicker` 0/24 离群、零白闪、遮罩 19 档缩放；
`--web-morph-capture` 最终态干净。产品行为与第三轮一致（稳定基线），没有引入新的原生伪影。

### 下一轮该做什么

不要在「动原生窗口」上继续试。可行方向是**换渲染架构**：用 `libvlc` 的
`SetVideoFormatCallbacks` / `SetVideoCallbacks` 把解码帧接到自定义 vout（vmem / DirectComposition），
让画面变成 WPF 可合成的图层，才能真正做澎湃OS 那种内容本体连续变形。
代价是要重做硬解、色彩空间与音画同步，属于独立大改，不是本轮能顺手带上的。

---

## 第五轮（下一次会话）：VSR 抖动治理 + 落位交叉淡化

### 1. 真机日志暴露的独立问题：VSR 每秒级 ON/OFF

用户 `%LocalAppData%\BiliTouch\video-enhancement.log` 最后 40 行里有 **33 次
`turning VSR ON/OFF`**，间隔 1~2 秒。原因是驱动按「当前帧是否需要放大」做的瞬态判定，
而旧代码把 `turning VSR OFF` 也解析成用户可见状态「超分待机：当前画面无需放大」，
于是每切换一次：`State` 变一次 → `Changed?.Invoke()` → 播放器 `UpdateEnhancementButton`
重刷按钮颜色 / ToolTip，播放中每 1~2 秒抖一下。

修复（`BiliTouch/Services/VideoEnhancement.cs`）：

- 抽出 `ParseState(string)`：`turning VSR OFF` 返回 `null` —— 它是瞬态，不覆盖已确认状态。
- 抽出 `ShouldLogVsrToggle()`：同类 VSR 瞬态日志 2 秒内只落一条（真机 1~2 秒一次切换，
  原来会把日志文件刷爆，并且每条都走 `ThreadPool` 写盘）。
- `Observe` 改为调用这两个函数；`Status/core` 的公开行为不变。

回归：新增 `tests/TouchTests/VideoEnhancementTests.cs`（`--enhancement-vsr`），
7 项断言，其中「20 次 ON/OFF 交替只产生 2 次状态变更」和「20 次切换只写 1 条日志」
直接锁死该问题。

### 2. 落位那一帧的硬切 → 交叉淡化

`OnWebVideoFrame` 在 `t >= 1` 时把原生窗口从内联几何一次性落到右栏小窗（尺寸大跳变），
原来遮罩在 140ms 后直接收起，视觉上是一次硬切。改成在 settle 的 140ms 内把
`DockTransitionCover.Opacity` 从 0.65 线性淡到 0 —— 跳变藏在渐隐里。

### 验证

八项回归 **154 项全绿**：
`--enhancement-vsr` 9、`--danmaku-recovery` 13、`--web-player-layout` 33、
`--web-dock-flicker` 7（0/134 离群、零白闪、41 档遮罩缩放）、`--web-controls-unread` 41、
`--live-room` 50、`--player-video-load` 1、`--web-morph-capture` 通过。

### 仍然没做到的（如实记录）

- **跟手**：仍是「跨滞回阈值 → 430ms 自动动画」，不是手指拖多少走多少。
- **内容本体连续变形**：受 `HwndHost` 架构限制，见第四轮结论；需要换 vout 才能突破。
- **柔和投影**：遮罩只有圆角描边，没有澎湃OS 那种外发光阴影。
- **VSR 本身的开销**：本轮只治了它引起的 UI 抖动与日志风暴，驱动侧反复重协商的 GPU 成本仍在；
  想彻底避免只能让用户关掉「自动厂商增强」或改默认档位。

---

## 第六轮：停靠动画的「手感」重做（去弹跳 + 方向一致 + 浮动卡片）

目标仍是「继续磨，问题很大」。这一轮不碰原生窗口，只把**纯 WPF 动效本身**做对。

### 1. 曲线：从欠阻尼弹跳改成临界阻尼

旧曲线 `1 - e^(-7t)(cos11t + .42 sin11t)` 的阻尼比只有 **ζ≈0.54**，实测会冲到
**1.089**（过冲 8.9%），观感就是"弹一下"——这是"廉价/生硬"的主要来源，不是时长问题。

改成临界阻尼 `1 - e^(-ωt)(1+ωt)`（`ω=15.5`），**单调收敛、零过冲**，420ms 走完，
半程已到 0.836（前端自然、不是最后一下窜进去）。反向打断（滚回去）用 240ms，
减少"追不上手"的迟滞。

抽成 `PlayerWindow.WebMorphEase(elapsedMs, durationMs)`，由 `--web-morph-motion` 直接断言
形状（11 项：单调、不过冲、起止点、半程位置、反向时长）。

### 2. 方向：遮罩朝**真实停靠位**收缩

旧实现硬编码"缩到视频区右上角"（`scale = 1-0.58p`，位移按 50% 居中），
与最终落点（右栏 `WebDockAnchor`）方向不一致 —— 缩完还是瞬移，观感断裂。

现在 `UpdateWebCoverTargetGeometry()` 用 `CalculateWebVideoBounds(1)` 取真实落点，
算出归一化的目标缩放 / 目标中心：

```
实测（1320×860, UI 110%, 225% DPI）：scale 0.383、nx 0.788、ny 0.725
```

并把目标夹在「收缩后整块仍留在视频区内」的范围内，保证收尾能盖住落位那一跳。
★ 特意不用 `PrepareWebDockAnchor(1)` 取几何：那会把 `WebDockAnchor.Height` 真设成满高，
动画一开始右栏内容就被顶下去，等于制造一次新的布局跳变。

### 3. 视觉：从"一层薄纱"改成"浮动玻璃卡片"

排查中发现**第二个隐藏问题**：`TransformGroup` 一直挂在 `DockTransitionCover.Child` 上，
而 Child 是 `BrandMark` —— **缩小的从来只是图标，那块板一直满屏**。
第三轮只修了"group 被覆盖"，没修"挂错了对象"。

现在 XAML 拆成两层：外层 `DockTransitionCover`（透明容器，负责裁剪与 Z 序）+
内层 `DockTransitionCard`（真正被缩放/位移的卡片）。卡片做成：

- `#4D000000`（30% 黑）玻璃底 —— 视频仍然透出来，但卡片边界清晰；
- 2px `#B3FFFFFF` 亮边；
- **三层递减透明度的圆角 Border 叠出阴影**（Margin -22/-13/-6，Radius 34/26/20），
  刻意不用 `DropShadowEffect`：大尺寸模糊每帧重采样会掉帧；
- 卡片自身在头 12% 进度内淡入，避免手势一开始整块盖上去"闪一下变暗"。

### 4. 验证

八项回归 **165 项全绿**：
`--web-morph-motion` 11、`--enhancement-vsr` 9、`--danmaku-recovery` 13、
`--web-player-layout` 33、`--web-dock-flicker` 7（0/124 离群、零白闪、96 档缩放）、
`--web-controls-unread` 41、`--live-room` 50、`--player-video-load` 1。

`--web-morph-capture` 在 225% DPI + UI 110% 下逐帧截屏：`f01` 能清楚看到
圆角玻璃卡片带亮边与阴影、视频透出、正朝右栏方向收缩；时间线
`coverScale 1.000 → 0.568 → 0.412 → 0.383`，约 470ms 收完（旧实现会被拖到 1.5s）。

### 仍然没做到的

- **跟手**：还是「跨阈值 → 自动动画」。试过让 progress 直接跟滚动偏移，但会短路
  落位逻辑、`_webRendering` 不置位、两套布局回归挂掉（第四轮记录），本轮没有硬上。
- **内容本体连续变形**：`HwndHost` 天花板未变。
- **卡片是"代表窗口"，不是窗口本身**：用户仍能看到卡片下面的视频在原尺寸播放，
  这是这套架构下的固有限制，只能靠阴影/边框/运动让它读起来像一张浮起来的窗口。

---

## 第七轮：把「唯一能到澎湃水平」的路跑通（vmem 自定义输出）

前六轮都在 `HwndHost` 的框子里打补丁。这一轮直接量**换掉渲染架构**的代价：
用 `libvlc` 的 `vmem` 回调把解码帧取到内存，再画进 WPF 的 `WriteableBitmap` ——
这样画面就变成**普通 WPF 元素**，可以裁圆角、挂阴影、逐帧连续变形，也能被
`RenderTargetBitmap` 截到（`HwndHost` 这三件事全做不到）。

### 1. 性能：跑得动

探针 `artifacts/vmem-probe/`（`--vout=vmem`，`RV32`/BGRA，避免二次转换），
每组跑 8 秒，读回调计数与 memcpy 计时：

| 场景 | 帧率 | 每帧拷贝 | WPF 上传（WriteableBitmap） |
|---|---:|---:|---:|
| 720p60 + 拷贝 | **60.0** | 0.25 ms | 0.16 ms |
| 720p60 不拷贝（基线） | **60.0** | — | 0.18 ms |
| 1080p60 + 拷贝 | **60.0** | 0.51 ms | 0.44 ms |
| 1080p60 不拷贝（基线） | **60.0** | — | 0.45 ms |

1080p60 稳满，每帧总开销 ≈ 1 ms，占 16.7 ms 预算的 **6%**。
（第一次跑出 53fps 是起播抖动，重跑即 60.0；`copy_ms` 早期把累加 tick 当时间戳，已修。）

**端到端上屏**（`VmemMorph.exe rate`，5 秒，同时统计 vmem 交付与 WPF 合成回调）：

| 场景 | 交付帧率 | WPF 合成帧率 | p50 | p95 | 判定 |
|---|---:|---:|---:|---:|---|
| 720p60 | **59.8** | **124.9** | 6.80 ms | 17.99 ms | SIXTY |
| 1080p60 | **59.8** | **116.1** | 7.47 ms | 18.02 ms | SIXTY |

交付 59.8fps 的同时合成循环还跑在 116~125Hz，说明「每帧拷进 WPF」**没有拖住呈现**，
不是纸面数字。

### 2. 效果：内容本体连续变形

原型 `artifacts/vmem-morph/` 把 vmem 帧画进一个带圆角裁剪 + 三层阴影的卡片，
按产品同一条临界阻尼曲线从「内联大画面」变形到「右栏小窗」，逐关键帧截图：

```
morph-000.png  rect=24,24,1000x562   （内联，画面铺满卡片）
morph-025.png  rect=467,85,671x377
morph-050.png  rect=790,129,432x243
morph-075.png  rect=899,144,350x197
morph-100.png  rect=940,150,320x180  （停靠小窗）
```

截图里能直接看到：**视频内容留在卡片里一起缩放**，圆角是真的把画面裁掉了，
阴影跟着卡片走 —— 这正是澎湃OS 那种「窗口本体连续变形」，而不是第六轮那种
「一张玻璃片盖在全尺寸视频上收缩」。

### 3. 集成代价（下一轮的工作量）

要把这条路接进产品，得把 `PlayerWindow` 的 `VideoView` 换成 `Image`：

- **必须重做**：`VideoView.Content` 那套浮层（弹幕、控制栏、手势层、Loading/EOF 遮罩）
  现在寄生在 LibVLCSharp 的独立浮层窗口里；换成 Image 后它们回到普通 WPF 树，
  `Detach`/`Attach`、`ForegroundWindow` 键盘钩子、`VideoBackdropInterop` 全部要改。
- **会顺带删掉一批补丁**：`VlcPlacePictureGuard`、`ArmResizeCover`、vout 重协商白边、
  `ApplyHwndHostBlackBackground` 这些**只为 `HwndHost` 存在**的东西全部不再需要。
- **要接受**：vmem 走软件解码 + BGRA 转换，硬解优势丢失；1080p60 实测没问题，
  4K / AV1 需要实测，可能要保留「性能不足时回退到 VideoView」的开关。
- **不受影响**：音频同步由 libvlc 自己负责；`TakeSnapshot`、倍速、seek 均照旧。

### 4. 本轮结论

- 这条路的**可行性已被实测证实**（60fps + 1ms/帧 + 原型截图）。
- 但它是**架构级替换**，牵动播放器窗口的整个浮层结构，不是一两百行能收干净的。
  本轮只做到「证明可行 + 量化代价 + 出原型」，**没有动产品代码**，避免又一次
  「改一半就发出去、用户看到更糟」。
- 下一轮建议：先做 `VmemVideoView` 控件（Image + vmem + 尺寸/宽高比/暂停），
  在**独立测试窗口**里跑通播放与变形，再整体替换 `PlayerWindow` 的视频宿主。

---

## 第八轮：VmemVideoView 落地 —— vmem 从原型变成产品里的控件

上一轮证明了 vmem 可行，这一轮把它做成**产品程序集里的真控件**并在独立窗口里跑通播放。

### 1. 新增 BiliTouch/Helpers/VmemVideoView.cs

Image 的子类，内部接管 libvlc 的 vmem 三件套（format / lock-unlock / display）：

- 格式协商成 `RV32`（BGRA），与 WPF `Bgra32` 一致，省一次色彩转换；
- 每帧先拷进一块稳定的 `_present`，再在 UI 线程写 `WriteableBitmap`；
- **帧合并**：上一帧还没画完就不排队（`_pending` 标志），避免 60fps 时把
  Dispatcher 队列堆爆；
- 暴露 `DeliveredFrames` / `CoalescedFrames` / `VideoSize` 供诊断；
- `Stretch=Uniform` + `Linear` 缩放（解码帧直接贴，不让 WPF 再做高质量重采样）。

配套新增 `VlcCore.GetVmem()`：一个**专供 vmem 的 LibVLC 实例**（`--vout=vmem`）。
`--vout` 是实例级选项，不能污染被普通播放器使用的共享实例；两个实例共用同一份
libvlccore，代价只是多一份插件状态。

### 2. 踩到并修掉的三个真 bug（都会导致 0xC0000005）

| 现象 | 根因 | 修法 |
|---|---|---|
| 进程退出必崩 | 传给 `SetVideoCallbacks` 的是**方法组**，托管侧没有强引用，GC 回收后原生回调跳进已释放的桩 | 五个委托全部存字段保活 |
| `Stop()` 之后崩溃 | 停止瞬间原生线程还在 lock/display，缓冲已被释放 | `Stop` 放后台线程执行；释放后 `OnLock` 返回一块**永不释放的垫子内存**，绝不给 0 |
| `Dispose()` 之后崩溃 | 队列里还压着几帧渲染回调，`_present` 已归零，`CopyMemory` 从空指针读 | 在渲染回调里再次确认 `_present`/`_bytes` 有效，无效直接跳过 |

第三条是最阴的：只在「播完 → 立刻 dispose」这条路径上必现，正常用可能几小时才撞一次。

### 3. 验证（`--vmem-view`，7 项，连跑三次 exit=0）

| 断言 | 结果 |
|---|---|
| vmem 通过控件持续交付帧 | 11~12 帧/秒起步正常 |
| 格式协商 | 640×386 |
| 交付帧率跟上片源 | **30.0 fps vs 源 30.0 fps** |
| 画面确实在变（非冻结首帧） | 两次像素哈希不同 |
| **`RenderTargetBitmap` 能截到画面**（HwndHost 做不到） | 4893/5195 采样点非背景 |
| **能裁圆角**（澎湃卡片的前提） | 生效 |

截图 `artifacts/touch-tests/vmem-view/vmem-view.png`：视频铺在圆角卡片里，**四角真的被裁掉**
（露出深色底），这是 VideoView 永远做不到的。

### 4. 现状与下一步

- 控件本身**已可用**，但**还没接进 PlayerWindow**：现有播放器的弹幕/控制栏/手势层
  都寄生在 VideoView.Content 的独立浮层窗口里，换宿主时必须一并搬回普通 WPF 树。
- 九项回归 **171 项全绿**（vmem-view 6、web-morph-motion 11、enhancement-vsr 9、
  danmaku-recovery 13、web-player-layout 33、web-dock-flicker 7、web-controls-unread 41、
  live-room 50、player-video-load 1）。
- 下一轮：把 PlayerWindow 的视频宿主换成 VmemVideoView（先做「网页版布局」这一条路径），
  浮层控件改挂普通 WPF 树，逐项回归。