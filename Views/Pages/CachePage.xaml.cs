using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Services;
using BiliDesk.ViewModels;
namespace BiliDesk.Views.Pages;
public partial class CachePage : UserControl
{
    private CacheViewModel? Vm=>DataContext as CacheViewModel;
    public CachePage()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>{if(Vm!=null){Vm.Attach();await Vm.LoadAsync();}};
        Unloaded+=(_,_)=>Vm?.Detach();
        ModuleGestures.Install(this,()=>Vm?.LoadAsync()??Task.CompletedTask,item=>Vm?.BeginEdit(item),item=>{if(item is OfflineEntry entry)Vm?.Activate(entry);});
    }
    private void OnSelectionClick(object sender,RoutedEventArgs e){Vm?.NotifySelection();e.Handled=true;}
    private void OnRowClick(object sender,MouseButtonEventArgs e)
    {
        if(e.Handled || e.ChangedButton!=MouseButton.Left || VisualTreeUtil.FindAncestor<Button>(e.OriginalSource as DependencyObject)!=null || VisualTreeUtil.FindAncestor<CheckBox>(e.OriginalSource as DependencyObject)!=null)return;
        if(sender is FrameworkElement {DataContext:OfflineEntry entry}){Vm?.Activate(entry);e.Handled=true;}
    }
}
