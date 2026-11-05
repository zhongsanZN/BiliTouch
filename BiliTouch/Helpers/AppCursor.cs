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
    private static Cursor? _normal,_pressed;
    public static Cursor Normal=>_normal??=Load("Cursor_up.cur");
    public static Cursor Pressed=>_pressed??=Load("Cursor_down.cur");
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
                using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/BiliTouch;component/Assets/Cursors/"+name))!.Stream;
                using var bytes=new System.IO.MemoryStream();stream.CopyTo(bytes);
                // CUR contains the untouched source PNG after its 22-byte directory/header.
                var png="data:image/png;base64,"+Convert.ToBase64String(bytes.ToArray()[22..]);
                var svg=$"<svg xmlns='http://www.w3.org/2000/svg' width='{Size}' height='{Size}' viewBox='0 0 60 60'><image width='60' height='60' href='{png}'/></svg>";
                return "url(data:image/svg+xml,"+Uri.EscapeDataString(svg)+") 4 4, auto";
            }
            var normal=JsonSerializer.Serialize(Pointer("Cursor_up.cur"));var pressed=JsonSerializer.Serialize(Pointer("Cursor_down.cur"));
            return "(()=>{if(window.__biliTouchCursor)return;window.__biliTouchCursor=true;const normal="+normal+",pressed="+pressed+";"+"""
                function init(){
                  const root=document.documentElement;if(!root)return;
                  const style=document.createElement('style');style.textContent='[data-bilitouch-pointer]{cursor:var(--bilitouch-pointer)!important}';root.appendChild(style);
                  root.style.setProperty('--bilitouch-pointer',normal);root.setAttribute('data-bilitouch-pointer','');
                  document.addEventListener('pointerover',e=>{
                    if(!(e.target instanceof Element))return;
                    const cursor=getComputedStyle(e.target).cursor;
                    if(/^(auto|default|pointer)$/.test(cursor))e.target.setAttribute('data-bilitouch-pointer','');
                  },true);
                  document.addEventListener('pointerdown',()=>root.style.setProperty('--bilitouch-pointer',pressed),true);
                  const reset=()=>root.style.setProperty('--bilitouch-pointer',normal);
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
        EventManager.RegisterClassHandler(typeof(UIElement),Mouse.QueryCursorEvent,new QueryCursorEventHandler(OnQueryCursor),true);
        InputManager.Current.PostProcessInput+=(_,e)=>
        {if(e.StagingItem.Input is MouseButtonEventArgs button && button.StylusDevice==null)Mouse.UpdateCursor();};
    }
    private static void OnQueryCursor(object sender,QueryCursorEventArgs e)
    {
        var cursor=e.Cursor;
        if(cursor!=null && cursor!=Cursors.Arrow && cursor!=Cursors.Hand && cursor!=Normal && cursor!=Pressed)return;
        e.Cursor=Mouse.LeftButton==MouseButtonState.Pressed || Mouse.RightButton==MouseButtonState.Pressed?Pressed:Normal;
        e.Handled=true;
    }
}
