using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.ViewModels;
namespace BiliTouch.Views.Pages;
public partial class CachePage : UserControl
{
    private readonly List<UniformGrid> _columnGrids = new();
    private CacheViewModel? Vm=>DataContext as CacheViewModel;
    public CachePage()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>{if(Vm!=null){Vm.Attach();await Vm.LoadAsync();}};
        Unloaded+=(_,_)=>Vm?.Detach();
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape && Vm?.IsDetailView==true){Vm.SelectedGroup=null;e.Handled=true;}};
        ModuleGestures.Install(this,()=>Vm?.LoadAsync()??Task.CompletedTask,item=>Vm?.BeginEdit(item),item=>{if(item!=null)Vm?.Activate(item);});
    }
    private void OnSelectionClick(object sender,RoutedEventArgs e){Vm?.NotifySelection();e.Handled=true;}
    private void OnCardClick(object sender,MouseButtonEventArgs e)
    {
        if(e.Handled || e.ChangedButton!=MouseButton.Left || VisualTreeUtil.FindAncestor<Button>(e.OriginalSource as DependencyObject)!=null || VisualTreeUtil.FindAncestor<CheckBox>(e.OriginalSource as DependencyObject)!=null)return;
        if(sender is FrameworkElement {DataContext:object item}){Vm?.Activate(item);e.Handled=true;}
    }
    private void OnGridPanelLoaded(object sender,RoutedEventArgs e)
    {
        if(sender is UniformGrid grid && !_columnGrids.Contains(grid))_columnGrids.Add(grid);
        UpdateGridColumns();
    }
    private void OnCachePageSizeChanged(object sender,SizeChangedEventArgs e)=>UpdateGridColumns();
    private void UpdateGridColumns()
    {
        var cols=ActualWidth is >0 and <600 ? 1 : 2;
        foreach(var g in _columnGrids)g.Columns=cols;
    }
}
