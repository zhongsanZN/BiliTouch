using System;
using System.Windows.Threading;

namespace BiliTouch.Views;

/// <summary>
/// 播放器窗口的**触控板横向双指滑动 / 鼠标横向滚轮**(2026-11-04)。
///
/// ## 为什么单独一个文件
/// 这条链路和首页那个是同一套原生消息(`WM_MOUSEHWHEEL` → <see cref="FluentWindow.OnHorizontalWheel"/>),
/// 但目标不同: 首页是切 tab, 播放器是切右栏「简介 / 评论」。逻辑不共享, 但**限位语义必须一致**
/// (一次滚动流只翻一页), 所以两边的常量取值保持一致。
///
/// ## 限位为什么用"空闲定时器"而不是时间冷却
/// 见 MainWindow.OnHorizontalWheel 的长注释: 时间冷却会让"划得快"的后续手势落进冷却期
/// 完全不响应; 而**滚动流是否中断**(90ms 内没有新 delta)才是可靠的判据。
/// </summary>
public partial class PlayerWindow
{
    /// <summary>一格滚轮的标准 delta</summary>
    private const int PageWheelNotch = 120;

    /// <summary>判定"滚动流已结束"的空闲时长(ms), 与首页保持一致</summary>
    private const int PageWheelIdleMs = 90;

    /// <summary>
    /// 方向开关: true = 取反。
    /// ★ 与首页同一个常量语义 —— 实测本机 WM_MOUSEHWHEEL 正 delta 对应"手指向左滑",
    ///   而右栏的语义是"手指向左滑 = 看右边那页(评论)", 正好是 +1。
    /// </summary>
    private const bool PageWheelInvert = false;

    private DispatcherTimer? _pageWheelIdle;
    private bool _pageWheelConsumed;
    private int _pageWheelAccum;

    /// <summary>
    /// 触控板/倾斜滚轮横向滑动 → 右栏翻页。
    ///
    /// ★ 只在**指针位于右栏**时接管: 鼠标在视频画面上横向滚应该什么都不做,
    ///   不该莫名翻右栏的页。
    /// ★ 直播没有"简介/评论"翻页, 直接不响应。
    /// </summary>
    protected override void OnHorizontalWheel(int delta)
    {
        if (delta == 0 || _closing || _isLive) return;
        if (InfoPanel == null || !InfoPanel.IsMouseOver) return;

        if (_pageWheelAccum != 0 && Math.Sign(delta) != Math.Sign(_pageWheelAccum))
        {
            _pageWheelAccum = 0;
            _pageWheelConsumed = false;
        }
        _pageWheelAccum += delta;
        RestartPageWheelIdle();

        if (_pageWheelConsumed || _infoPageAnimating) return;

        var signedAccum = PageWheelInvert ? -_pageWheelAccum : _pageWheelAccum;
        var dir = Math.Sign(signedAccum);
        if (dir == 0) return;

        if (Math.Abs(_pageWheelAccum) < PageWheelNotch)
        {
            // ★ 精确式触控板连续双指横滑: 在达到整格阈值前实时驱动双页并排中间态(BeginPageDrag + ApplyPageDrag),
            //   让触控板横滑拥有与触屏完全一致的跟手预览与边缘橡皮筋手感。
            if (PageHost != null && PageHost.ActualWidth > 1)
            {
                var width = Math.Max(1, PageHost.ActualWidth);
                var maxPreview = Math.Min(width * 0.45, 160);
                _infoDragDx = -signedAccum / (double)PageWheelNotch * maxPreview;
                if (!_infoDraggingPage) _infoDraggingPage = BeginPageDrag();
                if (_infoDraggingPage) ApplyPageDrag(_infoDragDx);
            }
            return;
        }

        _pageWheelAccum = 0;
        _pageWheelConsumed = true;

        var step = PageWheelInvert ? -Math.Sign(delta) : Math.Sign(delta);
        if (step == 0) step = dir;
        SwitchInfoPageByWheel(step);
    }

    /// <summary>滚动流空闲检测(见类注释)</summary>
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

                // 若触控板双指横滑停在半途(未达到 120 整格阈值就松手), 按当前跟手进度吸附或弹回
                if (!consumed && _infoDraggingPage && !_infoPageAnimating && _pendingCaptureDevice == null)
                {
                    var target = PageIndex + (_infoDragDx < 0 ? 1 : -1);
                    var shouldSwitch = (target is 0 or 1) &&
                        (Math.Abs(accum) >= PageWheelNotch * 0.58 || Math.Abs(_infoDragDx) >= PageSwitchDistance);
                    FinishPageDrag(shouldSwitch, target);
                }
            };
        }
        _pageWheelIdle.Stop();
        _pageWheelIdle.Start();
    }

    /// <summary>
    /// 按方向切右栏页(0=简介, 1=评论/推荐), 并播放双页并排过渡动画。
    /// 到头了(已在两端)则若处于触控板半途跟手态平滑回弹, 否则不动。
    /// </summary>
    /// <summary>测试入口: 直接驱动滚轮翻页(不经原生消息)</summary>
    internal void SwitchInfoPageByWheelForTest(int direction) => SwitchInfoPageByWheel(direction);

    private void SwitchInfoPageByWheel(int direction)
    {
        if (direction == 0) return;
        if (_infoPageAnimating) return;
        if (PageHost == null || PageHost.ActualWidth <= 1) return;

        var target = PageIndex + direction;
        if (target is not (0 or 1))
        {
            if (_infoDraggingPage && _pendingCaptureDevice == null)
                FinishPageDrag(switchPage: false, target);
            return;
        }

        // 若触控板前序小 delta 已经启动了跟手中间态(_infoDraggingPage == true),
        // 直接从当前双页中间位置无缝续接 FinishPageDrag 收尾, 不重置起点!
        if (_infoDraggingPage && _pendingCaptureDevice == null)
        {
            FinishPageDrag(switchPage: true, target);
            return;
        }
        if (_infoDraggingPage) return;   // 触屏手势进行中不接管

        var width = Math.Max(1, PageHost.ActualWidth);
        _infoDragDx = direction > 0 ? -width : width;   // 符号决定另一页摆在哪侧
        if (!BeginPageDrag())
        {
            // 布局还没出高度(刚切回面板): 退回直接切 tab, 至少不吞掉这次操作
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
