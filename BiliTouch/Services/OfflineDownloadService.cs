using System.IO;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.ViewModels;

namespace BiliTouch.Services;

public enum OfflineState { Queued, Downloading, Paused, Failed, Completed }

public sealed class OfflineEntry : ObservableObject
{
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string Bvid { get; set; }="";
    public long Cid { get; set; }
    public string Title { get; set; }="";
    public string PartTitle { get; set; }="";
    public int Page { get; set; }=1;
    public string Author { get; set; }="";
    public string ImportSource {get;set;}="";
    public string CoverUrl { get; set; }="";
    public string RootDirectory { get; set; }="";
    public int Quality { get; set; }=64;
    public int RequestedQuality {get;set;}=64;
    public DateTimeOffset AddedAt { get; set; }=DateTimeOffset.Now;
    public OfflineState State { get; set; }
    public long Done { get; set; }
    public long Total { get; set; }
    public string Error { get; set; }="";
    [JsonIgnore] public bool IsExternalClientCache { get; set; }
    [JsonIgnore] public string ExternalDirectory { get; set; } = "";
    [JsonIgnore] public string ExternalDirectMediaPath { get; set; } = "";
    [JsonIgnore] public string ExternalVideoPath { get; set; } = "";
    [JsonIgnore] public string ExternalAudioPath { get; set; } = "";
    [JsonIgnore] public string ExternalCoverPath { get; set; } = "";
    [JsonIgnore] public string ExternalGroupKey { get; set; } = "";
    [JsonIgnore] public string ExternalGroupTitle { get; set; } = "";
    [JsonIgnore] public string ExternalGroupCoverPath { get; set; } = "";
    [JsonIgnore] public string ExternalGroupCoverUrl { get; set; } = "";
    [JsonIgnore] public int DurationSeconds { get; set; }
    [JsonIgnore] public string GroupKey => ExternalGroupKey.Length > 0 ? ExternalGroupKey : (Bvid.Length > 0 ? $"bvid:{Bvid}" : $"id:{Id}");
    [JsonIgnore] public string GroupTitle => ExternalGroupTitle.Length > 0 ? ExternalGroupTitle : Title;
    [JsonIgnore] public string GroupCover => File.Exists(ExternalGroupCoverPath) ? ExternalGroupCoverPath : (!string.IsNullOrWhiteSpace(ExternalGroupCoverUrl) ? ExternalGroupCoverUrl : Cover);
    [JsonIgnore] public string DisplayPartTitle => PartTitle.Length > 0 ? PartTitle : Title;
    [JsonIgnore] public string DurationText => DurationSeconds <= 0 ? "" : TimeSpan.FromSeconds(DurationSeconds) is { TotalHours: >= 1 } ts ? ts.ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(DurationSeconds).ToString(@"mm\:ss");
    [JsonIgnore] public string FilePath=>IsExternalClientCache && ExternalDirectMediaPath.Length>0?ExternalDirectMediaPath:Path.Combine(RootDirectory,"videos",Id+".mp4");
    [JsonIgnore] public string PartPath=>FilePath+".part";
    [JsonIgnore] public string CoverPath=>Path.Combine(RootDirectory,"covers",Id+".jpg");
    [JsonIgnore] public string Cover=>File.Exists(ExternalCoverPath)?ExternalCoverPath:(File.Exists(CoverPath)?CoverPath:CoverUrl);
    [JsonIgnore] public bool HasPlayableMedia=>IsExternalClientCache?(ExternalDirectMediaPath.Length>0?File.Exists(ExternalDirectMediaPath):File.Exists(ExternalVideoPath)):File.Exists(FilePath);
    [JsonIgnore] public double Percent=>Total>0?Math.Clamp(Done*100.0/Total,0,100):0;
    [JsonIgnore] public bool IsComplete=>State==OfflineState.Completed;
    [JsonIgnore] public bool IsSelected {get=>_selected;set=>SetProperty(ref _selected,value);}
    private bool _selected;
    [JsonIgnore] public string QualityText
    {
        get
        {
            if (ImportSource.Length > 0) return "本地导入";
            var q = Quality switch
            {
                >= 120 => "4K",
                >= 116 => "1080P 60帧",
                >= 112 => "1080P+",
                >= 80 => "1080P",
                >= 64 => "720P",
                >= 32 => "480P",
                _ => "360P"
            };
            return IsExternalClientCache ? $"{q} · B站客户端缓存" : q;
        }
    }
    [JsonIgnore] public string ActionText=>State is OfflineState.Downloading or OfflineState.Queued?"暂停":State==OfflineState.Completed?"播放":State==OfflineState.Failed?"重试":"继续";
    [JsonIgnore] public string StatusText=>State switch {
        OfflineState.Completed=>$"{(IsExternalClientCache ? "客户端缓存" : "已缓存")} · {ByteSize.Format(Done)}",
        OfflineState.Downloading=>Total>0?$"正在缓存 {Percent:0}% · {ByteSize.Format(Done)} / {ByteSize.Format(Total)}":$"正在缓存 · {ByteSize.Format(Done)}",
        OfflineState.Queued=>"等待缓存", OfflineState.Paused=>$"已暂停 · {ByteSize.Format(Done)}",
        _=>"缓存失败 · "+Error };
    public void Notify(){foreach(var p in new[]{nameof(State),nameof(Done),nameof(Total),nameof(Percent),nameof(IsComplete),nameof(StatusText),nameof(ActionText),nameof(Cover),nameof(QualityText)})OnPropertyChanged(p);}
}

