# GitHub 发布约定

自平板模式版本起，每次 GitHub Release 同步上传 **两个架构的安装器附件**：
1. `BiliTouch-版本号-setup.exe`（Windows x64 · Intel / AMD 64 位设备；保持历史命名以兼容所有老版本内置更新）
2. `BiliTouch-版本号-setup-arm64.exe`（Windows ARM64 · ARM 版 Surface 等骁龙 Windows 设备）

便携包（`BiliTouch-版本号-portable.zip` / `BiliTouch-版本号-portable-arm64.zip`）、独立源码 ZIP、README、LICENSE、校验文件和测试 JSON 不作为下载附件；本机构建和校验记录可以继续保留。对应版本的源码由 Git 标签及 GitHub 自动源码下载提供，许可和第三方声明保留在仓库中。

发布前运行 `发布.ps1` 一键构建双架构产物并完成安装器载荷、PE 架构头与起播验证，推送对应标签，再从仓库目录运行：

```powershell
python Publish/github_release.py --version 0.3.6 --setup Publish/BiliTouch-0.3.6-setup.exe --setup-arm64 Publish/BiliTouch-0.3.6-setup-arm64.exe --notes docs/release-notes/v0.3.6.md
```

（若省略 `--setup-arm64`，脚本会自动校验 `--setup` 同目录下的 `BiliTouch-版本号-setup-arm64.exe`。）

脚本只允许向 `zhongsanZN/BiliTouch` 发布，校验两份安装器 SHA-256 与字节大小后再公开 Release，并清除该 Release 的其他上传附件。GitHub 自带的 Source code ZIP/TAR 下载项由平台生成，无法当作上传附件删除。

历史版本按原构建日期标注，正式应用版本的已有标签保持不变。更名前 `BiliDesk 1.5.4 touch.2～touch.9` 对应历史编号 `0.0.2～0.0.9`；无法确认匹配源码和安装器的早期记录只保留历史说明，其标签位于 `history/early-builds` 归档分支，不是伪造的旧应用源码。没有原 setup 的版本不使用新代码重制旧安装器。

2026-11-08 补充：0.2.4～0.2.6 仅找到了原始安装器与构建验证，未找到可确认匹配的完整源码快照。此三版采用明确标注的历史构建归档标签，不以当前源码冒充旧版源码；0.2.7 提供当前完整源码标签。发布脚本根据标签是否含应用工程区分源码链接与归档链接，历史归档不设为 Latest。
