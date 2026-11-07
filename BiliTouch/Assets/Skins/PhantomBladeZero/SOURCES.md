# 影之刃零 · 影境

2026-11-07 从 S-GAME 官方网站 https://pbz.s-game.com/zh-CN/ 收集 19 份，并检查用户提供的官网 ZIP 共 24 份。网络原文件、直链、大小及 SHA-256 见 `theme-research/phantom-blade-zero/manifest.json`；用户压缩包及文件哈希、尺寸见 `user-archive/manifest.json`。同名文件不覆盖原始研究资料。

- `stone.png`、`mist.png`：官网原始场景储备；按用户纠正，当前顶部和侧栏不再调用这些内容图。
- `head_myself_bg.png`：用户官网包 `news-bg.jpg`（3840×2160），背身剑客与山水，个人页主图。
- `brush.png`：用户官网包 `news-preview-bg.png`，原始透明笔刷，边界与互动材质。
- `paper.png`：用户官网包 `popwin.png`，原始透明宣纸，用于卡片区、视频详情内容背景与个人卡片，已撤出顶部与侧栏。
- `weave.png`：用户官网包 `preorder-platform-bg.png` 原文件，织物细纹；`foil.png`：用户官网包 `preorder-edition-bg-deluxe.png` 原文件，金箔材质。用于顶部与侧栏，渲染时为织物保留原 RGB 纤维并单独控制透明度，避免原图 12% alpha 导致暗色模式下纹理消失。
- `divider.png`：用户官网包 `wishlist-divider.png`，原始细刀痕，用于页头横向边界与侧栏旋转后的纵向边界，取代实色分隔线；浅色与暗色分别按原图透明轮廓调整线条颜色。
- `duelist.png`：用户官网包 `1.png`，保留原透明边缘的剑客储备图，已撤出页头。
- `logo.png`：`assets/icons/logo-cn.svg`，官方中文标志，使用 resvg 栅格化；`logo_dark.png` 仅改变颜色、保留透明轮廓以适配浅色背景。
- `hat.png`：`assets/icons/shape-icon-hat.svg`，官方斗笠储备图；不再作为点赞、投币、收藏的统一反馈。
- `memory_flower.png`、`jade.png`：用户于本轮提供的透明原图，分别用于收藏与投币。完整原始文件另存工作区 `theme-research/phantom-blade-zero/interaction-originals/`，应用内原样复制，不重绘、不改色。
- `play.png`：用户官网包中的原始播放图标，逐字节复制；统一用于视频卡片、动态卡片、播放器、网页小窗与独立直播间的播放态，暂停态保留暂停图形。
- `like_off.png`：[异铜锭](https://wiki.biligame.com/yingzhiren/异铜锭)，点赞未激活；`like_on.png`：[万锻天外异铜](https://wiki.biligame.com/yingzhiren/万锻天外异铜)，点赞后及成功反馈。按用户指定选材，下载 Wiki 原始 PNG，不使用网页放大缩略图。

顶部与侧栏仅使用 `weave.png`、`foil.png` 与 `divider.png`，没有人物、建筑或标志内容。水墨纸纹改放在视频卡片下方和详情内容背景。材料导航 106 个条目索引、10 种候选原图及 SHA-256 见工作区 `materials/nav-index.json` 和 `materials/manifest.json`。候选长明灯火等不用于本次点赞。

衍生过程：`.tools/prepare-pbz.py`。位图保持原始构图，仅缩小到最大 1920×1080 并转换 PNG；原 SVG 和 JPG/PNG 独立保留。本地渲染使用裁切、透明度与遮罩适配，不使用生成式重绘。

所有美术权利归 S-GAME 及相应权利人。公开可下载不等于授予再分发许可；仓库代码许可证不覆盖这些游戏美术。当前为用户要求的本地装扮制作，未发布。

首页标志 HomeLogo.xaml 由用户提供的官网 logo-cn.svg 的 292 条路径转换，保留原始视框和不透明度。窗口关闭按钮直接使用同目录的 close-icon.png 透明轮廓。
