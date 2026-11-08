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

所有美术权利归 S-GAME 及相应权利人。公开可下载不等于授予再分发许可；仓库代码许可证不覆盖这些游戏美术。本素材按用户要求用于影之刃零主题适配。

首页标志 HomeLogo.xaml 由用户提供的官网 logo-cn.svg 的 292 条路径转换，保留原始视框和不透明度。窗口关闭按钮直接使用同目录的 close-icon.png 透明轮廓。

2026-11-08 按用户逐项指定接入 `ui_*.png`：来自本机游戏提取图集，编号对应 `Ixxxx`。原 PNG 逐字节复制，来源及 SHA-256 见 `UI-SOURCES.json`。`ui_gramophone.png` 为用户提供的留声机透明图片。

界面图标按原透明边界适配。单色反色、未收藏的中性色、收藏后的蓝色及小图标状态色由绘制时使用原图透明遮罩着色完成，不重绘轮廓；原始 PNG 保持不变。设置／首页／动态按深浅模式分别选择用户提供的两张源图。留声机静音时加斜线，保留原有静音功能；画质图标旁保留当前清晰度文字。详细位置及状态表见 `docs/PHANTOM-BLADE-SELECTED-UI-2026-11-08.md`。

完整未点赞火焰 I0395 后续改为保留原 RGB 灰度层次，并按深浅背景展开灰色烟纹与白色火焰的明暗差，替代此前抹掉内部层次的统一遮罩着色。仅生成缓存中的显示纹理，逐像素保留原透明度，不改写源 PNG。
