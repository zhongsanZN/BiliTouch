<#
.SYNOPSIS
  Counts how often the screen actually changes while BiliTouch is being scrolled.
.DESCRIPTION
  Sends real mouse-wheel packets through Win32 SendInput at a fixed cadence and counts presented
  changes with ffmpeg's desktop capture. Fixtures describe a synthetic gesture; this describes the
  live app under input the operating system delivered.

  .\measure-scroll.ps1 -Exe ..\..\dist\BiliTouch-滚动测量\BiliTouch.exe -Page Home
  .\measure-scroll.ps1 -Exe ..\..\dist\BiliTouch-滚动测量\BiliTouch.exe -Page Follow
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [ValidateSet('Home', 'Follow')][string]$Page = 'Home',
    [ValidateSet('precision', 'notch')][string]$Mode = 'precision',
    [int]$Seconds = 4,
    [int]$Delta = 90,
    [int]$CropWidth = 240,
    [int]$CropHeight = 192,
    [switch]$LeaveOpen
)

$ErrorActionPreference = 'Stop'

$Trace = Join-Path $env:TEMP 'measure-scroll.trace.log'
Set-Content -Path $Trace -Value "start $(Get-Date -Format O)"
function Step([string]$text)
{
    $line = "{0:HH:mm:ss.fff} {1}" -f (Get-Date), $text
    Add-Content -Path $Trace -Value $line
    Write-Host $line
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ScrollBenchNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public int mouseFlags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr lParam);

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr t, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();

    /// <summary>The first visible top-level window belonging to the process.</summary>
    public static IntPtr FindWindow(int targetPid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid != targetPid) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static void Wheel(int delta)
    {
        var input = new INPUT
        {
            type = 1,        // INPUT_MOUSE
            mi = new MOUSEINPUT { mouseData = delta, mouseFlags = 0x0800 }   // MOUSEEVENTF_WHEEL
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void Rect(IntPtr handle, out int left, out int top, out int right, out int bottom)
    {
        GetWindowRect(handle, out var r);
        left = r.left; top = r.top; right = r.right; bottom = r.bottom;
    }

    /// <summary>CPU time used by the calling thread, in milliseconds.</summary>
    public static double CurrentThreadCpuMs()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user)) return double.NaN;
        return (kernel + user) / 10_000.0;      // 100ns ticks
    }
}
'@
Step "helpers ready"

function Invoke-UiaSelect([IntPtr]$handle, [string]$automationId)
{
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byHandle = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NativeWindowHandleProperty, $handle.ToInt32())
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byHandle)
    if (-not $window) { throw "UIA window not found" }
    $byId = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    $target = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byId)
    if (-not $target) { throw "UIA element $automationId not found" }
    $pattern = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
}

$exePath = (Resolve-Path $Exe).Path
# The app is single-instance: launching a second copy only wakes the window that is already up.
Get-Process BiliTouch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$process = Start-Process -FilePath $exePath -PassThru
Step "started pid=$($process.Id)"

$handle = [IntPtr]::Zero
$target = $process.Id
for ($try = 0; $try -lt 80 -and $handle -eq [IntPtr]::Zero; $try++)
{
    Start-Sleep -Milliseconds 250
    $handle = [ScrollBenchNative]::FindWindow($target)
    if ($handle -eq [IntPtr]::Zero -and $process.HasExited)
    {
        $alive = @(Get-Process BiliTouch -ErrorAction SilentlyContinue)
        if ($alive.Count -eq 0) { break }
        $target = $alive[0].Id
        Step "handed over to running pid=$target"
    }
}
if ($handle -eq [IntPtr]::Zero) { throw "no window appeared for pid=$target" }
Step "window=$handle pid=$target"

[void][ScrollBenchNative]::ShowWindow($handle, 3)      # SW_MAXIMIZE
Start-Sleep -Milliseconds 1500
[void][ScrollBenchNative]::SetForegroundWindow($handle)

if ($Page -eq 'Follow') { Invoke-UiaSelect $handle 'NavFollow'; Start-Sleep -Milliseconds 3000 }
else { Start-Sleep -Milliseconds 5000 }               # let the first home wall fill
Step "settled on page=$Page"

[int]$left = 0; [int]$top = 0; [int]$right = 0; [int]$bottom = 0
[ScrollBenchNative]::Rect($handle, [ref]$left, [ref]$top, [ref]$right, [ref]$bottom)
$x = $left + [int](($right - $left) * 0.62)
$y = $top + [int](($bottom - $top) * 0.55)
[void][ScrollBenchNative]::SetCursorPos($x, $y)

