using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BiliTouch.Helpers;

/// <summary>
/// 给 libvlccore 的 <c>vout_display_PlacePicture</c> 加一道「除零守卫」—— 修 0.2.9 的
/// **退出播放器全屏后卡死闪退**（异常代码 0xC0000094 / INTEGER_DIVIDE_BY_ZERO）。
///
/// ★★★ 根因（2026-11-08 用 minidump + 反汇编钉死，别再猜）：
///   崩溃线程 RIP = libvlccore.dll+0x6F4E0，寄存器 <c>Rax = Rcx = 0</c>，故障指令是
///   <code>
///     06F4DE  4899        cqo
///     06F4E0  48F7F9      idiv rcx      ← 0 / 0 ⇒ #DE
///   </code>
///   该地址在 3.0.x 源码里就是 <c>src/video_output/display.c</c> 的这一行：
///   <code>
///     const int64_t scaled_height = (int64_t)height * display_width * cfg->display.sar.num * source->i_sar_den
///                                 / (width * source->i_sar_num * cfg->display.sar.den);
///   </code>
///   分子为 0 是因为 <c>source->i_visible_height == 0</c>，分母为 0 是因为
///   <c>source->i_visible_width == 0</c> —— **两个可见尺寸同时是 0**。
///   函数开头只挡了 <c>cfg->display.width/height == 0</c>，**没有**挡 source 尺寸为 0。
///
///   调用方是 <c>libdirect3d11_plugin.dll</c>（dump 里返回地址 0xCBE6 = 对
///   <c>__imp_vout_display_PlacePicture</c> 那次 call 的返回点），它来自：
///   <code>
///     // modules/video_output/win32/direct3d11.c:653  UpdateBackBuffer()
///     if (proc_upscale)            // ← 只有 processor / super 两种模式为真
///         D3D11_UpscalerUpdate(..., &amp;sys-&gt;pool_fmt, &amp;sys-&gt;quad_fmt, &amp;cfg);
///     // d3d11_scaler.cpp:257
///     vout_display_PlacePicture(&amp;place, fmt /* = pool_fmt */, &amp;display, true);
///   </code>
///   <c>pool_fmt</c> 在 <c>Direct3D11DestroyPool()</c> 里被 <c>video_format_Clean()</c> 整块清零，
///   而 <c>UpdateBackBuffer()</c> 在这之后仍会被调用一次（vout 重建/尺寸剧变的窗口期）
///   ⇒ 传进去的 source 是「全 0 的 video_format_t」⇒ 0/0。
///   ★ 退出全屏正好是最密集的 vout 重建时机（窗口从整屏缩回工作区，一次布局里连改多次尺寸），
///     所以现象是「一退全屏就闪退」，而不是随机崩。
///
/// ★★ 为什么在**应用侧**打补丁，而不是换个 upscale 模式：
///   <c>processor</c> 与 <c>super</c> 共用同一个 <c>D3D11_UpscalerUpdate()</c> 调用点
///   （<c>proc_upscale = upscale_VideoProcessor || upscale_SuperResolution</c>），换过去照样崩；
///   唯一能绕开的是 <c>linear</c> / <c>point</c>（<c>scaleProc == NULL</c>，压根不调用），
///   但那就等于把用户的「厂商超分」功能废掉。上游 3.0.x 至今没有修（只有 4.0 的 238acb6a 重构），
///   NuGet 上 <c>VideoLAN.LibVLC.Windows</c> 最新也只有 3.0.24。
///   ⇒ 只能我们自己在 IAT 上插一层：参数非法时直接给出「空 place」并返回，合法时原样转发。
///
/// ★ 只补 <c>i_visible_width/height</c> 与 sar 的零值判断 —— 其余逻辑**一律交给原函数**，
///   绝不在托管侧重算几何：几何算错会让画面错位/黑屏，比崩溃更难查。
/// </summary>
internal static class VlcPlacePictureGuard
{
    /// <summary>守卫是否已经挂上（<see cref="Install"/> 成功后为 true）</summary>
    public static bool IsInstalled { get; private set; }

    /// <summary>
    /// 是否尝试过安装。用来区分「还没试」与「试了但失败」——
    /// 后者要触发降级（见 <see cref="VideoEnhancement"/>），不能静默继续用会崩的模式。
    /// </summary>
    public static bool Attempted { get; private set; }

    /// <summary>安装失败的原因（诊断用，成功时为 null）</summary>
    public static string? FailureReason { get; private set; }

