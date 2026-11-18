using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace DeskShell.Files;

/// <summary>
/// Zipping and unzipping, like Explorer's "Compress to ZIP file" and "Extract all". Both run on a
/// background thread so the window stays responsive, can be cancelled, and never leave a half-made
/// zip or folder behind when they fail.
/// </summary>
public static class Archive
{
    /// <summary>How often progress is passed on; reporting every small file would flood the UI thread.</summary>
    const int ProgressEveryMs = 100;

    public static bool IsZip(string path) => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes a .zip next to the first item: named after that item if there's one, or after the folder
    /// they're in if there are several. Folders go in whole, under their own name. Returns the zip's path.
    /// </summary>
    public static Task<string> CompressAsync(IReadOnlyList<string> paths, IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        if (paths.Count == 0) throw new ArgumentException("Nothing to compress.", nameof(paths));
        var items = paths.Select(p => p.TrimEnd('\\', '/')).ToList();
        var folder = Path.GetDirectoryName(items[0]) ?? throw new IOException("A whole drive can't be compressed this way.");

        string baseName;
        if (items.Count == 1)
            baseName = Directory.Exists(items[0]) ? Path.GetFileName(items[0]) : Path.GetFileNameWithoutExtension(items[0]);
        else
            baseName = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Archive";

        var zipPath = UniquePath(Path.Combine(folder, baseName + ".zip"));
        var throttle = Stopwatch.StartNew();
        void Report(string name, bool force = false)
        {
            if (progress == null || (!force && throttle.ElapsedMilliseconds < ProgressEveryMs)) return;
            throttle.Restart();
            progress.Report($"Compressing {name}…");
        }

        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var top = Path.GetFileName(item);
                if (Directory.Exists(item)) AddFolder(zip, item, top, zipPath, Report, ct);
                else if (File.Exists(item)) { Report(top, force: true); AddFile(zip, item, top, ct); }
                else throw new FileNotFoundException($"\"{top}\" isn't there any more.", item);
            }
            return zipPath;
        }
        catch
        {
            // The zip is ours and incomplete; leaving it would look like a finished archive.
            try { File.Delete(zipPath); } catch { /* still locked or already gone */ }
            throw;
        }
    }, ct);

    /// <summary>
    /// Extracts into a new folder named after the zip (made unique if taken) and returns it. Entries that
    /// would land outside that folder ("../../Windows/…", the "zip slip" trick) are skipped.
    /// </summary>
    public static Task<string> ExtractAsync(string zip, IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var folder = Path.GetDirectoryName(zip) ?? throw new IOException("That zip isn't in a folder.");
        var name = Path.GetFileNameWithoutExtension(zip);
        if (string.IsNullOrWhiteSpace(name)) name = "Extracted";
        var target = UniquePath(Path.Combine(folder, name), isFolder: true);
        var root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
        var throttle = Stopwatch.StartNew();

        Directory.CreateDirectory(target);
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var first = true;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // outside the folder: refuse

                // Folder entries end with a slash and have no file name of their own.
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }

                if (progress != null && (first || throttle.ElapsedMilliseconds >= ProgressEveryMs))
                {
                    first = false;
                    throttle.Restart();
                    progress.Report($"Extracting {entry.Name}…");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var source = entry.Open())
                using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                    CopyTo(source, output, ct);
                try { File.SetLastWriteTime(dest, entry.LastWriteTime.LocalDateTime); } catch { /* keep today's date */ }
            }
            return target;
        }
        catch
        {
            // The folder is new and ours; a half-filled one would pass for a complete extraction.
            try { Directory.Delete(target, recursive: true); } catch { /* something in it is still open */ }
            throw;
        }
    }, ct);

    /// <summary>
    /// The path itself if nothing is there, else "Name (2).ext", "Name (3).ext"… For folders pass
    /// <paramref name="isFolder"/> so a dot in the name ("v1.2") isn't mistaken for an extension.
    /// </summary>
    public static string UniquePath(string path, bool isFolder = false)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path) ?? "";
        var name = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var extension = isFolder ? "" : Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{name} ({n}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Adds a folder and everything in it, hidden files included (as Explorer does). Linked folders
    /// (junctions, symlinks) are added empty rather than followed, so a link back up can't loop forever.
    /// </summary>
    static void AddFolder(ZipArchive zip, string folder, string entryRoot, string zipPath, Action<string, bool> report, CancellationToken ct)
    {
        var pending = new Stack<(string Path, string Entry)>();
        pending.Push((folder, entryRoot));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, entry) = pending.Pop();
            var info = new DirectoryInfo(dir);
            var empty = true;
            foreach (var child in info.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                ct.ThrowIfCancellationRequested();
                empty = false;
                var childEntry = entry + "/" + child.Name;
                if (child is DirectoryInfo sub)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) zip.CreateEntry(childEntry + "/");
                    else pending.Push((sub.FullName, childEntry));
                }
                else if (!string.Equals(child.FullName, zipPath, StringComparison.OrdinalIgnoreCase))
                {
                    report(child.Name, false);
                    AddFile(zip, child.FullName, childEntry, ct);
                }
            }
            // Zips only remember folders through what's in them, so an empty one needs its own entry.
            if (empty) zip.CreateEntry(entry + "/");
        }
    }

    static void AddFile(ZipArchive zip, string path, string entryName, CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        // Zip dates only cover 1980–2107; anything outside would throw.
        var modified = File.GetLastWriteTime(path);
        if (modified.Year < 1980) modified = new DateTime(1980, 1, 1);
        else if (modified.Year > 2107) modified = new DateTime(2107, 12, 31);
        entry.LastWriteTime = modified;
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = entry.Open();
        CopyTo(source, output, ct);
    }

    /// <summary>Copies in chunks so a big file can still be cancelled part-way.</summary>
    static void CopyTo(Stream source, Stream output, CancellationToken ct)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }
}
