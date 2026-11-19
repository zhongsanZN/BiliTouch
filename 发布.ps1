# BiliTouch 双架构发布脚本（x64 + ARM64）
#
# 默认同时构建 Windows x64（Intel/AMD）与 Windows ARM64（ARM 版 Surface 等设备）：
#   1) 便携版(zip): 自包含 + 单文件压缩 —— 用户机器**不用装 .NET**, 解压即用;
#      · Publish\BiliTouch-<ver>-portable.zip         (x64)
#      · Publish\BiliTouch-<ver>-portable-arm64.zip   (ARM64)
#   2) 安装器(setup.exe): 框架依赖(只含 app + 对应架构 LibVLC) —— 配 Publish\installer.iss 打 Inno 安装包,
#      .NET 10+ 桌面运行时由安装器按对应架构检测并引导下载。
#      · Publish\BiliTouch-<ver>-setup.exe            (x64)
#      · Publish\BiliTouch-<ver>-setup-arm64.exe      (ARM64)
#
# 用法(在仓库根目录):
#   powershell -ExecutionPolicy Bypass -File 发布.ps1 [-Version 0.3.5] [-Arch all|x64|arm64]
#
# 注意: 默认版本号自动从 BiliTouch\BiliTouch.csproj 的 <Version> 读取（单一真理源），
# 也可通过 -Version 显式覆盖。
param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [ValidateSet("all", "x64", "arm64")][string]$Arch = "all",
    [switch]$KeepStaging
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$CsprojPath = Join-Path $Root "BiliTouch\BiliTouch.csproj"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = ([xml](Get-Content $CsprojPath -Raw)).Project.PropertyGroup |
               ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "无法从 $CsprojPath 读取 <Version>，请显式传入 -Version"
    }
    $Version = $Version.Trim()
}
# 优先使用本项目配套 SDK 与离线 NuGet 缓存，避免 PATH 里只有桌面运行时而无法构建。
$bundledDotnetRoot = [IO.Path]::GetFullPath((Join-Path $Root '..\.tools\dotnet'))
if (Test-Path -LiteralPath (Join-Path $bundledDotnetRoot 'sdk')) {
    $env:DOTNET_ROOT = $bundledDotnetRoot
    $env:PATH = $bundledDotnetRoot + [IO.Path]::PathSeparator + $env:PATH
}
$bundledNuget = [IO.Path]::GetFullPath((Join-Path $Root '..\.tools\nuget'))
if (Test-Path -LiteralPath $bundledNuget) {
    $env:NUGET_PACKAGES = $bundledNuget
}
$sdkList = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or !($sdkList -match '^10\.')) {
    throw '构建需要 .NET 10 SDK；仅安装桌面运行时无法构建。'
}

$Out = Join-Path $Root "Publish"
# Publish 根目录只建不清理: 里面还放着 installer.iss, 任何 Remove-Item Publish\* 都会连带删掉它。
if (-not (Test-Path $Out)) { New-Item -ItemType Directory -Path $Out -Force | Out-Null }

$tfm = ([xml](Get-Content $CsprojPath -Raw)).Project.PropertyGroup |
       ForEach-Object { $_.TargetFramework } | Where-Object { $_ } | Select-Object -First 1
if (-not ($tfm -match '^net(\d+)\..*$')) { throw "无法从 csproj 读出 TargetFramework: $tfm" }
$expectedMajor = [int]$Matches[1]

