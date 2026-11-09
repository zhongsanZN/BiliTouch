using System;
using System.Windows.Threading;

namespace BiliTouch.Views;

/// <summary>
/// 播放器窗口的触控板横向双指滑动 / 鼠标横向滚轮（全幅双页并排中间态 + 自然过半阈值）。
/// </summary>
public partial class PlayerWindow
{
    /// <summary>一格滚轮的标准 delta</summary>
    private const int PageWheelNotch = 120;

    /// <summary>触控板完整翻一页对应的累计 delta</summary>
    private const double PageFullSwipeWheelDelta = 420.0;

    /// <summary>判定"滚动流已结束"的空闲时长(ms), 与首页保持一致</summary>
    private const int PageWheelIdleMs = 90;

    /// <summary>
    /// 方向开关: false = 不取反（WM_MOUSEHWHEEL 正 delta 对应手指向左滑 → 切向右侧页 +1）。
    /// </summary>
    private const bool PageWheelInvert = false;

    private DispatcherTimer? _pageWheelIdle;
    private bool _pageWheelConsumed;
    private int _pageWheelAccum;

    protected override void OnHorizontalWheel(int delta)
    {
        if (delta == 0 || _closing || _isLive || _isFullscreen || _isFillWindow) return;
        if (InfoPanel == null || !InfoPanel.IsVisible) return;

        var wasFreshStream = _pageWheelAccum == 0;
        if (_pageWheelAccum != 0 && Math.Sign(delta) != Math.Sign(_pageWheelAccum))
        {
            _pageWheelAccum = 0;
            _pageWheelConsumed = false;
            wasFreshStream = true;
        }
        _pageWheelAccum += delta;
        RestartPageWheelIdle();

        if (_pageWheelConsumed) return;
        if (_infoPageAnimating) CompleteInfoPageSettle();

        var signedAccum = PageWheelInvert ? -_pageWheelAccum : _pageWheelAccum;
        var dir = Math.Sign(signedAccum);
        if (dir == 0) return;

        // 单次标准鼠标侧滚轮整格(120)或达到全幅(>=420)时，若尚未满幅则先走连续全幅跟手预览
        var progress = Math.Clamp(signedAccum / PageFullSwipeWheelDelta, -1.0, 1.0);
        if (Math.Abs(progress) < 0.999)
        {
            if (PageHost != null && PageHost.ActualWidth > 1)
            {
                var width = Math.Max(1, PageHost.ActualWidth);
                _infoDragDx = -progress * width;
                if (!_infoDraggingPage) _infoDraggingPage = BeginPageDrag();
                if (_infoDraggingPage) ApplyPageDrag(_infoDragDx);
            }
            return;
        }

        _pageWheelAccum = 0;
        _pageWheelConsumed = true;
        _ = wasFreshStream;

        var step = PageWheelInvert ? -Math.Sign(delta) : Math.Sign(delta);
        if (step == 0) step = dir;
        SwitchInfoPageByWheel(step);
    }

    /// <summary>滚动流空闲检测</summary>
    private void RestartPageWheelIdle()
    {
        if (_pageWheelIdle == null)
        {
            _pageWheelIdle = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(PageWheelIdleMs)
            };
            _pageWheelIdle.Tick += (_, _) =>
            {
                _pageWheelIdle!.Stop();
                var accum = _pageWheelAccum;
                var consumed = _pageWheelConsumed;
                _pageWheelConsumed = false;
                _pageWheelAccum = 0;

                if (!consumed && _infoDraggingPage && _pendingCaptureDevice == null)
                {
                    var width = Math.Max(1, PageHost?.ActualWidth ?? 360);
                    var target = PageIndex + (_infoDragDx < 0 ? 1 : -1);
                    // 自然过半（>= 50% 页面宽度）或单次鼠标侧滚轮 120 整格触发切页
                    var singleNotchClick = Math.Abs(accum) == PageWheelNotch;
                    var shouldSwitch = (target is 0 or 1) &&
                        (Math.Abs(_infoDragDx) >= width * 0.50 || singleNotchClick);
                    FinishPageDrag(shouldSwitch, target);
                }
            };
        }
        _pageWheelIdle.Stop();
        _pageWheelIdle.Start();
    }

    /// <summary>测试入口: 直接驱动滚轮翻页(不经原生消息)</summary>
    internal void SwitchInfoPageByWheelForTest(int direction) => SwitchInfoPageByWheel(direction);

    private void SwitchInfoPageByWheel(int direction)
    {
        if (direction == 0) return;
        if (_infoPageAnimating) CompleteInfoPageSettle();
        if (PageHost == null || PageHost.ActualWidth <= 1) return;

        var target = PageIndex + direction;
        if (target is not (0 or 1))
        {
            if (_infoDraggingPage && _pendingCaptureDevice == null)
                FinishPageDrag(switchPage: false, target);
            return;
        }

        if (_infoDraggingPage && _pendingCaptureDevice == null)
        {
            FinishPageDrag(switchPage: true, target);
            return;
        }
        if (_infoDraggingPage) return;

        var width = Math.Max(1, PageHost.ActualWidth);
        _infoDragDx = direction > 0 ? -width : width;
        if (!BeginPageDrag())
        {
            _infoPageSwitching = true;
            try
            {
                if (target == 1) SecondaryPageButton().IsChecked = true;
                else TabIntro.IsChecked = true;
            }
            finally { _infoPageSwitching = false; }
            return;
        }
        FinishPageDrag(switchPage: true, target);
    }
}
