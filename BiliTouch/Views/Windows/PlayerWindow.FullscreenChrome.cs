using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.Views.Controls;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliTouch.Views;

public partial class PlayerWindow
{
    // ------------------------------------------------------------ 视频区边缘缩放

    /// <summary>缩放带宽度(DIP), 与 XAML 里 WindowChrome 的 ResizeBorderThickness=6 一致</summary>
    private const double ResizeEdgeDip = 6;

    private const int WmNcLButtonDown = 0x00A1;

    // Win32 的 HT*(10~17), 给 DefWindowProc 判定缩放方向
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    /// <summary>
    /// 视频区按下左键时先走这里: 指针在缩放带里就发起系统缩放并返回 true,
    /// 调用方要吞掉这次按下 —— 它是"拖边缘", 不能顺带当成视频区手势(播放/暂停等)。
    ///
    /// 视频区把窗口的左/右/下边缘盖住了, WindowChrome 的缩放边框够不着, 这里手动补上"拖边缘改大小"。
    ///
    /// ★ 根因(2026-10-05 修"铺满窗口时窗口边缘无法拖动缩放"):
    ///   窗口是 WindowStyle=None + WindowChrome(ResizeBorderThickness=6), 那 6px 缩放带的
    ///   命中测试发生在 **PlayerWindow** 的窗口过程上。而视频区被 LibVLC 的两层 HWND 盖着:
    ///   ① VideoView 的原生视频子窗口; ② VideoView 的 ForegroundWindow(弹幕/控制栏所在的
    ///   透明浮动顶层窗口, 见 DetachVideoOverlay 的说明)。鼠标压在这两层上时消息根本到不了
    ///   PlayerWindow, 缩放带形同虚设 —— 平时只有右侧信息栏那一边是纯 WPF 还能拖, 「铺满窗口」
    ///   把信息栏收掉后右边缘也被视频区盖住, 于是"整个窗口边缘都拖不动"。
    ///
    /// ★ 修法: 视频区左键按下时, 指针落在哪条缩放带就替系统发起哪个方向的缩放 ——
    ///   ReleaseCapture() 之后**直接调 DefWindowProc**(WM_NCLBUTTONDOWN + 对应 HT*)。
    ///   ★★ 必须"直接调 DefWindowProc", 不要改成 SendMessage 把消息发回主窗口:
    ///   实测(真窗口探针 .probes/bd-probe-edgeresize, 2026-10-05)合成的 WM_NCLBUTTONDOWN
    ///   会被 WPF/WindowChrome 的消息链吃掉, 根本走不到 DefWindowProc, 表现是"点了毫无反应";
    ///   直接调则与原生拖边缘完全同一条路(ENTER → SIZING… → EXITSIZE, FluentWindow 里
    ///   CardWall.NotifyModalResize 照常收到)。实测拖 120px: 窗口宽 1000 → 1120。
    /// </summary>
    private bool TryBeginEdgeResize()
    {
        if (_closing) return false;
        // 全屏/最大化时没有"改窗口大小"这回事, 交给原有手势
        if (_isFullscreen || WindowState != WindowState.Normal) return false;

        var ht = HitTestResizeEdge();
        if (ht == 0) return false;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;

        // 手势状态全部作废: 缩放循环会把松开事件吃掉, 挂起的长按/单击不能再触发
        _singleClickTimer?.Stop();
        _longPressTimer?.Stop();
        _suppressSingleClick = true;

        // 先放掉 WPF 持有的鼠标捕获, 否则模态缩放循环拿不到鼠标
        ReleaseCapture();
        // ★ 遮罩交给 FluentWindow 的 WM_ENTERSIZEMOVE/EXITSIZEMOVE 钩子管(见 OnModalResizeStarted):
        //   DefWindowProc 进入的是**系统模态缩放循环**, 拖动全程由它占着消息循环,
        //   WndProc 钩子照样能收到 ENTER/EXIT —— 所以这里不必手动压, 免得多一份状态。
        //   (2026-10-05 第一版就是在这里手动压的, 压一次就撤 ⇒ 闪白依旧;
        //    真因是"按一下就放行"而非"没压到", 正确修法见 ArmResizeCover。)
        // 直接把"按在缩放带上"交给 DefWindowProc: 它进入系统模态缩放循环, 松开鼠标才返回
        DefWindowProc(hwnd, WmNcLButtonDown, (IntPtr)ht, IntPtr.Zero);
        return true;
    }

    /// <summary>
    /// 指针现在落在窗口四边/四角的哪条缩放带上, 返回 HT*(0 = 不在缩放带)。
    /// 用 Win32 的屏幕物理像素(GetCursorPos + GetWindowRect)而不是 WPF 坐标:
    /// 要判定的是**窗口**的边缘, 而事件来自另一层浮动窗口, 坐标原点/DPI 换算都不好拿。
    /// </summary>
    private int HitTestResizeEdge()
    {
        if (!GetCursorPos(out var pt)) return 0;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return 0;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var border = Math.Max(2, (int)Math.Ceiling(ResizeEdgeDip * scale));

        // ★ 必须先确认光标**落在窗口里**。下面的判据全是"离某条边多远 < border",
        //   而"远在窗口外"的坐标同样满足它(比如窗口在 y=100, 光标在 y=0 ⇒ top=true)
        //   ⇒ 不先框住范围的话, 光标移到窗口外也会被判成"在缩放带上"。
        //   实测踩到: 光标停在屏幕最顶端(y=0)时, 这个函数返回了非 0,
        //   于是视频区右键被当成"点在缩放带"而整个吞掉。
        if (pt.X < r.Left || pt.X > r.Right || pt.Y < r.Top || pt.Y > r.Bottom) return 0;

        var left = pt.X - r.Left < border;
        var right = r.Right - pt.X <= border;
        var top = pt.Y - r.Top < border;
        var bottom = r.Bottom - pt.Y <= border;

        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return 0;
    }

    /// <summary>
    /// 鼠标在视频区移动时, 落在缩放带上就把指针换成对应的方向箭头 ——
    /// 原生缩放带会自动换指针, 但这里事件在浮动窗口里, 不给反馈就没人知道边缘能拖。
    /// (不在缩放带要换回普通箭头: 从边缘移回来时得变回去。)
    /// </summary>
    private void UpdateVideoAreaResizeCursor()
    {
        if (_closing || VideoAreaRoot == null) return;
        if (_isFullscreen || WindowState != WindowState.Normal) return;

        VideoAreaRoot.Cursor = HitTestResizeEdge() switch
        {
            HtLeft or HtRight => Cursors.SizeWE,
            HtTop or HtBottom => Cursors.SizeNS,
            HtTopLeft or HtBottomRight => Cursors.SizeNWSE,
            HtTopRight or HtBottomLeft => Cursors.SizeNESW,
            _ => Cursors.Arrow,
        };
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }


    // ------------------------------------------------------------ 控制栏自动隐藏

    /// <summary>控制栏从有鼠标活动到自动隐藏的等待时长</summary>
    private static readonly TimeSpan ControlsHideDelay = TimeSpan.FromMilliseconds(2400);
    private DispatcherTimer? _controlsHideTimer;
    private bool _controlsHidden;

    /// <summary>
    /// 视频区有鼠标移动 → 立即显示控制栏并重置倒计时。
    /// ControlBar 就挂在这个视频区 Grid 里(同一个 ForegroundWindow 浮层), 所以鼠标压在
    /// 控制栏上时 MouseMove 也会一路冒泡到这里 —— 不会被自己藏掉。
    /// </summary>
    private void OnVideoAreaMouseMove(object sender, MouseEventArgs e)
    {
        if (TouchSupport.IsTouchMouse(e)) return;
        _lastPlayerInputWasTouch = false;
        if (_controlsHidden) ShowControls();
        if (ShouldHideControls()) StartControlsHideCountdown();
        else _controlsHideTimer?.Stop();
        // 靠近窗口边缘时把指针换成缩放箭头(见 UpdateVideoAreaResizeCursor)
        UpdateVideoAreaResizeCursor();
    }

