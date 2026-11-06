# GitHub 发布约定

Release 的上传附件只保留一个 `BiliTouch-版本号-setup.exe`。便携包、独立源码 ZIP、README、LICENSE、校验文件和测试 JSON 不作为下载附件；本机构建和校验记录可以继续保留。对应版本的源码由 Git 标签及 GitHub 自动源码下载提供，许可和第三方声明保留在仓库中。

发布前完成安装器载荷与起播验证，推送对应标签，再从仓库目录运行：

```powershell
python Publish/github_release.py --version 0.2.3 --setup ../dist/BiliTouch-0.2.3/BiliTouch-0.2.3-setup.exe --notes docs/RELEASE-0.2.3-2026-11-06.md
```

脚本只允许向 `zhongsanZN/BiliTouch` 发布，校验安装器 SHA-256 后再公开 Release，并清除该 Release 的其他上传附件。GitHub 自带的 Source code ZIP/TAR 下载项由平台生成，无法当作上传附件删除。

历史版本按原构建日期标注，正式应用版本的已有标签保持不变。更名前 `BiliDesk 1.5.4 touch.2～touch.9` 对应历史编号 `0.0.2～0.0.9`；无法确认匹配源码和安装器的早期记录只保留历史说明，其标签位于 `history/early-builds` 归档分支，不是伪造的旧应用源码。没有原 setup 的版本不使用新代码重制旧安装器。