    /// <summary>
    /// 被守卫拦下来的次数 —— **这就是"本来会崩"的直接证据**。
    ///
    /// ★ 为什么非要这个计数：崩溃是进程级闪退，事后只能从 dump 里读；
    ///   而"修好了"这件事如果只凭"跑了几轮没崩"，无法区分
    ///   「危险路径压根没走到」与「走到了但被挡住了」。
    ///   有这个计数，探针跑完就能说清是哪一种（见 FullscreenVerification）。
    /// </summary>
    public static long BlockedCount => Interlocked.Read(ref _blockedCount);

    /// <summary>成功转发给 libvlccore 的次数（用来确认守卫没有把正常帧也挡掉）</summary>
    public static long ForwardedCount => Interlocked.Read(ref _forwardedCount);

    private static long _blockedCount;
    private static long _forwardedCount;

    private const string PluginFileName = "libdirect3d11_plugin.dll";
    private const string CoreFileName = "libvlccore.dll";
    private const string ExportName = "vout_display_PlacePicture";

    /// <summary>
    /// <c>vout_display_place_t</c>：4 个 int（x / y / width / height），共 16 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Place
    {
        public int X;
        public int Y;
        public uint Width;
        public uint Height;
    }

    /// <summary>
    /// 原生函数签名。bool 用 <see cref="byte"/> 接：Win64 下 C 的 bool 占 1 字节。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PlacePictureDelegate(IntPtr place, IntPtr source, IntPtr cfg, byte doClipping);

    /// <summary>托管侧实现的签名（给 <see cref="Marshal.GetFunctionPointerForDelegate"/> 用）</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PlacePictureShim(IntPtr place, IntPtr source, IntPtr cfg, byte doClipping);

    /// <summary>
    /// 原函数的地址。**必须静态持有**：委托一旦被 GC，原生那边就跳到已释放的 thunk。
    /// </summary>
    private static PlacePictureDelegate? _original;

    /// <summary>我们自己那一层。同样必须静态持有，否则 GC 后 IAT 指向野指针。</summary>
    private static readonly PlacePictureShim ShimDelegate = Shim;

    private static readonly IntPtr ShimPointer = Marshal.GetFunctionPointerForDelegate(ShimDelegate);

    /// <summary><c>video_format_t</c> 里我们要读的那几个字段的字节偏移（见 include/vlc_es.h）</summary>
    private const int FormatVisibleWidthOffset = 20;   // i_chroma(4) + i_width(4) + i_height(4) + x/y_offset(8)
    private const int FormatVisibleHeightOffset = 24;
    private const int FormatSarNumOffset = 32;
    private const int FormatSarDenOffset = 36;

    /// <summary>
    /// 为 true 时**不**装守卫。只给对照实验用（<c>--verify-fullscreen --no-guard</c>）：
    /// 关掉它应该能复现闪退 —— 能复现才证明这个崩溃真的由那句话引起，而不是我们猜的。
    /// 产品代码里没有任何地方会置它。
    /// </summary>
    public static bool DisabledByFlag { get; set; }