    /// <summary>现在是否处于"应该隐藏控制栏"的状态: 只在**播放中**自动隐藏。</summary>
    private bool ShouldHideControls()
    {
        if (_videoTouches.Count > 0 || TouchHelp.Visibility == Visibility.Visible) return false;
        if (_closing || _mp == null || !_mp.IsPlaying) return false;
        if (QualityPopup != null && QualityPopup.IsOpen) return false;
        if (SubtitlePopup != null && SubtitlePopup.IsOpen) return false;
        if (DanmakuAreaPopup != null && DanmakuAreaPopup.IsOpen) return false;
        if (VolumePopup != null && VolumePopup.IsOpen) return false;
        if (SpeedPopup != null && SpeedPopup.IsOpen) return false;
        if (DanmakuInput?.IsKeyboardFocusWithin == true) return false;
        if (_isSeeking) return false;                       // 拖进度条时别藏
        if (!_lastPlayerInputWasTouch && ControlBar.IsMouseOver) return false;
        if (LoadingOverlay != null && LoadingOverlay.Visibility == Visibility.Visible) return false;
        return true;
    }

    private void StartControlsHideCountdown()
    {
        if (!ShouldHideControls()) { _controlsHideTimer?.Stop(); return; }
        if (_controlsHideTimer == null)
        {
            _controlsHideTimer = new DispatcherTimer { Interval = ControlsHideDelay };
            _controlsHideTimer.Tick += (_, _) =>
            {
                _controlsHideTimer.Stop();
                // Tick 时状态可能已变(暂停/弹层开了): 不满足就再等下一轮鼠标活动
                if (ShouldHideControls()) HideControls();
            };
        }
        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
    }

    /// <summary>
    /// 单击画面 = 切换控制栏显隐(2026-11-04)。
    ///
    /// ★ 语义与"鼠标移动唤出控制栏"完全一致(见 OnVideoAreaMouseMove 里的 ShowControls):
    ///   控制栏藏着 → 这次单击把它唤出来; 已经显示 → 这次单击把它收起来。
    ///   与"晃动鼠标"并不冲突: 移动鼠标永远只会**唤出**, 只有单击才会收起。
    /// ★ 收起时顺手停掉自动隐藏倒计时 —— 否则用户手动藏起来后, 180ms 的淡出动画刚结束,
    ///   倒计时又把它"该隐藏"的状态重新排一遍(虽然结果一样, 但会扰乱后续计时)。
    /// </summary>
    private void ToggleControlsByClick()
    {
        if (_closing) return;
        if (_controlsHidden)
        {
            ShowControls();
            // 唤出后按常规规则重新开始自动隐藏倒计时(播放中才会真的藏)
            if (ShouldHideControls()) StartControlsHideCountdown();
        }
        else
        {
            _controlsHideTimer?.Stop();
            HideControls();
        }
    }

    private void HideControls()
    {
        if (_controlsHidden || _closing) return;
        _controlsHidden = true;
        _controlsHideTimer?.Stop();
        CloseVolumePopup();                     // 控制栏一藏, 挂在它上面的音量弹层必须跟着收
        CloseDanmakuAreaPopup();
        CloseSubtitlePopup();
        ControlBar.IsHitTestVisible = false;   // 隐形时不能再吃视频区的点击/滚轮
        VideoAreaRoot.Cursor = System.Windows.Input.Cursors.None;
        ControlBar.BeginAnimation(OpacityProperty, null);
        var (_, trans) = HyperMotion.EnsureScaleTranslate(ControlBar, new Point(0.5, 1.0));
        HyperMotion.SpringTo(ControlBar, "Opacity", ControlBar.Opacity, 0.0,
            HyperMotion.SmoothFade, v => ControlBar.Opacity = Math.Clamp(v, 0, 1));
        HyperMotion.SpringTo(ControlBar, "Y", trans.Y, 10.0,
            HyperMotion.SheetDismiss, v => trans.Y = v);
        UpdateSubtitleLayoutPosition();
        UpdateAlternateVideoControls();
    }

    private void ShowControls()
    {
        _controlsHideTimer?.Stop();
        if (!_controlsHidden)
        {
            // 没藏着也要确保可见(动画/其它路径可能把透明度留成中间值)
            if (ControlBar.Opacity < 1)
            {
                HyperMotion.Cancel(ControlBar);
                ControlBar.BeginAnimation(OpacityProperty, null);
                ControlBar.Opacity = 1;
                var (_, existingTrans) = HyperMotion.EnsureScaleTranslate(ControlBar, new Point(0.5, 1.0));
                existingTrans.Y = 0;
            }
            ControlBar.IsHitTestVisible = true;
            VideoAreaRoot.Cursor = System.Windows.Input.Cursors.Arrow;
            UpdateSubtitleLayoutPosition();
            UpdateAlternateVideoControls();
            return;
        }
        _controlsHidden = false;
        VideoAreaRoot.Cursor = System.Windows.Input.Cursors.Arrow;
        ControlBar.BeginAnimation(OpacityProperty, null);
        ControlBar.IsHitTestVisible = true;
        var (_, trans) = HyperMotion.EnsureScaleTranslate(ControlBar, new Point(0.5, 1.0));
        HyperMotion.SpringTo(ControlBar, "Opacity", ControlBar.Opacity, 1.0,
            HyperMotion.SmoothFade, v => ControlBar.Opacity = Math.Clamp(v, 0, 1));
        HyperMotion.SpringTo(ControlBar, "Y", Math.Abs(trans.Y) > 0.5 ? trans.Y : 10.0, 0.0,
            HyperMotion.SheetSlide, v => trans.Y = v);
        UpdateSubtitleLayoutPosition();
        UpdateAlternateVideoControls();
    }


    // ------------------------------------------------------------ 全屏

    /// <summary>
    /// 全屏切换。**几何变化本身依然是同步的**(按下的那一刻就把布局与窗口状态改完),
    /// 但"看得见的动画"这件事**交给 Windows/DWM 自己播** —— 见下。
    ///
    /// ★★★ 2026-10-02 按用户要求"把软件全屏动画改成 Windows 系统自带的动画, 不再自己绘制":
    ///   两条一起来的改动(缺一条都还是"自己画的"):
    ///     ① **不再关掉系统动画** —— 原来在 SourceInitialized 里调
    ///        `DwmInterop.DisableWindowTransitions`(DWMWA_TRANSITIONS_FORCEDISABLED),
    ///        把 DWM 那段窗口缩放过渡掐掉了, 所以全屏是"硬切"。该调用与其 API 已整体删除。
    ///     ② 自绘的那套过渡早已不在: 四段(渐暗压黑 60ms → 改布局 → 持黑 150ms → 揭幕 130ms,
    ///        合计 340ms, 2026-09-30 删)与 FluentWindow 的整窗 Scale+淡入(2026-10-02 删)
    ///        都不存在了。现在这个窗口一切换就是"改布局 + 最大化", 剩下的动效由 DWM 负责。
    ///
    /// ★ 那层**全屏黑幕(见 ArmFullscreenCover)必须保留** —— 它挡的是 LibVLC 重新协商 vout
    ///   时的白底空窗, 不是动画: 交给系统播之后窗口尺寸照样在变, 空窗照样出现,
    ///   去掉它浅色主题下就会重新闪白(2026-10-01 修过的老问题)。
    ///
    /// 窗口现在**始终**是 WindowStyle=None + WindowChrome 的自绘标题栏, 所以全屏不再靠
    /// 切换 WindowStyle 实现(之前那套 `None + Maximized` 会让自绘标题栏和系统框打架),
    /// 而是: 最大化 + 把顶栏那一行收成 0 高 + 藏起右侧信息栏。
    /// </summary>
    private void ToggleFullscreen()
    {
        if (_closing) return;

        _isFullscreen = !_isFullscreen;
        ApplyFullscreen(_isFullscreen);
    }

    /// <summary>全屏的开关(布局细节与顺序见 ApplyFullscreenLayout)</summary>
    private void ApplyFullscreen(bool on)
    {
        if (_closing) return;
        ApplyFullscreenLayout(on);
    }

