# B 站首页实际页面与线上主包核查（2026-11-08）

## 本轮实际完成的范围

通过 Codex 内置浏览器打开 `https://www.bilibili.com/`，核对 URL、标题、首页 DOM 和页面实际加载的脚本/CSS URL。此浏览器未登录，视口为 531×777 CSS px。Edge 扩展能被发现，但创建测试页被扩展界面阻挡。

内置浏览器可读取 DOM、执行键盘翻页；低层 CDP Runtime.evaluate 多次超时，连续采样也未成功。因此本报告包含**真实页面的定点观察、一次已完成的键盘翻页、线上脚本静态核查**，不包含物理触控板惯性曲线、wheel 事件时序、帧率或延迟的实测。未检查视频评论页或登录后的动态页。

## 已观察到的事实

| 项目 | 初始读取 | 一次完成的 PageDown 后读取 |
|---|---:|---:|
| document.scrollingElement | HTML | HTML |
| 页面滚动 Y | 0 | 1069.77783203125 |
| 页面总高度 | 1832 | 2641 |
| .bili-video-card DOM 数量 | 21 | 30 |

初始根节点的计算 scroll-behavior 为 auto。主容器 class 为 bili-feed4-layout。首张卡片骨架有 `--cover-radio:56.25%`，CSS 的封面占位使用 `padding-top:var(--cover-radio)`。

上表两个状态不是逐帧连续采样：不能把最终 Y 当成单个触控板包的距离，不能由 PageDown 推算触控板灵敏度或惯性。后续一条组合翻页命令超时，最终状态未知，没有用它补充数据。

## 实际加载的线上资源

- [首页主脚本 index-84fb5632.js](https://s1.hdslb.com/bfs/static/shanks/laputa-home/assets/index-84fb5632.js)
- [首页 CSS index-4d416e23.css](https://s1.hdslb.com/bfs/static/shanks/laputa-home/assets/index-4d416e23.css)

资源 URL 来自本轮已打开页面的 script/link DOM，随后下载到独立 artifacts 目录核查。

主脚本 SHA256：271ecaca131b61e909601b56c5a652ec8014ad0409bfefb863b832a2c14c0465。

CSS SHA256：4d416e23b775f6de703d334c94b74a9f1d7e9be8869733b0408dd324bcf1a05f。

## 主脚本支持的逻辑

以下位置是解码后字符串的零基字符偏移，不是行号。

1. **没有在该主包找到 wheel / mousewheel 字样。** 主包的 6 处 preventDefault 分别出现在 Vue 事件修饰器通用实现、禁用按钮、轮播键盘操作、logo 点击、搜索提交、稍后再看点击，未发现接管整页滚轮后按 delta 重算 scrollTop 的逻辑。结合实际 HTML 滚动位置和 auto 样式，证据支持首页依赖浏览器原生页面滚动。未取得运行时所有监听清单，不能将这一结论扩大为所有 B 站页面或所有第三方脚本均无滚轮监听。
2. **滚动监听用于观察与业务触发。** `_b`（215967 附近）读取 scrollTop/clientHeight/scrollHeight，默认事件选项 passive:true。它更新方向、边界、isScrolling 等状态，未在每帧重写位置。首页页头吸顶监听还做 200ms 节流；返回顶部按钮的 scroll 监听显式 passive:true。
3. **初始内容会预先补足。** `Id`（237829 附近）反复调用加载回调，直到 body.scrollHeight 大于 innerHeight+offset，或达到次数/结束条件。首页传入 offset:700；通用默认最大 10 次、间隔 300ms。这是内容补足策略，不是惯性时间常数。
4. **下一批内容由加载锚点触发。** `e8`（1464961 附近）读锚点 getBoundingClientRect 的 bottom，与 innerHeight 比较；首页调用 `e8(O,Ne,{offset:200})`（1548533 附近），默认节流 200ms。锚点进入视口下方约 200px 范围或已经过该位置时触发；这不是“只在整页最后 200px 加载”，因为锚点后还有骨架占位行。
5. **防止加载重叠，并保留骨架行。** `Ne`（1548141 附近）检查 loadingMore、其他加载状态和刷新状态，再发起 loadMore；模板在 load-more-anchor 周围预置骨架卡片。封面骨架先保留比例空间，有助于避免资源回来后突然改变几何尺寸；没有做用户账号上的布局偏移量实测。

## 对项目的含义

B 站这个首页样本没有提供一套可直接复制到 WPF 的“触控板惯性公式”。需要分别对照浏览器的滚动执行路径，以及 B 站的内容预备/接入策略。不能把网页的 scroll 业务监听误当成驱动滚动的算法；也不能只因为两边都按像素滚动，就认为两边的输入、布局与呈现路径相同。

Chromium 的原生滚动可以由合成线程执行；是否需要等待主线程，取决于非被动输入监听等条件。[Chromium 当前滚动原因定义](https://chromium.googlesource.com/chromium/src/+/HEAD/cc/input/main_thread_scrolling_reason.h)。这是浏览器机制说明，不是本轮已测出该页面始终在合成线程滚动。

对 WPF 后续有依据的检查是：加载前保留稳定尺寸、在内容耗尽前预取、把网络结果接入和控件生成的 UI 工作控制在预算内，以及检查起步/惯性时 UI 线程忙碌是否阻断滚动提交。不能依据本报告直接宣布某一项就是用户全部卡顿的原因。

## 证据文件与未完成项

`artifacts/bilibili-web-scroll-2026-11-08/` 内保存原始主包、CSS、observed-page-state.json、source-excerpts.json 和 focused-source-evidence.json。

待实测：物理触控板 wheel 包与滚动位置的同步记录、落指/抬指/重新接触分段、惯性阶段的呈现间隔、主线程阻塞下的滚动、视频评论页的实际滚动行为。本轮未更改项目功能代码或滚动参数。
