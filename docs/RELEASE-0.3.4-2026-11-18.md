# BiliTouch 0.3.4

本版新增播放器长按点赞一键三连交互，集成星创组同人游戏内嵌运行台，并同步至上游 BiliDesk v1.7.6 的播放器卡顿恢复、跳转校准、窗口防闪与弹层交互等多项稳定性优化。

- **长按点赞一键三连（触屏与鼠标双端适配）**：新增外框进度环装饰器 `PressRingAdorner`（不参与布局拉伸）与两段式长按手势（前 500ms 静默确认、后 1500ms 扫满触发）；接入 `/x/web-interface/archive/like/triple` 官方三连接口，触发时依次播放「点赞红 → 投币金 → 收藏蓝」联动动效并浮现「一键三连」徽章，中途松手自动抑制误触单击并放行键盘 `L` 快捷键。
- **同步上游至 v1.7.6 播放与恢复优化**：引入 `StallRecoveryPolicy` 多级渐进卡顿自愈与 `BufferPolicy.ListenAudio`「听视频」缓冲保底；增强长视频跳转精度（`:start-time` 参数与 `SeekBounce` 回跳纠偏、假 EOF 续播修复）、`DanmakuMotion.Calibrate` 弹幕时钟平稳校准与 `ApiClient._buvidGate` 风控指纹初始化并发锁。
- **窗口与交互细节修复**：应用 `WindowMaximizeInset` 修复无边框最大化边缘裁切与云母/亚克力窗口缩放视频底色闪烁；通过 `PopupToggle` 防抖隔离、点击捕获与文字字体解析完善弹幕设置弹层及「弹幕描边」体验；为 `LiveRoomWindow` 补齐 `:http-reconnect` 重连、聊天滚动虚拟化保护，并将 `BrowserWindow` 收敛为单地址栏及 `LinkLauncher` 安全外链。
- **星创组同人游戏内嵌游玩**：在 `StarPartyPage` 中点击 `/play/...` 网页小游戏改由页面内置运行台加载，不再弹出独立浏览器窗口。

安装器包含程序及播放依赖；下载附件仅上传一个 `setup.exe`，源码随 `v0.3.4` 标签提供。
