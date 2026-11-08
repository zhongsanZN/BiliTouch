using System.Windows;
using System.Windows.Input;
using System.Text.Json;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace BiliTouch.Helpers;

/// <summary>App-local pointer assets; text editing, resizing and hidden video cursors keep their purpose.</summary>
public static class AppCursor
{
    public const int Size=30;
    private static bool _installed;
    private static Cursor? _normal,_pressed,_phantom;
    private static Cursor Phantom=>_phantom??=Load("PBZCursor.cur");
    public static Cursor Normal=>SkinService.Instance.IsPhantomBlade?Phantom:_normal??=Load("Cursor_up.cur");
    public static Cursor Pressed=>SkinService.Instance.IsPhantomBlade?Phantom:_pressed??=Load("Cursor_down.cur");
    private static readonly Dictionary<string,string> WebPointers = new();
    private static Cursor Load(string name)
    {
        using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/BiliTouch;component/Assets/Cursors/"+name))!.Stream;
        var path=Path.Combine(Path.GetTempPath(),"BiliTouch-cursor-"+Guid.NewGuid().ToString("N")+".cur");
        try
        {
            using(var file=File.Create(path))stream.CopyTo(file);
            var handle=LoadImage(IntPtr.Zero,path,2,Size,Size,0x10);
            if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
            return CursorInteropHelper.Create(handle);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or Win32Exception){return Cursors.Arrow;}
        finally{try{File.Delete(path);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}}
    }
    public static string WebScript
    {
        get
        {
            string Pointer(string name)
            {
                if(WebPointers.TryGetValue(name,out var cached))return cached;
                using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/BiliTouch;component/Assets/Cursors/"+name))!.Stream;
                using var bytes=new System.IO.MemoryStream();stream.CopyTo(bytes);
                // CUR contains the untouched source PNG after its 22-byte directory/header.
                var data=bytes.ToArray();var width=data[6];var height=data[7];
                var png="data:image/png;base64,"+Convert.ToBase64String(data[22..]);
                var svg=$"<svg xmlns='http://www.w3.org/2000/svg' width='{Size}' height='{Size}' viewBox='0 0 {width} {height}'><image width='{width}' height='{height}' href='{png}'/></svg>";
                var x=(int)Math.Round(BitConverter.ToUInt16(data,10)*(double)Size/width);
                var y=(int)Math.Round(BitConverter.ToUInt16(data,12)*(double)Size/height);
                return WebPointers[name]="url(data:image/svg+xml,"+Uri.EscapeDataString(svg)+$") {x} {y}, auto";
            }
            var phantom=SkinService.Instance.IsPhantomBlade;
            var normal=JsonSerializer.Serialize(Pointer(phantom?"PBZCursor.cur":"Cursor_up.cur"));var pressed=JsonSerializer.Serialize(Pointer(phantom?"PBZCursor.cur":"Cursor_down.cur"));
            return "(()=>{let normal="+normal+",pressed="+pressed+";if(window.__biliTouchSetCursor){window.__biliTouchSetCursor(normal,pressed);return;}window.__biliTouchCursor=true;"+"""
                function init(){
                  const root=document.documentElement;if(!root)return;
                  let down=false;
                  window.__biliTouchSetCursor=(n,p)=>{normal=n;pressed=p;root.style.setProperty('--bilitouch-pointer',down?pressed:normal);};
                  const style=document.createElement('style');style.textContent='[data-bilitouch-pointer]{cursor:var(--bilitouch-pointer)!important}';root.appendChild(style);
                  root.style.setProperty('--bilitouch-pointer',normal);root.setAttribute('data-bilitouch-pointer','');
                  document.addEventListener('pointerover',e=>{
                    if(!(e.target instanceof Element))return;
                    const cursor=getComputedStyle(e.target).cursor;
                    if(/^(auto|default|pointer)$/.test(cursor))e.target.setAttribute('data-bilitouch-pointer','');
                  },true);
                  document.addEventListener('pointerdown',()=>{down=true;root.style.setProperty('--bilitouch-pointer',pressed);},true);
                  const reset=()=>{down=false;root.style.setProperty('--bilitouch-pointer',normal);};
                  document.addEventListener('pointerup',reset,true);document.addEventListener('pointercancel',reset,true);window.addEventListener('blur',reset);
                }
                if(document.documentElement)init();else document.addEventListener('DOMContentLoaded',init,{once:true});
                })();
                """;
        }
    }
    [DllImport("user32.dll",EntryPoint="LoadImageW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern CursorHandle LoadImage(IntPtr instance,string name,uint type,int width,int height,uint flags);
    [DllImport("user32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(IntPtr cursor);
    private sealed class CursorHandle:SafeHandleZeroOrMinusOneIsInvalid
    {
        public CursorHandle():base(true){}
        protected override bool ReleaseHandle()=>DestroyCursor(handle);
    }
    public static void Install()
    {
        if(_installed)return;_installed=true;
        SkinService.Instance.Changed+=Mouse.UpdateCursor;
        EventManager.RegisterClassHandler(typeof(UIElement),Mouse.QueryCursorEvent,new QueryCursorEventHandler(OnQueryCursor),true);
        InputManager.Current.PostProcessInput+=(_,e)=>
        {if(e.StagingItem.Input is MouseButtonEventArgs button && button.StylusDevice==null)Mouse.UpdateCursor();};
    }
    private static void OnQueryCursor(object sender,QueryCursorEventArgs e)
    {
        var cursor=e.Cursor;
        if(cursor!=null && cursor!=Cursors.Arrow && cursor!=Cursors.Hand && cursor!=_normal && cursor!=_pressed && cursor!=_phantom)return;
        e.Cursor=Mouse.LeftButton==MouseButtonState.Pressed || Mouse.RightButton==MouseButtonState.Pressed?Pressed:Normal;
        e.Handled=true;
    }
}