$outFile = Join-Path $env:TEMP 'scroll-capture.md5'
Remove-Item $outFile -ErrorAction SilentlyContinue
$arguments = "-hide_banner -loglevel error -f lavfi -i ddagrab=framerate=120:offset_x=${x}:offset_y=${y}:video_size=${CropWidth}x${CropHeight}:draw_mouse=0,hwdownload,format=bgra -t $($Seconds + 3) -threads 1 -fps_mode passthrough -f framemd5 -flush_packets 1 `"$outFile`""
$capture = Start-Process -FilePath 'ffmpeg' -ArgumentList $arguments -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 2
Step "capturing pid=$($capture.Id) at $x,$y"

$interval = if ($Mode -eq 'precision') { 8 } else { 120 }
$unit = if ($Mode -eq 'precision') { -$Delta } else { -120 }
$packets = [int]($Seconds * 1000 / $interval)
$half = [int]($packets / 2)
$sw = [Diagnostics.Stopwatch]::StartNew()
$next = 0.0
[void][ScrollBenchNative]::timeBeginPeriod(1)
$cpuStart = [ScrollBenchNative]::CurrentThreadCpuMs()
for ($i = 0; $i -lt $packets; $i++)
{
    $d = if ($i -lt $half) { $unit } else { -$unit }
    [ScrollBenchNative]::Wheel($d)
    $next += $interval
    while ($sw.Elapsed.TotalMilliseconds -lt $next) { Start-Sleep -Milliseconds 1 }
}
$driverCpu = [ScrollBenchNative]::CurrentThreadCpuMs() - $cpuStart
[void][ScrollBenchNative]::timeEndPeriod(1)
Step "sent $packets packets over $($sw.Elapsed.TotalMilliseconds)ms"

if (-not $capture.WaitForExit(20000)) { $capture.Kill() }

$lines = @(Get-Content $outFile | Where-Object { $_ -like '0,*' })
if ($lines.Count -lt 10) { throw "only $($lines.Count) frames captured" }
$tick = 1.0 / 120
$tb = Get-Content $outFile | Where-Object { $_ -like '#tb 0:*' } | Select-Object -First 1
if ($tb) { $parts = ($tb.Split(':')[1]).Trim().Split('/'); $tick = [double]$parts[0] / [double]$parts[1] }

$times = New-Object System.Collections.Generic.List[double]
$hashes = New-Object System.Collections.Generic.List[string]
foreach ($line in $lines)
{
    $f = $line.Split(',')
    $times.Add(([long]$f[2]) * $tick)
    $hashes.Add($f[-1].Trim())
}

$start = $times[0] + 1.0
$stop = $start + $Seconds
$changes = New-Object System.Collections.Generic.List[double]
for ($i = 1; $i -lt $times.Count; $i++)
{
    if ($times[$i] -ge $start -and $times[$i] -le $stop -and $hashes[$i] -ne $hashes[$i - 1])
    { $changes.Add($times[$i]) }
}
$gaps = @(for ($i = 1; $i -lt $changes.Count; $i++) { ($changes[$i] - $changes[$i - 1]) * 1000 } | Sort-Object)
function Get-Percentile($sorted, $p)
{
    if (-not $sorted -or $sorted.Count -eq 0) { return [double]::NaN }
    $sorted[[int]($p * ($sorted.Count - 1))]
}

$result = 'RESULT page={0} mode={1} crop={2}x{3} changes={4} changed/s={5:F1} p50={6:F1}ms p95={7:F1}ms ' +
          'p99={8:F1}ms max={9:F1}ms captured={10} capture-fps={11:F1} driver-cpu={12:F1}%'
$text = [string]::Format($result, $Page, $Mode, $x, $y, $changes.Count, ($changes.Count / $Seconds),
    (Get-Percentile $gaps .50), (Get-Percentile $gaps .95), (Get-Percentile $gaps .99),
    $(if ($gaps.Count) { $gaps[-1] } else { [double]::NaN }), $times.Count,
    ($times.Count / ($times[-1] - $times[0])), ($driverCpu / ($Seconds * 1000) * 100))
Step $text

if (-not $LeaveOpen) { Stop-Process -Id $target -Force -ErrorAction SilentlyContinue }
