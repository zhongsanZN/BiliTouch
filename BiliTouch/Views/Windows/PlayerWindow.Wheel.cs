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
        if (delta == 0 || _closing || _isLive || IsWebVideoLayout) return;
        if (InfoPanel == null || !InfoPanel.IsMouseOver) return;

        if (Math.Sign(delta) != Math.Sign(_pageWheelAccum)) _pageWheelAccum = 0;
        _pageWheelAccum += delta;
        RestartPageWheelIdle();

        if (_pageWheelConsumed) return;
        if (Math.Abs(_pageWheelAccum) < PageWheelNotch) return;

        var dir = Math.Sign(_pageWheelAccum);
        _pageWheelAccum = 0;
        _pageWheelConsumed = true;

        var step = PageWheelInvert ? -dir : dir;
        // ★ 与触摸手势共用同一套入口: 直接调 InfoDown/InfoMove/InfoUp 会走"跟手"那条路,
        //   但触控板没有绝对坐标。这里复用**降级切换**(直接切 tab + 入场动画),
        //   对应 InfoUp 里降级路径的语义 —— 观感是"整页滑入", 不是硬切。
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
                _pageWheelConsumed = false;
                _pageWheelAccum = 0;
            };
        }
        _pageWheelIdle.Stop();
        _pageWheelIdle.Start();
    }

    /// <summary>
    /// 按方向切右栏页(0=简介, 1=评论), 并播放入场动画。
    /// 到头了(已在两端)就不动 —— 与触摸手势的"到头不绕圈"一致。
    /// </summary>
    /// <summary>测试入口: 直接驱动滚轮翻页(不经原生消息)</summary>
    internal void SwitchInfoPageByWheelForTest(int direction) => SwitchInfoPageByWheel(direction);

    private void SwitchInfoPageByWheel(int direction)
    {
        if (direction == 0) return;
        if (_infoDraggingPage || _infoPageAnimating) return;   // 触屏手势进行中不接管
        if (PageHost == null || PageHost.ActualWidth <= 1) return;

        var target = PageIndex + direction;
        if (target is not (0 or 1)) return;

        // ★★★ 2026-11-04 用户反馈"触控板还是硬切, 看不到另一边的内容":
        //
        //   上一版我只调 `FinishPageDrag`, **没有先 `BeginPageDrag`** ——
        //   而 BeginPageDrag 正是"把另一页摆到侧面 + 锁高"的那一步。
        //   少了它, 另一页的位移还停在 0, `FinishPageDrag` 里
        //   "滑出去 + 滑进来"的动画就退化成"当前页滑走、另一页原地淡入" ⇒ 观感就是硬切。
        //
        //   ⇒ 现在先按触屏的方式把两页摆好(当前页居中、另一页在侧面),
        //     FinishPageDrag 才有东西可滑 —— 与手指横滑看到的**完全同一条动画**。
        var width = Math.Max(1, PageHost.ActualWidth);
        _infoDragDx = direction > 0 ? -width : width;   // 符号决定另一页摆在哪侧
        if (!BeginPageDrag())
        {
            // 布局还没出高度(刚切回面板): 退回直接切 tab, 至少不吞掉这次操作
            _infoPageSwitching = true;
            try
            {
                if (target == 1) TabComments.IsChecked = true;
                else TabIntro.IsChecked = true;
            }
            finally { _infoPageSwitching = false; }
            return;
        }
        FinishPageDrag(switchPage: true, target);
    }
}
