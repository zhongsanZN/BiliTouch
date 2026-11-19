using System;
using System.IO;
using System.Text;

namespace BiliTouch.Helpers;

/// <summary>
/// 进程安全的原子文件写入工具：先写入同目录唯一临时文件，再通过 <see cref="File.Move(string, string, bool)"/>
/// 替换目标文件，避免并发写碰撞（共享 .tmp 冲突）及断电/崩溃导致的半截损坏文件。
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content, Encoding? encoding = null)
    {
        EnsureDirectory(path);
        var tmp = BuildTempPath(path);
        try
        {
            File.WriteAllText(tmp, content, encoding ?? Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct = default)
    {
        EnsureDirectory(path);
        var tmp = BuildTempPath(path);
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static async Task CopyStreamAsync(Stream source, string path, CancellationToken ct = default)
    {
        EnsureDirectory(path);
        var tmp = BuildTempPath(path);
        try
        {
            await using (var target = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static string BuildTempPath(string path)
        => $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}
