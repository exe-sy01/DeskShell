using System.IO;
using System.IO.Enumeration;

namespace DeskShell.Files;

/// <summary>
/// Adds up everything inside a folder, on a background thread. Results are remembered for a few
/// minutes so flicking back and forth between folders doesn't walk the disk again.
/// </summary>
public static class FolderSize
{
    static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);
    static readonly Dictionary<string, (DateTime At, (long Bytes, int Files, int Folders) Result)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The total size, file count and folder count under <paramref name="folder"/>. Hidden and
    /// system files count (like Explorer's Properties). Links and junctions to other folders are
    /// not followed, so loops and double counting can't happen; cloud placeholder files (which are
    /// also reparse points) still count.
    /// </summary>
    public static Task<(long Bytes, int Files, int Folders)> MeasureAsync(string folder, CancellationToken ct)
    {
        if (TryCached(folder, out var known)) return Task.FromResult(known);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
                BufferSize = 64 * 1024
            };
            var entries = new FileSystemEnumerable<(bool IsFolder, long Length)>(folder,
                (ref FileSystemEntry e) => (e.IsDirectory, e.IsDirectory ? 0 : e.Length), options)
            {
                ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0
            };
            long bytes = 0;
            int files = 0, folders = 0, seen = 0;
            foreach (var (isFolder, length) in entries)
            {
                if ((++seen & 127) == 0) ct.ThrowIfCancellationRequested();
                if (isFolder) folders++;
                else
                {
                    files++;
                    bytes += length;
                }
            }
            ct.ThrowIfCancellationRequested();
            var result = (bytes, files, folders);
            lock (Cache)
            {
                // Keep the cache from growing without end over a long session.
                if (Cache.Count > 500)
                {
                    var now = DateTime.UtcNow;
                    foreach (var stale in Cache.Where(pair => now - pair.Value.At > KeepFor).Select(pair => pair.Key).ToList())
                        Cache.Remove(stale);
                    if (Cache.Count > 500) Cache.Clear();
                }
                Cache[Key(folder)] = (DateTime.UtcNow, result);
            }
            return result;
        }, ct);
    }

    /// <summary>A measurement from the last five minutes, if there is one.</summary>
    public static bool TryCached(string folder, out (long Bytes, int Files, int Folders) result)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(Key(folder), out var entry) && DateTime.UtcNow - entry.At <= KeepFor)
            {
                result = entry.Result;
                return true;
            }
        }
        result = default;
        return false;
    }

    /// <summary>Forgets a folder's size (after something in it changed).</summary>
    public static void Forget(string folder)
    {
        lock (Cache) Cache.Remove(Key(folder));
    }

    /// <summary>"0 bytes", "12.3 KB", "4.5 GB".</summary>
    public static string Human(long bytes)
    {
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = { "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);
        return value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>The same folder written with or without a trailing slash is one cache entry.</summary>
    static string Key(string folder)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch { return folder; }
    }
}
