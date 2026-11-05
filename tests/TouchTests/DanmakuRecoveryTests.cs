using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
internal static class DanmakuRecoveryTests
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Pump(int ms) { var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f); }
    public static void Run(Action<bool,string> check)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var apply=typeof(SettingsStore).GetMethod("ApplySanitized",Hidden)!;
        var snapshot=typeof(SettingsStore).GetMethod("Snapshot",Hidden)!;
        var original=(AppSettings)snapshot.Invoke(Svc.Settings,null)!;
        var legacy=new AppSettings{DanmakuEnabled=true,DanmakuFilterScroll=false,DanmakuFilterFixed=false,DanmakuFilterColorful=false,DanmakuFilterAdvanced=false};
        var filter=typeof(PlayerWindow).GetMethod("ApplyDanmakuFilter",BindingFlags.Static|BindingFlags.NonPublic)!;
        var raw=new[]{new RawDanmaku(0,"滚动恢复验证",1),new RawDanmaku(1,"固定恢复验证",5),new RawDanmaku(2,"彩色恢复验证",1,0xff1122),new RawDanmaku(3,"高级恢复验证",7)};
        PlayerWindow? win=null;
        try {
            apply.Invoke(Svc.Settings,new object[]{legacy});
            check(Svc.Settings.HasVisibleDanmakuTypes,"legacy enabled/all-hidden settings recover visible types");
            check(((IEnumerable<RawDanmaku>)filter.Invoke(null,new object[]{raw})!).Count()==4,"recovered saved settings retain real parsed danmaku through the production filter");
            var migrated=(AppSettings)snapshot.Invoke(Svc.Settings,null)!;
            check(migrated.DanmakuTypeSelectionVersion==1,"settings snapshot marks type-selection migration completed");
            legacy.DanmakuTypeSelectionVersion=1;
            apply.Invoke(Svc.Settings,new object[]{legacy});
            check(!Svc.Settings.HasVisibleDanmakuTypes,"explicit all-hidden choice in a current configuration is preserved");
            var vm=new SettingsViewModel();var notified=new HashSet<string>();vm.PropertyChanged+=(_,e)=>notified.Add(e.PropertyName!);
            check(vm.DanmakuTypeFilterHint.Contains("不会显示任何弹幕"),"all-hidden UI states clearly that no danmaku will be displayed");
            win=new PlayerWindow{Width=1280,Height=800};win.Show();Pump(100);
            var toggle=(Button)win.FindName("BtnDanmaku");
            check(System.Windows.Automation.AutomationProperties.GetName(toggle)=="恢复弹幕类型","player exposes blocked type state instead of falsely claiming danmaku is visible");
            vm.RestoreDanmakuTypesCommand.Execute(null);
            check(Svc.Settings.DanmakuEnabled && vm.DanmakuFilterScroll && vm.DanmakuFilterFixed && vm.DanmakuFilterColorful && vm.DanmakuFilterAdvanced,"restore command enables all types and the master switch");
            check(new[]{"DanmakuFilterScroll","DanmakuFilterFixed","DanmakuFilterColorful","DanmakuFilterAdvanced","DanmakuTypeFilterHint"}.All(notified.Contains),"restore refreshes every type chip and its hint");
            check(System.Windows.Automation.AutomationProperties.GetName(toggle)=="关闭弹幕","open player immediately reflects restored settings");
            var accepted=((IEnumerable<RawDanmaku>)filter.Invoke(null,new object[]{raw})!).First();
            typeof(PlayerWindow).GetMethod("CalibrateDanmakuClock",Hidden)!.Invoke(win,new object[]{0d});
            typeof(PlayerWindow).GetMethod("SetDanmakuClockRunning",Hidden)!.Invoke(win,new object[]{true});
            typeof(PlayerWindow).GetMethod("AddDanmaku",Hidden)!.Invoke(win,new object[]{accepted.Text,accepted.Mode,accepted.Time,accepted.Color});Pump(50);
            var canvas=(Canvas)win.FindName("DanmakuCanvas");check(canvas.Children.Count>0,"settings-to-filter-to-overlay path creates visible danmaku");
            var transform=(TranslateTransform)((FrameworkElement)canvas.Children[0]).RenderTransform;var x=transform.X;Pump(160);
            check(transform.X<x-1,"recovered danmaku moves with the animation clock");
            using var icon=AppBranding.CreateTrayIcon();check(icon.Width>0 && icon.Height>0,"tray icon loads from the shipped branding assets");
            var disabled=new SettingsStore();legacy.DanmakuTypeSelectionVersion=0;legacy.DanmakuEnabled=false;apply.Invoke(disabled,new object[]{legacy});
            check(!disabled.DanmakuEnabled && !disabled.HasVisibleDanmakuTypes,"migration respects an explicitly disabled master switch");
        } finally { win?.Close();Pump(300);apply.Invoke(Svc.Settings,new object[]{original});Svc.Settings.SaveNow(); }
    }
}