/// <summary>Persistent cache inventory. It never infers ownership by scanning unrelated Downloads files.</summary>
public sealed class OfflineDownloadService
{
    public ObservableCollection<OfflineEntry> Items {get;}=new();
    public event Action? Changed;
    private readonly Dictionary<string,CancellationTokenSource> _running=new();
    private readonly Dictionary<string,Task> _work=new();
    private static readonly HttpClient Http=new(new SocketsHttpHandler { AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TimeSpan.FromSeconds(20) }) { Timeout=Timeout.InfiniteTimeSpan };
    public static OfflineDownloadService Instance {get;}=new();
    private static string IndexPath=>Path.Combine(AppPaths.DataDir,"offline-downloads.json");
    private readonly JsonSerializerOptions _json=new(){WriteIndented=true};
    private readonly Func<string,long,int,Task<(bool ok,string? err,string? url,string? audio,int quality)>> _source;
    private readonly HttpClient _http;
    private readonly string? _externalCacheDir;
    public OfflineDownloadService() : this((b,c,q)=>Svc.Api.GetOfflineDownloadSourceAsync(b,c,q),Http,BilibiliClientCacheScanner.DefaultCacheDirectory) {}
    internal OfflineDownloadService(Func<string,long,int,Task<(bool ok,string? err,string? url,string? audio,int quality)>> source,HttpClient http,string? externalCacheDir=null)
    { _source=source;_http=http;_externalCacheDir=externalCacheDir; }
    private bool _loaded;
    private bool _closing;

    public void Load()
    {
        if(_loaded)return;
        if(File.Exists(IndexPath))
        {
            try
            {
                var entries=JsonSerializer.Deserialize<List<OfflineEntry>>(File.ReadAllText(IndexPath))??new();
                foreach(var e in entries.Where(e=>Guid.TryParseExact(e.Id,"N",out _) && Path.IsPathFullyQualified(e.RootDirectory)).DistinctBy(e=>e.Id))
                {
                    if(e.State is OfflineState.Downloading or OfflineState.Queued)e.State=OfflineState.Paused;
                    if(e.State==OfflineState.Completed && !File.Exists(e.FilePath)){e.State=OfflineState.Failed;e.Error="文件已移动或删除";}
                    Items.Add(e);
                }
            }
            catch(Exception ex){App.ReportError(ex);throw new IOException("缓存记录读取失败，原记录已保留",ex);}
        }
        _loaded=true;
    }

