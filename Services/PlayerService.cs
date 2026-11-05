using System;
using System.Windows;
using BiliDesk.Views;

namespace BiliDesk.Services;

/// <summary>
/// 原生播放器窗口管理(LibVLC 内核): 复用同一个窗口, 切换视频不重新加载 LibVLC。
/// </summary>
public class PlayerService
{
    private PlayerWindow? _window;
    private LiveRoomWindow? _liveWindow;

    /// <summary>
    /// 指定的播放器窗口是否仍是当前被复用的那个。
    /// 旧窗口关闭时用它判断"是否已经有新窗口接管", 避免把焦点抢回主窗口、
    /// 把刚打开的新播放器挤到后台(表现上就像"点了视频又立刻退出")。
    /// </summary>
    public bool IsCurrent(PlayerWindow window) => ReferenceEquals(_window, window);

    /// <summary>
    /// 预热播放器窗口(2026-11-04, 2026-11-04 二次修)。
    ///
    /// ## 为什么必须"建出来但不 Show"
    /// 官方客户端点开视频是"秒出", 我们是"先一团大色块" —— 那团色块是 WPF 窗口**第一次**
    /// 创建时的同步开销(XAML 树首次 Measure/Arrange、HwndHost 建原生窗口、DWM chrome)。
    ///
    /// ## ★★★ 为什么**不能** Show 出来(桌面上一块黑的根因)
    /// LibVLC 的 `VideoView` 是 **HwndHost**, 它内部是一个**原生子窗口**。
    /// WPF 的 `Opacity` 只作用于 WPF 自己的合成层, **管不到 HwndHost 里的原生窗口** ——
    /// 所以 `Opacity = 0` 对视频区那块黑色矩形**完全无效**, 一 Show 就直接画到桌面上。
    /// 之前的 `Show() → Hide()` 就是这个现象的来源: 中间那一帧黑块已经上屏了。
    ///
    /// ## 现在的做法
    /// 只做**不依赖可见性**的那部分预热:
    ///   · `InitializeComponent()` 已经跑了(构造 PlayerWindow 时就跑完) —— XAML 树建好;
    ///   · 强制一次 Measure/Arrange, 把布局算好(不需要窗口可见);
    ///   · 初始化 MediaPlayer 等托管对象。
    /// 真正的 HwndHost 原生窗口会在第一次 Show 时创建 —— 那一步很便宜(几十毫秒),
    /// 换来的是**绝不出现桌面上那块黑**。
    ///
    /// ★ 结论: 用"少预热一点"换"绝不出脏东西", 这个取舍没有第二种选法。
    /// </summary>
    public void WarmUp()
    {
        if (_window != null || _closing) return;
        try
        {
            var win = new PlayerWindow();

            // 只做布局预热, **不 Show**(见上面那段: Opacity 对 HwndHost 无效)。
            // Measure/Arrange 在不可见窗口上照样能跑, 足够把 XAML 树的首次开销付掉。
            win.Measure(new Size(1280, 760));
            win.Arrange(new Rect(0, 0, 1280, 760));

            // 位置先摆好: 真正 Show 时就在屏幕中央, 不会有一帧跑到角落
            var wa = SystemParameters.WorkArea;
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = wa.Left + Math.Max(0, (wa.Width - 1280) / 2);
            win.Top = wa.Top + Math.Max(0, (wa.Height - 760) / 2);

            win.ShowInTaskbar = true;
            win.ShowActivated = false;
            win.Topmost = false;
            win.Owner = null;

            // ★ 不 Show、不 Hide: 窗口保持"已构造未显示"状态。
            //   首次真正播放时 GetOrCreateWindow 会 Show 它, 那时才建原生窗口。
            _window = win;
        }
        catch
        {
            // 预热失败就退回"用时再建", 与从前一致
        }
    }

    private bool _closing;
    /// <summary>应用退出时不再预热/新建窗口</summary>
    public void MarkClosing() => _closing = true;

