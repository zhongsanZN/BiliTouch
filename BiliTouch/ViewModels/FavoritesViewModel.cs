using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;

namespace BiliTouch.ViewModels;

/// <summary>Cloud favorites. Reads belong to one view; writes keep their confirmed target.</summary>
public class FavoritesViewModel : VideoListSelectionViewModel
{
    private bool _loading, _loadingMore, _hasMore, _isEditMode, _busy;
    private string _error = "", _busyText = "";
    private int _pn = 1, _generation, _operationGeneration;
    private long _currentFolderId;
    private SessionAccount _account = Svc.Session.CaptureAccount();
    private readonly Func<string, bool> _confirm;

    public ObservableCollection<FavFolder> Folders { get; } = new();
    public ObservableCollection<VideoItem> Items { get; } = new();
    public ObservableCollection<CardMenuItem> CardMenuItems { get; } = new();
    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string CountText => Items.Count > 0 ? $"({Items.Count})" : "";
    public bool IsEditMode
    {
        get => _isEditMode;
        private set { if (SetProperty(ref _isEditMode, value) && !value) ClearSelection(); }
    }
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanOperate));
            ((RelayCommand)SelectFolderCommand).RaiseCanExecuteChanged();
        }
    }
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }
    public string CurrentFolderTitle { get; private set; } = "";
    public bool CanOperate => !Busy;
    public FavFolder? PendingFolder { get; set; }
    public ICommand ReloadCommand { get; }
    public ICommand SelectFolderCommand { get; }
    public ICommand ToggleEditCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand ClearFolderCommand { get; }

    public FavoritesViewModel() : this(text => MessageBox.Show(text, "BiliTouch",
        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { }

    internal FavoritesViewModel(Func<string, bool> confirm)
    {
        _confirm = confirm;
        CardMenuItems.Add(new CardMenuItem { Header = "移出收藏", Glyph = "\uE74D",
            Command = new RelayCommand(p => { if (p is VideoItem v) Remove(v); }) });
        ReloadCommand = new RelayCommand(() => _ = LoadAsync());
        SelectFolderCommand = new RelayCommand(p =>
        { if (p is FavFolder folder) _ = SelectFolderAsync(folder); }, () => !Busy);
        ToggleEditCommand = new RelayCommand(() => { if (!Busy) IsEditMode = !IsEditMode; });
        SelectAllCommand = new RelayCommand(() =>
        { if (!Busy) SetSelectionBulk(VisibleItems(), !AllSelected); });
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync);
        ClearFolderCommand = new AsyncRelayCommand(ClearFolderAsync);
        HookSelectionSource();
        WeakEventManager<SessionManager, EventArgs>.AddHandler(Svc.Session,
            nameof(SessionManager.AccountChanged), OnAccountChanged);
    }

    protected override ObservableCollection<VideoItem> SelectionSource => Items;
    protected override void OnSourceItemsChanged() => UpdateCount();
    private void UpdateCount() => OnPropertyChanged(nameof(CountText));
    private void OnAccountChanged(object? sender, EventArgs e)
    {
        _account = Svc.Session.CaptureAccount();
        ++_generation; ++_operationGeneration;
        Loading = LoadingMore = Busy = false; BusyText = "";
        IsEditMode = false;
        Items.Clear(); Folders.Clear(); PendingFolder = null;
        _currentFolderId = 0; _pn = 1; HasMore = false;
        CurrentFolderTitle = ""; OnPropertyChanged(nameof(CurrentFolderTitle));
        Error = Svc.Session.HasLogin ? "" : "云端收藏需要登录后查看";
    }
    private void EnsureAccount()
    { if (!_account.IsCurrent) OnAccountChanged(null, EventArgs.Empty); }
    private bool IsCurrent(SessionAccount account, int generation, long folderId)
        => account.IsCurrent && generation == _generation && folderId == _currentFolderId;

    public async Task LoadAsync()
    {
        EnsureAccount();
        if (!Svc.Session.HasLogin || Loading || Busy) return;
        var account = Svc.Session.CaptureAccount(); var generation = ++_generation;
        var previousFolderId = _currentFolderId;
        Loading = true; LoadingMore = false; Error = "";
        Folders.Clear(); Items.Clear(); _currentFolderId = 0; _pn = 1;
        HasMore = false; IsEditMode = false;
        try
        {
            var (ok, err, folders) = await Svc.Api.GetFavFoldersAsync();
            if (!account.IsCurrent || generation != _generation) return;
            if (!ok || folders == null) { Error = err ?? "云端收藏夹加载失败"; return; }
            if (folders.Count == 0) { Error = "账号下还没有创建收藏夹"; return; }
            foreach (var folder in folders) Folders.Add(folder);
            var target = PendingFolder != null
                ? Folders.FirstOrDefault(f => f.Id == PendingFolder.Id) ?? Folders[0]
                : Folders.FirstOrDefault(f => f.Id == previousFolderId) ?? Folders[0];
            PendingFolder = null; Loading = false;
            await SelectFolderAsync(target);
        }
        finally { if (account.IsCurrent && generation == _generation) Loading = false; }
    }
    public async Task SelectFolderAsync(FavFolder folder)
    {
        EnsureAccount();
        if (folder == null || Busy || !Svc.Session.HasLogin) return;
        ++_generation; Loading = LoadingMore = false;
        _currentFolderId = folder.Id; CurrentFolderTitle = folder.Title;
        OnPropertyChanged(nameof(CurrentFolderTitle));
        foreach (var candidate in Folders) candidate.IsCurrent = candidate.Id == folder.Id;
        folder.IsCurrent = true;
        IsEditMode = false; Items.Clear(); _pn = 1; HasMore = false;
        await LoadPageAsync(1);
    }
    public async Task LoadMoreAsync()
    {
        EnsureAccount();
        if (Loading || LoadingMore || Busy || !HasMore || _currentFolderId <= 0) return;
        await LoadPageAsync(_pn + 1);
    }
    private async Task LoadPageAsync(int page)
    {
        var folderId = _currentFolderId; var generation = _generation;
        var account = Svc.Session.CaptureAccount();
        if (folderId <= 0 || !account.IsLoggedIn) return;
        LoadingMore = true; Error = "";
        try
        {
            var (ok, err, items, hasMore) = await Svc.Api.GetFavResourcesAsync(folderId, page, 20);
            if (!IsCurrent(account, generation, folderId)) return;
            if (!ok || items == null) { Error = err ?? "收藏夹内容加载失败"; return; }
            _pn = page;
            var seen = Items.Select(x => x.Bvid).ToHashSet();
            foreach (var item in items)
                if (!string.IsNullOrEmpty(item.Bvid) && seen.Add(item.Bvid)) Items.Add(item);
            HasMore = hasMore;
            if (Items.Count == 0) Error = "这个收藏夹还是空的";
        }
        finally { if (IsCurrent(account, generation, folderId)) LoadingMore = false; }
    }
    private void ShrinkFolderCount(long folderId, int removed)
    {
        var folder = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folder != null && removed > 0) folder.MediaCount = Math.Max(0, folder.MediaCount - removed);
    }
    public void Remove(VideoItem item)
    { if (item != null && !string.IsNullOrEmpty(item.Bvid)) _ = RemoveFromCloudAsync(item); }
    private async Task RemoveFromCloudAsync(VideoItem item)
    {
        EnsureAccount();
        if (Busy || _currentFolderId <= 0 || !Items.Contains(item) || !Svc.Session.HasLogin) return;
        var account = Svc.Session.CaptureAccount(); var folderId = _currentFolderId;
        var generation = _generation; var operation = ++_operationGeneration;
        Busy = true;
        try
        {
            var aid = await EnsureAidAsync(item);
            if (!account.IsCurrent) return;
            if (aid <= 0) { Svc.Toast.Show("无法获取视频 avid, 移出失败"); return; }
            var (ok, err) = await Svc.Api.DealFavoriteAsync(aid, delIds: new[] { folderId });
            if (!IsCurrent(account, generation, folderId)) return;
            if (!ok) { Svc.Toast.Show("移出收藏失败: " + (err ?? "未知错误")); return; }
            Items.Remove(item); ShrinkFolderCount(folderId, 1);
        }
        finally { if (operation == _operationGeneration) Busy = false; }
    }
    private async Task<long> EnsureAidAsync(VideoItem item)
    {
        if (item.Aid > 0) return item.Aid;
        var (_, _, detail) = await Svc.Api.GetVideoAsync(item.Bvid);
        return detail?.Aid ?? 0;
    }
    private async Task RemoveSelectedAsync()
    {
        EnsureAccount();
        if (Busy || _currentFolderId <= 0 || !Svc.Session.HasLogin) return;
        var account = Svc.Session.CaptureAccount(); var folderId = _currentFolderId;
        var generation = _generation; var title = CurrentFolderTitle;
        var targets = VisibleItems().Where(x => x.IsSelected).ToList();
        if (targets.Count == 0) { Svc.Toast.Show("还没有选择要移出的内容"); return; }
        if (!_confirm($"确定要把选中的 {targets.Count} 个视频移出「{title}」吗?")
            || !IsCurrent(account, generation, folderId)) return;
        var operation = ++_operationGeneration;
        Busy = true;
        var removed = 0; var fail = 0;
        try
        {
            var withAid = new List<(VideoItem item, long aid)>();
            for (var i = 0; i < targets.Count; i++)
            {
                if (!account.IsCurrent) return;
                BusyText = $"正在准备 {i + 1}/{targets.Count}…";
                var aid = await EnsureAidAsync(targets[i]);
                if (!account.IsCurrent) return;
                if (aid > 0) withAid.Add((targets[i], aid)); else fail++;
            }
            foreach (var batch in withAid.Chunk(20))
            {
                if (!account.IsCurrent) return;
                BusyText = $"正在移出 {removed}/{withAid.Count}…";
                var (ok, _) = await Svc.Api.BatchDelFavResourcesAsync(folderId, batch.Select(x => x.aid));
                if (!account.IsCurrent) return;
                if (!ok) { fail += batch.Length; continue; }
                removed += batch.Length;
                if (IsCurrent(account, generation, folderId))
                    foreach (var entry in batch) Items.Remove(entry.item);
            }
            if (!IsCurrent(account, generation, folderId)) return;
            ShrinkFolderCount(folderId, removed); RaiseSelection();
            Svc.Toast.Show(fail == 0 ? $"已移出 {removed} 个视频" : $"已移出 {removed} 个, {fail} 个失败");
            if (Items.Count == 0) { Error = "这个收藏夹还是空的"; IsEditMode = false; }
        }
        finally { if (operation == _operationGeneration) { Busy = false; BusyText = ""; } }
    }
    private async Task ClearFolderAsync()
    {
        EnsureAccount();
        if (Busy || _currentFolderId <= 0 || !Svc.Session.HasLogin) return;
        var account = Svc.Session.CaptureAccount(); var folderId = _currentFolderId;
        var generation = _generation; var title = CurrentFolderTitle;
        if (!_confirm($"确定要清空收藏夹「{title}」吗?\n\n夹内全部内容都会被移出(不只是当前已加载的), 且无法撤销。")
            || !IsCurrent(account, generation, folderId)) return;
        var operation = ++_operationGeneration;
        const int maxRounds = 80;
        Busy = true;
        var removed = 0; string? lastSignature = null;
        var stopped = ""; var empty = false;
        try
        {
            for (var round = 0; round < maxRounds; round++)
            {
                if (!account.IsCurrent) return;
                var (ok, err, items, _) = await Svc.Api.GetFavResourcesAsync(folderId, 1, 30);
                if (!account.IsCurrent) return;
                if (!ok || items == null) { stopped = err ?? "读取收藏夹内容失败"; break; }
                if (items.Count == 0) { empty = true; break; }
                var batch = items.Where(x => x.Aid > 0).ToList();
                if (batch.Count == 0) { stopped = "收藏夹仍有无法移出的内容"; break; }
                var signature = string.Join(",", batch.Select(x => x.Aid));
                if (signature == lastSignature) { stopped = "服务端没有真正删除内容, 已停止"; break; }
                lastSignature = signature;
                BusyText = $"正在清空, 已移出 {removed} 个…";
                var (deleted, error) = await Svc.Api.BatchDelFavResourcesAsync(folderId, batch.Select(x => x.Aid));
                if (!account.IsCurrent) return;
                if (!deleted) { stopped = error ?? "移出失败"; break; }
                removed += batch.Count;
            }
            if (!IsCurrent(account, generation, folderId)) return;
            ShrinkFolderCount(folderId, removed); IsEditMode = false;
            if (empty)
            {
                Items.Clear(); HasMore = false; Error = "这个收藏夹还是空的";
                Svc.Toast.Show($"已清空「{title}」, 共移出 {removed} 个");
            }
            else
            {
                Items.Clear(); _pn = 1; HasMore = false;
                await LoadPageAsync(1);
                if (IsCurrent(account, generation, folderId))
                    Error = "清空未完成: " + (stopped.Length > 0 ? stopped : "已达到本次处理上限，请重试");
            }
        }
        finally { if (operation == _operationGeneration) { Busy = false; BusyText = ""; } }
    }
}
