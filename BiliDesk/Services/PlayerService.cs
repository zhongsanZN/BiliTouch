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

    /// <summary>
    /// 指定的播放器窗口是否仍是当前被复用的那个。
    /// 旧窗口关闭时用它判断"是否已经有新窗口接管", 避免把焦点抢回主窗口、
    /// 把刚打开的新播放器挤到后台(表现上就像"点了视频又立刻退出")。
    /// </summary>
    public bool IsCurrent(PlayerWindow window) => ReferenceEquals(_window, window);

    /// <summary>
    /// 预热播放器窗口(2026-11-04)。
    ///
    /// 官方客户端点开视频是"秒出", 我们是"先一团大色块, 等一会儿才出画面" ——
    /// 那团色块是 WPF 窗口**第一次**创建时的同步开销:
    ///   · PlayerWindow 的 XAML 树(2000+ 行, 含评论模板/弹幕层/控制栏)第一次 Measure/Arrange;
    ///   · LibVLC 的 VideoView(HwndHost)第一次建原生窗口;
    ///   · FluentWindow 的 WindowChrome / DWM 扩展第一次走一遍。
    /// 这三件事加起来在 3K/225% 下是几百毫秒到一秒级, 而且全部落在 UI 线程上。
    ///
    /// 做法: 启动后空闲时把窗口**建出来但不显示**(Opacity=0 + 屏幕外), 让它把这些
    /// 一次性开销先付掉; 真正播放时只需 Show + 起播, 那一步很快。
    ///
    /// ★ 必须等到主窗口 Loaded 之后再预热: 启动瞬间抢 UI 线程会让首页也慢一拍。
    /// ★ 预热失败必须静默(见 catch): 预热是优化, 不是功能, 失败不能影响点视频。
    /// </summary>
    public void WarmUp()
    {
        if (_window != null || _closing) return;
        try
        {
            var win = new PlayerWindow();
            // ★★★ 2026-11-04 修"点开视频详情页一直不出现"的**真根因**:
            //
            //   预热原来是: Left/Top = -32000 → Show() → Hide() → Left/Top = NaN。
            //   实测日志(player-show.log)打下起播那一刻的真实状态:
            //       vis=True state=Normal left=-14563.55 top=-14563.55
            //   也就是说 **NaN 没能把位置复位** —— WPF 对已经过布局的窗口,
            //   Left/Top 设 NaN 只表示"没显式设过", 并不会重新居中; 而
            //   WindowStartupLocation=CenterScreen 只在**首次** Show 时生效一次,
            //   这个窗口已经 Show 过了 ⇒ 两者都不起作用, 窗口永远停在屏幕外。
            //   (另外 DWM 会把它夹到 -14563 这种"虚拟桌面边界"值, 于是连我原来
            //    `Left <= -30000` 的兜底判断都永远不成立 —— 双重失效。)
            //
            //   现在改成: **预热也用真实屏幕坐标**, 只是靠 Opacity=0 + 移出可视区一点点
            //   来保证用户看不见。位置从一开始就在屏幕内, 不存在"要不要复位"的问题。
            var wa = SystemParameters.WorkArea;
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = wa.Left + Math.Max(0, (wa.Width - 1280) / 2);
            win.Top = wa.Top + Math.Max(0, (wa.Height - 760) / 2);
            win.Opacity = 0;          // 全透明: 用户看不见, 但布局/原生窗口照样建出来
            win.ShowInTaskbar = false;
            win.ShowActivated = false;
            win.Topmost = false;      // ★ 绝不置顶: 否则预热窗口会盖在首页上
            win.Show();
            // 建完立刻藏起来: 留在屏幕上会被 DWM 当成可见窗口持续合成, 白耗 GPU。
            win.Hide();
            win.Opacity = 1;
            win.ShowInTaskbar = true;
            // ★★★ 2026-11-04 修"刚进首页时播放详情页的 UI 控件直接叠在首页上, 点一下才消失":
            //
            //   根因是预热的那个 Show(): 窗口虽然 Opacity=0 且随即 Hide(), 但它在这段间隙里
            //   已经**完成了一次前台激活**(WPF 在 Show 时会尝试激活, 即使 ShowActivated=false
            //   也可能因为抢占而短暂置前), 并且它的 WPF 视觉层被 DWM 合成了那一帧 ——
            //   于是首页上会残留播放器控制栏/右栏的半透明影像, 直到用户点一下触发重绘才消失。
            //
            //   修法(两道):
            //     ① 预热结束把窗口**彻底降权**: 不置顶、不激活、清 Owner;
            //     ② 立刻把主窗口重新激活一次(见 App.WarmUp 调用点), 把焦点与 Z 序抢回首页。
            //   这里只做 ①, ② 交给调用方(它拿得到主窗口)。
            win.Topmost = false;
            win.Owner = null;
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
    /// 打开直播间(首页「直播」tab 用)。复用同一个播放器窗口, 走"直播"分支:
    /// 不查视频详情、不加载弹幕、不查三连状态、不上报历史进度。
    /// </summary>
    public void PlayLive(long roomId, string? title = null)
        => Play(title, win => win.PlayLive(roomId, title));

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
        var win = GetOrCreateWindow();
        if (win == null) return;

        start(win);
        if (!string.IsNullOrEmpty(title)) win.Title = title + " - BiliDesk";

        // ★★★ 2026-11-04 修"点了视频详情页还是不出现"(第三版, 按实测日志定案):
        //
        //   日志给出的那一刻状态是 vis=True / state=Normal / left=-14563 / top=-14563 ——
        //   窗口**可见、状态正常**, 只是位置在屏幕外。所以问题从来不是"没 Show",
        //   而是"位置在屏幕外, 且复位逻辑没生效"(根因见 WarmUp 里那段说明)。
        //
        //   这里改成**按屏幕矩形判定**, 不再用某个魔法阈值(-30000 那个判断被 DWM 夹到
        //   -14563 的值绕过了)。判定标准: 窗口矩形与任何屏幕工作区**不相交**就拉回。
        win.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppPaths.DataDir, "player-show.log"),
                    $"[after-start] vis={win.IsVisible} state={win.WindowState} " +
                    $"left={win.Left} top={win.Top} opacity={win.Opacity} " +
                    $"taskbar={win.ShowInTaskbar} closing={win.IsClosing}\n");
                if (win.IsClosing) return;
                if (!win.IsVisible) win.Show();
                if (win.WindowState == WindowState.Minimized)
                    win.WindowState = WindowState.Maximized;

                // 位置兜底: 按**真实屏幕矩形**判定, 与阈值无关
                var wa = SystemParameters.WorkArea;
                if (!IsOnAnyScreen(win))
                {
                    win.WindowStartupLocation = WindowStartupLocation.Manual;
                    win.Left = wa.Left + Math.Max(0, (wa.Width - win.Width) / 2);
                    win.Top = wa.Top + Math.Max(0, (wa.Height - win.Height) / 2);
                }
                win.Activate();
                win.Topmost = true;
                win.Topmost = false;   // 一闪拿焦点, 但不长期置顶

                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppPaths.DataDir, "player-show.log"),
                    $"[after-fix] left={win.Left} top={win.Top} onScreen={IsOnAnyScreen(win)}\n");
            }
            catch { /* 兜底失败不影响播放 */ }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
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
            // ★★★ 2026-11-04 修"点了视频在播但看不见窗口":
            //   预热(WarmUp)把窗口建在屏幕外 (-32000,-32000) 并 Hide() 掉了,
            //   而这里原来只调 Activate() —— 对一个隐藏窗口 Activate 是**无效**的,
            //   于是音频正常播、画面留在屏幕外, 用户完全看不到。
            //   必须: 先 Show(若是隐藏态) → 拉回屏幕内 → 再 Activate。
            if (!reuse.IsVisible) reuse.Show();
            if (reuse.Left <= -30000 || reuse.Top <= -30000)
            {
                // 从预热位置挪回主屏中央(与原 WindowStartupLocation=CenterScreen 等价)
                reuse.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                reuse.Left = double.NaN;
                reuse.Top = double.NaN;
            }
            if (reuse.WindowState == WindowState.Minimized)
                reuse.WindowState = WindowState.Normal;
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
