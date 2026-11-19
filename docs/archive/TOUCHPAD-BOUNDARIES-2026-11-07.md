# 触控板起步、抬手与惯性段排查（0.2.7）

## 用户现象

全局刷信息流和评论时卡顿；以接近的手指速度滚动，实际距离忽短忽长；落指、离指尤为明显，离指后系统惯性滑动途中也会卡顿。

## 已复现并修复

测试使用本地空白长页面，不请求网络或图片。

1. **未提交的滚动命令被误判为外部移动。** `ScrollToVerticalOffset` 异步排队，实际 VerticalOffset 在布局提交前仍为旧值。旧驱动只和上一次请求值比较，相差超过 2 DIP 就取消累计目标。两次 -60 输入原应走 18 DIP，实测仅 9 DIP。现在同时识别已观察位置与请求位置，保留未提交输入。
2. **触控板末尾输入切换倍率。** 旧驱动在空闲 90ms 后既停止渲染订阅又结束设备推断。后续恰好为 120 倍数的包转入整格倍率，-20 后停 160ms 再 -120，原应合计 21 DIP，实测 48 DIP。现在渲染订阅仍空闲 90ms 释放，设备连续性单独保留 750ms；系统明确报告触控板时优先采信，不用单个 delta 判断。
3. **取消后已排队的命令仍会执行。** 旧 Cancel 只移除帧订阅，无法撤回 ScrollViewer 的队列。重新接触/导航接管时，现在还用当前实际位置替换未提交的滚动命令；自然完成与主动取消分开，避免把最后一段正常位移也撤回。

未对精密输入添加第二层惯性或结束缓动。新增递减输入测试，包含不均匀派发、末尾停顿，预期和实际均为 30.60 DIP，结束后没有应用自行追加的位移。鼠标刻度原有缓动与灵敏度参数保留。

## 网络与剩余卡顿

网络慢本身不能解释本地固定页面上的丢距离、换倍率和取消后位移。资源下载完成后的 UI 更新、图片贴入、控件创建和排版仍可能增加负担；本轮没有证据能量化其在用户账号页面上的比例。

本地纯文字与可选中文字两种评论夹具中，WPF Rendering 回调 P95 仍可到约 27–31ms。这是受调度、测试派发和渲染影响的回调间隔，不能直接当成显示器实际呈现帧率，更不能因此承诺卡顿全部消除。

## 兼容边界

Windows/驱动不一定给传统滚轮消息提供可区分的触控板身份。回退路径仍是启发式：长时间无精密输入后的首包若恰为 120 倍数，仍可能存在歧义；触控板后很快换用物理鼠标也有短暂误识别风险。本轮未使用预发布的 touchpad-capable 注册 API，也未宣称已经完成真实设备原始输入采样。用户的物理触控板手感仍须复测。

依据：[微软高精度触控板说明](https://learn.microsoft.com/en-us/windows/compatibility/precision-touchpad-devices)、[输入来源 API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcurrentinputmessagesource)、[触控板注册兼容说明](https://learn.microsoft.com/en-us/windows/win32/input-precisiontouchpad/registertouchpadcapable)。

日志：`artifacts/boundary-before.log`、`boundary-mid.log`、`boundary-contact-before.log`、`boundary-final-scroll-boundaries.log`、`boundary-scroll-text-host-cost.log`、`boundary-final-summary.json`。

## 最终验证

- 应用和测试工程构建通过，0 错误；保留既有警告。
- 最终 5 组定向回归 182 项全部通过：边界/惯性 9、全局滚动 33、实际评论视口 12、动态/虚拟化 119、首页/版本/配置 9。
- 额外本地文字对照 4 项通过。可选屏幕采样诊断出现无有效变化和 NaN 统计，未用其数字判断实际呈现帧率，也未据此宣称视觉性能提升。
- 重新打包 0.2.7，保留版本号；安装包与便携包的 SHA256SUMS 同步更新。未发布到 GitHub。
