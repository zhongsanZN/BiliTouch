using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using D3D9Format = Vortice.Direct3D9.Format;
using Format = Vortice.DXGI.Format;

namespace BiliTouch.Helpers;

internal sealed class GpuVideoPresenter : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    private readonly IDirect3D9Ex _d3d9;
    private readonly IDirect3DDevice9Ex _device9;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext1 _videoContext;
    private ID3D11Texture2D? _input, _output;
    private IDirect3DTexture9? _texture9;
    private IDirect3DSurface9? _surface9;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorInputView? _inputView;
    private ID3D11VideoProcessorOutputView? _outputView;
    private readonly ID3D11DeviceContext4 _fenceContext;
    private readonly ID3D11Fence _fence;
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly RegisteredWaitHandle _completionWait;
    private ulong _signal;
    public event Action? FrameReady;
    private bool _pending;
    public long Attempts { get; private set; }
    public long LockMisses { get; private set; }
    public bool Pending => _pending;
    public D3DImage Image { get; } = new();
    public int Width { get; private set; }
    public int Height { get; private set; }
    private int _inputWidth, _inputHeight;
    private bool _disposed;
    public GpuVideoPresenter()
    {
        try
        {
            _d3d9 = D3D9.Direct3DCreate9Ex();
            var hwnd = GetDesktopWindow();
            var parameters = new Vortice.Direct3D9.PresentParameters { Windowed = true, SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
                DeviceWindowHandle = hwnd, BackBufferWidth = 1, BackBufferHeight = 1, BackBufferFormat = D3D9Format.X8R8G8B8,
                PresentationInterval = PresentInterval.Immediate };
            _device9 = _d3d9.CreateDeviceEx(0, DeviceType.Hardware, hwnd,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve, parameters);
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var luid = _d3d9.GetAdapterLuid(0);
            IDXGIAdapter1? selected = null;
            for (uint index = 0; factory.EnumAdapters1(index, out var adapter).Success; index++)
            {
                if (adapter.Description1.Luid == luid) { selected = adapter; break; }
                adapter.Dispose();
            }
            using (selected)
                D3D11.D3D11CreateDevice(selected, selected == null ? DriverType.Hardware : DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device, out _context).CheckError();
            _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _context.QueryInterface<ID3D11VideoContext1>();
            _fenceContext = _context.QueryInterface<ID3D11DeviceContext4>();
            using (var device5 = _device.QueryInterface<ID3D11Device5>())
            {
                _fence = device5.CreateFence<ID3D11Fence>(0, FenceFlags.None);
            }
            _completionWait = ThreadPool.RegisterWaitForSingleObject(_frameReady, (_, _) =>
            {
                if (_disposed) return;
                try { Image.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                    new Action(() => { if (!_disposed) FrameReady?.Invoke(); })); }
                catch (InvalidOperationException) { /* Dispatcher has shut down. */ }
            }, null, Timeout.Infinite, false);
        }
        catch { Dispose(); throw; }
    }
    public void Configure(int width, int height, int outputWidth = 0, int outputHeight = 0)
    {
        outputWidth = outputWidth > 0 ? outputWidth : width;
        outputHeight = outputHeight > 0 ? outputHeight : height;
        if (_inputWidth == width && _inputHeight == height && Width == outputWidth && Height == outputHeight) return;
        if (_pending) throw new InvalidOperationException("Previous GPU frame has not completed");
        ReleaseFrame();
        _inputWidth = width; _inputHeight = height;
        Width = outputWidth; Height = outputHeight;
        IntPtr shared = IntPtr.Zero;
        _texture9 = _device9.CreateTexture((uint)outputWidth, (uint)outputHeight, 1, Vortice.Direct3D9.Usage.RenderTarget,
            D3D9Format.A8R8G8B8, Pool.Default, ref shared);
        _surface9 = _texture9.GetSurfaceLevel(0);
        _output = _device.OpenSharedResource<ID3D11Texture2D>(shared);
        _input = _device.CreateTexture2D(new Texture2DDescription {
            Width = (uint)width, Height = (uint)((height + 31) & ~31), MipLevels = 1, ArraySize = 1,
            Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.Decoder });
        var description = new VideoProcessorContentDescription {
            InputFrameFormat = VideoFrameFormat.Progressive, InputWidth = (uint)width, InputHeight = (uint)height,
            OutputWidth = (uint)outputWidth, OutputHeight = (uint)outputHeight,
            InputFrameRate = new Rational(60, 1), OutputFrameRate = new Rational(60, 1), Usage = VideoUsage.PlaybackNormal };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator<ID3D11VideoProcessorEnumerator>(description);
        _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();
        _inputView = _videoDevice.CreateVideoProcessorInputView(_input, _enumerator,
            new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_output, _enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        var rect = new Vortice.RawRect(0, 0, width, height);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, rect);
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new Vortice.RawRect(0, 0, outputWidth, outputHeight));
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetStreamColorSpace1(_processor, 0, height >= 720
            ? ColorSpaceType.YcbcrStudioG22LeftP709 : ColorSpaceType.YcbcrStudioG22LeftP601);
        _videoContext.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext.VideoProcessorSetOutputAlphaFillMode(_processor, VideoProcessorAlphaFillMode.Opaque, 0);
        Image.Lock();
        try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9.NativePointer, true); }
        finally { Image.Unlock(); }
    }
    public bool Begin(IntPtr pixels, int pitch)
    {
        Attempts++;
        if (_pending || !Image.IsFrontBufferAvailable) return false;
        if (!Image.TryLock(new Duration(TimeSpan.Zero)))
        {
            // D3DImage increments its nesting count even when TryLock times out.
            // Balance that attempt too, or every later frame remains locked out.
            Image.Unlock(); LockMisses++; return false;
        }
        try
        {
            _context.UpdateSubresource(_input!, 0, null, pixels, (uint)pitch, 0);
            var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView! };
            _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, 1, [stream]).CheckError();
            _fenceContext.Signal(_fence, ++_signal);
            _fence.SetEventOnCompletion(_signal, _frameReady.SafeWaitHandle.DangerousGetHandle());
            _context.Flush();
            _pending = true;
            return true;
        }
        catch { Image.Unlock(); throw; }
    }
    public bool Complete()
    {
        if (!_pending) return false;
        if (_fence.CompletedValue < _signal)
        {
            _device.DeviceRemovedReason.CheckError();
            return false;
        }
        try { Image.AddDirtyRect(new Int32Rect(0, 0, Width, Height)); }
        finally { _pending = false; Image.Unlock(); }
        return true;
    }
    public BitmapSource Capture()
    {
        var desc = _output!.Description;
        desc.BindFlags = BindFlags.None; desc.MiscFlags = ResourceOptionFlags.None;
        desc.Usage = ResourceUsage.Staging; desc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = _device.CreateTexture2D(desc);
        _context.CopyResource(staging, _output);
        var map = _context.Map(staging, 0, MapMode.Read);
        try
        {
            var stride = Width * 4; var pixels = new byte[stride * Height];
            for (int y = 0; y < Height; y++) Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, pixels, y * stride, stride);
            var frame = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            frame.Freeze(); return frame;
        }
        finally { _context.Unmap(staging, 0); }
    }
    private void ReleaseFrame()
    {
        Image.Lock(); try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); } finally { Image.Unlock(); }
        _outputView?.Dispose(); _inputView?.Dispose(); _processor?.Dispose(); _enumerator?.Dispose();
        _input?.Dispose(); _output?.Dispose(); _surface9?.Dispose(); _texture9?.Dispose();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pending) { _pending = false; Image.Unlock(); }
        _completionWait?.Unregister(null);
        ReleaseFrame(); _fence?.Dispose(); _fenceContext?.Dispose(); _frameReady.Dispose();
        _videoContext?.Dispose(); _videoDevice?.Dispose();
        _context?.ClearState(); _context?.Flush();
        _context?.Dispose(); _device?.Dispose(); _device9?.Dispose(); _d3d9?.Dispose();
    }
}