    /// <summary>
    /// 改布局与窗口状态。**全屏唯一改动布局的地方**。
    ///
    /// 顺序仍然有讲究: 进全屏时**先把窗口底色改成黑**, 再收顶栏/信息栏、最后最大化 ——
    /// 底色先就位, 后面几步暴露出来的任何区域才是黑的。退出时反过来: 先把底色还给主题,
    /// 再改几何, 这样窗口缩回去的过程中露出来的边角是对的主题色而不是一块全屏黑。
    /// </summary>
    private void ApplyFullscreenLayout(bool on)
    {
        // 非 vmem 模式下 HwndHost + WindowChrome 玻璃区会在全屏尺寸剧变时闪烁，临时停用材质并在稳定后恢复
        SuspendBackdropForResize();
        // ★★★ 全屏切换期间**不要**让"最大化/还原黑罩"插手(2026-10-17):
        //   全屏 = 最大化 + 收顶栏, 所以它必然触发 WM_SYSCOMMAND / 尺寸剧变;
        //   而这条路径本来就有自己的一套遮罩时序(下面的 ArmFullscreenCover / ReleaseFullscreenCover)。
        //   两套叠加的实测后果: 退全屏后视频区**黑约 0.9 秒**(被这边的兜底计时器按住)。
        //   ⇒ 整段(含之后由消息循环送达的状态变化)排掉, 时长给足 —— 进出全屏最慢也就几百毫秒。
        SuppressStateCoverFor(1600);

        // 平板单窗口内嵌模式：全屏直接在主窗内铺满舞台（无需改动隐藏的 PlayerWindow 窗口态）
        if (_isEmbeddedInMain)
        {
            if (on)
            {
                RootGrid.Background = Brushes.Black;
                TopBar.Visibility = Visibility.Collapsed;
                TopRow.Height = new GridLength(0);
                ApplyInfoPanelState();
                UpdatePortraitImmersiveOverlay();
            }
            else
            {
                SetPortraitCommentDrawerOpen(false);
                RootGrid.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
                TopBar.Visibility = Visibility.Visible;
                TopRow.Height = new GridLength(44);
                ApplyInfoPanelState();
                UpdatePortraitImmersiveOverlay();
            }
            return;
        }

        if (on)
        {
            _beforeFullscreenState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            _beforeFullscreenBounds = RestoreBounds;
            // ★ 全屏期间**禁止**"失焦换窗口底色"那套处理接管(2026-10-06):
            //   全屏要求所有缝隙纯黑(紧接着那两处赋 Black), 而全屏途中一旦点到别的窗口,
            //   失焦逻辑会把 Background 改成主题实色 ⇒ 视频周围露出浅色缝。
            //   ★ 必须放在**赋 Black 之前**: 放在后面的话, 这中间的失焦回调仍有可能插进来。
            //   退出全屏时在下面把它放开。
            SuppressAcrylicActivationSwap = true;

            // ★ 第一步必须是它: 底色先黑, 后面收缩/放大暴露出来的区域才不会是主题色。
            //   ★★ 要切的是**两层**—— 窗口 Background 只是最底下那层, 上面还压着根网格
            //   RootGrid 的 AppBackgroundBrush(浅色主题 = 白), 它不被一起切掉,
            //   全屏期间露出来的任何缝隙就是一块白(2026-10-01 用户报"浅色全屏全白")。
            Background = Brushes.Black;
            RootGrid.Background = Brushes.Black;
            // ★★★ 上面那两层**都盖不住视频区**(见 ArmFullscreenCover 的说明):
            //   VideoView 是 HwndHost(airspace), 画在所有 WPF 元素之上, 而"视频区那层
            //   黑底 Grid"是它的**父容器** —— 父容器画不到子 HWND 上面, 以前那句
            //   "视频区那层 Grid 本身是黑底, 无需处理"是错的。
            //   真正能盖住 vout HWND 的只有覆盖窗口里的手势层 VideoAreaRoot,
            //   所以全屏切换期间必须把它压黑(见 ArmFullscreenCover)。
            ArmFullscreenCover();

            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            TopBar.Visibility = Visibility.Collapsed;
            TopRow.Height = new GridLength(0);
            // ★ 信息栏的收起**必须走这一个入口**(收起原因见 ApplyInfoPanelState 的说明):
            //   全屏与"铺满窗口"两个开关共用它, 谁都不许自己改 InfoPanel/RightColumn。
            ApplyInfoPanelState();

            // ★ 全屏必须**盖住任务栏**。
            // 无边框窗口的最大化尺寸由 WM_GETMINMAXINFO 决定, 而 FluentWindow 里那条钩子
            // 是给主窗口的"普通最大化"定的规矩: 只占工作区(任务栏照常露在外面)。
            // 播放器全屏要的是整块显示器, 所以这里把开关翻过来 —— 见 MaximizeCoversTaskbar。
            MaximizeCoversTaskbar = true;
            // ★★★ 尺寸铺满还不够: Windows 外壳只对"无标题栏样式 + 铺满显示器"的窗口做
            //   全屏判定(自动藏任务栏)。SourceInitialized 里为了系统动画补回的 WS_CAPTION
            //   必须在这里摘掉, 否则任务栏照样浮在全屏画面上(2026-10-03 用户报的 bug)。
            //   退全屏时由下面的对称分支补回来, 系统动画不受影响。
            DwmInterop.SuppressCaptionForFullscreen(this);
            // ★ 开关改完必须逼窗口**重新走一次最大化**: WM_GETMINMAXINFO 只在窗口开始
            //   最大化/移动/缩放时发一次, 已经处于 Maximized 的窗口再改开关不会有任何效果
            //   (表现就是"全屏进去了, 任务栏还在")。先退成 Normal 再最大化即可。
            if (WindowState == WindowState.Maximized) RefreshMaximizedBounds();
            else WindowState = WindowState.Maximized;

            // ★★ 进全屏同样会按新尺寸重建 vout ⇒ 新的 `Static` 子窗口又没被刷黑,
            //   全屏时画面两侧/上下会露白边。与退全屏对称, 这里也刷一次。
            StartVideoBackdropFix();
        }
        else
        {
            ArmFullscreenCover();
            // 先改外观再改几何: 窗口在缩回去的过程中露出来的边角就是正确的主题色,
            // 而不是刚才那块"全屏黑"。(FluentWindow 按深浅主题挑 WindowSolid*)
            MaximizeCoversTaskbar = false;
            // 放开"失焦换底色"的闸门: ApplyChrome 紧接着会按当前激活态重新给出正确的窗口底色
            // (全屏期间它被上面那行拦住了, 现在要还给这套机制)。
            SuppressAcrylicActivationSwap = false;
            ApplyChrome();
            // ★★ 不要在这里补 WS_CAPTION: 它由下面对"窗口状态还原之后"那次
            //   SyncCaptionForAcrylic 统一补(那时 WindowState 已回到 Normal, 才补得回来) ——
            //   在这里补会因为窗口还是 Maximized 而被跳过, 退全屏后就没动画了。
            //   (重复按钮的克制现在靠去掉 WS_SYSMENU, 不再靠摘 WS_CAPTION。)

            // ★★ 退出全屏后 libvlc 会按新尺寸**重建 vout**, 那里面的 `Static` 子窗口是新的,
            //   之前设的黑色类背景刷跟着旧窗口一起没了 ⇒ 视频区边缘又露出白(用户报的
            //   "先全屏再退全屏, 播放器页面部分出现白色区域")。
            //   所以这里必须重刷一次(和起播后那次同一个入口, 见 ApplyHwndHostBlackBackground)。
            StartVideoBackdropFix();
            // 根网格底色还给主题(与 ApplyChrome 同理: 缩回过程露出的边角要主题色)
            RootGrid.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");

            TopBar.Visibility = Visibility.Visible;
            TopRow.Height = new GridLength(44);
            // 先恢复进入前的窗口几何/工作区，再展开信息栏/网页布局，避免 MountWebLayout 在旧几何下读到 0 宽
            if (_beforeFullscreenState == WindowState.Maximized)
            {
                RefreshMaximizedBounds();
            }
            else
            {
                WindowState = WindowState.Normal;
                if (!_beforeFullscreenBounds.IsEmpty)
                {
                    if (!SetWindowBoundsOnce(_beforeFullscreenBounds))
                    {
                        Left = _beforeFullscreenBounds.Left; Top = _beforeFullscreenBounds.Top;
                        Width = _beforeFullscreenBounds.Width; Height = _beforeFullscreenBounds.Height;
                    }
                }
            }
            DwmInterop.SyncCaptionForAcrylic(this);
            // 与进全屏同一个入口: 退出全屏后信息栏是否回来, 还要看"铺满窗口"是不是还开着
            ApplyInfoPanelState();

            // 信息栏直接到位, 不再淡入 —— 全屏切换整体没有任何过渡动画了。
            // 顺手把可能的残留动画摘掉并归位, 免得哪天有人给它加动画时被这里顶掉。
            if (InfoPanel != null)
            {
                InfoPanel.BeginAnimation(OpacityProperty, null);
                InfoPanel.Opacity = 1;
            }
        }
    }

