using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BiliTouch.Helpers;
using LibVLCSharp.Shared;

namespace BiliTouch.Services;

/// <summary>
/// LibVLC 原生实例的进程级单例。
///
/// 为什么必须做成单例:
/// 之前每个播放器窗口都在构造函数里 new 一个 LibVLC, 在窗口关闭时 Dispose —— 而
/// LibVLCSharp 的 Core.Initialize / new LibVLC 属于"全进程只能初始化一次"的原生状态。
/// 快速反复开关播放器窗口时, 原生库被反复创建/销毁, 加上窗口销毁期间仍有原生回调
/// 进入托管代码, 会抛出 Windows 级错误弹窗:
///     "Exception Processing Message 0xc0000005 - Unexpected parameters"
/// (0xc0000005 = 访问违例, 不是 .NET 异常, 所以 DispatcherUnhandledException 兜不住它)
///
/// 现在统一由这里持有, 只在进程退出时释放一次。窗口开关只是挂载/卸载 MediaPlayer,
/// 不再触碰原生库的生命周期, 既消除了崩溃, 也让再次打开播放器变得更快。
/// </summary>
public static class VlcCore
{
    private static readonly object Gate = new();
    private static LibVLC? _instance;
    private static int _livePlayers;
    private static readonly Dictionary<string,LibVLC> EnhancementInstances = new();

    /// <summary>libvlc.dll / libvlccore.dll 所在目录</summary>
    public static string LibVlcDirectory =>
        Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");

    /// <summary>
    /// libvlc 插件目录(&lt;outdir&gt;/libvlc/win-x64/plugins/&lt;cat&gt;/*.dll)。
    ///
    /// ★ 为什么是"跟 libvlc.dll 放一起"而不是输出根目录(2026-09-26 瘦身时定的):
    ///   libvlccore 的插件扫描找的就是 libvlc.dll **同目录**下的 plugins/。以前我们把 plugins/
    ///   另拷一份到根目录、把 VLC_PLUGIN_PATH 指向那里, 结果发布包里同一套插件出现两次(-90MB)。
    ///   实测(bd-probe-vlc 探针, 在被测输出目录真播一段 MP4): 只留这一份时加载的模块数一模一样(497),
    ///   连 VLC_PLUGIN_PATH 指向不存在的目录都照样能播 —— 说明唯一必需的就是这一份。
    /// </summary>
    public static string PluginsDirectory =>
        Path.Combine(LibVlcDirectory, "plugins");

    /// <summary>
    /// 取得共享 LibVLC 实例; 首次调用时初始化原生库。
    /// 线程安全: 多个窗口/预热线程同时进入只会真正初始化一次。
    /// </summary>
    public static LibVLC Get()
    {
        lock (Gate)
        {
            var profile = EnhancementProfile.Current;
            if (_instance != null && !profile.Enabled) return _instance;
            if(profile.Enabled && EnhancementInstances.TryGetValue(profile.Key,out var enhanced))return enhanced;

            // VideoLAN.LibVLC.Windows 把原生库部署到 <outdir>/libvlc/win-x64/,
            // 而 LibVLC 默认只在 exe 同目录查找, 所以显式把路径告诉它。
            var libvlcDir = LibVlcDirectory;
            if (Directory.Exists(libvlcDir))
                Environment.SetEnvironmentVariable("LIBVLC_PATH", libvlcDir);

            try { Core.Initialize(Directory.Exists(libvlcDir) ? libvlcDir : null); }
            catch { /* 已初始化过, 忽略 */ }

            // ★★★ 给 libvlccore 的 vout_display_PlacePicture 挂除零守卫(2026-11-08)。
            //   背景: 0.2.9 退出播放器全屏后闪退, 异常代码 0xC0000094, 故障地址
            //   libvlccore+0x6F4E0 = 那句 `idiv rcx`, 分子分母同时为 0
            //   (source->i_visible_width / i_visible_height 都是 0)。
            //   触发条件是 d3d11 的 processor / super 两种缩放模式 —— 它们会在 vout 重建的
            //   窗口期拿一份被 video_format_Clean 清零的 pool_fmt 去算几何。
            //   ★ 必须在 Core.Initialize **之后**(否则还没有 libvlccore 可供插件导入)、
            //     在 new LibVLC **之前**(否则第一帧就可能崩在这个窗口期里)。
            //   细节与反汇编证据见 VlcPlacePictureGuard 的注释。
            VlcPlacePictureGuard.Install();

            // 显式给一份插件绝对路径(和 libvlc.dll 同目录那份是同一个地方)。
            // ★ 不要再传 `--plugin-path=`: VLC 3.0.20 已经删掉了这个选项, 传了会打印
            //   "Warning: option --plugin-path no longer exists"(实测, 它不生效, 只是噪声)。
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", PluginsDirectory);

            // 网络缓冲: 用用户设置(设置页「网络缓冲」, 默认 1000 = 旧版行为)。
            // ★ 媒体级还会再带一份(见 PlayerWindow.BuildMedia), 这里给"没有带选项的路径"
            //   (直播/重播兜底/探针)一个一致的值; 运行中改设置不重建内核, 所以这里只在首次生效。
            // ★ 2026-11-01: 自动档时这里也用 BufferPolicy 算出的值 —— 没有媒体选项的路径
            //   一样该吃到"按带宽挑预读"的收益; 手动档等价于直接用设置值。
            // ★ 实测它对**起播速度**几乎没影响(缓存按网速灌), 影响的是播放中的抗抖动余量。
            //
            // ★★★ 别加 `--demux=avformat`(2026-10-17 试过, 有致命副作用, 已撤回):
            //   它确实能修好 seek —— 实测 25%/70% 落点偏差都是 0ms(基线是 -32万ms)。
            //   **但它和 DASH 的 `input-slave` 音轨组合会让播放器在 seek 后直接进 Ended**:
            //   实测 state 从 Playing 变 Ended、位置冻在目标点不动、解码停止。
            //   而本项目的 DASH 一律是 input-slave 挂音轨 ⇒ 一换就"画面卡死"。
            //   (裸 libvlc 不带 slave 时是好的, 所以只测裸 libvlc 会得出错误结论 ——
            //    必须用 bd-probe-seekstick 走**产品自己的路径**验。)
            //   ⇒ seek 的问题改为在播放器层处理(见 PlayerWindow.RecoverFromFailedSeek)。
            var caching = BufferPolicy.Resolve(BufferMediaKind.VodSingle, null, shortVideo: false,
                SettingsStore.Instance.NetworkCachingMs, SettingsStore.Instance.AutoBufferPolicy);
            var options = new List<string>{"--intf=dummy","--no-osd","--no-snapshot-preview","--network-caching="+caching};
            if(profile.Enabled) options.AddRange(profile.Options);
            var instance = new LibVLC(options.ToArray());
            VideoEnhancement.Observe(instance,profile);
            // Keep every profile instance alive until process shutdown. Core.Initialize is process-wide;
            // toggling must never dispose a LibVLC instance while a player still references it.
            if(profile.Enabled)EnhancementInstances[profile.Key]=instance;else _instance=instance;
            return instance;
        }
    }