    /// <summary>播放视频(复用窗口)</summary>
    public void PlayVideo(string bvid, string? title = null)
        => Play(title, win => win.PlayVideo(bvid));

    /// <summary>
    /// Open an independent live window. VOD comments, actions and timeline stay in PlayerWindow.
    /// </summary>
    public void PlayLive(long roomId, string? title = null)
    {
        if (_closing || roomId <= 0) return;
        try
        {
            // Keep one active playback surface, as before the live/VOD split.
            var previous = _window; _window = null; previous?.Close();
            if (_liveWindow == null || _liveWindow.IsClosing)
            {
                var live = new LiveRoomWindow(); _liveWindow = live;
                live.Closed += (_, _) => { if (ReferenceEquals(_liveWindow, live)) _liveWindow = null; };
            }
            var window = _liveWindow;
            window.WindowState = WindowState.Maximized;
            if (!window.IsVisible) window.Show();
            window.OpenRoom(roomId, title); window.Activate();
        }
        catch (Exception ex) { App.ReportError(ex); Svc.Toast.Show("直播间初始化失败: " + ex.Message); }
    }

    /// <summary>
    /// 播放本地文件(离线缓存页用)。同样复用播放器窗口, 只是走"本地文件"分支:
    /// 不查接口、不加载弹幕、不上报历史。
    /// </summary>
    public void PlayLocalFile(string path, string? title = null)
        => Play(title, win => win.PlayLocalFile(path));

    /// <summary>
    /// 三种播放入口的共同外壳: 取(或新建)播放器窗口 → 交给具体分支起播 → 设标题。
    ///
    /// 差别只在"起播那一行", 以前三个方法把取窗口、判空、设标题各连同它抄了一遍。
    /// 复用闸门(<see cref="GetOrCreateWindow"/>)是这里最要紧的部分, 抄三份意味着
    /// 改它要同步改三处, 漏一处的表现是"某个入口不走复用逻辑"(点了视频又立刻退出)。
    ///
    /// 注: 三个入口以前都带一个 <c>coverUrl</c> 参数并由 4 处调用点传进来, 但**从来没用过**
    /// —— 窗口标题只用到 title。已删除, 别再加回来: 一个从不参与行为的参数会让人以为
    /// 播放器拿得到封面(比如想据此设置窗口图标), 而实际什么也没发生。
    /// </summary>
    private void Play(string? title, Action<PlayerWindow> start)
    {
        var live = _liveWindow; _liveWindow = null; live?.Close();
        var win = GetOrCreateWindow();
        if (win == null) return;

        start(win);
        if (!string.IsNullOrEmpty(title)) win.Title = title + " - BiliDesk";

        // ★★★ 2026-11-04 删除这里的"强制显形兜底"(它曾是桌面上那块黑的来源之一)。
        //
        //   原来这里排了一个 Dispatcher 回调, 里面有:
        //       if (!win.IsVisible) win.Show();
        //       win.Topmost = true;  win.Topmost = false;
        //   问题: 窗口的显示状态在 GetOrCreateWindow 里已经处理好了(该 Show 的 Show、
        //   该最大化的最大化)。这里再 Show 一次纯属重复, 而且 Topmost 一闪会让窗口
        //   抢到最前面 —— 配合 HwndHost(原生子窗口不受 Opacity 影响), 就会在桌面上
        //   闪出一块黑色视频区。
        //   ⇒ 显形只有一个入口: GetOrCreateWindow。这里什么都不做。
    }

