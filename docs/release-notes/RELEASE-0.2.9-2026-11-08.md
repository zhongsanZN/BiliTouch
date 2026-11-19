# 0.2.9 影境主题图标、鼠标修订与全屏闪退修复（2026-11-08）

## 最终改动

- 影之刃零主题接入本机游戏的原版刀锋鼠标，WPF 与 WebView2 页面随主题切换，保留编辑、缩放、隐藏指针语义。
- 接入用户逐项指定的 28 张界面 PNG，保留原图、透明边界及来源哈希；侧栏、刷新、返回、翻页、暂停、画质、弹幕设置、结束提示与音量使用相应原素材。
- 互动图标按位置区分完整与简易版本；点赞、投币数量与收藏状态保持语义对应，补齐必要反色与蓝色收藏显示。保留原位光效及低音量互动音效。
- 完整未点赞火焰保留原灰度层次，并分别增强深浅模式的明暗差；深色模式进一步提亮灰色外缘，避免边缘融进背景。显示纹理按模式缓存，源 PNG 不改写。
- 个人主页侠客在图片区中央按可见边界贴底，去掉原先的下方留白，避开右侧底图人物。
- 应用、程序集、安装器、发布脚本和下载索引同步为 0.2.9。

## 修复：退出播放器全屏后卡死闪退（本版新增，阻断发布）

崩溃现场：`libvlccore.dll 3.0.24`，异常代码 `0xC0000094`（整数除零），错误偏移 `0x6F4E0`。
反汇编该地址即 `vout_display_PlacePicture()` 里的 `idiv rcx`；转储里 `Rax = Rcx = 0`，
说明传入的 `video_format_t` 的 `i_visible_width` / `i_visible_height` 都是 0。

调用链（从转储的模块表与返回地址逐层确认）：

```
libdirect3d11_plugin.dll  UpdateBackBuffer()            ← 只有 processor / super 缩放模式会进来
  → D3D11_UpscalerUpdate()                              ← d3d11_scaler.cpp 里的 PlacePicture 调用
    → libvlccore  vout_display_PlacePicture()            ← 分子分母同时为 0 ⇒ #DE
```

`pool_fmt` 在 `Direct3D11DestroyPool()` 里被 `video_format_Clean()` 整块清零，而
`UpdateBackBuffer()` 在这之后仍会被调用一次；退出全屏是 vout 重建最密集的时刻，
网页布局的挂载／卸载还会额外搬动原生视频窗口，因此这条路径被稳定命中。
上游 3.0.x 至今未修（只有 4.0 的重构），NuGet 上也没有更新的 3.0.x 包。

三层处理：

1. `BiliTouch/Helpers/VlcPlacePictureGuard.cs` —— 在插件的导入表上把
   `vout_display_PlacePicture` 换成守卫：参数会让原函数除零时返回空矩形（与原函数
   "没有可见画面"的语义一致），其余原样转发。带确定性自证 `SelfTest()`。
2. `FluentWindow.SetWindowBoundsOnce` —— 退出全屏恢复窗口尺寸从四次属性赋值改为一次原生
   `SetWindowPos`，危险窗口从四次缩到一次。
3. `PlayerWindow.WebLayout.ApplyAlternateLayoutPreference` —— 全屏切换期间推迟网页布局的
   挂载／卸载，等 `_suppressStateCover` 落下后再做。

自证方式：`BiliTouch.exe --verify-fullscreen --media=<本地视频> --result=<json> --rounds=14`
（装好的程序里跑，隔离配置目录）。它会断言守卫已挂上、守卫自证通过、并在网页布局 + super
缩放下反复进出全屏 14 轮、每轮确认画面仍在推进。

## 内置检测更新

- 新增 `Services/UpdateInstaller.cs`：下载安装器 → 校验 SHA-256 → 静默安装并重启。
- `UpdateChecker` 增加按 `BiliTouch-<版本>-setup.exe` 精确匹配附件的路径，版本必须与 tag 一致。
- `UpdatePromptWindow` 增加「下载并安装」主按钮与进度条，「前往 GitHub」保留为退路。

素材映射和前期验证见 `PHANTOM-BLADE-SELECTED-UI-2026-11-08.md`，鼠标来源见 `PHANTOM-BLADE-CURSOR-2026-11-08.md`，
除零崩溃的完整取证与上游比对见 `artifacts/release-0.2.9/vlc-d3d11-divide-by-zero-research.md`。
