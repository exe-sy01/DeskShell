using System.ComponentModel;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace DeskShell.Files;

/// <summary>A file or folder shown in the explorer. Icons and thumbnails load when it first comes into view.</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    public FileItem(string folder, in FolderEntry entry)
    {
        Folder = folder;
        Name = entry.Name;
        Path = System.IO.Path.Join(folder, entry.Name);
        IsFolder = entry.IsFolder;
        Update(entry);
    }

    public string Folder { get; }
    public string Name { get; private set; }
    public string Path { get; private set; }
    public bool IsFolder { get; }
    public long Size { get; private set; }
    public DateTime Modified { get; private set; }
    public FileAttributes Attributes { get; private set; }

    public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(Name);
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public double Faded => IsCut ? 0.45 : IsHidden ? 0.6 : 1;

    /// <summary>The name as shown: without the extension if file extensions are hidden.</summary>
    public string DisplayName => IsFolder || FileView.ShowExtensions || Extension.Length == 0 ? Name : System.IO.Path.GetFileNameWithoutExtension(Name);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("g");
    public string SizeText => IsFolder ? _folderBytes is { } bytes ? FormatSize(bytes) : _measuring ? "…" : "" : FormatSize(Size);

    long? _folderBytes;
    bool _measuring;
    /// <summary>A folder's total size, once worked out (View → Folder sizes).</summary>
    public long? FolderBytes { get => _folderBytes; set { _folderBytes = value; _measuring = false; OnChanged(); OnChanged(nameof(SizeText)); } }
    public bool Measuring { get => _measuring; set { _measuring = value; OnChanged(nameof(SizeText)); } }

    string? _typeName;
    /// <summary>"File folder", "Text Document"…; in search results, where it was found.</summary>
    public string TypeName => Location ?? (_typeName ??= IsFolder ? "File folder" : FileTypes.NameOf(Extension));

    /// <summary>Set for search results: the folder it was found in, relative to where the search started.</summary>
    public string? Location { get; init; }

    public bool Update(in FolderEntry entry)
    {
        var changed = entry.Size != Size || entry.Modified != Modified || entry.Attributes != Attributes;
        Size = entry.Size;
        Modified = entry.Modified;
        Attributes = entry.Attributes;
        if (changed)
        {
            OnChanged(nameof(Size));
            OnChanged(nameof(SizeText));
            OnChanged(nameof(ModifiedText));
            OnChanged(nameof(Faded));
            // A changed image deserves a new thumbnail.
            if (_thumb != null)
            {
                _thumb = null;
                _thumbRequested = false;
                OnChanged(nameof(Thumbnail));
            }
        }
        return changed;
    }

    public void RefreshName() => OnChanged(nameof(DisplayName));

    // ---------- icons, loaded on demand ----------

    ImageSource? _icon, _thumb;
    bool _iconRequested, _thumbRequested;

    /// <summary>The small icon for the details view.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && !_iconRequested)
            {
                _iconRequested = true;
                _icon = IconLoader.Small(this, icon => { _icon = icon; OnChanged(nameof(Icon)); }, () => _iconRequested = false);
            }
            return _icon;
        }
    }

    /// <summary>A large thumbnail (or icon) for the grid view.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumb == null && !_thumbRequested)
            {
                _thumbRequested = true;
                IconLoader.Thumbnail(this, image => { _thumb = image; OnChanged(nameof(Thumbnail)); }, () => _thumbRequested = false);
            }
            return _thumb;
        }
    }

    // ---------- view state ----------

    bool _isCut, _isRenaming;
    public bool IsCut { get => _isCut; set { if (_isCut == value) return; _isCut = value; OnChanged(); OnChanged(nameof(Faded)); } }
    public bool IsRenaming { get => _isRenaming; set { if (_isRenaming == value) return; _isRenaming = value; OnChanged(); OnChanged(nameof(RenameVisibility)); } }
    public Visibility RenameVisibility => _isRenaming ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = { "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);
        return value >= 100 ? $"{value:0} {units[unit]}" : value >= 10 ? $"{value:0.#} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One directory entry, read without creating FileInfo objects.</summary>
public readonly record struct FolderEntry(string Name, bool IsFolder, long Size, DateTime Modified, FileAttributes Attributes);

/// <summary>Reads folders quickly, straight from the file system.</summary>
public static class FolderReader
{
    public static List<FolderEntry> Read(string folder, bool showHidden, CancellationToken cancel = default)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false, BufferSize = 64 * 1024 };
        var entries = new FileSystemEnumerable<FolderEntry>(folder,
            (ref FileSystemEntry e) => new FolderEntry(e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length,
                e.LastWriteTimeUtc.LocalDateTime, e.Attributes), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => Visible(e.Attributes, showHidden)
        };
        var list = new List<FolderEntry>();
        foreach (var entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            list.Add(entry);
        }
        return list;
    }

    /// <summary>Like Explorer: hidden items only when asked for, protected system files never.</summary>
    public static bool Visible(FileAttributes attributes, bool showHidden)
    {
        var hidden = (attributes & FileAttributes.Hidden) != 0;
        if (!hidden) return true;
        if ((attributes & FileAttributes.System) != 0) return false;
        return showHidden;
    }

    /// <summary>Searches everything under <paramref name="folder"/> for names containing <paramref name="text"/>, in batches.</summary>
    public static void Search(string folder, string text, bool showHidden, Action<List<FileItem>> found, CancellationToken cancel, int limit = 5000)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = true, BufferSize = 64 * 1024 };
        var entries = new FileSystemEnumerable<FileItem>(folder,
            (ref FileSystemEntry e) =>
            {
                var dir = e.Directory.ToString();
                var entry = new FolderEntry(e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.LocalDateTime, e.Attributes);
                var relative = System.IO.Path.GetRelativePath(folder, dir);
                return new FileItem(dir, entry) { Location = relative == "." ? System.IO.Path.GetFileName(folder.TrimEnd('\\')) : relative };
            }, options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => Visible(e.Attributes, showHidden) &&
                                                                e.FileName.Contains(text, StringComparison.OrdinalIgnoreCase),
            ShouldRecursePredicate = (ref FileSystemEntry e) => Visible(e.Attributes, showHidden) &&
                                                                (e.Attributes & FileAttributes.ReparsePoint) == 0
        };
        var batch = new List<FileItem>();
        var total = 0;
        var lastSent = Environment.TickCount64;
        foreach (var item in entries)
        {
            if (cancel.IsCancellationRequested) return;
            batch.Add(item);
            if (++total >= limit) break;
            if (Environment.TickCount64 - lastSent > 150)
            {
                found(batch);
                batch = new List<FileItem>();
                lastSent = Environment.TickCount64;
            }
        }
        if (!cancel.IsCancellationRequested) found(batch);
    }
}

