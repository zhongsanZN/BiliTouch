; ============================================================
; BiliTouch 安装器 (Inno Setup 6)
;
; 前提: 先跑 发布.ps1 生成 "BiliTouch-安装器-files" (框架依赖版, 不含 .NET)。
; 本脚本把它打包成安装器, 并在安装前检测 .NET 桌面运行时 —— 10 及以上任何大版本
; 都可以(程序在 csproj 里配了 RollForward=Major, 没有运行时 10 时可直接跑在 11+ 上),
; 一个都没有才引导下载 .NET 10 桌面运行时(所以安装器本体能保持 ~30MB,
; 而不是把 55MB 的运行时硬塞进去)。
;
; 用 Inno Setup 打开本文件编译即可。
; ============================================================
#define MyAppName "BiliTouch"
; BiliTouch 独立版本，与 App.AppVersion / csproj 保持一致
#ifndef MyAppVersion
#define MyAppVersion "0.3.11"
#endif
// VersionInfoVersion 必须是纯数字四段 (a.b.c.d), 不能带 -touch
#ifndef MyFileVersion
#define MyFileVersion "0.3.11.0"
#endif
#define MyAppPublisher "中三"
#define MyAppExeName "BiliTouch.exe"
; 指向发布脚本生成的框架依赖目录(相对本 .iss 所在目录)
#ifndef MySourceDir
#define MySourceDir "BiliTouch-安装器-files"
#endif
; 目标架构：x64（默认）或 arm64（用于 ARM 版 Surface 等设备）
#ifndef MyArch
#define MyArch "x64"
#endif
#if MyArch == "arm64"
#define MyOutputBaseFilename "BiliTouch-" + MyAppVersion + "-setup-arm64"
#define MyArchAllowed "arm64"
#define MyDotNetRuntimeUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-arm64.exe"
#else
#define MyOutputBaseFilename "BiliTouch-" + MyAppVersion + "-setup"
#define MyArchAllowed "x64"
#define MyDotNetRuntimeUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
#endif

[Setup]
; AppId 的字面 "{" 必须写成 "{{"(Inno 常量转义), 否则编译报 Unknown constant
AppId={{F4A7C086-EF18-4BD6-B709-C75A83B62D91}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyFileVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.
; 文件名用纯 ASCII: GitHub 会把非 ASCII 的资产名截掉(1.2.0 的 zip 在下载页显示成 "BiliTouch-1.2.0-.zip")
OutputBaseFilename={#MyOutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 按 MyArch 分别限定原生 x64 或原生 arm64，避免跨架构误装。
ArchitecturesAllowed={#MyArchAllowed}
ArchitecturesInstallIn64BitMode={#MyArchAllowed}
PrivilegesRequired=admin
SetupIconFile=..\BiliTouch\Assets\app.ico
UninstallDisplayIcon={app}\Assets\BiliTouch-{#MyAppVersion}.ico
ChangesAssociations=yes

; 安装向导语言: 简体中文。语言文件随仓库放在本 .iss 同目录, 任何版本的 Inno 都能直接编译。
[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Files]
; 框架依赖发布目录(不含 .NET 运行时、含 libvlc/win-x64)
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\BiliTouch-{#MyAppVersion}.ico"; IconIndex: 0
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\BiliTouch-{#MyAppVersion}.ico"; IconIndex: 0; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// ---- 检测 .NET 桌面运行时(Microsoft.WindowsDesktop.App 主版本 >= 10) ----
// 运行时安装在 {pf}\dotnet\shared\Microsoft.WindowsDesktop.App\<主版本>.x.y。
// 10 及以上都算可用: 程序在 csproj 里配了 RollForward=Major, 机器上只有更高大版本
// (如 .NET 11)时程序直接跑在上面。检测语义必须与那个 rollForward 一致 ——
// 用户装了 .NET 11 而没装 10 时程序照样能跑, 没理由拦着。
function HasWindowsDesktopAtLeast10(baseDir: String): Boolean;
var
  Rec: TFindRec;
  Name: String;
  DotPos: Integer;
  Major: Integer;
begin
  Result := False;
  if not DirExists(baseDir) then Exit;
  if FindFirst(baseDir + '\*', Rec) then
  begin
    repeat
      Name := Rec.Name;
      DotPos := Pos('.', Name);
      if DotPos > 1 then
      begin
        Major := StrToIntDef(Copy(Name, 1, DotPos - 1), 0);
        // net10.0 应用只能跑在 .NET 10 或更高(rollForward=Major 只往上滚), 所以门槛是 10。
        if Major >= 10 then Result := True;
      end;
    until (not FindNext(Rec)) or Result;
    FindClose(Rec);
  end;
end;

function IsDesktopRuntimeInstalled(): Boolean;
begin
#if MyArch == "arm64"
  // ARM64 原生应用仅认原生 ARM64 Program Files 下的 .NET 10+ 桌面运行时，不回退到 x86/x64 模拟目录
  Result := HasWindowsDesktopAtLeast10(ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App'));
#else
  Result := HasWindowsDesktopAtLeast10(ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App'))
         or HasWindowsDesktopAtLeast10(ExpandConstant('{pf32}\dotnet\shared\Microsoft.WindowsDesktop.App'));
#endif
end;

function InitializeSetup(): Boolean;
var
  Url: String;
  Code: Integer;
begin
  Result := True;
  if IsDesktopRuntimeInstalled() then Exit;

  // 引导装对应架构的 .NET 10 桌面运行时（x64 或 ARM64）
  Url := '{#MyDotNetRuntimeUrl}';
  if MsgBox('检测到尚未安装对应架构（{#MyArchAllowed}）的 .NET 桌面运行时（.NET 10 或更高版本），它是本应用运行所必需的。' + #13#10 +
            '点击「是」将下载 .NET 10 桌面运行时（约 55MB），安装完成后重新运行本安装程序。',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExec('open', Url, '', '', SW_SHOW, ewNoWait, Code);
  end;
  Result := False;   // 中止安装, 等运行时装好再来
end;
