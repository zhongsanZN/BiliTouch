using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using BiliTouch.Helpers;
using BiliTouch.Services;

namespace BiliTouch.ViewModels;

public sealed class CacheGroupItem : ObservableObject
{
    private readonly Action _onSelectionChanged;
    public string GroupKey { get; }
    public string Title { get; }
    public string Author { get; }
    public string Cover { get; }
    public string QualityText { get; }
    public ObservableCollection<OfflineEntry> Episodes { get; }
    public OfflineEntry PrimaryEntry => Episodes[0];
    public int EpisodeCount => Episodes.Count;
    public bool IsMultiEpisode => Episodes.Count > 1;
    public long TotalBytes => Episodes.Sum(e => Math.Max(e.Done, e.Total));
    public string TotalSizeText => ByteSize.Format(TotalBytes);
    public string BadgeText => IsMultiEpisode ? $"合集 · {EpisodeCount} 集" : PrimaryEntry.DurationText;
    public bool HasBadge => BadgeText.Length > 0;
    public string PreviewText => IsMultiEpisode
        ? $"共 {EpisodeCount} 集 · {TotalSizeText} · {PrimaryEntry.DisplayPartTitle}"
        : (PrimaryEntry.PartTitle.Length > 0 && !string.Equals(PrimaryEntry.PartTitle, PrimaryEntry.Title, StringComparison.Ordinal)
            ? $"{PrimaryEntry.PartTitle} · {PrimaryEntry.StatusText}"
            : PrimaryEntry.StatusText);
    public string DetailSummary => $"{Author} · 共 {EpisodeCount} 集 · {TotalSizeText}";
    public string ActionText => IsMultiEpisode ? "查看分集" : PrimaryEntry.ActionText;

    public bool IsSelected
    {
        get => Episodes.Count > 0 && Episodes.All(e => e.IsSelected);
        set
        {
            foreach (var entry in Episodes) entry.IsSelected = value;
            OnPropertyChanged();
            _onSelectionChanged();
        }
    }

    public CacheGroupItem(string groupKey, List<OfflineEntry> episodes, Action onSelectionChanged)
    {
        _onSelectionChanged = onSelectionChanged;
        GroupKey = groupKey;
        Episodes = new ObservableCollection<OfflineEntry>(episodes);
        var first = episodes[0];
        Title = first.GroupTitle.Length > 0 ? first.GroupTitle : first.Title;
        Author = first.Author;
        Cover = episodes.Select(e => e.GroupCover).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? first.Cover;
        QualityText = first.QualityText;
    }

    internal void NotifySelection() => OnPropertyChanged(nameof(IsSelected));
}