    /// <summary>
    /// 取一个**专供 vmem 输出**的 LibVLC 实例（进程内单例，随进程一起存活）。
    ///
    /// ★ 为什么要单独一个实例：`--vout=vmem` 是实例级选项，而共享实例
    ///   (`Get()`) 被普通播放器用着，不能为了 vmem 改它。两个实例共用同一份
    ///   libvlccore（`Core.Initialize` 是进程级的），代价只是多一份插件状态。
    /// ★ 用途：把解码帧交给 <see cref="BiliTouch.Helpers.VmemVideoView"/> 画成
    ///   普通 WPF 元素 —— 只有这样画面才能圆角、阴影、逐帧连续变形（HwndHost 不行）。
    /// </summary>
    public static LibVLC GetVmem()
    {
        lock (Gate)
        {
            if (_vmemInstance != null) return _vmemInstance;
            var profile = EnhancementProfile.Current;
            var libvlcDir = LibVlcDirectory;
            if (Directory.Exists(libvlcDir))
                Environment.SetEnvironmentVariable("LIBVLC_PATH", libvlcDir);
            try { Core.Initialize(Directory.Exists(libvlcDir) ? libvlcDir : null); } catch { }
            VlcPlacePictureGuard.Install();
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", PluginsDirectory);
            var caching = BufferPolicy.Resolve(BufferMediaKind.VodSingle, null, shortVideo: false,
                SettingsStore.Instance.NetworkCachingMs, SettingsStore.Instance.AutoBufferPolicy);
            // ★ vmem 下不需要 --vout=direct3d11 / 超分参数：帧不经过 D3D 交换链。
            var options = new List<string>
            {
                "--intf=dummy", "--no-osd", "--no-snapshot-preview",
                "--vout=vmem", "--network-caching=" + caching,
            };
            _vmemInstance = new LibVLC(options.ToArray());
            VideoEnhancement.Observe(_vmemInstance, profile);
            return _vmemInstance;
        }
    }

    private static LibVLC? _vmemInstance;

    /// <summary>
    /// 后台预热: 把原生库初始化 + 插件扫描的开销提前到应用启动阶段,
    /// 这样用户第一次点开视频时不用再等这几百毫秒。
    /// 失败只写日志, 不影响启动(真正的错误会在用户点开视频时暴露)。
    /// </summary>
    public static void Prewarm()
    {
        _ = Task.Run(() =>
        {
            try { Get(); }
            catch (Exception ex) { App.ReportError(ex); }
        });
    }

    /// <summary>登记一个窗口级 MediaPlayer(PlayerWindow 构造成功后调用)</summary>
    public static void RegisterPlayer() => Interlocked.Increment(ref _livePlayers);

    /// <summary>注销一个窗口级 MediaPlayer(PlayerWindow 关闭时调用)</summary>
    public static void UnregisterPlayer() => Interlocked.Decrement(ref _livePlayers);

    /// <summary>
    /// 进程退出时释放共享实例。
    /// 只有在"没有任何窗口级 MediaPlayer 存活"时才真正 Dispose:
    /// 退出瞬间若还有播放器没拆干净, 释放原生库正是 0xc0000005 访问违例的高发场景。
    /// 此时宁可交给操作系统回收(进程马上就结束了), 也不要冒弹错误框的风险。
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (Volatile.Read(ref _livePlayers) > 0) return;
            foreach(var core in EnhancementInstances.Values){try{core.Dispose();}catch{}}
            EnhancementInstances.Clear();
            if (_vmemInstance != null) { try { _vmemInstance.Dispose(); } catch { } _vmemInstance = null; }
            if (_instance == null) return;
            try { _instance.Dispose(); } catch { /* 退出阶段忽略 */ }
            _instance = null;
        }
    }
}