# Setup is a required deliverable, not a manual follow-up.
$isccCandidates = @((Join-Path $Root '..\.tools\inno612\app\ISCC.exe'), "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (!$iscc) { throw "找不到 Inno Setup 编译器，不能将本次发布标记为完成" }

function Get-PeMachine([string]$filePath) {
    $fs = [IO.File]::OpenRead($filePath)
    try {
        $br = New-Object IO.BinaryReader($fs)
        if ($br.ReadUInt16() -ne 0x5A4D) { throw "非 PE 文件: $filePath" }
        $fs.Seek(0x3C, [IO.SeekOrigin]::Begin) | Out-Null
        $peOffset = $br.ReadInt32()
        $fs.Seek($peOffset, [IO.SeekOrigin]::Begin) | Out-Null
        if ($br.ReadUInt32() -ne 0x00004550) { throw "PE 头签名无效: $filePath" }
        return $br.ReadUInt16()
    } finally {
        $fs.Dispose()
    }
}

$targetArchs = if ($Arch -eq "all") { @("x64", "arm64") } else { @($Arch) }
$fileVersion = ($Version -replace '-.*$', '') + '.0'
$deliverables = @()

foreach ($currentArch in $targetArchs) {
    $rid = "win-$currentArch"
    $otherRid = if ($currentArch -eq "arm64") { "win-x64" } else { "win-arm64" }
    $expectedMachine = if ($currentArch -eq "arm64") { 0xAA64 } else { 0x8664 }
    $archSuffix = if ($currentArch -eq "arm64") { "-arm64" } else { "" }

    $PortableDirName = "BiliTouch-便携版$archSuffix"
    $InstallerDirName = "BiliTouch-安装器-files$archSuffix"
    $PortableDir = Join-Path $Out $PortableDirName
    $InstallerDir = Join-Path $Out $InstallerDirName

    Write-Host ""
    Write-Host "================ 构建目标: $rid (v$Version) ================"

    # 两个产物目录必须先清空，防止旧 .NET 或旧架构原生库残留
    foreach ($dir in @($PortableDir, $InstallerDir)) {
        $resolved = [IO.Path]::GetFullPath($dir)
        $publishRoot = [IO.Path]::GetFullPath($Out)
        if ([IO.Path]::GetDirectoryName($resolved) -ne $publishRoot -or
            [IO.Path]::GetFileName($resolved) -notin @($PortableDirName, $InstallerDirName)) {
            throw "拒绝清理超出发布目录的路径: $resolved"
        }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }

    Write-Host "== 1/5 [$rid] 便携版(自包含 + 压缩) =="
    dotnet publish $CsprojPath -c $Configuration -p:Optimize=true -p:Version=$Version -r $rid `
        --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
        -o $PortableDir
    if ($LASTEXITCODE -ne 0) { throw "[$rid] 便携版发布失败" }

    Write-Host "== 2/5 [$rid] 安装器载荷(依赖 .NET 10+ 桌面运行时) =="
    dotnet publish $CsprojPath -c $Configuration -p:Optimize=true -p:Version=$Version -r $rid `
        --self-contained false -p:PublishSingleFile=false `
        -o $InstallerDir
    if ($LASTEXITCODE -ne 0) { throw "[$rid] 安装器载荷发布失败" }

    Write-Host "== 3/5 [$rid] 校验原生播放依赖、PE 架构头与 WPF 原生库版本 =="
    foreach ($payload in @($PortableDir, $InstallerDir)) {
        foreach ($required in @('libvlc.dll','libvlccore.dll','plugins\demux\libmp4_plugin.dll','plugins\codec\libavcodec_plugin.dll')) {
            if (!(Test-Path -LiteralPath (Join-Path $payload "libvlc\$rid\$required"))) {
                throw "发布包缺少原生播放依赖: $payload / libvlc\$rid\$required"
            }
        }
        if (Test-Path -LiteralPath (Join-Path $payload "libvlc\$otherRid")) {
            throw "发布包混入了跨架构 LibVLC 目录: $payload / libvlc\$otherRid"
        }
        foreach ($bin in @((Join-Path $payload 'BiliTouch.exe'), (Join-Path $payload "libvlc\$rid\libvlc.dll"))) {
            $machine = Get-PeMachine $bin
            if ($machine -ne $expectedMachine) {
                throw ("PE 架构不匹配: $bin 期望 0x{0:X4}，实际 0x{1:X4}" -f $expectedMachine, $machine)
            }
        }
    }

    $wpfNative = @("wpfgfx_cor3.dll", "PresentationNative_cor3.dll", "PenImc_cor3.dll")
    foreach ($dll in Get-ChildItem (Join-Path $PortableDir "*_cor3.dll") | Where-Object { $_.Name -in $wpfNative }) {
        $major = 0
        if ($dll.VersionInfo.FileVersion -match '^\s*(\d+)') { $major = [int]$Matches[1] }
        if ($major -ne $expectedMajor) {
            $msg = "[$rid] 便携版里 $($dll.Name) 是 $($dll.VersionInfo.FileVersion), 与 $tfm 不符 —— " +
                   "输出目录混进了旧 .NET 的残留, 这种包发出去会启动即报 EntryPointNotFoundException。"
            throw $msg
        }
        Write-Host ("  [{0}] {1,-28} {2}" -f $rid, $dll.Name, $dll.VersionInfo.FileVersion)
    }

    Write-Host "== 4/5 [$rid] 提回 Medium 完整性 =="
    icacls $Out /setintegritylevel Medium /T /C | Out-Null

    Write-Host "== 5/5 [$rid] 打包便携版与编译安装器 =="
    $zipName = "BiliTouch-$Version-portable$archSuffix.zip"
    $setupName = "BiliTouch-$Version-setup$archSuffix.exe"
    $zip = Join-Path $Out $zipName
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $PortableDir "*") -DestinationPath $zip

    $setupStage = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('BiliTouch-build\' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $setupStage -Force | Out-Null
    try {
        & $iscc ("/DMySourceDir=" + $InstallerDir) ("/DMyAppVersion=" + $Version) ("/DMyFileVersion=" + $fileVersion) ("/DMyArch=" + $currentArch) ("/O" + $setupStage) (Join-Path $Root 'Publish\installer.iss')
        if ($LASTEXITCODE -ne 0) { throw "[$rid] $setupName 编译失败" }
        Copy-Item -LiteralPath (Join-Path $setupStage $setupName) -Destination $Out -Force
    } finally {
        $stagedSetup = Join-Path $setupStage $setupName
        if (Test-Path -LiteralPath $stagedSetup) { Remove-Item -LiteralPath $stagedSetup -Force }
        if (!(Get-ChildItem -LiteralPath $setupStage -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $setupStage }
    }
    if (-not $KeepStaging) {
        foreach ($stageDir in @($PortableDir, $InstallerDir)) {
            if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
        }
    }
    $deliverables += [PSCustomObject]@{ Arch = $currentArch; Setup = (Join-Path $Out $setupName); Zip = $zip }
}

Write-Host ""
Write-Host "完成（双架构产物清单）:"
foreach ($item in $deliverables) {
    Write-Host "  [$($item.Arch)] 安装器     : $($item.Setup)"
    Write-Host "  [$($item.Arch)] 便携版 zip : $($item.Zip)"
}

# GitHub Release 同步上传 x64 与 ARM64 两份 setup.exe；便携包留作本机构建产物。参见 Publish/RELEASE_POLICY.md。
