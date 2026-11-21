using System.Globalization;
using System.Runtime.InteropServices;
using Vortice.Direct3D9;
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
 public static IReadOnlyList<GraphicsAdapter> UiChoices{get;}=BuildUiChoices();
 private static readonly Lazy<GraphicsAdapter?> LazyActiveUiAdapter = new(DetectActiveUiAdapter);
 public static GraphicsAdapter? ActiveUiAdapter => LazyActiveUiAdapter.Value;
 public static string ActiveUiStatusText
 {
  get
  {
   var active = ActiveUiAdapter;
   if (active == null) return "当前界面渲染：系统默认显卡";
   if (All.Count > 1)
   {
    var role = IsIntegratedOrPowerSaving(active) ? "核显 / 显示输出直连" : "独立显卡";
    return $"当前界面渲染：{active.Name}（{role}）";
   }
   return $"当前界面渲染：{active.Name}";
  }
 }
 public static bool IsIntegratedOrPowerSaving(GraphicsAdapter adapter)
 {
  if (All.Count <= 1) return true;
  var maxDedicated = All.Max(a => a.DedicatedBytes);
  var minDedicated = All.Min(a => a.DedicatedBytes);
  if (maxDedicated > minDedicated)
   return adapter.DedicatedBytes < maxDedicated;
  return adapter.VendorId == 0x8086;
 }
 private static IReadOnlyList<GraphicsAdapter> BuildUiChoices()
 {
  var list = new List<GraphicsAdapter>
  {
   new("auto", "自动（核显 / 显示输出直连，推荐）", 0, 0)
  };
  var uniqueByName = All.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
  foreach (var a in uniqueByName)
  {
   var label = uniqueByName.Length > 1
    ? $"{a.Name}（{(IsIntegratedOrPowerSaving(a) ? "核显 / 节能" : "独显 / 高性能")}）"
    : a.Name;
   list.Add(new GraphicsAdapter(a.Id, label, a.VendorId, a.DedicatedBytes));
  }
  return list;
 }
 private static GraphicsAdapter? DetectActiveUiAdapter()
 {
  try
  {
   using var d3d9 = D3D9.Direct3DCreate9Ex();
   var luid = d3d9.GetAdapterLuid(0);
   var id = $"{(uint)(luid >> 32):X8}:{(uint)luid:X8}";
   return All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All.FirstOrDefault();
  }
  catch
  {
   return All.FirstOrDefault();
  }
 }
 internal static string NormalizeUiAdapter(string? id)
  => string.IsNullOrWhiteSpace(id) || string.Equals(id, "auto", StringComparison.OrdinalIgnoreCase)
   ? "auto"
   : id.Length == 17 && id[8] == ':' &&
     uint.TryParse(id.AsSpan(0, 8), NumberStyles.HexNumber, null, out _) &&
     uint.TryParse(id.AsSpan(9, 8), NumberStyles.HexNumber, null, out _)
    ? id.ToUpperInvariant()
    : "auto";
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
 // Windows per-application UserGpuPreferences controls which DXGI/D3D9Ex adapter WPF UI
 // (wpfgfx_cor3.dll) and D3DImage composition bind to as Adapter 0.
 public static string ApplyApplicationPreference(string id)
 {
  id = NormalizeUiAdapter(id);
  if(Environment.ProcessPath is not{} exe)return "无法定位应用路径";
  try{
   using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
   int? preference = null;
   if(id!="auto"){
    var adapter=All.FirstOrDefault(a=>string.Equals(a.Id,id,StringComparison.OrdinalIgnoreCase));
    if(adapter==null)return "所选显卡已断开";
    preference=IsIntegratedOrPowerSaving(adapter)?1:2;
   }
   var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exe };
   foreach (var valueName in key.GetValueNames())
   {
    if (valueName.EndsWith(@"\BiliTouch.exe", StringComparison.OrdinalIgnoreCase))
     targets.Add(valueName);
   }
   var defaultInstalledExe = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BiliTouch", "BiliTouch.exe");
   if (System.IO.File.Exists(defaultInstalledExe))
    targets.Add(defaultInstalledExe);
   foreach (var target in targets)
   {
    var old=(key.GetValue(target) as string??"").Split(';',StringSplitOptions.RemoveEmptyEntries)
     .Where(x=>!x.StartsWith("GpuPreference=",StringComparison.OrdinalIgnoreCase)).ToList();
    if(preference.HasValue)
     old.Add("GpuPreference="+preference.Value);
    if(old.Count==0)key.DeleteValue(target,false);
    else key.SetValue(target,string.Join(';',old)+";");
   }
   return id=="auto"
    ? "已设为自动（直连显示输出 / 核显优先），重启应用后生效。"
    : "界面渲染显卡偏好已保存，重启应用后生效。";
  }catch{return "显卡偏好未能写入，请在 Windows 设置 → 显示 → 图形中选择 BiliTouch 显卡。";}
 }
}
