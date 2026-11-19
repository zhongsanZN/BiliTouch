using System;
using System.Runtime.InteropServices;
using BiliTouch.Models;
using Microsoft.Win32;

namespace BiliTouch.Helpers;

/// <summary>
/// 轻量设备硬件与平板姿态探测（纯注册表 + Win32，零 WMI 慢查询、零 WinRT 依赖）。
/// </summary>
public static class DeviceProfile
{
    private const int SmConvertibleSlateMode = 0x2003;

    private static readonly Lazy<bool> SurfaceDetected = new(DetectSurfaceFromBios);

    /// <summary>仅限自动化测试覆盖 Surface 硬件检测结果。</summary>
    internal static Func<bool>? SurfaceDetectorOverrideForTests { get; set; }

    /// <summary>当前设备是否为 Microsoft Surface 系列设备。</summary>
    public static bool IsSurfaceDevice =>
        SurfaceDetectorOverrideForTests != null
            ? SurfaceDetectorOverrideForTests()
            : SurfaceDetected.Value;

    /// <summary>当前是否处于 Windows 无键盘/平板姿态（SM_CONVERTIBLESLATEMODE == 0）。</summary>
    public static bool IsSlatePosture
    {
        get
        {
            try { return GetSystemMetrics(SmConvertibleSlateMode) == 0; }
            catch { return false; }
        }
    }

    /// <summary>
    /// 纯函数：根据 BIOS 厂商名与产品名判断是否为 Microsoft Surface 设备。
    /// </summary>
    public static bool IsSurfaceHardware(string? manufacturer, string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName)) return false;
        var hasSurface = productName.Contains("Surface", StringComparison.OrdinalIgnoreCase);
        if (!hasSurface) return false;
        if (string.IsNullOrWhiteSpace(manufacturer)) return false;
        return manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 是否应当在主界面展示一次性「Surface 平板模式引导提示」。
    /// 仅在：命中 Surface + 当前未开启平板模式 + 尚未关闭过提示时返回 true。
    /// </summary>
    public static bool ShouldShowSurfaceTabletHint(bool isSurface, TabletMode currentMode, bool dismissed) =>
        isSurface && currentMode == TabletMode.Off && !dismissed;

    private static bool DetectSurfaceFromBios()
    {
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", writable: false);
            if (bios == null) return false;
            var manufacturer = bios.GetValue("SystemManufacturer") as string;
            var product = bios.GetValue("SystemProductName") as string;
            return IsSurfaceHardware(manufacturer, product);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
