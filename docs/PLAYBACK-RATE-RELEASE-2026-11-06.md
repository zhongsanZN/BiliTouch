# 长按临时倍速：松手卡顿与音频中断排查（2026-11-06）

用户现象：**长按画面临时倍速，松手时卡顿或断音。**

本轮结论全部来自实测探针（真实 libvlc 3.0.24 + 真实 aout + 本地限速 HTTP 服务），
不是从注释里推断的。每条结论都标了可复跑的探针与数字。

---

## 0. 一句话结论

**用户听到的"松手后断音"，不是 `SetRate` 造成的，而是我们自己的音频自愈逻辑在修一个
根本修不好的东西 —— 它调 `SetAudioTrack(0)`，而这个调用在 libvlc 3.0.24 上救不回掉线的音轨，
音频会一直静默到下一次重建 input。**

修了两处（见第 4 节），均已用探针验证修复前/修复后对照。

---

## 1. 先纠正一条已经失效的旧结论

代码里关于"松手断音"有一大段定稿注释（`PlayerWindow.xaml.cs` 约 2955–2988 行、396–419 行），
写着：

> 根因：**libvlc 在 SetRate 生效时会丢弃整个音频输出缓冲**，原生日志打
> `playback way too late: flushing buffers`。松手后停顿 0.25~0.5s。
> 三个缓解手段全都更糟，什么都不做（base）就是最优。

**这条结论的适用前提已经变了**，而注释没有跟着更新：

| 项目 | 事实 |
| --- | --- |
| 那批实验跑在 | libvlc **3.0.20** |
| 项目现在用的 | libvlc **3.0.24**（`BiliTouch.csproj`，0.1.6 起） |
| 3.0.24 的变更 | mmdevice/WASAPI 音频线程模型重写、nonblocking play 重写、underrun 修复 |

实测（`artifacts/probes/rateaudio/run1.log`，5 轮 × 每次 2s 按住）：

```
SetRate(2.0) 阻塞 0.13 ms   SetRate(1.0) 阻塞 0.04 ms
松手瞬间音频事件数: 117   （与按住期间同量级）
全程 flush = 0，pause = 0，drain = 0
```

**在 3.0.24 上，"SetRate 丢弃音频缓冲"根本不发生。** 零 flush、零 pause、零 drain。

所以那段"别再想、三个方案都更糟"的结论，现在是**在指导后来的人绕开一个不存在的问题**。

---

## 2. 逐一否证掉的方向（都带数字）

| # | 假设 | 探针 | 结果 | 判定 |
| --- | --- | --- | --- | --- |
| 1 | `SetRate` 在 UI 线程上阻塞 | run1/run4 | 中位 0.01ms，最大 0.76ms（n=5轮×多场景） | **否证** |
| 2 | 倍速 OSD 浮层显示/隐藏开销大 | `probes/osd` | 分层窗口 0.07ms / 普通窗口 0.15ms | **否证** |
| 3 | SetRate 丢音频缓冲 | run1/run2 | flush=0 pause=0 drain=0 | **否证**（3.0.24） |
| 4 | 音频真的有静音段 | run3 + ffmpeg silencedetect | afile 录真波形，**无任何静音段** | **否证** |
| 5 | amem 看到的 250ms 空洞是真断音 | run2 | 空洞处回调携带 1024 采样，总量 0.994 连续 | **否证**（只是批量节奏） |
| 6 | `AudioTrack` 变速时掉成 -1，触发自愈误动作 | run4（muxed + DASH 双流各 2100+ 采样点） | **全程恒为 1，从未 -1** | **否证** |
| 7 | 2x 播放抽干网络缓冲 | run6（本地限速 HTTP） | 1.35× 码率下 2x 仍每 500ms 推进 1066ms | **否证** |
| 8 | 松手路径原生调用太贵 | run8（含真实视频输出） | 整段中位 **0.02ms**，95 分位 0.81ms | **否证** |

关于第 7 条要说清楚：限速 1.35× 码率时 2x 段确实出现过 626ms / 572ms 的窗口，
但松手后立刻回到 561/571ms 的正常节奏，**最长停滞 202ms ≈ 我的采样间隔本身**，
不是用户能感知的卡顿。带宽更富裕时（3.0×）也没有更好或更差。
⇒ 网络缓冲不是这个现象的主因。

