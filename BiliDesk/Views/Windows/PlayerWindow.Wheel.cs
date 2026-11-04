using System;
using System.Windows.Threading;

namespace BiliDesk.Views;

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
        // ★★★ 2026-11-04 用户要求: 「触控板逻辑要和触屏对齐, 触屏做得很好但触控板是硬切」。
        //
        //   原来这里直接 `TabComments.IsChecked = true` + 一个 44px 入场动画 —— 那是硬切:
        //   内容瞬间换掉、只有一点点横向滑入。而触屏走的是
        //   BeginPageDrag → ApplyPageDrag → FinishPageDrag(两页并排、跟手、吸附)。
        //   两者观感不同, 状态也不共享(触屏用 _infoDraggingPage / PageIndex,
        //   触控板只动 RadioButton) —— 于是"先滚轮切、再手指切"就会打架。
        //
        //   ⇒ 现在触控板也走**同一个 FinishPageDrag**: 不跟手(触控板没有绝对坐标),
        //     但用同一个切换函数、同一套动画、同一份 PageIndex。状态只有一个来源。
        if (_infoDraggingPage || _infoPageAnimating) return;   // 触屏手势进行中不接管

        var target = PageIndex + direction;
        if (target is not (0 or 1)) return;

        FinishPageDrag(switchPage: true, target);
    }
}