    /// <summary>
    /// 视频区遮罩: 在"窗口尺寸正在变"的窗口期把手势层压黑, 盖住 vout HWND 露出的白底。
    ///
    /// ★★ 2026-10-05 用户报"拖动窗口边缘调节大小时视频区闪白"—— 本方法原先只挂在**全屏切换**上,
    ///   而拖边缘 resize 走的是完全不同的代码路径, 从不经过这里, 于是白底照样露出来。
    ///   它挡的机制与全屏完全相同(LibVLC 要重协商 vout, 那一瞬没有新帧 → 露出 vout 的默认白底),
    ///   所以复用一个遮罩即可, 不必再造第二套。触发点见 <see cref="ArmFullscreenCover"/> /
    ///   <see cref="ArmResizeCover"/>(后者由 WM_ENTERSIZEMOVE 驱动)。
    /// </summary>
    ///
    /// <remarks>
    /// ★ 为什么必须压手势层, 而不是切窗口/RootGrid 底色(2026-10-01 修长期未愈的白闪):
    ///   `VideoView` 是 **HwndHost**(airspace), 它画在**所有 WPF 元素之上**。视频区那层
    ///   `Background="Black"` 的 Grid 是 VideoView 的**父容器** —— 父容器画不到子 HWND 上面。
    ///   所以历史上"底色先切黑就够"的前提其实不成立: 浅色主题下窗口尺寸剧变时,
    ///   LibVLC 要重新协商 vout, 那一瞬视频 HWND 没有新帧可显示, 露出它的**默认白底**,
    ///   而覆盖窗口里唯一能压住它的就是 `VideoAreaRoot`。深色主题下窗口底色本来就是
    ///   #202020, 同一处露底不刺眼, 所以"只有浅色模式出问题" —— 与用户描述一致。
    ///
    /// ★ 为什么"部分视频": 命��� `ShortVideoCache` 的短视频是**已下完的本地单文件**,
    ///   任意时刻都有已解出的帧; 远端 DASH 长视频要 flush/重协商 vout, 空窗明显得多。
    ///   这与 2026-10-01 那条日志记的判别式完全一致。
    ///
    /// ★ 这不是"过渡动画": 不淡入、不用 _fsShadeTimer(那个已被删除且别复活),
    ///   只是切换期间多压一层不透明黑, 首个新帧到达即撤掉。
    ///   ★★ 2026-10-02 用户要求"全屏动画改用 Windows 自带的"之后**它仍然必须留着**:
    ///   它挡的是 LibVLC vout 重协商时的**白底空窗**, 与"动画由谁播"完全无关 ——
    ///   改成系统动画后窗口尺寸变化依旧发生, 空窗依旧存在, 去掉它浅色主题就会闪白。
    /// </remarks>
    private void ArmFullscreenCover()
    {
        if (_closing || _vmemMode || VideoAreaRoot == null) return;
        VideoAreaRoot.Background = Brushes.Black;
        _fullscreenCoverArmed = true;
        _fullscreenCoverTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FullscreenCoverTimeoutMs)
        };
        _fullscreenCoverTimer.Tick -= OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Tick += OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Start();
    }

    /// <summary>
    /// 撤掉视频区遮罩(视频已经在出帧了, 再挡着就看不见画面了)。
    ///
    /// ★★ _resizeHold 这道闸是修"拖边缘闪白"的关键(2026-10-05):
    ///   拖动期间 LibVLC 会**反复**重协商 vout, 每轮都露一次白底。若此时 TimeChanged /
    ///   Playing 一发就把遮罩撤了, 后面每一轮重协商又白一下 —— 表现为"闪白依旧"。
    ///   所以按住期间(TimeChanged/Playing/兜底超时 都会走到这里)**一律不撤**,
    ///   只等 WM_EXITSIZEMOVE 由 ReleaseResizeHold 放闸后, 这里才真正生效。
    /// </summary>
    private void ReleaseFullscreenCover()
    {
        // ★ 拖动按住期间不放行(计时器也不停: 它只做"万一 EXIT 没送到"的兜底)
        if (_resizeHold) return;
        _fullscreenCoverTimer?.Stop();
        if (_closing || !_fullscreenCoverArmed || VideoAreaRoot == null) return;
        _fullscreenCoverArmed = false;
        VideoAreaRoot.Background = VideoAreaBackBrush;
    }

    /// <summary>
    /// 兜底: 一直没等到新帧(暂停中/静止帧/极短片)就把黑幕撤掉,
    /// 否则画面会被自己挡住。超时取 600ms —— 正常情况下远早于此就已撤掉。
    /// </summary>
    private void OnFullscreenCoverTimeout(object? sender, EventArgs e)
    {
        // 若已不在拖拽期（例如 WM_EXITSIZEMOVE 丢失），必须主动放闸防止黑幕永久锁死
        if (_resizeHold && !_modalResizeInProgress) _resizeHold = false;
        ReleaseFullscreenCover();
    }

    /// <summary>
    /// resize 期间的视频区遮罩。
    ///
    /// ★★★ 这一版与上一版的区别, 以及上一版为什么没修好(2026-10-05 用户反馈"闪白问题依旧"):
    ///   上一版是"进 resize 时压一次, 靠 TimeChanged/Playing 收到新帧就撤"。看着对, 但拖动期间
    ///   窗口尺寸**每帧都在变** ⇒ LibVLC 每一帧都在重协商 vout ⇒ 白底反复露出。
    ///   第一次重协商一结束遮罩就被撤了, 后面每一轮重协商又白一下 —— 观感就是"闪白依旧",
    ///   而且用户截图里那种整块白正是这一轮轮的叠加。
    ///   ★ 所以正确语义是"按住整个拖动期": 进入压黑, **期间谁来撤都不放行**(见 _resizeHold),
    ///     一直到 WM_EXITSIZEMOVE 才撤(见 OnModalResizeEnded)。
    ///   ★ 为什么仍然复用 _fullscreenCoverArmed 而不是另开一个字段: 两者最终都只改
    ///     VideoAreaRoot.Background, 各开一份状态就会出现两份状态互抢同一属性。
    ///     _resizeHold 是"额外的闸": 它为真时 ReleaseFullscreenCover 拒绝撤罩。
    /// </summary>
    private void ArmResizeCover()
    {
        if (_closing || _vmemMode || VideoAreaRoot == null) return;
        _resizeHold = true;
        VideoAreaRoot.Background = Brushes.Black;
        _fullscreenCoverArmed = true;
        // ★ 计时器在按住期间**只做兜底**: 它到期时若还在拖动就不撤(由 _resizeHold 挡住),
        //   万一 WM_EXITSIZEMOVE 因为异常路径没送到, 至少不会永远黑屏。
        _fullscreenCoverTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FullscreenCoverTimeoutMs)
        };
        _fullscreenCoverTimer.Tick -= OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Tick += OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Start();
    }

    /// <summary>拖动结束: 放掉"按住不放"的闸, 正常恢复由新帧驱动</summary>
    private void ReleaseResizeHold()
    {
        _resizeHold = false;
    }

    /// <summary>
    /// 拖动期间**按住**遮罩(2026-10-05 修"拖边缘 resize 闪白"的真正修法)。
    /// 见 <see cref="ArmResizeCover"/> 的说明。</summary>
    protected override void OnModalResizeStarted()
    {
        base.OnModalResizeStarted();
        if (!_vmemMode && IsLoaded && HitTestResizeEdge() != 0)
        {
            _modalResizeInProgress = true;
            _backdropResumeTimer?.Stop();
            SuspendBackdropForResize();
        }
        _lastModalRect = new Rect(Left, Top, Width, Height);
        _fullscreenCoverArmed = false;
        _resizeHold = false;
        // 不排计时器: 纯移动压根不该压罩, 计时器留给真正压上之后做兜底。
        StartModalResizeSizeWatch();
    }

    /// <summary>
    /// 拖动期间盯住"尺寸真的开始变了吗"。变了才补压遮罩(斜向拖边缘的场景)。
    /// 用 <see cref="CompositionTarget"/> 的 Rendering 回调而不是 DispatcherTimer:
    /// 它跟着渲染帧走, 每帧一次, 拖动结束自然停。
    /// </summary>
    private void StartModalResizeSizeWatch()
    {
        if (_modalSizeWatchAttached) return;
        _modalSizeWatchAttached = true;
        CompositionTarget.Rendering += OnModalResizeRendering;
    }

    private void OnModalResizeRendering(object? sender, EventArgs e)
    {
        if (_closing)
        {
            StopModalResizeSizeWatch();
            return;
        }

        // 尺寸确实变了 ⇒ 这次是"改大小"(可能同时在移动), 按住遮罩
        if (!_fullscreenCoverArmed &&
            (Math.Abs(Width - _lastModalRect.Width) > 0.5 ||
             Math.Abs(Height - _lastModalRect.Height) > 0.5))
        {
            ArmResizeCover();
        }

        // 一旦压上就不再需要盯了(遮罩要一直按住到 EXIT)
        if (_fullscreenCoverArmed) StopModalResizeSizeWatch();
    }

    private void StopModalResizeSizeWatch()
    {
        if (!_modalSizeWatchAttached) return;
        _modalSizeWatchAttached = false;
        CompositionTarget.Rendering -= OnModalResizeRendering;
    }

    /// <summary>这次模态拖动开始时记下的窗口矩形, 用来判断尺寸有没有变过</summary>
    private Rect _lastModalRect;
    private bool _modalSizeWatchAttached;

    /// <summary>
    /// 拖动结束才撤遮罩 —— 不是靠"新帧到达"(那样会在拖动中途被反复放行, 闪白依旧)。
    ///
    /// ★★ 2026-10-06: 纯移动(尺寸未变)根本没压过遮罩, 这里必须**无条件**把底色还原 ——
    ///   不能"只在压过时才撤": 用户拖完位置看到的黑屏, 恰恰是遮罩**已经撤不掉**的那一类
    ///   (靠 TimeChanged 等新帧恢复, 而移动时视频尺寸没变、LibVLC 不出新帧; 暂停时更是完全不发)。
    /// </summary>
    protected override void OnModalResizeEnded()
    {
        if (_modalResizeInProgress)
        {
            _modalResizeInProgress = false;
            ResumeBackdropAfterResize();
        }
        StopModalResizeSizeWatch();
        ReleaseResizeHold();
        // ★★ 2026-10-06 纯移动(尺寸未变)时压根没压过遮罩, 但底色**必须**还原 ——
        //   这正是"拖完位置黑屏"的那一类: 遮罩一旦压上就只能等 TimeChanged 等新帧来撤,
        //   而移动时窗口尺寸没变、LibVLC 不出新帧, 暂停时更是完全不发 ⇒ 撤不掉。
        //   这里无条件走一次撤罩, 无论压没压过。
        ReleaseFullscreenCover();
    }
    /// <summary>当前是否处于全屏(顶栏已收起、右侧信息栏已收成 0 宽)</summary>
    private bool _isFullscreen;
    private WindowState _beforeFullscreenState = WindowState.Normal;
    private Rect _beforeFullscreenBounds = Rect.Empty;

    /// <summary>
    /// "铺满窗口"是否开着(2026-10-04 新增, 顶栏右上角那个按钮)。
    ///
    /// 与 <see cref="_isFullscreen"/> 是**两个独立的开关**, 要叠加使用:
    ///   · 它只做一件事 —— 把右侧信息栏整个收掉, 让视频铺满窗口宽度; **窗口尺寸/状态一概不动**;
    ///   · 真全屏是"收顶栏 + 最大化到整块显示器 + 盖任务栏", 它不碰。
    /// 两者都收信息栏, 所以"信息栏收没收"由 <see cref="ApplyInfoPanelState"/> 取**或**之后统一决定 ——
    /// 那正是两个开关能叠加的原因(先铺满窗口再按 F 进全屏、退出全屏仍保持铺满)。
    /// </summary>
    private bool _isFillWindow;

    /// <summary>
    /// 右上角「铺满窗口」按钮的点击入口。
    /// Esc 的处理见 <see cref="HandleBackRequest"/> —— 它按"由内到外"退: 全屏 → 铺满窗口 → 关窗。
    /// </summary>
    private void OnFillWindowClick(object sender, RoutedEventArgs e) => ToggleFillWindow();

    private void ToggleFillWindow()
    {
        if (_closing) return;
        _isFillWindow = !_isFillWindow;
        ApplyInfoPanelState();
        UpdateFillWindowButton();
    }

    /// <summary>常规模式(信息栏展开)的窗口最小尺寸 —— 与 XAML 里的 MinWidth/MinHeight 保持一致</summary>
    private const double NormalWindowMinWidth = 840;
    private const double NormalWindowMinHeight = 600;

    /// <summary>
    /// 视频列在常规模式下的最小宽度(与 XAML 的 MinWidth 一致); 信息栏收起后放开到 0。
    /// ★ 480 → 320(2026-11-03, issue #31): 竖屏副屏 864 DIP 要能同时放下"视频 + 400 信息栏"。
    /// </summary>
    private const double VideoColumnNormalMinWidth = 320;

    /// <summary>
    /// 铺满窗口/全屏(信息栏收起)时允许缩到的最小尺寸 —— 此时窗口里只剩视频,
    /// 没有"布局放不下"的问题, 可以缩到很小。
    /// </summary>
    private const double FillWindowMinWidth = 320;
    private const double FillWindowMinHeight = 200;

    /// <summary>
    /// 按两个开关的当前值把右侧信息栏设成"该收"或"该展"。**唯一改 InfoPanel / RightColumn 的地方。**
    ///
    /// ★ 为什么信息栏必须**整个 Collapsed**, 不能只把列宽设成 0:
    ///   宽度为 0 的面板仍然参与 measure/arrange/render —— 里面挂着几百张评论卡
    ///   (无对象池, 见 notes-player-internals), 每切一次都要把这棵评论树重新量一遍,
    ///   这就是用户说的"卡顿一下"(退出时还要再付一次)。
    ///
    /// ★ 为什么必须收敛到一个入口: 收信息栏的现在有两个开关(全屏 / 铺满窗口), 各自改一遍
    ///   会立刻出现"退出全屏把铺满窗口的状态也顶掉"这种互相打架 ——
    ///   两个开关叠加时, 谁先谁后都不能让信息栏闪一下或者卡在错误状态。
    /// </summary>
    private void ApplyInfoPanelState()
    {
        if (_closing) return;
        var hide = _isFullscreen || _isFillWindow;
        if (InfoPanel != null)
            InfoPanel.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        var tabletStacked = !hide && (_tabletPortraitLayoutMounted || ShouldUseTabletPortraitLayout);
        var hostW = _embeddedHost?.ActualWidth ?? ActualWidth;
        // ★ 列宽必须跟着一起给回去: 只把 Visibility 改回 Visible 而列宽还停在 0,
        //   面板会量成 0 宽(用户看到的是"信息栏没回来")。
        if (RightColumn != null)
            RightColumn.Width = new GridLength(hide || tabletStacked ? 0 : InfoPanelWidthFor(hostW));

        if (VideoColumn != null)
            VideoColumn.MinWidth = hide || tabletStacked ? 0 : VideoColumnNormalMinWidth;
        MinWidth = hide ? FillWindowMinWidth : NormalWindowMinWidth;
        MinHeight = hide ? FillWindowMinHeight : NormalWindowMinHeight;
        ApplyAlternateLayoutPreference(allowPreLoad: _isEmbeddedInMain);
        if (_tabletPortraitLayoutMounted) UpdateTabletPortraitMeasurements();
        UpdateCommentPicturePlacement();
        UpdatePortraitImmersiveOverlay();
        QueueAlternateLayout();
    }

    // ------------------------------------------------------------ 信息栏宽度(平板式自适应)

    /// <summary>信息栏最窄/最宽。窄于 340 评论区的昵称和操作行就开始互相挤; 宽于 560 视频区白让。</summary>
    private const double InfoPanelMinWidth = 340;
    private const double InfoPanelMaxWidth = 560;

    /// <summary>
    /// 按窗口宽度算信息栏宽度 —— 安卓平板是"主内容 + 侧栏"按比例分, 不是写死一个像素。
    ///
    /// 手机/窄窗: 侧栏收到 340, 把宽度让给视频;
    /// 平板/宽窗: 侧栏跟着长(30%), 简介、评论、合集都能多显示内容, 不用一直滚。
    ///
    /// 为什么不是"越宽越好": 右栏是文字流, 超过 560 之后一行 40+ 个字, 阅读时眼睛要横跳,
    /// 安卓平板的详情页也是把侧栏限制在一个阅读舒适宽度、而不是铺满。
    /// </summary>
    private static double InfoPanelWidthFor(double windowWidth)
    {
        if (double.IsNaN(windowWidth) || windowWidth <= 0) return 400;
        var w = Math.Round(windowWidth * 0.30);
        return Math.Clamp(w, InfoPanelMinWidth, InfoPanelMaxWidth);
    }

    /// <summary>窗口宽度变了就重算右栏(全屏/铺满时列宽是 0, 由 ApplyInfoPanelState 管, 这里跳过)</summary>
    private void OnWindowSizeChangedForInfoPanel(object sender, SizeChangedEventArgs e)
    {
        if (_closing || RightColumn == null) return;
        if (_isFullscreen || _isFillWindow || _tabletPortraitLayoutMounted) return;
        var w = InfoPanelWidthFor(e.NewSize.Width);
        if (Math.Abs(RightColumn.Width.Value - w) > 0.5)
            RightColumn.Width = new GridLength(w);
    }

    // ------------------------------------------------------------ 窄视频区控制栏

    /// <summary>
    /// 视频区窄于这个宽度时, 控制栏先收起「弹幕设置」这种纯文字按钮。
    ///
    /// ★ 背景(issue #31): 竖屏副屏 864 DIP, 右栏占 400 后视频只剩 464 DIP;
    ///   操作行的固定项加起来超过这个宽度就会挤成一团甚至被裁掉。
    /// ★ 用 VideoAreaRoot 的实际宽度判断而不是窗口宽度: 信息栏一收/一展、
    ///   窗口一拖, 视频列宽度都会变, 这个信号最直接。
    /// ★ 2026-11-05: 音量滑杆已收进弹层, 不再参与这里的取舍(它藏起来就等于弹层里空了)。
    /// </summary>
    private const double CompactControlBarWidth = 560;

    /// <summary>视频区窄于此宽度（如竖屏优化左栏）时收起弹幕输入框，避免把右侧清晰度/全屏按钮挤出画面。</summary>
    private const double CompactDanmakuComposerWidth = 520;

    /// <summary>再窄一档(竖屏手机比例的小窗)连播放时间也收, 只留图标按钮。</summary>
    private const double CompactTimeWidth = 380;

    /// <summary>极窄宽度（最小窗口下的竖屏左栏）收起倍速按钮，确保全屏按钮不被裁出画面。</summary>
    private const double CompactSpeedWidth = 300;

    private void OnVideoAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BtnDanmakuArea == null) return;
        var compact = e.NewSize.Width > 0 && e.NewSize.Width < CompactControlBarWidth;
        // ★ 2026-11-05: 不再 Collapse VolumeSlider —— 它在弹层里, 藏掉就是"展开后一片空白"。
        BtnDanmakuArea.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (TimeText != null) TimeText.Visibility = e.NewSize.Width > 0 && e.NewSize.Width < CompactTimeWidth ? Visibility.Collapsed : Visibility.Visible;
        if (SpeedBtn != null) SpeedBtn.Visibility = e.NewSize.Width > 0 && e.NewSize.Width < CompactSpeedWidth ? Visibility.Collapsed : Visibility.Visible;
        UpdateDanmakuComposerVisibility(e.NewSize.Width);
    }

    private void UpdateDanmakuComposerVisibility(double videoWidth = -1)
    {
        if (DanmakuComposer == null) return;
        var w = videoWidth >= 0 ? videoWidth : (VideoAreaRoot?.ActualWidth ?? 0);
        var tooNarrow = w > 0 && w < CompactDanmakuComposerWidth;
        DanmakuComposer.Visibility = CanUseOnlineDanmaku && !tooNarrow ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 同步「铺满窗口」按钮的字形与提示(进入前 = 虚线框"填充这块区域", 已铺满 = 向内的双箭头"还原")。
    ///
    /// 为什么图标要跟着状态变: 同一个位置点两下是"进入/退出", 字形不变的话用户不知道
    /// 现在是不是已经铺满了, 也不知道再点一下会发生什么。
    ///
    /// ★ 视频区**右键**也切同一个开关(见 OnVideoAreaMouseRightUp): 那个手势不需要文案
    ///   —— 右键按下去画面当场就变了, 状态是"看得见"的, 不用靠字形容器。
    /// </summary>
    private void UpdateFillWindowButton()
    {
        if (BtnFillWindow == null) return;
        // E9A6 = 虚线框(填充区域) / E73F = 向内的双箭头(还原)。两者在本字体里形状差异明显。
        if (FillWindowGlyph != null) FillWindowGlyph.Text = _isFillWindow ? "\uE73F" : "\uE9A6";
        BtnFillWindow.ToolTip = _isFillWindow ? "还原 (退出铺满窗口)" : "铺满窗口";
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    // ---------------------------------------- 最大化 / 还原: 视频区白块

    /// <summary>
    /// 窗口**最大化/还原**期间把视频区压黑, 盖住 libvlc 重协商 vout 时的白底。
    ///
    /// ★★★ 为什么必须有这一段(2026-10-17 用户澄清"全屏"其实是**窗口最大化**后立刻查清):
    ///   视频区原有的黑罩(<see cref="ArmResizeCover"/>)**只挂在
    ///   `WM_ENTERSIZEMOVE`/`WM_EXITSIZEMOVE` 上** —— 那是"用户拖窗口边缘改大小"的消息。
    ///   而最大化/还原走的是 **WM_SYSCOMMAND + WM_SIZE**, 根本不经过 ENTERSIZEMOVE
    ///   ⇒ 那段"客户区尺寸剧变 + vout 重协商"全程**没有任何遮罩**,
    ///   于是浅色主题下还原之后视频区上下就露出白块。
    ///   (深色主题下露出来的也是同一块白, 只是周围本来就暗, 不如浅色扎眼 —— 与用户
    ///     "浅色才明显"的描述一致。)
    ///
    /// ★ 为什么还要重刷原生窗口底色(调 `StartVideoBackdropFix`):
    ///   最大化/还原会让 libvlc 按新尺寸**重建 vout**, 新的 `Static` 子窗口又没被刷黑;
    ///   而第一版的定时器在起播后几拍就停了(`_videoBackdropTries` 用完), 这时没人管。
    ///
    /// ★ 为什么用"遮罩 + 延迟撤"而不是直接切窗口底色:
    ///   与全屏同一套理由 —— `VideoView` 是 HwndHost(airspace), 画在所有 WPF 元素之上,
    ///   窗口/RootGrid 的底色**盖不住它**; 只有 `VideoAreaRoot`(在 VideoView.Content 浮层里)
    ///   压得住, 见 <see cref="ArmFullscreenCover"/> 的说明。
    ///
    /// ★ 为什么不等状态稳定后再压、而是**先压**:
    ///   白块出现在"尺寸正在变"的那几十毫秒里, 等 `StateChanged` 送达时窗口往往已经重绘完,
    ///   那块白早就闪过去了。所以顺序是: 压黑 → 改状态 → 让新帧来撤。
    ///
    /// ★★★ 这里**不是"压一次就按住"**, 而是"每次尺寸变化都重新压 + 重新计时"(2026-10-17):
    ///   最大化/还原在 DWM 过渡动画里会连着发好几次尺寸变化(几十毫秒一次),
    ///   每次都会让 libvlc 重协商一轮 vout。如果只压第一次, 后面的每一轮又会露白
    ///   (实测: 只压第一次 → 还原后边带亮度 242; 每次都重压 → 0)。
    ///   ⇒ 语义是"**只要尺寸还在变, 就一直压着**", 与拖边缘那条(ENTER→EXIT)一致。
    /// </summary>
    private void ArmMaximizeCover()
    {
        if (_closing || _vmemMode || VideoAreaRoot == null) return;
        _resizeHold = true;
        _maximizeCoverArmed = true;
        VideoAreaRoot.Background = Brushes.Black;
        _fullscreenCoverArmed = true;

        // ★★ 同时**立刻**重刷 vout 原生底色并起短时守护(2026-10-17 加):
        //   尺寸剧变后 libvlc 会重新协商 vout, 那一刻 `Static` 子窗口会用**自己的类默认背景(白)**
        //   擦一次背景 —— 而它铺满整个视频区, 盖在画面之外的地方(letterbox/控制栏下方)。
        //   实测(bd-probe-barwho): 三者的几何完全一致, 白条却出现在底部 12% 处
        //   ⇒ 白不是"窗口没跟上布局", 就是 `Static` 自己擦出来的。
        //   ⇒ 每次尺寸变化都重新藏它 + 刷黑, 并让守护把接下来 3 秒盖住。
        StartVideoBackdropFix();

        // ★ 关键: 每次进来都**重新起算** settle —— 尺寸还在变就一直压着。
        _maximizeSettle ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(MaximizeSettleMs)
        };
        _maximizeSettle.Tick -= OnMaximizeSettle;
        _maximizeSettle.Tick += OnMaximizeSettle;
        _maximizeSettle.Stop();
        _maximizeSettle.Start();

        // ★ 绝对兜底: `_resizeHold` 会让 ReleaseFullscreenCover 直接 return, 而最大化/还原
        //   **没有** WM_EXITSIZEMOVE 那样的结束消息。万一 settle 也没等到, 遮罩就永远黑着
        //   (用户会看到"视频区全黑")。所以再挂一道无条件放闸的计时器。
        _maximizeCoverSafety ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(MaximizeCoverSafetyMs)
        };
        _maximizeCoverSafety.Tick -= OnMaximizeCoverSafety;
        _maximizeCoverSafety.Tick += OnMaximizeCoverSafety;
        _maximizeCoverSafety.Stop();
        _maximizeCoverSafety.Start();

        // 普通兜底计时器在按住期间只做"万一"之用(见 OnFullscreenCoverTimeout → ReleaseFullscreenCover
        // 会被 _resizeHold 挡住), 这里启动只是保持与拖边缘那条路同构。
        _fullscreenCoverTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FullscreenCoverTimeoutMs)
        };
        _fullscreenCoverTimer.Tick -= OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Tick += OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Start();
    }

    /// <summary>
    /// 尺寸已经稳定(一段时间没有再变)⇒ 放闸, 让"下一帧到达"立刻揭幕。
    /// ★ 只放闸、不直接揭幕: 尺寸刚停的这几帧 vout 可能还在重协商, 立刻揭幕会露白。
    ///   交给新帧(TimeChanged/Playing 会调 ReleaseFullscreenCover)是最准的时机。
    /// </summary>
    private void OnMaximizeSettle(object? sender, EventArgs e)
    {
        _maximizeSettle?.Stop();
        if (_closing) return;
        _resizeHold = false;
    }

    /// <summary>
    /// 绝对兜底: 无条件放闸并撤罩。
    /// ★ 走到这里说明"新帧"和"状态回调"都没来 —— 再不放闸就是永久黑屏。
    ///   宁可多露一帧白, 也不能让用户对着黑屏等下去。
    /// </summary>
    private void OnMaximizeCoverSafety(object? sender, EventArgs e)
    {
        _maximizeCoverSafety?.Stop();
        if (_closing) return;
        _resizeHold = false;
        _maximizeCoverArmed = false;
        ReleaseFullscreenCover();
    }

    /// <summary>
    /// 尺寸"稳定"的判定窗口(ms)。
    /// ★ 取 260 而不是 100: 最大化的 DWM 过渡动画实测约 200ms, 期间尺寸一直在变;
    ///   窗口太短会在动画中途就放闸, 后半段的白又露出来(实测 160ms 时仍有 242 的白)。
    ///   "尺寸停了再放闸"的代价只有这一小段黑屏, 而它正好被过渡动画盖住, 用户看不到。
    /// </summary>
    private const int MaximizeSettleMs = 260;

    /// <summary>绝对兜底时长(ms)。见 <see cref="ArmMaximizeCover"/> 里那段说明。</summary>
    private const int MaximizeCoverSafetyMs = 1200;

    private DispatcherTimer? _maximizeSettle;
    private DispatcherTimer? _maximizeCoverSafety;

    /// <summary>
    /// 最大化/还原结束: 放闸让遮罩能撤, 并把新 vout 的原生底色再刷一次。
    ///
    /// ★ 顺序必须是"先放闸再刷": 遮罩要靠"新帧到达"撤掉(TimeChanged/Playing 都会调
    ///   <see cref="ReleaseFullscreenCover"/>), 而它会检查 `_resizeHold`。
    /// ★ 这里**不**直接揭幕: 尺寸刚变完的这几帧 vout 可能还在重协商, 立刻揭幕会露白。
    ///   交给"新帧到达"撤(最准), 计时器只兜底(暂停中/极短片没有新帧)。
    /// </summary>
    private void ReleaseMaximizeCover()
    {
        if (_closing) return;
        _maximizeCoverSafety?.Stop();
        _resizeHold = false;
        _maximizeCoverArmed = false;

        // 状态已经落定 ⇒ 现在的窗口尺寸就是最终尺寸, 此时改类背景刷不会被打断
        StartVideoBackdropFix();
    }

    /// <summary>
    /// 窗口状态落定(基类在 <c>OnStateChanged</c> 里调): 放掉最大化期间的黑罩 + 重刷 vout 底色。
    /// ★ 全屏切换自己有一套更严格的收尾(见 ApplyFullscreenLayout), 这里跳过它免得两套互抢。
    /// </summary>
    protected override void OnWindowStateSettled()
    {
        if (_closing) return;
        if (_isFullscreen || _suppressStateCover) return;   // 全屏那条路由 ApplyFullscreenLayout 自己收尾
        if (!_maximizeCoverArmed) return;

        _maximizeCoverArmed = false;
        ReleaseMaximizeCover();
    }

    /// <summary>
    /// 标记"接下来一段时间内, 窗口状态变化由调用方自己负责遮罩"。
    ///
    /// 见 <see cref="_suppressStateCover"/> 的说明 —— 进出全屏必须走这个,
    /// 否则两套遮罩叠加会把画面压黑近一秒。
    /// </summary>
    private void SuppressStateCoverFor(int ms)
    {
        _suppressStateCover = true;
        _suppressStateCoverTimer ??= new DispatcherTimer();
        _suppressStateCoverTimer.Tick -= OnSuppressStateCoverDone;
        _suppressStateCoverTimer.Tick += OnSuppressStateCoverDone;
        _suppressStateCoverTimer.Stop();
        _suppressStateCoverTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _suppressStateCoverTimer.Start();
    }

    private void OnSuppressStateCoverDone(object? sender, EventArgs e)
    {
        _suppressStateCoverTimer?.Stop();
        _suppressStateCover = false;
    }

    /// <summary>
    /// 上一次看到的窗口尺寸, 用来发现"尺寸变了但没收到 WM_SYSCOMMAND"的那条路
    /// (例如代码里直接赋 `WindowState`, 或系统动画/贴边布局替我们最大化)。
    /// 见 <see cref="OnLayoutSizeChanged"/>。
    /// </summary>
    private Size _lastMeasuredSize;

    /// <summary>
    /// 兜底: 客户区尺寸**真的变了**但没压过遮罩时补压一次。
    ///
    /// ★ 为什么需要兜底: `WM_SYSCOMMAND` 只覆盖"用户点按钮/双击标题栏/快捷键"那几条路;
    ///   而**代码里直接赋 `WindowState`**(ScMaximize 那套之外)以及系统的"贴边布局(Aero Snap)"
    ///   不一定会经过它。2026-10-17 探针实测: 直接 `win.WindowState = Maximized` 就不发
    ///   WM_SYSCOMMAND, 那条路原本完全没有遮罩。
    /// ★ 只在"从没压过"时补, 且**不设** `_maximizeCoverArmed`: 这次补压没有对应的
    ///   WM_SYSCOMMAND, 由计时器兜底撤罩即可(见 ReleaseMaximizeCover 的说明)。
    /// </summary>
    private const int BackdropResumeDelayMs = 180;
    private DispatcherTimer? _backdropResumeTimer;
    private bool _modalResizeInProgress;
    private bool _backdropSuspendQueued;

    private void SuspendBackdropForResize()
    {
        if (_closing || _vmemMode || BackdropSuspended) return;
        if (!IsBackdropMaterialActive) return;
        SetBackdropSuspended(true);
        if (!_modalResizeInProgress) ResumeBackdropAfterResize();
    }

    private void QueueBackdropSuspendForResize()
    {
        if (_closing || _vmemMode || _backdropSuspendQueued || BackdropSuspended) return;
        _backdropSuspendQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _backdropSuspendQueued = false;
            if (!_closing) SuspendBackdropForResize();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ResumeBackdropAfterResize()
    {
        if (_closing || !BackdropSuspended) return;
        _backdropResumeTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(BackdropResumeDelayMs)
        };
        _backdropResumeTimer.Tick -= OnBackdropResumeTick;
        _backdropResumeTimer.Tick += OnBackdropResumeTick;
        _backdropResumeTimer.Stop();
        _backdropResumeTimer.Start();
    }

    private void OnBackdropResumeTick(object? sender, EventArgs e)
    {
        _backdropResumeTimer?.Stop();
        if (_closing) return;
        SetBackdropSuspended(false);
    }

    private void OnPlayerResizedForBackdrop()
    {
        if (_closing || _vmemMode || !IsLoaded || !IsVisible) return;
        if (_modalResizeInProgress) return;
        if (BackdropSuspended)
        {
            ResumeBackdropAfterResize();
            return;
        }
        if (WindowState == WindowState.Minimized) return;
        if (IsBackdropMaterialActive) QueueBackdropSuspendForResize();
    }

    protected override void OnWindowStateTransitionStarting()
    {
        base.OnWindowStateTransitionStarting();
        SuspendBackdropForResize();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (_closing || !IsLoaded) return;
        OnPlayerResizedForBackdrop();
        if (_isFullscreen || _suppressStateCover) return;
        if (sizeInfo.PreviousSize.Width < 1 || sizeInfo.PreviousSize.Height < 1) return;
        _lastMeasuredSize = sizeInfo.NewSize;

        // ★★ 每次尺寸变化都**重新压 + 重新计时**(2026-10-17):
        //   最大化/还原的过渡动画期间尺寸会连着变好几次, 每次 libvlc 都重协商一轮 vout。
        //   只压第一次的话, 后面每一轮又露白(实测: 只压第一次 → 还原后边带 242; 每次都压 → 0)。
        //   ⇒ 只要尺寸还在变, 就一直压着(不管幅度大小); 尺寸停了由 settle 计时器放闸。
        //   ★ 这里**不能**按"变化幅度"过滤: 还原动画的后半段变化幅度会变小,
        //     一旦据此放闸, 那一刻遮罩就撤了, 而 vout 还在重协商 ⇒ 白块又回来了(实测 242)。
        var dw = Math.Abs(sizeInfo.NewSize.Width - sizeInfo.PreviousSize.Width);
        var dh = Math.Abs(sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height);
        if (dw > 0.5 || dh > 0.5) ArmMaximizeCover();
    }

    /// <summary>最大化/还原的黑罩是否压着(见 <see cref="ArmMaximizeCover"/>)</summary>
    private bool _maximizeCoverArmed;

    /// <summary>
    /// 即将执行"最大化/还原/最小化"(WM_SYSCOMMAND) 时压黑视频区。
    ///
    /// ★ 为什么必须抢在**尺寸变化之前**: 白块出现在客户区尺寸剧变的那几十毫秒里;
    ///   等 `OnStateChanged` 送达时窗口已经重绘完, 那块白早就闪过去了。
    /// ★ 最小化也压上: 它同样会让 vout 重协商, 而恢复时露出来的白更显眼
    ///   (窗口整个重新可见)。恢复(SC_RESTORE)同理。
    /// </summary>
    protected override void OnSystemCommand(int command)
    {
        if (_closing) return;
        // 视频全屏期间那条路由 ApplyFullscreenLayout 自己管(它连着改多次窗口状态,
        // 这里再压一次会和它的收尾打架)。
        if (_isFullscreen || _suppressStateCover) return;

        if (command is SC_MAXIMIZE or SC_RESTORE or SC_MINIMIZE)
        {
            ArmMaximizeCover();
        }
    }

    /// <summary>
    /// 为真时**不要**为窗口状态变化压黑罩。
    ///
    /// ★★★ 为什么必须有它(2026-10-17 加, 修一处自己引入的回归):
    ///   "全屏"本身就是"最大化 + 收顶栏", 所以进出全屏**也会**触发
    ///   `WM_SYSCOMMAND`/尺寸剧变 —— 而全屏那条路已经有一套更严格的遮罩时序
    ///   (见 ApplyFullscreenLayout, 它自己 Arm/Release)。两边都压的后果实测是:
    ///   退全屏时这边又压了一层、且它要等 `_resizeHold` 的兜底超时才放
    ///   ⇒ 视频区在退全屏后**黑约 0.9 秒**(探针 bd-probe-fullscreen 立刻抓到)。
    ///   ⇒ 用这个标志把"全屏切换期间"整段排除掉。
    ///
    /// ★ 用**计时器**而不是"方法开头置真/结尾置假": 状态变化与尺寸变化是
    ///   `ApplyFullscreenLayout` 返回**之后**才由消息循环送达的, 结尾置假根本盖不住。
    /// </summary>
    private bool _suppressStateCover;

    private DispatcherTimer? _suppressStateCoverTimer;

    /// <summary>收藏状态只有这一个入口 —— 分散着改很容易出现两边显示不一致</summary>
    private void SetFavorited(bool favorited)
    {
        _isFavorited = favorited;
        UpdateActionCounts();
    }
    // "关闭"按钮已随顶栏改版删除(2026-09-26): 关播放器统一走 Esc/鼠标侧键的 HandleBackRequest。

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        FailOverlay.Visibility = Visibility.Collapsed;
        // 直播失败要重连直播, 离线/本地失败要重试离线/本地播放, 不能落回在线点播
        if (_isLive) _ = LoadLiveAsync(_liveRoomId);
        else if (_isLocalPlayback)
        {
            if (_currentOfflineEntry != null) _ = LoadOfflineEntryAsync(_currentOfflineEntry, ++_loadGen);
            else if (!string.IsNullOrEmpty(_currentLocalPath)) _ = ReplaceNativeInputAsync(() => LoadLocalFile(_currentLocalPath));
        }
        else _ = LoadVideoAsync();
    }

}
