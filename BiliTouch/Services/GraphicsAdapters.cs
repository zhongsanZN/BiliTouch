using System.Runtime.InteropServices;
namespace BiliTouch.Services;
public sealed record GraphicsAdapter(string Id,string Name,uint VendorId,ulong DedicatedBytes)
{
 public string Vendor=>VendorId switch{0x10de=>"NVIDIA",0x8086=>"Intel",0x1002=>"AMD",0x1ed5=>"摩尔线程",_=>"其他"};
 public bool HasVendorSuperResolution=>VendorId is 0x10de or 0x8086 or 0x1ed5;
 public override string ToString()=>Name;
}
public static class GraphicsAdapters
{
 public static IReadOnlyList<GraphicsAdapter> All{get;}=Enumerate();
 public static IReadOnlyList<GraphicsAdapter> Choices{get;}=new[]{new GraphicsAdapter("auto","自动（系统默认显卡）",0,0)}.Concat(All).ToArray();
 [DllImport("dxgi.dll")]static extern int CreateDXGIFactory1(ref Guid iid,out IntPtr factory);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int EnumAdapters(IntPtr self,uint index,out IntPtr adapter);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int GetDesc(IntPtr self,out AdapterDesc desc);
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct AdapterDesc{
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Description;
  public uint VendorId,DeviceId,SubSysId,Revision;public UIntPtr DedicatedVideoMemory,DedicatedSystemMemory,SharedSystemMemory;
  public uint LuidLow;public int LuidHigh;public uint Flags;
 }
 static T Slot<T>(IntPtr obj,int index)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),index*IntPtr.Size));
 private static IReadOnlyList<GraphicsAdapter> Enumerate()
 {
  var result=new List<GraphicsAdapter>();IntPtr factory=IntPtr.Zero;
  try{var iid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");if(CreateDXGIFactory1(ref iid,out factory)<0)return result;
   for(uint i=0;i<32;i++){if(Slot<EnumAdapters>(factory,12)(factory,i,out var adapter)<0)break;
    try{if(Slot<GetDesc>(adapter,10)(adapter,out var d)>=0 && (d.Flags&2)==0)result.Add(new($"{d.LuidHigh:X8}:{d.LuidLow:X8}",d.Description.TrimEnd('\0'),d.VendorId,d.DedicatedVideoMemory.ToUInt64()));}
    finally{Marshal.Release(adapter);}
   }
  }catch{}finally{if(factory!=IntPtr.Zero)Marshal.Release(factory);}return result.GroupBy(a=>a.Id).Select(g=>g.First()).ToArray();
 }
 public static GraphicsAdapter? Selected=>All.FirstOrDefault(a=>a.Id==SettingsStore.Instance.DecoderAdapterId)
     ??All.FirstOrDefault(a=>a.Id==SettingsStore.Instance.EnhancementAdapterId)??All.FirstOrDefault();
 // VLC 3 creates the D3D11 device itself. Use the Windows per-application preference,
 // never pretend that a media option can select an arbitrary DXGI adapter.
 public static string ApplyApplicationPreference(string id)
 {
  if(Environment.ProcessPath is not{} exe)return "无法定位应用路径";
  try{
   using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
   var old=(key.GetValue(exe) as string??"").Split(';',StringSplitOptions.RemoveEmptyEntries).Where(x=>!x.StartsWith("GpuPreference=",StringComparison.OrdinalIgnoreCase)).ToList();
   if(id!="auto"){
    var adapter=All.FirstOrDefault(a=>a.Id==id);if(adapter==null)return "所选显卡已断开";
    var preference=adapter.VendorId==0x8086 || (All.Any(a=>a.DedicatedBytes>adapter.DedicatedBytes) && adapter.DedicatedBytes<512UL*1024*1024)?1:2;
    old.Add("GpuPreference="+preference);
   }
   if(old.Count==0)key.DeleteValue(exe,false);else key.SetValue(exe,string.Join(';',old)+";");
   return "显卡偏好已保存，重启应用后生效；实际处理显卡由驱动确认。";
  }catch{return "显卡偏好未能写入，请在 Windows 设置 → 显示 → 图形中选择 BiliTouch 显卡。";}
 }
}
