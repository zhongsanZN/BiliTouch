using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch.Helpers;

internal static class VmemBufferTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(VmemVideoView view, string name, params object[] args) =>
        typeof(VmemVideoView).GetMethod(name, Hidden)!.Invoke(view, args);

    internal static void Run(Action<bool, string> check)
    {
        var settings = new BiliTouch.Services.SettingsStore();
        settings.SetVmemRenderer(false);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<BiliTouch.Models.AppSettings>("{\"UseVmemRenderer\":false}")!;
        typeof(BiliTouch.Services.SettingsStore).GetMethod("ApplySanitized", Hidden)!.Invoke(settings, new object[] { legacy });
        check(settings.UseVmemRenderer, "legacy profiles with the old disabled flag migrate to composited video");
        var view = new VmemVideoView();
        var window = new Window { Width = 400, Height = 300, Content = view, ShowActivated = false };
        var chroma = Marshal.AllocHGlobal(4);
        var planes = Marshal.AllocHGlobal(IntPtr.Size);
        IntPtr format = IntPtr.Zero;
        try
        {
            window.Show(); Pump();
            var args = new object[] { IntPtr.Zero, chroma, 8u, 4u, 0u, 0u };
            check((uint)Call(view, "OnFormat", args)! == 1, "frame format allocates aligned native buffers");
            format = (IntPtr)args[0];
            var pitch = (int)(uint)args[4];
            void Deliver(byte value)
            {
                var pointer = (IntPtr)Call(view, "OnLock", format, planes)!;
                Marshal.Copy(Enumerable.Repeat(value, pitch * 4).ToArray(), 0, pointer, pitch * 4);
                Call(view, "OnDisplay", format, pointer);
            }
            byte Pixel()
            {
                var image = (BitmapSource)view.Source;
                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
                image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                check(pixels.All(p => p == pixels[0]), "a published frame has no partially overwritten pixels");
                return pixels[0];
            }

            Deliver(30); Deliver(90); Deliver(170); Pump();
            check(Pixel() == 170 && view.CoalescedFrames >= 2,
                "a busy UI uploads the newest frame after multiple buffer handoffs");
            Deliver(220); Pump();
            check(Pixel() == 220, "the recycled decode buffer produces the next complete frame");
            var before = view.PresentedFrames;
            view.Visibility = Visibility.Hidden;
            Deliver(60); Deliver(120); Pump();
            check(view.PresentedFrames == before, "hidden video skips full-frame WPF uploads");
            view.Visibility = Visibility.Visible; Pump();
            check(Pixel() == 120, "revealing paused video uploads the last pending frame without another decode");

            Deliver(45);
            Call(view, "OnCleanup", format); format = IntPtr.Zero;
            Pump();
            check(Pixel() == 120, "queued uploads ignore buffers retired by format cleanup");
            view.Dispose(); Pump();
            check(view.Source == null, "disposal clears the bitmap and pending presentation work");
        }
        finally
        {
            view.Dispose();
            if (format != IntPtr.Zero) Call(view, "OnCleanup", format);
            window.Close(); Marshal.FreeHGlobal(chroma); Marshal.FreeHGlobal(planes);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
