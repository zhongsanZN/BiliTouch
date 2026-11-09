# BiliTouch 发布脚本
#
# 一次产出两套(2026-09-26 定):
#   1) 便携版(zip): 自包含 + 单文件压缩 —— 用户机器**不用装 .NET**, 解压即用;
#   2) 安装器载荷: 框架依赖(只含 app + LibVLC, ~67MB) —— 配 Publish\installer.iss 打 Inno 安装包,
#      .NET 桌面运行时(8+)由安装器检测、全无时引导下载 .NET 10(见 installer.iss 的 [Code];
#      程序在 csproj 配了 RollForward=Major, 只有 .NET 10+ 也能跑)。
#
# 产物统一落在仓库根的 Publish\ (installer.iss 就在那里, 它的相对路径按同级目录写):
#   Publish\BiliTouch-便携版\            Publish\BiliTouch-安装器-files\
#   Publish\BiliTouch-<ver>-portable.zip
# (2026-10-03 由 "Pulish" 更正为 "Publish" —— 这是拼写错误, 目录、脚本、.gitignore、.iss 一起改。)
#
# 用法(在仓库根目录):
#   powershell -ExecutionPolicy Bypass -File 发布.ps1 [-Version 1.5.3]
#
# 注意: 这里的默认版本号要跟 App.AppVersion / csproj 的 <Version> / installer.iss 的
# MyAppVersion 一起改 —— 它只决定 zip 的文件名, 不参与程序内部显示。
param([string]$Version = "0.2.13")

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
# 优先使用本项目配套 SDK，避免 PATH 里只有桌面运行时而无法构建。
$bundledDotnetRoot = [IO.Path]::GetFullPath((Join-Path $Root '..\.tools\dotnet'))
if (Test-Path -LiteralPath (Join-Path $bundledDotnetRoot 'sdk')) {
    $env:DOTNET_ROOT = $bundledDotnetRoot
    $env:PATH = $bundledDotnetRoot + [IO.Path]::PathSeparator + $env:PATH
}
$sdkList = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or !($sdkList -match '^10\.')) {
    throw '构建需要 .NET 10 SDK；仅安装桌面运行时无法构建。'
}

$Out = Join-Path $Root "Publish"
$PortableDir = Join-Path $Out "BiliTouch-便携版"
$InstallerDir = Join-Path $Out "BiliTouch-安装器-files"

# Publish 根目录只建不清理: 里面还放着 installer.iss, 任何 Remove-Item Publish\* 都会连带删掉它。
if (-not (Test-Path $Out)) { New-Item -ItemType Directory -Path $Out -Force | Out-Null }