关于第 8 条：`Time`/`Length`/`Position`/`State`/`IsPlaying`/`Rate`/`AudioTrack`/
`AudioTrackCount` 这些 getter 每个约 **0.003~0.006ms**，`SetAudioTrack(0)` 首次调用
10 次测量中位 **0.02ms**（最大 0.15ms）。**松手路径在 UI 线程上不构成卡顿。**

---

## 3. 找到的真问题：音频自愈是一条死路

### 3.1 症状链

`PlayerWindow.xaml.cs`：

```
StopFastForward()                              // 松手
  └─ HealAudioIfDead("fastforward-end")        // 行 481
       ├─ 闸门：距上次检查 < 1500ms 就 return  // 行 2848
       ├─ if (_mp.AudioTrack != -1) return;    // 行 2855
       └─ SetAudioTrack(0)                     // 行 2856  ← 修不好
```

代码注释自己写着这个竞态：

> 实测机理是 libvlc 在 seek/变速时重建音频输出，偶发地**把选中的音轨丢回"无轨道"**
> （AudioTrack 变 -1，音轨数明明 >0）—— 这是库的内部竞态。
> 修复动作 = `SetAudioTrack(0)` 把第一条音轨选回来。

### 3.2 实测：`SetAudioTrack(0)` 救不回来

`artifacts/probes/rateaudio/run9.log` / `run10.log`：

```
[修复前 SetAudioTrack(0)]
  正常态 AudioTrack=1 Count=2
  掉线后 AudioTrack=-1  600ms 内 play 0 次
  恢复动作 0.31ms → AudioTrack=-1
  恢复后 1.5s play 0 次 ⇒ ✗ 仍然静默          ← 状态是 Playing，但音频永久没了
```

而按**真实 track id** 重选：

```
[修复后 按 AudioTrackDescription 取 id]
  掉线后 AudioTrack=-1  600ms 内 play 0 次
  恢复动作 3.48ms → AudioTrack=1
  恢复后 1.5s play 71 次 ⇒ ✓ 音频恢复
```

**根因**：`0` 不是"第一条音轨"，而是一个绝对索引。音轨掉线后 libvlc 内部轨道表已重排，
写 `0` 落在一个不存在的槽位上并被**静默丢弃**（setter 不看返回值，所以我们连失败都看不到）。
必须从 `AudioTrackDescription` 里取真实 id。

### 3.3 第二个缺陷：松手那次检查经常根本不会执行

`_audioHealCheckAt` 是**全窗口共享**的闸门基准（1500ms）。长按 2 秒期间虽然没人主动检查，
但换片 / seek 到位 / 别的自愈点都可能刚把它写过。一旦上一次检查落在 1.5s 内，
`HealAudioIfDead("fastforward-end")` 就直接 return —— **而松手是一次性事件，之后再没人补检查**。
那条竞态就永久留在那儿。

### 3.4 这解释了什么

- 为什么"偶发"：竞态本身偶发。
- 为什么"松手时"最容易撞上：变速是重建音频输出的暴露点。
- 为什么"有时一直没声音"：自愈修不好，且不会再检查。
- 为什么本地/短片段测不出来：需要真实 seek/变速重建 aout 才触发。

---

## 4. 已做的修改

只改 `BiliTouch/Views/Windows/PlayerWindow.xaml.cs`，两处。

### 4.1 `HealAudioIfDead`：按真实 track id 重选

```diff
-            if (_mp.AudioTrackCount <= 0) return;
-            if (_mp.AudioTrack != -1) return;
-            var ok = _mp.SetAudioTrack(0);
+            var trackCount = _mp.AudioTrackCount;
+            if (trackCount <= 0) return;
+            if (_mp.AudioTrack != -1) return;
+
+            // ★★★ 必须按**真实 id** 重选, 不能写 0(见方法说明里的实测)。
+            var desc = _mp.AudioTrackDescription;
+            var picked = -1;
+            if (desc != null)
+            {
+                foreach (var d in desc)
+                {
+                    if (d.Id >= 0) { picked = d.Id; break; }
+                }
+            }
+            if (picked < 0)
+            {
+                WriteLibVLCDebug(...跳过);
+                return;
+            }
+            var ok = _mp.SetAudioTrack(picked);
```