public sealed class CacheViewModel : ObservableObject
{
    private readonly OfflineDownloadService Service;
    public ObservableCollection<OfflineEntry> Items=>Service.Items;
    public ICollectionView VisibleItems {get;}
    public ObservableCollection<CacheGroupItem> VisibleGroups {get;}=new();
    private CacheGroupItem? _selectedGroup;
    private bool _completed=true,_loading,_editing;
    private string _error="";
    public bool IsCompletedTab {get=>_completed;set{if(SetProperty(ref _completed,value)){SelectedGroup=null;ExitEdit();OnPropertyChanged(nameof(IsDownloadingTab));RefreshView();}}}
    public bool IsDownloadingTab {get=>!_completed;set{if(value)IsCompletedTab=false;}}
    public CacheGroupItem? SelectedGroup
    {
        get=>_selectedGroup;
        set
        {
            if(SetProperty(ref _selectedGroup,value))
            {
                OnPropertyChanged(nameof(IsDetailView));
                OnPropertyChanged(nameof(IsOverview));
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }
    public bool IsDetailView=>_selectedGroup!=null;
    public bool IsOverview=>_selectedGroup==null;
    public bool Loading {get=>_loading;private set=>SetProperty(ref _loading,value);}
    public string Error {get=>_error;private set=>SetProperty(ref _error,value);}
    public bool IsEditMode {get=>_editing;private set=>SetProperty(ref _editing,value);}
    public string CacheDirectory=>AppPaths.DownloadDir;
    public string Summary=>$"已缓存 {Items.Count(e=>e.IsComplete)} 集 · 进行中 {Items.Count(e=>!e.IsComplete)} 集";
    public bool ShowEmpty=>!VisibleGroups.Any() && Error.Length==0;
    public string EmptyText=>IsCompletedTab?"还没有已完成的缓存":"暂时没有缓存任务";
    public string SelectedText=>$"已选 {Items.Count(e=>e.IsSelected)} 项";
    public ICommand ActionCommand {get;}
    public ICommand OpenGroupCommand {get;}
    public ICommand BackFromGroupCommand {get;}
    public ICommand ToggleEditCommand {get;}
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
        VisibleItems.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OfflineEntry.GroupKey)));
        ImportCommand=new RelayCommand(()=>
        {
            var dialog=new Microsoft.Win32.OpenFileDialog {Title="导入已有视频（复制到缓存，原文件保留）",Multiselect=true,Filter="视频文件|*.mp4;*.mkv;*.flv;*.avi;*.mov;*.webm;*.ts;*.m4v;*.wmv"};
            if(dialog.ShowDialog()!=true)return;
            try{Service.Import(dialog.FileNames);IsCompletedTab=false;Svc.Toast.Show("已加入导入任务，原文件将保留");}
            catch(Exception ex){Svc.Toast.Show("导入失败："+ex.Message);}
        });
        ActionCommand=new RelayCommand(p=>{if(p!=null)Activate(p);});
        OpenGroupCommand=new RelayCommand(p=>{if(p is CacheGroupItem g)SelectedGroup=g;});
        BackFromGroupCommand=new RelayCommand(()=>SelectedGroup=null);
        ToggleEditCommand=new RelayCommand(()=>{if(IsEditMode)ExitEdit();else IsEditMode=true;});
        OpenFolderCommand=new RelayCommand(()=>{try{Directory.CreateDirectory(CacheDirectory);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CacheDirectory){UseShellExecute=true});}catch(Exception ex){Svc.Toast.Show(ex.Message);}});
        DeleteSelectedCommand=new AsyncRelayCommand(DeleteSelectedAsync);
        ExitEditCommand=new RelayCommand(ExitEdit);
        SelectAllCommand=new RelayCommand(()=>
        {
            var visible=(SelectedGroup?.Episodes ?? (IEnumerable<OfflineEntry>)VisibleItems.Cast<OfflineEntry>()).ToList();
            var select=visible.Any(e=>!e.IsSelected);
            foreach(var e in visible)e.IsSelected=select;
            NotifySelection();
        });
    }
    public void Attach(){Service.Changed-=RefreshView;Service.Changed+=RefreshView;}
    public void Detach()=>Service.Changed-=RefreshView;
    public async Task LoadAsync()
    {
        if(Loading)return;Loading=true;Error="";
        try{Service.Load();RefreshView();await Service.RefreshExternalCachesAsync();RefreshView();}
        catch(Exception ex){Error=ex.Message;}
        finally{Loading=false;OnPropertyChanged(nameof(ShowEmpty));}
    }
    private void RefreshView()
    {
        VisibleItems.Refresh();
        var prevKey=_selectedGroup?.GroupKey;
        var groups=Items
            .Where(e=>e.IsComplete==IsCompletedTab)
            .GroupBy(e=>e.GroupKey,StringComparer.OrdinalIgnoreCase)
            .Select(g=>new
            {
                Key=g.Key,
                Latest=g.Max(x=>x.AddedAt),
                Episodes=g.OrderBy(x=>x.Page).ThenBy(x=>x.AddedAt).ToList()
            })
            .OrderByDescending(x=>x.Latest)
            .Select(x=>new CacheGroupItem(x.Key,x.Episodes,NotifySelection))
            .ToList();
        VisibleGroups.Clear();
        foreach(var g in groups)VisibleGroups.Add(g);
        if(!string.IsNullOrEmpty(prevKey))
            SelectedGroup=VisibleGroups.FirstOrDefault(g=>string.Equals(g.GroupKey,prevKey,StringComparison.OrdinalIgnoreCase) && g.IsMultiEpisode);
        foreach(var name in new[]{nameof(Summary),nameof(ShowEmpty),nameof(EmptyText),nameof(CacheDirectory),nameof(SelectedText)})OnPropertyChanged(name);
    }
    internal void NotifySelection()
    {
        foreach(var g in VisibleGroups)g.NotifySelection();
        OnPropertyChanged(nameof(SelectedText));
    }
    public void BeginEdit(object item)
    {
        if(item is OfflineEntry e){IsEditMode=true;e.IsSelected=true;NotifySelection();}
        else if(item is CacheGroupItem g){IsEditMode=true;g.IsSelected=true;NotifySelection();}
    }
    public void Activate(object item)
    {
        if(item is CacheGroupItem group)
        {
            if(IsEditMode){group.IsSelected=!group.IsSelected;return;}
            if(group.IsMultiEpisode){SelectedGroup=group;return;}
            Act(group.PrimaryEntry);
            return;
        }
        if(item is OfflineEntry e)
        {
            if(IsEditMode){e.IsSelected=!e.IsSelected;NotifySelection();return;}
            Act(e);
        }
    }
    public void Activate(OfflineEntry e) => Activate((object)e);
    private void Act(OfflineEntry e)
    {
        if(e.IsComplete)
        {
            if(e.HasPlayableMedia)Svc.Player.PlayOfflineEntry(e);
            else{Svc.Toast.Show("缓存文件已移动或删除");e.State=OfflineState.Failed;e.Error="文件已移动或删除";e.Notify();RefreshView();}
        }
        else if(e.State is OfflineState.Downloading or OfflineState.Queued)Service.Pause(e);
        else Service.Resume(e);
    }
    private void ExitEdit(){IsEditMode=false;foreach(var e in Items)e.IsSelected=false;NotifySelection();}
    private async Task DeleteSelectedAsync()
    {
        var selected=Items.Where(e=>e.IsSelected).ToList();if(selected.Count==0)return;
        var native=selected.Where(e=>!e.IsExternalClientCache).ToList();
        var externalCount=selected.Count-native.Count;
        if(native.Count==0)
        {
            Svc.Toast.Show("哔哩哔哩客户端离线缓存请在官方客户端内管理（原文件已保留）");
            ExitEdit();
            return;
        }
        if(MessageBox.Show($"删除选中的 {native.Count} 项缓存及本地视频？","删除缓存",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        try
        {
            foreach(var e in native)await Service.DeleteAsync(e);
            ExitEdit();
            if(externalCount>0)Svc.Toast.Show($"已删除 {native.Count} 项；另有 {externalCount} 项官方客户端缓存未删除原文件");
        }
        catch(Exception ex){Svc.Toast.Show("删除失败："+ex.Message);}
        RefreshView();
    }
}
