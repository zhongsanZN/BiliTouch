using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
namespace BiliTouch.Helpers;
/// <summary>One decoded-frame capture, pure WPF spring motion, then one native surface commit.</summary>
internal sealed class VideoFrameTransition : IDisposable
{
 private Window? _window;private bool _disposed;
 private readonly CancellationTokenSource _lifetime = new();
 [DllImport("dwmapi.dll")] private static extern int DwmFlush();
 [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

 // Rendering fires before WPF submits that frame. Crossing two distinct rendering
 // timestamps, then flushing DWM, keeps the old surface until its replacement exists.
 internal static async Task WaitForPresentationAsync(CancellationToken cancellation = default)
 {
  var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  TimeSpan? first = null;
  EventHandler render = (_, e) => {
   var stamp = ((RenderingEventArgs)e).RenderingTime;
   if (first == null) first = stamp;
   else if (stamp != first) done.TrySetResult();
  };
  CompositionTarget.Rendering += render;
  try { await done.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellation); DwmFlush(); }
  finally { CompositionTarget.Rendering -= render; }
 }
 internal static double Spring(double t)=>t>=1?1:1-Math.Exp(-7*t)*(Math.Cos(11*t)+.42*Math.Sin(11*t));
 internal static async Task<BitmapSource?> CaptureAsync(MediaPlayer player)
 {
  var dir=Path.Combine(BiliTouch.Services.AppPaths.CacheRoot,"transitions");Directory.CreateDirectory(dir);var path=Path.Combine(dir,Guid.NewGuid()+".png");
  try{
   if(!player.TakeSnapshot(0,path,1280,0))return null;
   var deadline=Stopwatch.StartNew();while(deadline.ElapsedMilliseconds<180){
    if(File.Exists(path)){try{var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(path);bitmap.EndInit();bitmap.Freeze();return bitmap;}catch(IOException){}catch(NotSupportedException){}}
    await Task.Delay(12);
   }
  }catch{}finally{try{File.Delete(path);}catch{}}
  return null;
 }
 internal async Task RunAsync(Window owner,FrameworkElement stage,Rect from,Rect to,BitmapSource frame,Action hideNative,Action commitNative)
 {
  if(_disposed)return;var dpi=VisualTreeHelper.GetDpi(stage);var screen=stage.PointToScreen(new Point());
  var canvas=new Canvas{ClipToBounds=true};var move=new MatrixTransform();
  var picture=new Image{Source=frame,Width=from.Width,Height=from.Height,Stretch=Stretch.Uniform,RenderTransform=move};RenderOptions.SetBitmapScalingMode(picture,BitmapScalingMode.HighQuality);
  var backing=new Border{Width=from.Width,Height=from.Height,Background=Brushes.Black,Child=picture,RenderTransform=move};picture.RenderTransform=Transform.Identity;
  canvas.Children.Add(backing);
  _window=new Window{Owner=owner,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,Left=screen.X/dpi.DpiScaleX,Top=screen.Y/dpi.DpiScaleY,Width=Math.Max(1,stage.ActualWidth),Height=Math.Max(1,stage.ActualHeight),Content=canvas,Focusable=false,IsHitTestVisible=false};
  move.Matrix=new Matrix(1,0,0,1,from.X,from.Y);_window.Show();
  try { await WaitForPresentationAsync(_lifetime.Token); }
  catch { Dispose(); return; } // No presented cover: leave the native video visible.
  if(_disposed)return;
  hideNative();
  var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);long start=Stopwatch.GetTimestamp();
  EventHandler? render=null;render=(_,_)=>{
   if(_disposed){CompositionTarget.Rendering-=render;done.TrySetResult();return;}
   var t=Math.Clamp(Stopwatch.GetElapsedTime(start).TotalMilliseconds/380d,0,1);var p=Spring(t);
   var rect=new Rect(from.X+(to.X-from.X)*p,from.Y+(to.Y-from.Y)*p,Math.Max(1,from.Width+(to.Width-from.Width)*p),Math.Max(1,from.Height+(to.Height-from.Height)*p));
   move.Matrix=new Matrix(rect.Width/from.Width,0,0,rect.Height/from.Height,rect.X,rect.Y);
   if(t>=1){CompositionTarget.Rendering-=render;done.TrySetResult();}
  };
  CompositionTarget.Rendering+=render;
  try{
   await done.Task.WaitAsync(_lifetime.Token);if(_disposed)return;commitNative();
   // Showing VideoView can raise its separate overlay HWND. Keep the opaque frame
   // above it while the resized native surface resumes presentation.
   if(_window!=null)SetWindowPos(new WindowInteropHelper(_window).Handle,IntPtr.Zero,0,0,0,0,0x0013);
   await Task.Delay(180,_lifetime.Token);
   await WaitForPresentationAsync(_lifetime.Token);
   // Reveal atomically. Fading the whole layered window exposes intermediate
   // native clears and blends a stale screenshot with a different live frame.
  }catch(OperationCanceledException) when(_disposed){
  }finally{CompositionTarget.Rendering-=render;Dispose();}
 }
 public void Dispose(){if(_disposed)return;_disposed=true;_lifetime.Cancel();try{_window?.Close();}catch{} _window=null;}
}
