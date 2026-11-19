using System.Windows;

namespace BiliTouch.Helpers;

/// <summary>
/// 统一横向切页手势宿主接口：精确式触控板（PrecisionTouchpadInput）与横向滚轮（WM_MOUSEHWHEEL）
/// 通过这一唯一契约与 Home / Follow / Mine / Settings / Messages / Search 通信，
/// 避免 MainWindow 为每个页面重复维护两套硬编码路由分支。
/// </summary>
internal interface IHorizontalSwipeHost
{
    bool ContainsSwipePoint(Point screenPoint);
    void PreviewHorizontalWheel(double progress);
    void SettleHorizontalWheelPreview(bool commit);
    void SettlePrecisionTouchpadPreview(bool commit);
}