    public async Task RefreshExternalCachesAsync()
    {
        Load();
        if(string.IsNullOrWhiteSpace(_externalCacheDir))return;
        var root=_externalCacheDir;
        var detected=await Task.Run(()=>BilibiliClientCacheScanner.ScanDirectory(root));
        MergeExternalCaches(detected);
    }

    internal void MergeExternalCaches(List<OfflineEntry> detected)
    {
        var changed=false;
        var byId=detected.ToDictionary(e=>e.Id,StringComparer.Ordinal);
        for(var i=Items.Count-1;i>=0;i--)
        {
            if(Items[i].IsExternalClientCache && !byId.ContainsKey(Items[i].Id))
            {
                Items.RemoveAt(i);
                changed=true;
            }
        }
        var existingExternalIds=Items.Where(e=>e.IsExternalClientCache).ToDictionary(e=>e.Id,StringComparer.Ordinal);
        foreach(var ext in detected)
        {
            if(existingExternalIds.TryGetValue(ext.Id,out var old))
            {
                old.Title=ext.Title;
                old.PartTitle=ext.PartTitle;
                old.Author=ext.Author;
                old.Quality=ext.Quality;
                old.Done=ext.Done;
                old.Total=ext.Total;
                old.CoverUrl=ext.CoverUrl;
                old.ExternalDirectory=ext.ExternalDirectory;
                old.ExternalDirectMediaPath=ext.ExternalDirectMediaPath;
                old.ExternalVideoPath=ext.ExternalVideoPath;
                old.ExternalAudioPath=ext.ExternalAudioPath;
                old.ExternalCoverPath=ext.ExternalCoverPath;
                old.Notify();
            }
            else
            {
                Items.Add(ext);
                changed=true;
            }
        }
        if(changed)Changed?.Invoke();
    }
    public void Enqueue(VideoDetail video,IEnumerable<VideoPage> pages,int quality)
    {
        Load();
        var groupDate=Items.FirstOrDefault(e=>e.Bvid==video.Bvid)?.AddedAt ?? DateTimeOffset.Now;
        foreach(var p in pages)
        {
            var old=Items.FirstOrDefault(e=>e.Bvid==video.Bvid && e.Cid==p.Cid && e.RequestedQuality==quality);
            if(old!=null){if(old.State!=OfflineState.Completed)Resume(old);continue;}
            Items.Insert(0,new OfflineEntry { Bvid=video.Bvid,Cid=p.Cid,Title=video.Title,PartTitle=p.DisplayTitle,Page=p.Page,
                AddedAt=groupDate,CoverUrl=video.Pic,Author=video.Owner,RootDirectory=AppPaths.DownloadDir,Quality=quality,RequestedQuality=quality,State=OfflineState.Queued });
        }
        Save();Changed?.Invoke();Pump();
    }
    public void Import(IEnumerable<string> paths)
    {
        Load();
        foreach(var path in paths)
        {
            var full=Path.GetFullPath(path);
            if(!File.Exists(full))continue;
            var existing=Items.FirstOrDefault(e=>string.Equals(e.ImportSource,full,StringComparison.OrdinalIgnoreCase));
            if(existing!=null){Resume(existing);continue;}
            var entry=new OfflineEntry {ImportSource=full,Title=Path.GetFileNameWithoutExtension(full),PartTitle="本地视频",RootDirectory=AppPaths.DownloadDir,State=OfflineState.Queued};
            entry.Bvid="local-"+entry.Id;Items.Insert(0,entry);
        }
        Save();Changed?.Invoke();Pump();
    }