# ============================================================
# ★★★ 两个**产物目录**必须先清空 —— 2026-11-02 的事故就是漏了这一步
#
# dotnet publish -o 不是"干净输出": 它在原有目录上增量复制, 判据是"源文件更新才覆盖"。
# .NET 8 → .NET 10 升级后, 目录里留着 .NET 8 的 WPF 原生库
# (wpfgfx_cor3.dll / PresentationNative_cor3.dll / PenImc_cor3.dll, 文件版本 8.0.x),
# 它们的修改时间恰好比 .NET 10 运行时包里那几份**更晚** ⇒ 复制一直被跳过, 于是一直留在原地。
# 而单文件发布**不打包 WPF 原生库**(它们就是散在 exe 旁边的), 于是 .NET 10 的托管代码去调
# .NET 8 的 PresentationNative_cor3.dll 里根本不存在的导出 LsDisableSpecialCharacterLigature:
#     System.EntryPointNotFoundException: Entry point was not found.
#     at MS.Internal.WindowsBase.NativeMethodsSetLastError.LsDisableSpecialCharacterLigature(...)
#     at System.Windows.Interop.HwndTarget..ctor(...)   ← 首个窗口一显示就炸
# 1.5.1 便携版就是这么发出去的(1.5.0 同样中招)。清目录, 再配合下面第 3 步的版本校验。
# ============================================================
foreach ($dir in @($PortableDir, $InstallerDir)) {
    $resolved = [IO.Path]::GetFullPath($dir)
    $publishRoot = [IO.Path]::GetFullPath($Out)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $publishRoot -or
        [IO.Path]::GetFileName($resolved) -notin @('BiliTouch-便携版','BiliTouch-安装器-files')) {
        throw "拒绝清理超出发布目录的路径: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

Write-Host "== 1/5 便携版(自包含 + 压缩) =="
dotnet publish (Join-Path $Root "BiliTouch\BiliTouch.csproj") -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
    -o $PortableDir
if ($LASTEXITCODE -ne 0) { throw "便携版发布失败" }

Write-Host "== 2/5 安装器载荷(依赖 .NET 10+ 桌面运行时) =="
dotnet publish (Join-Path $Root "BiliTouch\BiliTouch.csproj") -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=false `
    -o $InstallerDir
if ($LASTEXITCODE -ne 0) { throw "安装器载荷发布失败" }

foreach ($payload in @($PortableDir,$InstallerDir)) {
    foreach ($required in @('libvlc.dll','libvlccore.dll','plugins\demux\libmp4_plugin.dll','plugins\codec\libavcodec_plugin.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $payload "libvlc\win-x64\$required"))) { throw "发布包缺少原生播放依赖: $payload / $required" }
    }
}
Write-Host "== 3/5 校验 WPF 原生库版本(防旧 .NET 残留混进包) =="
# 只查 WPF 那三个 dll: 它们的文件版本大版本号必须等于 app 目标框架的大版本(net10.0 → 10)。
# D3DCompiler_47_cor3.dll 是 Windows SDK 的、vcruntime140_cor3.dll 是 VC 运行时的,
# 版本号与 .NET 无关, 排除在外。
$tfm = ([xml](Get-Content (Join-Path $Root "BiliTouch\BiliTouch.csproj") -Raw)).Project.PropertyGroup |
       ForEach-Object { $_.TargetFramework } | Where-Object { $_ } | Select-Object -First 1
if (-not ($tfm -match '^net(\d+)\..*$')) { throw "无法从 csproj 读出 TargetFramework: $tfm" }
$expectedMajor = [int]$Matches[1]
$wpfNative = @("wpfgfx_cor3.dll", "PresentationNative_cor3.dll", "PenImc_cor3.dll")
foreach ($dll in Get-ChildItem (Join-Path $PortableDir "*_cor3.dll") | Where-Object { $_.Name -in $wpfNative }) {
    $major = 0
    if ($dll.VersionInfo.FileVersion -match '^\s*(\d+)') { $major = [int]$Matches[1] }
    if ($major -ne $expectedMajor) {
        $msg = "便携版里 $($dll.Name) 是 $($dll.VersionInfo.FileVersion), 与 $tfm 不符 —— " +
               "输出目录混进了旧 .NET 的残留, 这种包发出去会启动即报 EntryPointNotFoundException。"
        throw $msg
    }
    Write-Host ("  {0,-28} {1}" -f $dll.Name, $dll.VersionInfo.FileVersion)
}

# ============================================================
# ★★★ 4/5 把产物从 Low 完整性提回 Medium —— **不做这一步, 发出去的包全是坏的**
#
# 为什么需要: 本机(DSH 沙箱)会给**工作区内**新建的文件打 Low 完整性标签
# (`Mandatory Label\Low Mandatory Level`)。带这个标签的 exe 一旦被跑起来,
# 进程就以**低完整性**运行, 于是:
#   ① 读不了机器级证书存储 ⇒ VLC 的 GnuTLS 无法校验 B 站证书 ⇒ **视频一帧都放不出**
#      (表现:"视频播放出错, 请检查网络或尝试其他清晰度")。
#      ★ 这正是"bin\Debug 下播放正常、便携版却放不出"的原因 —— 那个目录是 Medium
#        (2026-10-11 修过一次, 见长期记忆)。
#   ② Inno 安装包更直接: 它以低完整性运行, 连在 %TEMP% 下建自己的工作目录都被拒
#      ⇒ 报 `Setup was unable to create the directory "...\Temp\is-XXXX.tmp". Error 5: 拒绝访问。`
#
# 少了它, 本地怎么测都是好的, 只有用户拿到包才会炸 —— 2026-10-17 一次性踩掉
# "便携版放不出视频 + 安装包打不开"两件事, 就是这个原因。
#
# ★ 必须对**目录**递归设(-T), 不能只设文件: 目录本身带 Low 时, 之后新写进去的文件
#   (紧接着 ISCC 生成的 setup.exe)会**继承 Low**, 于是又白干了。
# ★ 顺序必须在"打包 zip / 编译安装器"**之前**, 否则后一步产出的文件又变成 Low。
# ★ 这条在 CI 或别的机器上跑是无害的空操作(那些文件本来就不是 Low)。
# ============================================================
Write-Host "== 4/5 提回 Medium 完整性(修 Low 标签导致的放不出视频 / 安装包打不开) =="
icacls $Out /setintegritylevel Medium /T /C | Out-Null

Write-Host "== 5/5 打包便携版 =="
# 文件名用纯 ASCII: GitHub 会把非 ASCII 的资产名截掉(1.2.0 的 zip 在下载页显示成 "BiliTouch-1.2.0-.zip")
$zip = Join-Path $Out "BiliTouch-$Version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $PortableDir "*") -DestinationPath $zip

# Setup is a required deliverable, not a manual follow-up.
$isccCandidates = @((Join-Path $Root '..\.tools\inno612\app\ISCC.exe'), "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (!$iscc) { throw "找不到 Inno Setup 编译器，不能将本次发布标记为完成" }
# Inno 的资源更新会在输出目录重命名临时文件。部分工作区继承了 DENY DeleteChild，
# 普通文件写入成功也仍会在 EndUpdateResource(110) 失败；在独立构建目录编译后复制。
$setupStage = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('BiliTouch-build\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $setupStage -Force | Out-Null
try {
    & $iscc ("/DMySourceDir=" + $InstallerDir) ("/O" + $setupStage) (Join-Path $Root 'Publish\installer.iss')
    if ($LASTEXITCODE -ne 0) { throw "setup.exe 编译失败" }
    Copy-Item -LiteralPath (Join-Path $setupStage "BiliTouch-$Version-setup.exe") -Destination $Out -Force
} finally {
    $stagedSetup = Join-Path $setupStage "BiliTouch-$Version-setup.exe"
    if (Test-Path -LiteralPath $stagedSetup) { Remove-Item -LiteralPath $stagedSetup -Force }
    # 不递归删除，以免误删编译器留下的额外诊断文件。
    if (!(Get-ChildItem -LiteralPath $setupStage -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $setupStage }
}
Write-Host "完成:"
Write-Host "  安装器     : $(Join-Path $Out "BiliTouch-$Version-setup.exe")"
Write-Host "  便携版 zip : $zip"

# GitHub Release 只上传 setup.exe；便携包留作本机构建产物。参见 Publish/RELEASE_POLICY.md。