    /// <summary>
    /// 挂守卫。幂等、绝不抛异常 —— 挂不上只是「还可能崩」，挂的时候抛出去就是「一定起不来」。
    ///
    /// 调用时机：<c>VlcCore.Get()</c> 里 <c>Core.Initialize</c> **之后**、创建 LibVLC 实例**之前**。
    /// 原因：必须先有 libvlccore 才能把它导出给插件，而插件要我们主动预加载（否则它只在
    /// 第一次开视频时才被 module_need 载入，那时补丁就赶不上第一帧）。
    /// </summary>
    public static void Install()
    {
        if (IsInstalled) return;
        Attempted = true;
        if (DisabledByFlag)
        {
            FailureReason = "DisabledByFlag";
            Log("按对照实验要求跳过了守卫（--no-guard）");
            return;
        }
        try
        {
            var pluginPath = System.IO.Path.Combine(
                BiliTouch.Services.VlcCore.LibVlcDirectory, "plugins", "video_output", PluginFileName);
            if (!System.IO.File.Exists(pluginPath))
            {
                FailureReason = "找不到 " + pluginPath;
                return;
            }

            // 预加载：libvlccore 之后 module_need 时会命中这份已加载的模块（同一路径 ⇒ 同一个模块），
            // 于是我们改的 IAT 就是它真正会用的那份。
            // ★ 必须用 LOAD_WITH_ALTERED_SEARCH_PATH —— 插件的依赖(libvlccore.dll)就在它上一层目录,
            //   普通 LoadLibrary 只按 exe 目录/PATH 找, 会直接失败(错误 126)。VLC 自己加载插件用的
            //   也正是这个标志(见 src/win32/plugin.c), 用同一个才能保证命中同一份模块。
            var module = LoadLibraryExW(pluginPath, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (module == IntPtr.Zero)
            {
                FailureReason = "LoadLibrary 失败，Win32 错误 " + Marshal.GetLastWin32Error();
                return;
            }

            var core = GetModuleHandleW(CoreFileName);
            if (core == IntPtr.Zero)
            {
                FailureReason = "libvlccore 未加载";
                return;
            }

            var originalExport = GetProcAddress(core, ExportName);
            if (originalExport == IntPtr.Zero)
            {
                FailureReason = "libvlccore 没有导出 " + ExportName;
                return;
            }

            // ★ 顺序要紧: **先**把原函数存进 _original, **再**改 IAT。
            //   反过来的话, 从"改完 IAT"到"_original 赋值"之间若真有帧进来,
            //   Shim 会看到 _original == null 而直接 return ⇒ 那一帧算出全 0 的 place ⇒ 画面闪一下黑。
            //   虽然窗口极小, 但这条顺序没有任何代价, 没理由留个坑。
            _original = Marshal.GetDelegateForFunctionPointer<PlacePictureDelegate>(originalExport);

            if (!TryPatchImport(module, CoreFileName, ExportName, ShimPointer, out var detail))
            {
                _original = null;   // 没打上补丁就别留着一个"看起来能用"的委托
                FailureReason = detail;
                return;
            }

            IsInstalled = true;
            FailureReason = null;
            Log("已挂上 vout_display_PlacePicture 除零守卫（插件 " + PluginFileName + "）");
        }
        catch (Exception ex)
        {
            FailureReason = ex.GetType().Name + ": " + ex.Message;
        }

        // 走到这里说明没挂上。这条必须留痕：挂不上就等于"还会崩"，而它静默失败的话
        // 用户只会看到又一次闪退、我们却没有任何线索。
        if (!IsInstalled) Log("未能挂上除零守卫: " + FailureReason);
    }

    /// <summary>
    /// 把一行结果写进 <c>vlc-debug.txt</c>（<see cref="BiliTouch.Services.AppPaths"/> 里那个）。
    /// 不用 <c>App.ReportError</c>：那不是异常，混进 errors.txt 会让人以为出了 bug。
    /// 失败一律吞掉 —— 打日志失败绝不能影响播放。
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(BiliTouch.Services.AppPaths.DataDir, "vlc-debug.txt"),
                "[PlacePictureGuard] " + message + Environment.NewLine);
        }
        catch { /* 日志失败不影响播放 */ }
    }

    /// <summary>
    /// 在 <paramref name="module"/> 的导入表里找到 <paramref name="importedFrom"/> 提供的
    /// <paramref name="importName"/>，把它的 IAT 槽改写成 <paramref name="replacement"/>。
    ///
    /// 为什么要走 IAT 而不是 inline hook：x64 的 inline hook 要解码指令长度、处理 RIP 相对寻址，
    /// 一处算错就是「整个播放器炸在别的地方」；而插件对 <c>vout_display_PlacePicture</c> 的调用
    /// 恰恰是**间接调用**（反汇编实测 <c>call qword ptr [rip+…]</c> → thunk → IAT），
    /// 改 IAT 是唯一不需要碰指令流的做法。
    /// </summary>
    internal static bool TryPatchImport(IntPtr module, string importedFrom, string importName,
        IntPtr replacement, out string detail)
    {
        detail = "";
        var baseAddress = module;
        if (!TryReadDosAndNtHeaders(baseAddress, out var ntHeaders, out var sizeOfImage))
        {
            detail = "读 PE 头失败";
            return false;
        }

        // DataDirectory[1] = 导入表
        var optionalHeader = ntHeaders + 24;                       // IMAGE_NT_HEADERS64.OptionalHeader
        var importDirRva = ReadUInt32(baseAddress, optionalHeader + 112 + 1 * 8);
        if (importDirRva == 0)
        {
            detail = "插件没有导入表";
            return false;
        }

        for (var index = 0; ; index++)
        {
            var descriptor = baseAddress + (int)importDirRva + index * 20;   // IMAGE_IMPORT_DESCRIPTOR = 20 字节
            var originalFirstThunk = ReadUInt32(baseAddress, descriptor);
            var nameRva = ReadUInt32(baseAddress, descriptor + 12);
            var firstThunk = ReadUInt32(baseAddress, descriptor + 16);
            if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0) break;   // 全 0 描述符 = 结束
            if (nameRva == 0 || firstThunk == 0) continue;

            var dllName = ReadAscii(baseAddress + (int)nameRva);
            if (!string.Equals(dllName, importedFrom, StringComparison.OrdinalIgnoreCase)) continue;

            var thunkRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
            for (var slot = 0; ; slot++)
            {
                var thunk = ReadUInt64(baseAddress, baseAddress + (int)thunkRva + slot * 8);
                if (thunk == 0) break;
                // 最高位为 1 ⇒ 按序号导入，没有名字，跳过
                if ((thunk & (1UL << 63)) != 0) continue;

                var hintName = ReadAscii(baseAddress + (int)(thunk & 0x7FFFFFFF) + 2);
                if (!string.Equals(hintName, importName, StringComparison.Ordinal)) continue;

                var iatSlot = baseAddress + (int)firstThunk + slot * 8;
                if (!VirtualProtect(iatSlot, (UIntPtr)IntPtr.Size, PageReadWrite, out var oldProtect))
                {
                    detail = "VirtualProtect 失败，Win32 错误 " + Marshal.GetLastWin32Error();
                    return false;
                }
                try
                {
                    Marshal.WriteIntPtr(iatSlot, replacement);
                }
                finally
                {
                    VirtualProtect(iatSlot, (UIntPtr)IntPtr.Size, oldProtect, out _);
                }
                _ = sizeOfImage;   // 只用于 PE 头校验，改 IAT 不需要它
                return true;
            }
        }

        detail = "插件没有从 " + importedFrom + " 导入 " + importName;
        return false;
    }

    private static bool TryReadDosAndNtHeaders(IntPtr module, out IntPtr ntHeaders, out uint sizeOfImage)
    {
        ntHeaders = IntPtr.Zero;
        sizeOfImage = 0;
        try
        {
            if (ReadUInt16(module, module) != 0x5A4D) return false;              // 'MZ'
            var lfanew = ReadInt32(module, module + 0x3C);
            if (lfanew <= 0 || lfanew > 0x1000) return false;
            ntHeaders = module + lfanew;
            if (ReadUInt32(module, ntHeaders) != 0x00004550) return false;       // 'PE\0\0'
            sizeOfImage = ReadUInt32(module, ntHeaders + 24 + 56);               // OptionalHeader.SizeOfImage
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ushort ReadUInt16(IntPtr buffer, IntPtr address) =>
        (ushort)Marshal.ReadInt16(buffer, (int)(address - buffer));

    private static uint ReadUInt32(IntPtr buffer, IntPtr address) =>
        (uint)Marshal.ReadInt32(buffer, (int)(address - buffer));

    private static uint ReadUInt32(IntPtr buffer, int offset) =>
        (uint)Marshal.ReadInt32(buffer, offset);

    private static int ReadInt32(IntPtr buffer, IntPtr address) =>
        Marshal.ReadInt32(buffer, (int)(address - buffer));

    private static ulong ReadUInt64(IntPtr buffer, IntPtr address) =>
        (ulong)Marshal.ReadInt64(buffer, (int)(address - buffer));

    private static string ReadAscii(IntPtr address)
    {
        var builder = new StringBuilder(64);
        for (var i = 0; i < 64; i++)
        {
            var b = Marshal.ReadByte(address, i);
            if (b == 0) break;
            builder.Append((char)b);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 守卫本体：参数会让原函数除零时，直接返回「空 place」；否则原样转发给 libvlccore。
    ///
    /// ★ 判据只取「原函数会拿去做分母的那几个量」：
    ///   <c>width * source->i_sar_num * cfg->display.sar.den</c> 与
    ///   <c>height * source->i_sar_den * cfg->display.sar.num</c>。
    ///   这里只查 source 侧（cfg 的 display 尺寸原函数自己已经挡了，sar 在正常播放里恒为 1:1），
    ///   宁可少判也不要多判 —— 多判会把正常帧挡成黑屏。
    /// </summary>
    private static void Shim(IntPtr place, IntPtr source, IntPtr cfg, byte doClipping)
    {
        var original = _original;
        if (original == null) return;

        // place 为空/原函数为空都是「不该发生」，静默跳过，绝不在这里抛（会直接穿透到原生栈）。
        if (place == IntPtr.Zero) return;

        if (source == IntPtr.Zero)
        {
            Clear(place);
            return;
        }

        try
        {
            var visibleWidth = (uint)Marshal.ReadInt32(source, FormatVisibleWidthOffset);
            var visibleHeight = (uint)Marshal.ReadInt32(source, FormatVisibleHeightOffset);
            var sarNum = (uint)Marshal.ReadInt32(source, FormatSarNumOffset);
            var sarDen = (uint)Marshal.ReadInt32(source, FormatSarDenOffset);

            // ★ 崩溃现场就是 visible_width == visible_height == 0（0/0）。
            //   sar 为 0 同样会让分母为 0，一起挡掉。
            if (visibleWidth == 0 || visibleHeight == 0 || sarNum == 0 || sarDen == 0)
            {
                Interlocked.Increment(ref _blockedCount);
                Clear(place);
                return;
            }
        }
        catch
        {
            // 读不到参数（理论上不可能）⇒ 走原函数，让它的行为与打补丁前一致
        }

        Interlocked.Increment(ref _forwardedCount);
        original(place, source, cfg, doClipping);
    }

    /// <summary>
    /// 自证：拿一份**全 0 的 video_format_t**（就是崩溃现场那种）直接喂给守卫，
    /// 断言它不再把参数转发给 libvlccore，而是直接给出空 place。
    ///
    /// ★ 为什么不靠"多跑几轮全屏看它崩不崩"：崩溃需要 vout 重建的精确时序，
    ///   本地复现不稳定（实测 12 轮全屏 + 网页布局 + super 缩放也没崩）。
    ///   而这段逻辑本身是可以**确定性**验证的 —— 给定零参数，它必须不调用原函数。
    ///   两者合起来才叫"验过了"：确定性逻辑靠断言，时序问题靠长时间跑。
    /// </summary>
    /// <returns>null = 通过；否则是失败原因</returns>
    public static string? SelfTest()
    {
        if (!IsInstalled) return "守卫未安装，无法自证";

        var source = Marshal.AllocHGlobal(256);
        var cfg = Marshal.AllocHGlobal(64);
        var place = Marshal.AllocHGlobal(16);
        try
        {
            // 全 0 的 source —— 与 minidump 里那份一致（i_visible_width/height = 0, sar = 0/0）
            for (var offset = 0; offset < 256; offset += 4) Marshal.WriteInt32(source, offset, 0);
            for (var offset = 0; offset < 64; offset += 4) Marshal.WriteInt32(cfg, offset, 0);
            // place 先写成"脏"值：如果守卫没生效，原函数会把它覆盖成别的（或直接崩掉进程）
            for (var offset = 0; offset < 16; offset += 4) Marshal.WriteInt32(place, offset, 0x7F7F7F7F);

            var beforeBlocked = BlockedCount;
            var beforeForwarded = ForwardedCount;
            Shim(place, source, cfg, 0);

            if (ForwardedCount != beforeForwarded) return "零参数被错误地转发给了 libvlccore";
            if (BlockedCount != beforeBlocked + 1) return "守卫没有记录这次拦截";
            for (var offset = 0; offset < 16; offset += 4)
                if (Marshal.ReadInt32(place, offset) != 0) return "拦截后 place 没有清零(偏移 " + offset + ")";
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(source);
            Marshal.FreeHGlobal(cfg);
            Marshal.FreeHGlobal(place);
        }
    }

    private static void Clear(IntPtr place)
    {
        // 原函数在「display 尺寸为 0」时也是 memset(place, 0, sizeof(*place)) 后返回，
        // 这里与它保持同一语义：没有可见画面 ⇒ place 全 0，下游按 0 尺寸跳过绘制。
        Marshal.WriteInt32(place, 0, 0);
        Marshal.WriteInt32(place, 4, 0);
        Marshal.WriteInt32(place, 8, 0);
        Marshal.WriteInt32(place, 12, 0);
    }

    // ---------------------------------------------------------------- P/Invoke

    private const uint PageReadWrite = 0x04;

    /// <summary>LOAD_WITH_ALTERED_SEARCH_PATH —— 让插件按**自己所在目录**找依赖(libvlccore)。</summary>
    private const uint LoadWithAlteredSearchPath = 0x00000008;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);
}
