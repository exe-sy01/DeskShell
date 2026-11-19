using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DeskShell.Files;

namespace DeskShell.Search;

/// <summary>One result as shown: name, folder, icon, and size and date (read from disk when it comes into view).</summary>
public sealed class SearchRow : INotifyPropertyChanged
{
    readonly bool _system, _hidden;

    public SearchRow(int index, string name, string folder, bool isFolder, bool hidden, bool system)
    {
        Index = index;
        Name = name;
        Folder = folder;
        IsFolder = isFolder;
        _hidden = hidden;
        _system = system;
    }

    public int Index { get; }
    public string Name { get; }
    /// <summary>The folder it's in ("C:\Users\…").</summary>
    public string Folder { get; }
    public bool IsFolder { get; }
    public string Path => Folder.EndsWith('\\') ? Folder + Name : Folder + "\\" + Name;
    public double Faded => _missing ? 0.4 : _hidden ? 0.6 : 1;

    long _size = -1;
    DateTime _modified;
    bool _infoAsked, _missing;

    public long Size => _size;
    public DateTime Modified => _modified;

    public string SizeText
    {
        get
        {
            AskInfo();
            return IsFolder || _size < 0 ? "" : FileItem.FormatSize(_size);
        }
    }

    public string ModifiedText
    {
        get
        {
            AskInfo();
            return _modified == default ? "" : _modified.ToString("g");
        }
    }

    void AskInfo()
    {
        if (_infoAsked) return;
        _infoAsked = true;
        RowInfo.Request(this);
    }

    /// <summary>Size and date, known already (sorting by them read them all).</summary>
    internal void SetInfo(long size, DateTime modified, bool missing)
    {
        _infoAsked = true;
        _size = size;
        _modified = modified;
        _missing = missing;
        OnChanged(nameof(SizeText));
        OnChanged(nameof(ModifiedText));
        if (missing) OnChanged(nameof(Faded));
    }

    ImageSource? _icon;
    bool _iconAsked;

    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && !_iconAsked)
            {
                _iconAsked = true;
                _icon = IconLoader.Small(Path, IsFolder, IconLoader.KeyFor(Name, IsFolder, _system), icon =>
                {
                    _icon = icon;
                    OnChanged(nameof(Icon));
                }, () => _iconAsked = false);
            }
            return _icon;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Reads sizes and dates for rows as they come into view, newest first, on one background thread.</summary>
static class RowInfo
{
    static readonly ConcurrentStack<SearchRow> Queue = new();
    static readonly SemaphoreSlim Signal = new(0);
    static Thread? _thread;

    public static void Request(SearchRow row)
    {
        Queue.Push(row);
        if (_thread == null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "DeskShell search row info", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
        Signal.Release();
    }

    /// <summary>Forgets rows not shown any more (a new search).</summary>
    public static void Clear() => Queue.Clear();

    static void Run()
    {
        var dispatcher = Application.Current.Dispatcher;
        while (true)
        {
            Signal.Wait();
            if (!Queue.TryPop(out var row)) continue;
            var (size, modified, missing) = Read(row.Path);
            dispatcher.BeginInvoke(() => row.SetInfo(size, modified, missing), DispatcherPriority.Background);
        }
    }

    // WIN32_FILE_ATTRIBUTE_DATA: its FILETIMEs are two DWORDs, so nothing is 8-byte aligned.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct AttributeData
    {
        public uint Attributes;
        public long Created, Accessed, Written;
        public uint SizeHigh, SizeLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetFileAttributesEx(string path, int level, out AttributeData data);

    /// <summary>Size, last write time, and whether it's gone. One call, no handle opened.</summary>
    public static (long Size, DateTime Modified, bool Missing) Read(string path)
    {
        if (!GetFileAttributesEx(path.Length >= 248 ? @"\\?\" + path : path, 0, out var data)) return (-1, default, true);
        var size = (long)data.SizeHigh << 32 | data.SizeLow;
        var modified = data.Written > 0 ? DateTime.FromFileTime(data.Written) : default;
        return ((data.Attributes & 0x10) != 0 ? -1 : size, modified, false);
    }
}

/// <summary>
/// The result list the window shows: rows are made only for the ones scrolled into view (a search
/// can match millions), in name order or in an order sorted by another column.
/// </summary>
public sealed class ResultList : IList, INotifyCollectionChanged
{
    readonly Dictionary<int, SearchRow> _rows = new();
    SearchResult _result;
    List<FileIndex.Hit>? _sorted;                 // when sorted by something other than name
    Dictionary<FileIndex.Hit, (long Size, DateTime Modified)>? _info;
    bool _reversed;

    public ResultList(SearchResult result) => _result = result;

    public SearchResult Result => _result;

    /// <summary>New results (same list object, so the view keeps its scroll position).</summary>
    public void Replace(SearchResult result, List<FileIndex.Hit>? sorted, bool reversed, Dictionary<FileIndex.Hit, (long, DateTime)>? info)
    {
        _result = result;
        _sorted = sorted;
        _reversed = reversed;
        _info = info;
        _rows.Clear();
        RowInfo.Clear();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public int Count => _sorted?.Count ?? _result.Count;

    public FileIndex.Hit HitAt(int index)
    {
        if (_sorted != null) return _sorted[_reversed ? _sorted.Count - 1 - index : index];
        return _result[_reversed ? _result.Count - 1 - index : index];
    }

    public SearchRow RowAt(int index)
    {
        if (_rows.TryGetValue(index, out var row)) return row;
        var hit = HitAt(index);
        var volume = hit.Volume;
        volume.Lock.EnterReadLock();
        try
        {
            if (!volume.IsLive(hit.Slot))
                row = new SearchRow(index, "(removed)", volume.Drive + @":\", false, true, false);
            else
            {
                var flags = volume.Flags(hit.Slot);
                row = new SearchRow(index, volume.Name(hit.Slot), volume.FolderOf(hit.Slot), (flags & VolumeIndex.IsDir) != 0,
                    (flags & VolumeIndex.IsHidden) != 0, (flags & VolumeIndex.IsSystem) != 0);
            }
        }
        finally
        {
            volume.Lock.ExitReadLock();
        }
        if (_info != null && _info.TryGetValue(hit, out var known)) row.SetInfo(known.Size, known.Modified, false);
        _rows[index] = row;
        return row;
    }

    public object? this[int index]
    {
        get => RowAt(index);
        set => throw new NotSupportedException();
    }

    public int IndexOf(object? value) =>
        value is SearchRow row && row.Index < Count && _rows.TryGetValue(row.Index, out var known) && ReferenceEquals(known, row) ? row.Index : -1;

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    // A read-only list as far as WPF is concerned.
    public bool IsFixedSize => false;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++) array.SetValue(RowAt(i), index + i);
    }

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return RowAt(i);
    }
}
