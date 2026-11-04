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
            // 屏幕外 + 全透明: 用户看不见, 但布局/原生窗口照样会建出来。
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -32000;
            win.Top = -32000;
            win.Opacity = 0;
            win.ShowInTaskbar = false;
            win.ShowActivated = false;
            win.Show();
            // 建完立刻藏起来: 留在屏幕外会被 DWM 当成可见窗口持续合成, 白耗 GPU。
            win.Hide();
            win.Opacity = 1;
            win.ShowInTaskbar = true;
            // ★ 把位置复位: 下次 Show 出来就在屏幕中央, 不用再靠 GetOrCreateWindow 兜底。
            win.Left = double.NaN;
            win.Top = double.NaN;
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
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
