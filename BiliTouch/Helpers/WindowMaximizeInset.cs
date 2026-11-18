using System;

namespace BiliTouch.Helpers;

/// <summary>
/// 最大化时根内容的四边内缩量(DIP)。四个值都 >= 0。
/// </summary>
public readonly record struct WindowInsets(double Left, double Top, double Right, double Bottom);

/// <summary>
/// 计算"最大化窗口矩形"与"可见目标矩形"之间的差值, 供根内容做内缩补偿。
///
/// ★ Windows 执行最大化时会把带缩放边框的窗口撑成"工作区 + 每边一条不可见边框";
///   WindowStyle=None + WindowChrome 把非客户区压成 0 后, 客户区 = 整个窗口矩形,
///   根内容最外圈会落在屏幕外。保持窗口不动并给根内容补 Margin 内缩即可让内容严丝合缝落在可见区内。
/// ★ 播放器全屏在最大化前摘掉了 WS_CAPTION, 系统精确摆到整块显示器, 算出内缩为 0。
/// </summary>
public static class WindowMaximizeInset
{
    public static WindowInsets Compute(
        int windowLeft, int windowTop, int windowRight, int windowBottom,
        int targetLeft, int targetTop, int targetRight, int targetBottom,
        double scaleX, double scaleY)
    {
        if (!IsValidScale(scaleX)) scaleX = 1;
        if (!IsValidScale(scaleY)) scaleY = 1;

        return new WindowInsets(
            Inset(targetLeft - windowLeft, scaleX),
            Inset(targetTop - windowTop, scaleY),
            Inset(windowRight - targetRight, scaleX),
            Inset(windowBottom - targetBottom, scaleY));
    }

    private static bool IsValidScale(double scale)
        => scale > 0 && double.IsFinite(scale);

    private static double Inset(int physicalPixels, double scale)
        => physicalPixels <= 0 ? 0 : physicalPixels / scale;
}
