<div align="center">

<img src="BiliTouch/Assets/app.png" width="104" alt="BiliTouch 图标">

# BiliTouch

**面向触摸屏、触控板手势与人体工学优化的哔哩哔哩第三方 Windows 客户端**

作者：**中三** · 当前版本：**0.2.1**

仓库：[zhongsanZN/BiliTouch](https://github.com/zhongsanZN/BiliTouch) · [下载与历史版本](https://github.com/zhongsanZN/BiliTouch/releases)

原作者、上游项目与许可证信息见[鸣谢](docs/ACKNOWLEDGEMENTS.md)。

触屏横滑翻页、手势调节进度/亮度/音量、长按加速、触控板交互与更易点按的操作入口是本分支的重点。

*WPF · .NET 10 · LibVLC*

<br>

**BiliTouch v0.2.1**
[![Platform](https://img.shields.io/badge/Windows-10%2B-0078D4?logo=windows11&logoColor=white&style=flat-square)](#-下载安装)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white&style=flat-square)](#-从源码构建)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue?style=flat-square)](LICENSE)

[上游鸣谢](docs/ACKNOWLEDGEMENTS.md) · [功能特性](#-功能特性) · [快捷键](#-快捷键) · [从源码构建](#-从源码构建)

<br>

<img width="100%" alt="BiliTouch 0.2.1 实际首页" src="docs/screenshot-home-current.png">

</div>

> [!IMPORTANT]
> 本项目为非官方、非商业的开源软件，与哔哩哔哩官方无任何关联；视频内容与版权归哔哩哔哩及各 UP 主所有。

## ✨ 功能特性

**播放**

- LibVLC 内核，DASH 音视频双流，最高 4K，HEVC 优先选流
- 默认画质可预选（该清晰度不可用时自动降级），线路自动测速 / 手动指定，免登录 1080P
- 倍速播放、视频「铺满窗口」与全屏
- 按住鼠标左/右键 2 倍速快进，长视频快进体验顺滑
- 听视频模式，把 B 站当播客用
- 短视频本地合流：≤ 3 分钟的视频自动下载 1080P 后合流为 MP4 缓存播放
- SponsorBlock 跳过赞助片段（默认关闭，需手动开启）

**弹幕**

- 滚动 / 顶部 / 底部全类型渲染
- 智能屏蔽、关键词黑名单、彩色开关、显示区域调节

**浏览**

- 独立直播间：实时画面弹幕、可滚动聊天侧栏、醒目留言（SC）置顶与倒计时、弹幕发送、清晰度切换、断线重连和开播/下播状态；无礼物购买入口

- 扫码登录，历史 / 收藏 / 稍后再看 / 动态 / 消息
- 首页推荐 / 热门 / 排行榜 / 直播 / 追番，分区浏览与搜索

**个性化**

- 亮色 / 暗色 / 跟随系统三态主题，主题色可自定义
- 亚克力 / 云母窗口材质
- 托盘常驻，自动检查更新

## ⌨️ 快捷键

| 按键 | 功能 |
| :---: | :--- |
| <kbd>Space</kbd> | 播放 / 暂停 |
| <kbd>←</kbd> <kbd>→</kbd> | 快退 / 快进 5 秒（按住 <kbd>Shift</kbd> 为 30 秒） |
| <kbd>↑</kbd> <kbd>↓</kbd> | 音量增减 |
| <kbd>M</kbd> | 静音切换 |
| <kbd>F</kbd> | 全屏 |
| <kbd>D</kbd> | 弹幕开关 |
| <kbd>L</kbd> | 点赞 |
| <kbd>U</kbd> | 打开 UP 主主页 |
| <kbd>Esc</kbd> | 逐级返回（全屏 → 铺满窗口 → 关闭） |

## 📥 下载安装

本地发行包位于上级 `dist/BiliTouch-0.2.1`。BiliTouch 独立发布渠道尚未配置，程序不会将 BiliTouch 上游安装包作为本分支更新。原上游提供的发行形式如下：

| 发行包 | 说明 |
| :--- | :--- |
| **便携版 zip** | 自包含，解压即用，无需安装 .NET 运行时 |
| **安装器** | 需要 .NET 10 或更高版本的桌面运行时（安装器会自动检测并引导下载；只装了 .NET 11 也可以直接用） |

## 🧰 从源码构建

需要 Windows 10+ 与 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（x64）。

```powershell
cd BiliTouch
dotnet build BiliTouch/BiliTouch.csproj -c Release
```

LibVLC 原生库由 NuGet 包（`VideoLAN.LibVLC.Windows`）自动部署，无需手动安装。
完整发布（便携版 zip + Inno 安装器载荷）运行仓库根目录的 `发布.ps1`。

## ⚖️ 许可

本项目基于 [GPL-3.0](LICENSE) 协议开源。
