# 影之刃零原版鼠标

从本机 Steam 游戏的 `ProjectPBZ-Windows.pak` 只读提取 `ProjectPBZ/Content/Slate/Cursors/PBZCursor*.png` 五档原图。应用采用游戏自带 30×30 PNG，原样封装为 CUR；尖端热点适配为 (7,1)。来源、哈希和权利说明见 `../BiliTouch/Assets/Cursors/SOURCES.md`。工作区的 `theme-research/phantom-blade-zero/cursor/` 保留全部原图、配置与索引。

影之刃零装扮下，普通与按下状态均采用原版银白刀锋指针；游戏没有提供独立按下态，因此不另行绘制。默认和星引擎继续使用原有粉／蓝指针。主题变化立即更新原生窗口和已打开的 WebView2 页面；网页导航完成后同步当前主题，保留编辑、缩放与隐藏光标语义。网页图片 URL 缓存，更新时复用监听器。

验证：Debug 构建成功（0 错误，10 项既有警告）；`--web-cursor` 18 项通过，包括原生热点、旧窗口的缓存光标、网页原地切换与重新导航、编辑／缩放／隐藏指针、切回星引擎；`--phantom-blade` 67 项通过。日志在工作区 `artifacts/pbz-cursor-{build,test,theme-regression}.log`。开发预览已更新，未重做安装器、便携包或上传发布。
