using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliTouch.Helpers;
using BiliTouch.Models;

namespace BiliTouch.Services;

/// <summary>
/// 扫描并准备哔哩哔哩官方 Windows 客户端默认下载目录(<c>%USERPROFILE%\Videos\bilibili</c>)
/// 中的离线缓存视频, 使其直接出现在「离线缓存 → 已缓存」中并可完整读取封面/标题/分P/UP主信息播放。
/// 原官方客户端目录下的文件一律只读, 合流产物写入 BiliTouch 自身缓存目录。
/// </summary>
public static class BilibiliClientCacheScanner
{
    private const int MaxRemuxCacheFiles = 8;

    public static string DefaultCacheDirectory
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return string.IsNullOrWhiteSpace(videos)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "bilibili")
                : Path.Combine(videos, "bilibili");
        }
    }

    public static List<OfflineEntry> ScanDefault() => ScanDirectory(DefaultCacheDirectory);

    public static List<OfflineEntry> ScanDirectory(string rootDir)
    {
        var results = new List<OfflineEntry>();
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return results;

        var parsed = new List<(OfflineEntry entry, string title, string groupTitle, int page, string tabName)>();
        IEnumerable<string> subDirs;
        try { subDirs = Directory.EnumerateDirectories(rootDir); }
        catch { return results; }

        foreach (var dir in subDirs)
        {
            try
            {
                if (TryParseEntry(dir) is { } item) parsed.Add(item);
            }
            catch
            {
                // 单个损坏目录不影响其余缓存识别
            }
        }

        // B 站客户端会用 groupId 把"跨多 BV 的系列合集"(如同一课程第 1~30 集)
        // 和"单 BV 多分P"(groupId 往往等于 BV 号)归为同一组。按 GroupKey 聚合!
        var groupCounts = parsed
            .GroupBy(x => x.entry.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var groupCovers = parsed
            .GroupBy(x => x.entry.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.entry.GroupCover).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "",
                StringComparer.OrdinalIgnoreCase);

        foreach (var (entry, rawTitle, groupTitle, page, tabName) in parsed)
        {
            var isMultiEpisodeGroup = groupCounts.TryGetValue(entry.GroupKey, out var cnt) && cnt > 1;
            if (isMultiEpisodeGroup)
            {
                var resolvedGroupTitle = !string.IsNullOrWhiteSpace(groupTitle)
                    ? groupTitle
                    : (!string.IsNullOrWhiteSpace(rawTitle) ? rawTitle : entry.Bvid);
                entry.ExternalGroupTitle = resolvedGroupTitle;
                entry.Title = resolvedGroupTitle;
                entry.PartTitle = !string.IsNullOrWhiteSpace(rawTitle)
                    ? rawTitle
                    : (string.IsNullOrWhiteSpace(tabName) || string.Equals(tabName, "正片", StringComparison.Ordinal)
                        ? $"P{page}"
                        : $"P{page} · {tabName}");
            }
            else
            {
                var singleTitle = !string.IsNullOrWhiteSpace(rawTitle)
                    ? rawTitle
                    : (!string.IsNullOrWhiteSpace(groupTitle) ? groupTitle : Path.GetFileName(entry.ExternalDirectory));
                entry.Title = singleTitle;
                entry.ExternalGroupTitle = singleTitle;
                if (!string.IsNullOrWhiteSpace(groupTitle) &&
                    !string.Equals(groupTitle, entry.Title, StringComparison.Ordinal))
                {
                    entry.PartTitle = $"{groupTitle} · P{page}";
                }
                else
                {
                    entry.PartTitle = string.IsNullOrWhiteSpace(tabName) || string.Equals(tabName, "正片", StringComparison.Ordinal)
                        ? $"P{page}"
                        : $"P{page} · {tabName}";
                }
            }

            if (string.IsNullOrWhiteSpace(entry.ExternalGroupCoverUrl) &&
                string.IsNullOrWhiteSpace(entry.ExternalGroupCoverPath) &&
                groupCovers.TryGetValue(entry.GroupKey, out var fallbackCover) &&
                !string.IsNullOrWhiteSpace(fallbackCover))
            {
                if (File.Exists(fallbackCover)) entry.ExternalGroupCoverPath = fallbackCover;
                else entry.ExternalGroupCoverUrl = fallbackCover;
            }
            results.Add(entry);
        }

        return results;
    }

    private static (OfflineEntry entry, string title, string groupTitle, int page, string tabName)? TryParseEntry(string dir)
    {
        var metaPath = Path.Combine(dir, ".videoInfo");
        if (!File.Exists(metaPath)) metaPath = Path.Combine(dir, "videoInfo.json");
        if (!File.Exists(metaPath)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath, Encoding.UTF8));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var status = GetStr(root, "status");
        var loaded = GetBool(root, "loaded");
        var progress = GetLong(root, "progress");
        var completed = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || (loaded && progress >= 100);
        if (!completed) return null;

        var totalSize = GetLong(root, "totalSize");
        var loadedSize = GetLong(root, "loadedSize");
        var expectedBytes = totalSize > 0 ? totalSize : loadedSize;
        var codecid = (int)GetLong(root, "codecid");

        if (!TryLocateMedia(dir, expectedBytes, codecid, out var directPath, out var videoPath, out var audioPath, out var mediaBytes))
            return null;

        var bvid = GetStr(root, "bvid");
        var groupId = GetIdStr(root, "groupId");
        var cid = GetLong(root, "cid");
        if (cid <= 0) cid = GetLong(root, "itemId");
        var page = (int)Math.Max(1, GetLong(root, "p"));
        var duration = (int)Math.Max(0, GetLong(root, "duration"));
        var tabName = GetStr(root, "tabName");
        var title = GetStr(root, "title");
        var groupTitle = GetStr(root, "groupTitle");
        var author = GetStr(root, "uname");
        var qn = (int)GetLong(root, "qn");
        var itemCoverUrl = GetStr(root, "coverUrl");
        var groupCoverUrl = GetStr(root, "groupCoverUrl");
        var coverUrl = !string.IsNullOrWhiteSpace(itemCoverUrl) ? itemCoverUrl : groupCoverUrl;
        var itemCoverPath = ResolveLocalCover(dir, GetStr(root, "coverPath"), "");
        var groupCoverPath = ResolveLocalCover(dir, GetStr(root, "groupCoverPath"), "");
        var coverPath = !string.IsNullOrWhiteSpace(itemCoverPath) ? itemCoverPath : groupCoverPath;
        var timeMs = GetLong(root, "completionTime");
        if (timeMs <= 0) timeMs = GetLong(root, "updateTime");
        if (timeMs <= 0) timeMs = GetLong(root, "createTime");
        var addedAt = ParseTimestamp(timeMs, dir);

        var dirName = Path.GetFileName(dir);
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("bili-desktop:" + dirName));
        var id = new Guid(hash).ToString("N");
        var size = mediaBytes > 0 ? mediaBytes : expectedBytes;
        var resolvedBvid = string.IsNullOrWhiteSpace(bvid) ? $"bili-{dirName}" : bvid;
        var groupKey = !string.IsNullOrWhiteSpace(groupId)
            ? $"client:{groupId}"
            : (!string.IsNullOrWhiteSpace(bvid) ? $"bvid:{bvid}" : $"dir:{dirName}");

        var entry = new OfflineEntry
        {
            Id = id,
            Bvid = resolvedBvid,
            Cid = cid,
            Page = page,
            DurationSeconds = duration,
            Author = string.IsNullOrWhiteSpace(author) ? "哔哩哔哩客户端" : author,
            CoverUrl = coverUrl,
            RootDirectory = dir,
            Quality = qn > 0 ? qn : 64,
            RequestedQuality = qn > 0 ? qn : 64,
            AddedAt = addedAt,
            State = OfflineState.Completed,
            Done = size,
            Total = size,
            IsExternalClientCache = true,
            ExternalDirectory = dir,
            ExternalDirectMediaPath = directPath ?? "",
            ExternalVideoPath = videoPath ?? "",
            ExternalAudioPath = audioPath ?? "",
            ExternalCoverPath = coverPath ?? "",
            ExternalGroupKey = groupKey,
            ExternalGroupTitle = groupTitle,
            ExternalGroupCoverPath = groupCoverPath ?? coverPath ?? "",
            ExternalGroupCoverUrl = groupCoverUrl ?? coverUrl,
        };
        return (entry, title, groupTitle, page, tabName);
    }

    private static bool TryLocateMedia(
        string dir, long expectedBytes, int codecid,
        out string? directPath, out string? videoPath, out string? audioPath, out long actualBytes)
    {
        directPath = null;
        videoPath = null;
        audioPath = null;
        actualBytes = 0;

        foreach (var ext in new[] { "*.mp4", "*.mkv", "*.flv" })
        {
            var candidate = Directory.EnumerateFiles(dir, ext)
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Length > 1024)
                .OrderByDescending(fi => fi.Length)
                .FirstOrDefault();
            if (candidate != null)
            {
                directPath = candidate.FullName;
                actualBytes = candidate.Length;
                return true;
            }
        }

        var m4sFiles = Directory.EnumerateFiles(dir, "*.m4s")
            .Select(f => new FileInfo(f))
            .Where(fi => fi.Length > 16)
            .ToList();
        if (m4sFiles.Count == 0) return false;

        var videos = new List<(FileInfo info, int trackId)>();
        var audios = new List<(FileInfo info, int trackId)>();
        foreach (var fi in m4sFiles)
        {
            var id = ParseStreamId(fi.Name);
            if (id is >= 30200 and < 30400) audios.Add((fi, id));
            else if (id > 0) videos.Add((fi, id));
            else
            {
                var kind = DashRemuxer.TryDetectTrackKind(fi.FullName);
                if (kind == "soun") audios.Add((fi, 0));
                else videos.Add((fi, 0));
            }
        }

        if (videos.Count == 0 && audios.Count == 1)
        {
            // 万一音频轨被误判成唯一候选, 不直接返回; 反之如果确有音频无视频(听视频缓存), 不当完整视频
            return false;
        }
        if (videos.Count == 0) return false;

        FileInfo? chosenVideo = null;
        FileInfo? chosenAudio = null;

        if (expectedBytes > 0 && audios.Count > 0)
        {
            long bestDiff = long.MaxValue;
            foreach (var v in videos)
            foreach (var a in audios)
            {
                var diff = Math.Abs((v.info.Length + a.info.Length) - expectedBytes);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    chosenVideo = v.info;
                    chosenAudio = a.info;
                }
            }
            if (bestDiff > 256) { chosenVideo = null; chosenAudio = null; }
        }

        if (chosenVideo == null)
        {
            chosenVideo = codecid switch
            {
                12 => videos.FirstOrDefault(v => v.trackId >= 100000).info ?? videos.OrderByDescending(v => v.info.Length).First().info,
                7 => videos.FirstOrDefault(v => v.trackId is > 0 and < 100000).info ?? videos.OrderByDescending(v => v.info.Length).First().info,
                _ => videos.OrderByDescending(v => v.info.Length).First().info,
            };
        }

        chosenAudio ??= audios.OrderByDescending(a => a.trackId == 30280 ? 1000 : a.trackId)
            .ThenByDescending(a => a.info.Length)
            .Select(a => a.info)
            .FirstOrDefault();

        videoPath = chosenVideo.FullName;
        audioPath = chosenAudio?.FullName;
        actualBytes = chosenVideo.Length + (chosenAudio?.Length ?? 0);
        return true;
    }

    private static int ParseStreamId(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var dash = stem.LastIndexOf('-');
        var part = dash >= 0 ? stem[(dash + 1)..] : stem;
        return int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    private static string? ResolveLocalCover(string dir, string jsonCover, string jsonGroupCover)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(dir, "image.jpg"),
            Path.Combine(dir, "image.png"),
            Path.Combine(dir, "image.webp"),
            jsonCover,
            Path.Combine(dir, "group.jpg"),
            Path.Combine(dir, "group.png"),
            jsonGroupCover,
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// 按需准备可播放的本地 mp4 文件(绝不修改 B 站官方缓存目录中的原始 .m4s)。
    /// 已合流过的同尺寸同时间戳文件直接秒级复用。
    /// </summary>
    public static Task<string> PreparePlayableMediaAsync(OfflineEntry entry, CancellationToken token = default)
        => PreparePlayableMediaCoreAsync(entry, AppPaths.CacheDir, token);

    internal static Task<string> PreparePlayableMediaCoreAsync(OfflineEntry entry, string cacheBaseDir, CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!entry.IsExternalClientCache) return entry.FilePath;
            if (!string.IsNullOrEmpty(entry.ExternalDirectMediaPath) && File.Exists(entry.ExternalDirectMediaPath))
                return entry.ExternalDirectMediaPath;
            if (string.IsNullOrEmpty(entry.ExternalVideoPath) || !File.Exists(entry.ExternalVideoPath))
                throw new FileNotFoundException("未找到哔哩哔哩客户端缓存视频文件", entry.ExternalVideoPath);

            var vInfo = new FileInfo(entry.ExternalVideoPath);
            var hasAudio = !string.IsNullOrEmpty(entry.ExternalAudioPath) && File.Exists(entry.ExternalAudioPath);
            var aLen = hasAudio ? new FileInfo(entry.ExternalAudioPath).Length : 0L;
            var remuxDir = Path.Combine(cacheBaseDir, "external-remux");
            Directory.CreateDirectory(remuxDir);
            var outPath = Path.Combine(remuxDir, $"{entry.Id}_{vInfo.Length}_{aLen}_{vInfo.LastWriteTimeUtc.Ticks:x}.mp4");
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
            {
                try { File.SetLastAccessTimeUtc(outPath, DateTime.UtcNow); } catch { }
                return outPath;
            }

            token.ThrowIfCancellationRequested();
            var ok = hasAudio
                ? DashRemuxer.TryMerge(entry.ExternalVideoPath, entry.ExternalAudioPath, outPath, out var err)
                : DashRemuxer.TryStripLeadingZeros(entry.ExternalVideoPath, outPath, out err);
            if (!ok) throw new IOException($"无法合流客户端离线缓存: {err ?? "未知错误"}");

            PruneRemuxCache(remuxDir, keepPath: outPath);
            return outPath;
        }, token);
    }

    internal static string? TryFindRemuxCachePath(OfflineEntry entry, string? cacheBaseDir = null)
    {
        if (!entry.IsExternalClientCache || string.IsNullOrEmpty(entry.ExternalVideoPath) || !File.Exists(entry.ExternalVideoPath))
            return null;
        var vInfo = new FileInfo(entry.ExternalVideoPath);
        var hasAudio = !string.IsNullOrEmpty(entry.ExternalAudioPath) && File.Exists(entry.ExternalAudioPath);
        var aLen = hasAudio ? new FileInfo(entry.ExternalAudioPath).Length : 0L;
        var baseDir = cacheBaseDir ?? AppPaths.CacheDir;
        var path = Path.Combine(baseDir, "external-remux", $"{entry.Id}_{vInfo.Length}_{aLen}_{vInfo.LastWriteTimeUtc.Ticks:x}.mp4");
        return File.Exists(path) ? path : null;
    }

    private static void PruneRemuxCache(string remuxDir, string keepPath)
    {
        try
        {
            var files = Directory.EnumerateFiles(remuxDir, "*.mp4")
                .Where(f => !string.Equals(f, keepPath, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.LastAccessTimeUtc)
                .Skip(MaxRemuxCacheFiles - 1)
                .ToList();
            foreach (var stale in files)
            {
                try { stale.Delete(); } catch { }
            }
        }
        catch { }
    }

    private static DateTimeOffset ParseTimestamp(long ms, string dir)
    {
        try
        {
            if (ms > 946_684_800_000L && ms < 4_102_444_800_000L)
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        }
        catch { }
        try { return new DirectoryInfo(dir).LastWriteTimeUtc.ToLocalTime(); }
        catch { return DateTimeOffset.Now; }
    }

    private static string GetStr(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static string GetIdStr(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return "";
        if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? "";
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n) && n > 0) return n.ToString(CultureInfo.InvariantCulture);
        return "";
    }

    private static bool GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static long GetLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return (long)d;
        return 0;
    }
}