/// <summary>Registered type names ("Text Document"), looked up once per extension.</summary>
public static class FileTypes
{
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    public static string NameOf(string extension)
    {
        lock (Names)
        {
            if (Names.TryGetValue(extension, out var known)) return known;
        }
        var info = new ShellNative.SHFILEINFO();
        var name = ShellNative.SHGetFileInfo("x" + extension, ShellNative.FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellNative.SHFILEINFO>(),
            ShellNative.SHGFI_TYPENAME | ShellNative.SHGFI_USEFILEATTRIBUTES) != IntPtr.Zero && !string.IsNullOrEmpty(info.szTypeName)
            ? info.szTypeName
            : extension.Length > 1 ? extension[1..].ToUpperInvariant() + " File" : "File";
        lock (Names) Names[extension] = name;
        return name;
    }
}

/// <summary>Sort orders; folders always come first.</summary>
public sealed class FileComparer : IComparer<FileItem>
{
    public FileComparer(string by, bool descending)
    {
        By = by;
        Descending = descending;
    }

    public string By { get; }
    public bool Descending { get; }

    public int Compare(FileItem? a, FileItem? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
        var result = By switch
        {
            "Modified" => a.Modified.CompareTo(b.Modified),
            "Type" => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            "Size" => a.Size.CompareTo(b.Size),
            _ => 0
        };
        if (result == 0) result = ShellNative.StrCmpLogicalW(a.Name, b.Name);
        if (result == 0) result = string.CompareOrdinal(a.Path, b.Path);
        return Descending ? -result : result;
    }
}
