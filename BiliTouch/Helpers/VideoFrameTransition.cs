using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
namespace BiliTouch.Helpers;
/// <summary>One decoded-frame capture, pure WPF spring motion, then one native surface commit.</summary>
internal sealed class VideoFrameTransition : IDisposable
{
 private Window? _window;private bool _disposed;
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
  move.Matrix=new Matrix(1,0,0,1,from.X,from.Y);_window.Show();hideNative();
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
   await done.Task;if(_disposed)return;commitNative();
   await Task.Delay(180);if(_disposed || _window==null)return;
   var fade=new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(100)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
   var faded=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);fade.Completed+=(_,_)=>faded.TrySetResult();_window.BeginAnimation(UIElement.OpacityProperty,fade);
   await Task.WhenAny(faded.Task,Task.Delay(160));
  }finally{CompositionTarget.Rendering-=render;Dispose();}
 }
 public void Dispose(){if(_disposed)return;_disposed=true;try{_window?.Close();}catch{} _window=null;}
}