方法文档里补了完整的实测证据与失效原因。

### 4.2 `StopFastForward`：松手后排一次延迟复查

新增 `ScheduleAudioHealRecheck()`：在 `AudioHealGraceMs + 200ms` 后把闸门基准清零并复查一次，
保证"这一次变速"至少被检查过一次。用 `DispatcherTimer`（会被现有反射式
`StopAllTimers` 自动收掉，不会泄漏）。

---

## 5. 验证

| 项 | 结果 |
| --- | --- |
| Release 构建 | 0 错误，4 个既存警告（与改动无关） |
| 修复前/修复后对照 | 0 次回调 → 71 次回调（`run10.log`） |
| 正常态不误动 | `自愈是否动手=False`，音轨 1→1 |
| 多音轨选择正确 | 描述 `Id=-1 Disable / Id=1 Track 1`，选中 Id=1 = 当前音轨 |

---

## 6. 复查入口

探针工程：`artifacts/probes/rateaudio/`（独立 csproj，不污染主工程）
输出目录已被 `.gitignore` 的 `/artifacts/` 覆盖。

| 文件 | 内容 |
| --- | --- |
| `run1.log` | SetRate 阻塞 + amem 音频事件（5 轮） |
| `run2.log` | 四变体变速（2→1 / 2→1.5→1 / 立即松手 / 不变速）回调空洞 |
| `run3-base.log` + `aout-base.wav` | afile 真实 aout 波形 |
| `run4-muxed.log` / `run4-slave.log` | AudioTrack 过渡期取值（单流 / DASH 双流） |
| `run5.log` | demux 组合 × input-slave 起播矩阵 |
| `run6.log` | 网络限速下 2x 是否抽干缓冲 |
| `run7.log` | SetAudioTrack 是否制造断音 |
| `run8.log` | 各原生 getter 阻塞代价（含真实视频输出） |
| `run9.log` | SetAudioTrack 代价 + 可恢复性（发现死路） |
| `run10.log` | **修复前/修复后对照**（验收用） |

复跑命令（离线，不需要联网）：

```powershell
$env:DOTNET_CLI_HOME="$PWD\artifacts\dotnet-home"
$env:NUGET_PACKAGES="..\.tools\nuget"
& "..\.tools\dotnet\dotnet.exe" build artifacts\probes\rateaudio\RateAudioProbe.csproj `
    -c Release -p:RestoreSources="..\.tools\nuget"
Copy-Item "BiliTouch\bin\x64\Release\net10.0-windows\LibVLCSharp.dll" `
    "artifacts\probes\rateaudio\bin\Release\net10.0-windows" -Force
& "artifacts\probes\rateaudio\bin\Release\net10.0-windows\RateAudioProbe.exe"
```

---

## 7. 还留在桌上的事

1. **那段 3.0.20 时代的"别再想"注释应该改掉**。它现在明确阻止后来的人去试
   音频输出后端 / 缓冲深度这类方向，而它描述的 flush 现象在 3.0.24 上已经不存在。
   本轮没有动它（超出排查范围），但它是下一轮的最大障碍。
2. `BuildMedia` 无条件给所有 VOD 源加 `:demux=avformat`（行 1995），
   而 DASH 双流还要再挂 `:input-slave`（行 2007）—— 仓库历史里明确写过这个组合会
   `Ended` 卡死。本轮 `run5.log` 实测该组合在 3.0.24 上**能正常起播并正常变速**
   （松手后首 500ms 推进 533ms），所以**暂不构成 bug**，但它是被"无条件"引入的，
   与注释里"绝不能加"的记录直接冲突，值得下一轮用真实 B 站 DASH 源再确认一次。
3. 触摸路径 `OnVideoTouchUp` 与鼠标路径 `OnVideoAreaMouseLeftUp` 都调
   `StopFastForward` + `ShowSpeedHint("已结束快进")`，行为一致；
   但 `OnVideoAreaMouseLeave` 里有**两次** `StopFastForward()`（行 4957 与 4963，
   第二次是兜底），第二次因 `_isFastForwarding` 已 false 而立即返回，无副作用，属可清理的冗余。