    internal bool IsRunning(OfflineEntry e)=>_running.ContainsKey(e.Id);
    public void Pause(OfflineEntry e)
    {
        if(e.State==OfflineState.Completed)return;
        if(_running.TryGetValue(e.Id,out var cts))cts.Cancel();
        e.State=OfflineState.Paused;e.Notify();Save();Changed?.Invoke();
    }
    public void Resume(OfflineEntry e)
    {
        if(_closing || e.IsExternalClientCache || _running.ContainsKey(e.Id) || !Items.Contains(e))return;
        if(e.State==OfflineState.Completed && File.Exists(e.FilePath))return;
        e.State=OfflineState.Queued;e.Error="";e.Notify();Save();Changed?.Invoke();Pump();
    }
    public async Task DeleteAsync(OfflineEntry e)
    {
        if(e.IsExternalClientCache)
        {
            var remux=BilibiliClientCacheScanner.TryFindRemuxCachePath(e);
            if(remux!=null && File.Exists(remux))try{File.Delete(remux);}catch{}
            throw new IOException("哔哩哔哩客户端离线缓存请在官方客户端中管理（原文件已保留）");
        }
        Pause(e);if(_work.TryGetValue(e.Id,out var work))await work;
        // Paths are generated from a validated task ID, not an arbitrary path in the manifest.
        if(!Guid.TryParseExact(e.Id,"N",out _))throw new IOException("无效缓存记录");
        foreach(var file in new[]{e.FilePath,e.PartPath,e.CoverPath})if(File.Exists(file))File.Delete(file);
        Items.Remove(e);Save();Changed?.Invoke();
    }
    private void Pump()
    {
        if(_closing)return;
        foreach(var e in Items.Where(e=>e.State==OfflineState.Queued && !_running.ContainsKey(e.Id)).ToList())
        {
            if(_running.Count>=2)break;
            var cts=new CancellationTokenSource();_running.Add(e.Id,cts);
            _work[e.Id]=System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>RunAsync(e,cts)).Task.Unwrap();
        }
    }
    private async Task RunAsync(OfflineEntry e,CancellationTokenSource cts)
    {
        await Task.Yield(); // Ensure the work handle exists before cleanup can run.
        using var progress=Svc.DownloadProgress.Begin(e.Title);
        try
        {
            e.State=OfflineState.Downloading;e.Notify();Save();Changed?.Invoke();
            if(e.ImportSource.Length>0){await CopyImportedAsync(e,cts.Token,progress);return;}
            var previousQuality=e.Quality;
            var(ok,err,url,_,actualQuality)=await _source(e.Bvid,e.Cid,e.RequestedQuality);
            e.Quality=actualQuality;
            cts.Token.ThrowIfCancellationRequested();
            if(!ok || string.IsNullOrWhiteSpace(url))throw new IOException(err??"获取缓存地址失败");
            Directory.CreateDirectory(Path.GetDirectoryName(e.FilePath)!);
            if(previousQuality!=actualQuality && File.Exists(e.PartPath))File.Delete(e.PartPath);
            var offset=File.Exists(e.PartPath)?new FileInfo(e.PartPath).Length:0;
            using var request=new HttpRequestMessage(HttpMethod.Get,url);
            request.Headers.UserAgent.ParseAdd(HttpDefaults.UserAgent);
            request.Headers.Referrer=new Uri($"https://www.bilibili.com/video/{e.Bvid}");
            request.Headers.TryAddWithoutValidation("Origin","https://www.bilibili.com");
            if(offset>0)request.Headers.Range=new RangeHeaderValue(offset,null);
            using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cts.Token);
            // A server that ignores Range requires a clean restart, never append a full response.
            if(response.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable)
            { if(File.Exists(e.PartPath))File.Delete(e.PartPath);throw new IOException("续传位置已失效，请重试"); }
            response.EnsureSuccessStatusCode();
            var mediaType=response.Content.Headers.ContentType?.MediaType;
            if(mediaType is "text/html" or "application/json")throw new IOException("服务器未返回视频数据");
            if(response.StatusCode==HttpStatusCode.PartialContent)
            {if(response.Content.Headers.ContentRange?.From!=offset)throw new IOException("缓存续传校验失败");}
            else offset=0;
            e.Total=response.Content.Headers.ContentRange?.Length ?? ((response.Content.Headers.ContentLength??0)>0?offset+response.Content.Headers.ContentLength!.Value:0);
            e.Done=offset;
            await using(var input=await response.Content.ReadAsStreamAsync(cts.Token))
            await using(var output=new FileStream(e.PartPath,offset>0?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.Read,81920,true))
            {
                var buffer=new byte[81920];long tick=0;int count;
                while((count=await input.ReadAsync(buffer,cts.Token))>0)
                {
                    await output.WriteAsync(buffer.AsMemory(0,count),cts.Token);e.Done+=count;progress.Report(e.Done,e.Total);
                    if(Environment.TickCount64-tick>300){tick=Environment.TickCount64;e.Notify();}
                }
            }
            cts.Token.ThrowIfCancellationRequested();
            if(e.Done==0)throw new IOException("服务器返回了空视频");
            if(e.Total>0 && e.Done!=e.Total)throw new IOException("视频未完整下载，请重试");
            File.Move(e.PartPath,e.FilePath,true);e.State=OfflineState.Completed;e.Error="";
            using var coverTimeout=CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            coverTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await SaveCoverAsync(e,coverTimeout.Token); } catch(OperationCanceledException) { }

            TrayService.Instance.Notify("缓存完成",e.Title+" · "+e.PartTitle);
        }
        catch(OperationCanceledException){e.State=File.Exists(e.FilePath)?OfflineState.Completed:OfflineState.Paused;}
        catch(Exception ex){e.State=OfflineState.Failed;e.Error=ex.Message;}
        finally
        {
            if(File.Exists(e.PartPath))e.Done=new FileInfo(e.PartPath).Length;
            _running.Remove(e.Id);_work.Remove(e.Id);cts.Dispose();e.Notify();Save();Changed?.Invoke();Pump();
        }
    }
    private async Task CopyImportedAsync(OfflineEntry e,CancellationToken token,DownloadProgressService.Job progress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(e.FilePath)!);
        await using var input=new FileStream(e.ImportSource,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);
        var offset=File.Exists(e.PartPath)?new FileInfo(e.PartPath).Length:0;
        if(offset>input.Length || (e.Total>0 && e.Total!=input.Length))offset=0;
        e.Total=input.Length;e.Done=offset;input.Position=offset;
        await using(var output=new FileStream(e.PartPath,offset>0?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.Read,81920,true))
        {
            var buffer=new byte[81920];int count;long tick=0;
            while((count=await input.ReadAsync(buffer,token))>0)
            {
                await output.WriteAsync(buffer.AsMemory(0,count),token);e.Done+=count;progress.Report(e.Done,e.Total);
                if(Environment.TickCount64-tick>300){tick=Environment.TickCount64;e.Notify();}
            }
        }
        token.ThrowIfCancellationRequested();
        if(e.Done<=0 || e.Done!=e.Total)throw new IOException("本地视频复制不完整");
        File.Move(e.PartPath,e.FilePath,true);e.State=OfflineState.Completed;e.Error="";
    }

    private async Task SaveCoverAsync(OfflineEntry e,CancellationToken token)
    {
        try
        {
            if(!Uri.TryCreate(e.CoverUrl,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https"))return;
            using var response=await _http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            Directory.CreateDirectory(Path.GetDirectoryName(e.CoverPath)!);
            await using var source=await response.Content.ReadAsStreamAsync(token);
            await using var target=File.Create(e.CoverPath);await source.CopyToAsync(target,token);
        }
        catch(Exception ex) when(ex is not OperationCanceledException){App.ReportError(ex);}
    }
    private void Save()
    {
        if(!_loaded)return;
        Directory.CreateDirectory(AppPaths.DataDir);
        var native=Items.Where(e=>!e.IsExternalClientCache).ToList();
        var temp=IndexPath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(native,_json));File.Move(temp,IndexPath,true);
    }
    public void Shutdown()
    {
        _closing=true;foreach(var cts in _running.Values)cts.Cancel();
        foreach(var e in Items.Where(e=>e.State is OfflineState.Queued or OfflineState.Downloading)){e.State=OfflineState.Paused;e.Notify();}
        Save();
    }
}
