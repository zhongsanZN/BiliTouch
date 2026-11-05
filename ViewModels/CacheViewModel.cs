using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

public sealed class CacheViewModel : ObservableObject
{
    private readonly OfflineDownloadService Service;
    public ObservableCollection<OfflineEntry> Items=>Service.Items;
    public ICollectionView VisibleItems {get;}
    private bool _completed=true,_loading,_editing;
    private string _error="";
    public bool IsCompletedTab {get=>_completed;set{if(SetProperty(ref _completed,value)){ExitEdit();OnPropertyChanged(nameof(IsDownloadingTab));RefreshView();}}}
    public bool IsDownloadingTab {get=>!_completed;set{if(value)IsCompletedTab=false;}}
    public bool Loading {get=>_loading;private set=>SetProperty(ref _loading,value);}
    public string Error {get=>_error;private set=>SetProperty(ref _error,value);}
    public bool IsEditMode {get=>_editing;private set=>SetProperty(ref _editing,value);}
    public string CacheDirectory=>AppPaths.DownloadDir;
    public string Summary=>$"已缓存 {Items.Count(e=>e.IsComplete)} 集 · 进行中 {Items.Count(e=>!e.IsComplete)} 集";
    public bool ShowEmpty=>!VisibleItems.Cast<object>().Any() && Error.Length==0;
    public string EmptyText=>IsCompletedTab?"还没有已完成的缓存":"暂时没有缓存任务";
    public string SelectedText=>$"已选 {Items.Count(e=>e.IsSelected)} 项";
    public ICommand ActionCommand {get;}
    public ICommand ImportCommand {get;}
    public ICommand OpenFolderCommand {get;}
    public ICommand DeleteSelectedCommand {get;}
    public ICommand ExitEditCommand {get;}
    public ICommand SelectAllCommand {get;}
    public CacheViewModel() : this(OfflineDownloadService.Instance) {}
    internal CacheViewModel(OfflineDownloadService service)
    {
        Service=service;
        VisibleItems=new ListCollectionView(Items) { Filter=o=>o is OfflineEntry e && e.IsComplete==IsCompletedTab };
        VisibleItems.SortDescriptions.Add(new SortDescription(nameof(OfflineEntry.AddedAt),ListSortDirection.Descending));
        VisibleItems.SortDescriptions.Add(new SortDescription(nameof(OfflineEntry.Page),ListSortDirection.Ascending));
        VisibleItems.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OfflineEntry.Bvid)));
        ImportCommand=new RelayCommand(()=>
        {
            var dialog=new Microsoft.Win32.OpenFileDialog {Title="导入已有视频（复制到缓存，原文件保留）",Multiselect=true,Filter="视频文件|*.mp4;*.mkv;*.flv;*.avi;*.mov;*.webm;*.ts;*.m4v;*.wmv"};
            if(dialog.ShowDialog()!=true)return;
            try{Service.Import(dialog.FileNames);IsCompletedTab=false;Svc.Toast.Show("已加入导入任务，原文件将保留");}
            catch(Exception ex){Svc.Toast.Show("导入失败："+ex.Message);}
        });
        ActionCommand=new RelayCommand(p=>{if(p is OfflineEntry e)Act(e);});
        OpenFolderCommand=new RelayCommand(()=>{try{Directory.CreateDirectory(CacheDirectory);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CacheDirectory){UseShellExecute=true});}catch(Exception ex){Svc.Toast.Show(ex.Message);}});
        DeleteSelectedCommand=new AsyncRelayCommand(DeleteSelectedAsync);
        ExitEditCommand=new RelayCommand(ExitEdit);
        SelectAllCommand=new RelayCommand(()=>{var visible=VisibleItems.Cast<OfflineEntry>().ToList();var select=visible.Any(e=>!e.IsSelected);foreach(var e in visible)e.IsSelected=select;OnPropertyChanged(nameof(SelectedText));});
    }
    public void Attach(){Service.Changed-=RefreshView;Service.Changed+=RefreshView;}
    public void Detach()=>Service.Changed-=RefreshView;
    public Task LoadAsync()
    {
        if(Loading)return Task.CompletedTask;Loading=true;Error="";
        try{Service.Load();RefreshView();}
        catch(Exception ex){Error=ex.Message;}
        finally{Loading=false;OnPropertyChanged(nameof(ShowEmpty));}
        return Task.CompletedTask;
    }
    private void RefreshView()
    {
        VisibleItems.Refresh();foreach(var name in new[]{nameof(Summary),nameof(ShowEmpty),nameof(EmptyText),nameof(CacheDirectory),nameof(SelectedText)})OnPropertyChanged(name);
    }
    internal void NotifySelection()=>OnPropertyChanged(nameof(SelectedText));
    public void BeginEdit(object item){if(item is OfflineEntry e){IsEditMode=true;e.IsSelected=true;OnPropertyChanged(nameof(SelectedText));}}
    public void Activate(OfflineEntry e)
    {
        if(IsEditMode){e.IsSelected=!e.IsSelected;OnPropertyChanged(nameof(SelectedText));return;}
        Act(e);
    }
    private void Act(OfflineEntry e)
    {
        if(e.IsComplete)
        {
            if(File.Exists(e.FilePath))Svc.Player.PlayLocalFile(e.FilePath,e.Title+" · "+e.PartTitle);
            else{Svc.Toast.Show("缓存文件已移动或删除");e.State=OfflineState.Failed;e.Error="文件已移动或删除";e.Notify();RefreshView();}
        }
        else if(e.State is OfflineState.Downloading or OfflineState.Queued)Service.Pause(e);
        else Service.Resume(e);
    }
    private void ExitEdit(){IsEditMode=false;foreach(var e in Items)e.IsSelected=false;OnPropertyChanged(nameof(SelectedText));}
    private async Task DeleteSelectedAsync()
    {
        var selected=Items.Where(e=>e.IsSelected).ToList();if(selected.Count==0)return;
        if(MessageBox.Show($"删除选中的 {selected.Count} 项缓存及本地视频？","删除缓存",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        try{foreach(var e in selected)await Service.DeleteAsync(e);ExitEdit();}
        catch(Exception ex){Svc.Toast.Show("删除失败："+ex.Message);}
        RefreshView();
    }
}
