using System.Windows.Interop;
using System.Windows.Media;

namespace BiliTouch.Helpers;

/// <summary>WPF 渲染模式偏好；在窗口创建前应用，修改设置后重启生效。</summary>
public static class RenderPolicy
{
    public static void ApplyProcessRenderMode()
    {
        // Default 允许 WPF 在可用时使用硬件，不能保证每台设备都使用 GPU。
        // 采样质量与渲染设备是独立设置，保留各控件自己的画质策略。
        RenderOptions.ProcessRenderMode = Svc.Settings.HardwareAccelerationEnabled
            ? RenderMode.Default
            : RenderMode.SoftwareOnly;
    }
}