    /// <summary>
    /// 窗口是否有**任何部分**落在某个屏幕的工作区里。
    ///
    /// ★ 为什么按矩形判而不是按 Left 阈值: 实测 DWM 会把屏幕外的窗口坐标夹到
    ///   -14563 这种"虚拟桌面边界", 任何写死的阈值(比如 -30000)都可能被绕过。
    ///   直接算矩形与 WorkArea 的交集才是可靠判据。
    /// </summary>
    private static bool IsOnAnyScreen(Window win)
    {
        double w = win.ActualWidth > 0 ? win.ActualWidth : win.Width;
        double h = win.ActualHeight > 0 ? win.ActualHeight : win.Height;
        if (double.IsNaN(w) || w <= 0) w = 320;
        if (double.IsNaN(h) || h <= 0) h = 240;
        var r = new Rect(win.Left, win.Top, w, h);
        var wa = SystemParameters.WorkArea;
        return r.IntersectsWith(wa) && r.Width > 0 && r.Height > 0;
    }

    private PlayerWindow? GetOrCreateWindow()
    {
        // 关键: 正在关闭的窗口不能复用。
        // 关闭流程是"先隐藏窗口 -> 后台拆解原生资源 -> 最后才真正 Close()"。
        // 这段窗口期内 IsLoaded 仍为 true, 如果直接复用, 拆解结束时的 Close() 会把
        // 刚打开的新视频一起关掉 —— 表现为"点了视频又立刻自动退出"。
        if (_window is { IsLoaded: true } reuse && !reuse.IsClosing)
        {
            // ★★★ 2026-11-04 修"一直以来根本没有默认最大化"的**根因**:
            //
            //   原来 `WindowState = Maximized` 只写在**新建窗口**那条路径里(见下面)。
            //   但预热之后, 用户点视频走的是**这条复用路径** —— 这里从头到尾没有设过
            //   WindowState, 所以窗口以 Normal(1280×760) 打开。
            //   用户看到的就是"从来没有默认最大化", 而我以为写了就等于生效了。
            //
            //   现在: 复用路径也**显式最大化**, 并且放在 Show() 之前(理由同下面那段:
            //   Show 之后再改会先按 Normal 布局一帧再跳, 那一下闪动正好落在起播画面上)。
            if (!reuse.IsVisible)
            {
                reuse.WindowState = WindowState.Maximized;   // ★ 先定状态
                reuse.Show();                                 // ★ 再显示
            }
            else if (reuse.WindowState == WindowState.Minimized)
            {
                reuse.WindowState = WindowState.Maximized;
            }
            else if (reuse.WindowState != WindowState.Maximized)
            {
                // 窗口已经可见但被用户还原过: 仍然按"进详情页默认最大化"的要求拉满
                reuse.WindowState = WindowState.Maximized;
            }

            // 位置兜底: 预热若把它摆到了屏幕外(不该发生, 但便宜), 拉回主屏中央
            if (!IsOnAnyScreen(reuse))
            {
                var wa0 = SystemParameters.WorkArea;
                reuse.WindowStartupLocation = WindowStartupLocation.Manual;
                reuse.Left = wa0.Left + Math.Max(0, (wa0.Width - 1280) / 2);
                reuse.Top = wa0.Top + Math.Max(0, (wa0.Height - 760) / 2);
            }
            reuse.Activate();
            return reuse;
        }

        PlayerWindow win;
        try
        {
            win = new PlayerWindow();
        }
        catch (Exception ex)
        {
            // LibVLC 原生库初始化失败时构造函数会抛异常, 这里兜住避免整个应用崩掉
            App.ReportError(ex);
            Svc.Toast.Show("播放器初始化失败: " + ex.Message);
            return null;
        }

        // 只在引用仍指向自己时才清空, 避免旧窗口的 Closed 把新窗口的引用一起置空
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, win)) _window = null;
        };
        _window = win;
        // ★ 进详情页默认最大化(用户要求): 平板/大屏上视频页本来就该占满可用空间。
        //   这是**窗口最大化**, 不是视频全屏 —— 右栏、控制栏都还在, 用户随时可以还原。
        //   ★ 必须放在 Show() 之前: Show 之后再改 WindowState 会先按 1280×760 布局一帧再跳,
        //     那一下闪动正好落在视频起播的画面上。
        win.WindowState = WindowState.Maximized;
        win.Show();
        return win;
    }
}
