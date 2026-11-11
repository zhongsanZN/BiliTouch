using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using BiliTouch.Helpers;
using BiliTouch.ViewModels;
using LibVLCSharp.Shared;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BiliTouch.Services;

public sealed class VideoDecodeStatus : ObservableObject
{
    public static VideoDecodeStatus Instance { get; } = new();
    private string _status = "尚未播放视频";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    internal void Update(string status)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(new Action(() => Status = status));
        else Status = status;
    }
}

/// <summary>
/// Selects the adapter passed to VLC's own D3D11 device creation. Devices and their
/// references remain owned by VLC, including after repeated media replacements.
/// The shim is restricted to the bundled VLC modules' GetProcAddress imports;
/// WPF and the application's presentation device keep their normal adapter policy.
/// </summary>
public static class VideoDecode
{
    private static readonly object Gate = new();
    private static IDXGIAdapter1? _adapter;
    private static bool _initialized;
    private static bool _hardwareEnabled = true;
    private static readonly ConcurrentDictionary<LibVLC, bool> HardwareSessions = new();
    private static readonly List<IntPtr> Modules = [];
    private static readonly Lazy<IReadOnlyList<GraphicsAdapter>> AdapterChoices = new(Enumerate);
    public static IReadOnlyList<GraphicsAdapter> Choices => AdapterChoices.Value;
    public static bool HardwareEnabled => _initialized ? _hardwareEnabled : SettingsStore.Instance.DecoderAdapterId != "software";
    public static string SelectedDeviceName { get; private set; } = "自动";
    public static string? SelectionFailure { get; private set; }
    internal static long RedirectedDeviceCreations;

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true)]
    private static extern IntPtr NativeGetProcAddress(IntPtr module, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", ExactSpelling = true)]
    private static extern int NativeCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device,
        out uint featureLevel, out IntPtr immediateContext);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr GetProcDelegate(IntPtr module, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateDeviceDelegate(IntPtr adapter, uint driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device,
        out uint featureLevel, out IntPtr immediateContext);
    private static readonly GetProcDelegate GetProcShim = Resolve;
    private static readonly CreateDeviceDelegate DeviceShim = CreateDevice;
    private static readonly IntPtr ResolvePointer = Marshal.GetFunctionPointerForDelegate(GetProcShim);
    private static readonly IntPtr DevicePointer = Marshal.GetFunctionPointerForDelegate(DeviceShim);

    internal static string Normalize(string? id)
        => id is "auto" or "software" ? id : id != null && id.Length == 17 && id[8] == ':' &&
           uint.TryParse(id.AsSpan(0, 8), NumberStyles.HexNumber, null, out _) &&
           uint.TryParse(id.AsSpan(9, 8), NumberStyles.HexNumber, null, out _) ? id.ToUpperInvariant() : "auto";

    private static string Id(long luid) => $"{(uint)(luid >> 32):X8}:{(uint)luid:X8}";
    private static IReadOnlyList<GraphicsAdapter> Enumerate()
    {
        var choices = new List<GraphicsAdapter> { new("auto", "自动选择可用显卡", 0, 0) };
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index = 0; factory.EnumAdapters1(index, out var adapter).Success; index++)
            {
                using (adapter)
                {
                    var description = adapter.Description1;
                    if ((description.Flags & AdapterFlags.Software) != 0) continue;
                    try
                    {
                        using var device = CreateTestDevice(adapter);
                        using var video = device.QueryInterface<ID3D11VideoDevice>();
                        if (video.VideoDecoderProfileCount == 0) continue;
                        var name = description.Description;
                        var duplicates = choices.Count(c => c.Name.StartsWith(name, StringComparison.Ordinal));
                        if (duplicates > 0) name += $"（{duplicates + 1}）";
                        choices.Add(new(Id(description.Luid), name, description.VendorId, (ulong)description.DedicatedVideoMemory));
                    }
                    catch { /* Disconnected/unsupported adapters are not offered as available decoders. */ }
                }
            }
        }
        catch { }
        choices.Add(new("software", "软件解码（兼容模式）", 0, 0));
        return choices;
    }

    private static ID3D11Device CreateTestDevice(IDXGIAdapter adapter)
    {
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out ID3D11Device? device).CheckError();
        return device ?? throw new InvalidOperationException("无法创建显卡设备");
    }

    internal static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
            var choice = Normalize(SettingsStore.Instance.DecoderAdapterId);
            _hardwareEnabled = choice != "software";
            if (choice is "auto" or "software") return;
            try
            {
                using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint index = 0; factory.EnumAdapters1(index, out var adapter).Success; index++)
                {
                    if (Id(adapter.Description1.Luid) == choice) { _adapter = adapter; break; }
                    adapter.Dispose();
                }
                if (_adapter == null) throw new InvalidOperationException("所选显卡已断开");
                using (var test = CreateTestDevice(_adapter))
                using (var video = test.QueryInterface<ID3D11VideoDevice>())
                    if (video.VideoDecoderProfileCount == 0) throw new InvalidOperationException("所选显卡没有可用的视频解码器");
                SelectedDeviceName = _adapter.Description1.Description;
                var codec = Path.Combine(VlcCore.PluginsDirectory, "codec", "libd3d11va_plugin.dll");
                var module = LoadLibrary(codec);
                if (module == IntPtr.Zero || !VlcPlacePictureGuard.TryPatchImport(module, "kernel32.dll", "GetProcAddress", ResolvePointer, out _))
                    throw new InvalidOperationException("解码设备选择未能初始化");
                Modules.Add(module); // Keep module/delegate import targets valid until process exit.
                foreach (var path in new[] {
                    Path.Combine(VlcCore.PluginsDirectory, "video_output", "libdirect3d11_plugin.dll"),
                    Path.Combine(VlcCore.PluginsDirectory, "video_chroma", "libdirect3d11_filters_plugin.dll") })
                {
                    var extra = LoadLibrary(path);
                    if (extra != IntPtr.Zero) { Modules.Add(extra); VlcPlacePictureGuard.TryPatchImport(extra, "kernel32.dll", "GetProcAddress", ResolvePointer, out _); }
                }
            }
            catch (Exception ex)
            {
                SelectionFailure = ex.Message + "，已改用自动选择";
                _adapter?.Dispose(); _adapter = null;
                VideoDecodeStatus.Instance.Update(SelectionFailure);
            }
        }
    }

    internal static void Observe(LibVLC core)
    {
        HardwareSessions[core] = false;
        core.Log += (_, e) =>
        {
            if (e.Message.Contains("Using D3D11VA", StringComparison.OrdinalIgnoreCase))
            {
                HardwareSessions[core] = true;
                var start = e.Message.IndexOf('(');
                var end = e.Message.IndexOf(", vendor", StringComparison.Ordinal);
                var name = start >= 0 && end > start ? e.Message[(start + 1)..end] : SelectedDeviceName;
                VideoDecodeStatus.Instance.Update("硬件解码：" + (string.IsNullOrWhiteSpace(name) ? SelectedDeviceName : name));
            }
            else if (e.Message.Contains("using video decoder module", StringComparison.OrdinalIgnoreCase) &&
                     !HardwareSessions.GetValueOrDefault(core))
                VideoDecodeStatus.Instance.Update(HardwareEnabled ? "兼容解码（当前视频未使用显卡解码）" : "软件解码");
        };
    }

    internal static void BeginMedia(LibVLC core)
    {
        HardwareSessions[core] = false;
        VideoDecodeStatus.Instance.Update(HardwareEnabled ? "正在准备显卡解码…" : "软件解码");
    }

    private static IntPtr Resolve(IntPtr module, IntPtr name)
    {
        // Ordinal imports are valid GetProcAddress inputs too.
        if (_adapter != null && (ulong)name.ToInt64() > ushort.MaxValue &&
            Marshal.PtrToStringAnsi(name) == "D3D11CreateDevice") return DevicePointer;
        return NativeGetProcAddress(module, name);
    }

    private static int CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags,
        IntPtr levels, uint count, uint sdk, out IntPtr device, out uint level, out IntPtr context)
    {
        var selected = _adapter;
        if (selected != null && adapter == IntPtr.Zero && driverType == 1)
        {
            var result = NativeCreateDevice(selected.NativePointer, 0, software, flags, levels, count, sdk, out device, out level, out context);
            if (result >= 0) { Interlocked.Increment(ref RedirectedDeviceCreations); return result; }
        }
        return NativeCreateDevice(adapter, driverType, software, flags, levels, count, sdk, out device, out level, out context);
    }
}
