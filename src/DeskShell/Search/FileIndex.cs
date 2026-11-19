using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskShell.Search;

/// <summary>
/// DeskShell's file-name index ("like Everything"): every file and folder on every fixed NTFS
/// drive, searchable in milliseconds. Runs in the file explorer's process.
///
/// No administrator rights are needed. A drive Windows lets us open is read straight from its
/// Master File Table (seconds); the system drive usually isn't, so its folders are walked once in
/// the background (a few minutes, at background I/O priority). After that the index is saved to
/// disk and kept current from each drive's USN change journal, which also carries it across
/// restarts: on the next start only the changes since are read. While a game runs (DeskShell's
/// game mode) building pauses and the journal is read rarely.
/// </summary>
public static class FileIndex
{
    static readonly object Gate = new();
    static readonly List<DriveWorker> Drives = new();
    static CancellationTokenSource? _stop;

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskShell", "search");

    public static bool Running { get; private set; }

    /// <summary>Raised (on a background thread) when entries were added, removed or renamed, or a drive finished building.</summary>
    public static event Action? Changed;

    public static void Start()
    {
        lock (Gate)
        {
            if (Running) return;
            Running = true;
            _stop = new CancellationTokenSource();
            Directory.CreateDirectory(Folder);
            foreach (var letter in Ntfs.FixedNtfsDrives())
            {
                var worker = new DriveWorker(letter, _stop.Token);
                Drives.Add(worker);
                worker.Start();
            }
            Log.Info($"File index started for {string.Join(", ", Drives.Select(d => d.Letter + ":"))}");
        }
    }

    /// <summary>Stops and saves (or, with <paramref name="delete"/>, throws the index away: indexing switched off).</summary>
    public static void Stop(bool delete = false)
    {
        List<DriveWorker> drives;
        lock (Gate)
        {
            if (!Running) return;
            Running = false;
            drives = Drives.ToList();
            Drives.Clear();
            foreach (var drive in drives) drive.Delete = delete;
            _stop?.Cancel();
        }
        foreach (var drive in drives) drive.Join();
        if (delete)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(Folder, "*.idx*")) File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't delete the file index", ex);
            }
        }
        Log.Info(delete ? "File index switched off" : "File index stopped");
        RaiseChanged();
    }

    internal static void RaiseChanged() => Changed?.Invoke();

    // ---------- searching ----------

    /// <summary>A result: the drive's index and the entry's slot in it. Rows read the name and path when they're shown.</summary>
    public readonly record struct Hit(VolumeIndex Volume, int Slot);

    /// <summary>
    /// Every match on every drive, in name order. <paramref name="within"/> narrows an earlier result
    /// (typing more letters) instead of scanning everything again.
    /// </summary>
    public static SearchResult Search(SearchQuery query, CancellationToken cancel, SearchResult? within = null)
    {
        var parts = new List<(VolumeIndex, int[])>();
        if (query.IsEmpty && query.Kind == SearchKind.All) return new SearchResult(query, parts);
        if (within != null)
        {
            foreach (var (volume, slots) in within.Parts)
            {
                cancel.ThrowIfCancellationRequested();
                volume.Lock.EnterReadLock();
                try { parts.Add((volume, volume.Filter(slots, query.Matches, cancel))); }
                finally { volume.Lock.ExitReadLock(); }
            }
            return new SearchResult(query, parts);
        }
        foreach (var volume in Volumes())
        {
            cancel.ThrowIfCancellationRequested();
            volume.Lock.EnterReadLock();
            try { parts.Add((volume, volume.Find(query.Matches, cancel))); }
            finally { volume.Lock.ExitReadLock(); }
        }
        return new SearchResult(query, parts);
    }

    static List<VolumeIndex> Volumes()
    {
        lock (Gate) return Drives.Select(d => d.Index).OfType<VolumeIndex>().ToList();
    }

    /// <summary>One line for the search window: what's indexed, or what's still being read.</summary>
    public static string Status()
    {
        List<DriveWorker> drives;
        lock (Gate) drives = Drives.ToList();
        if (drives.Count == 0) return Running ? "No NTFS drives to index" : "File indexing is off (DeskShell settings)";
        var building = drives.Where(d => d.Building).ToList();
        var total = drives.Sum(d => d.Building ? d.BuildCount : (long)(d.Index?.Count ?? 0));
        if (building.Count > 0)
            return $"Indexing {string.Join(", ", building.Select(d => d.Letter + ":"))}… {total:N0} so far" +
                   (Services.GameModeSignal.Active ? " (paused for a game)" : "");
        return $"{total:N0} files and folders on {string.Join(", ", drives.Select(d => d.Letter + ":"))}";
    }

    public static bool Building
    {
        get
        {
            lock (Gate) return Drives.Any(d => d.Building);
        }
    }

    public static long MemoryBytes()
    {
        List<DriveWorker> drives;
        lock (Gate) drives = Drives.ToList();
        return drives.Sum(d => d.Index?.MemoryBytes ?? 0);
    }

    // ---------- one drive ----------

    sealed class DriveWorker(char letter, CancellationToken stop)
    {
        public char Letter { get; } = letter;
        /// <summary>The searchable index (null until there is one; replaced when a rebuild finishes).</summary>
        public volatile VolumeIndex? Index;
        public volatile bool Building;
        public volatile bool Delete;
        public int BuildCount;
        Thread? _thread;
        string SnapshotPath => Path.Combine(Folder, $"{Letter}.idx");

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = $"DeskShell file index {Letter}:", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Join()
        {
            if (_thread != null && !_thread.Join(15000)) Log.Error($"File index for {Letter}: didn't stop in time");
        }

        void Run()
        {
            try
            {
                Work();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error($"File index for {Letter}: failed", ex);
            }
            finally
            {
                if (!Delete) SaveIfDirty(force: false);
                Index = null;
            }
        }

        void Work()
        {
            var serial = Ntfs.VolumeSerial(Letter);
            Ntfs.Journal? journal;
            using (var root = Ntfs.OpenRoot(Letter))
            {
                if (root.IsInvalid)
                {
                    Log.Error($"File index for {Letter}: can't open the drive ({Marshal.GetLastWin32Error()})");
                    return;
                }
                journal = Ntfs.QueryJournal(root);

                // A saved index whose journal still reaches back to it only needs the changes since.
                var saved = File.Exists(SnapshotPath) ? VolumeIndex.Load(SnapshotPath, Letter) : null;
                if (saved != null && saved.Complete && saved.Serial == serial && journal is { } j && saved.JournalId == j.Id && saved.NextUsn >= j.FirstUsn)
                {
                    Index = saved;
                    Log.Info($"File index for {Letter}: {saved.Count:N0} entries from the snapshot ({saved.Source}), catching up from the journal");
                    RaiseChanged();
                }
                else
                {
                    if (saved != null) Log.Info($"File index for {Letter}: the snapshot is out of date; rebuilding");
                    // Keep showing the old one while the new one is built.
                    if (saved is { Complete: true } && saved.Serial == serial) Index = saved;
                    using var volume = Ntfs.TryOpenVolume(Letter);
                    Build(root, volume, serial);
                }
            }

            if (Index == null) return;
            if (journal == null)
            {
                Log.Info($"File index for {Letter}: the drive has no change journal; it's re-read daily instead");
                var builtAt = Environment.TickCount64;
                while (!stop.WaitHandle.WaitOne(TimeSpan.FromMinutes(5)))
                {
                    if (Environment.TickCount64 - builtAt < TimeSpan.FromDays(1).TotalMilliseconds || Services.GameModeSignal.Active) continue;
                    using var root = Ntfs.OpenRoot(Letter);
                    using var volume = Ntfs.TryOpenVolume(Letter);
                    if (!root.IsInvalid) Build(root, volume, serial);
                    builtAt = Environment.TickCount64;
                }
                return;
            }
            Follow(serial);
        }

        /// <summary>Reads the whole drive into a new index, from the MFT if possible, else by walking its folders.</summary>
        void Build(SafeFileHandle root, SafeFileHandle? volume, uint serial)
        {
            var started = Stopwatch.StartNew();
            Building = true;
            BuildCount = 0;
            BackgroundMode(true);
            try
            {
                var journal = Ntfs.QueryJournal(root);
                // Changes made while building are replayed from the journal afterwards.
                var index = new VolumeIndex(Letter) { Serial = serial, JournalId = journal?.Id ?? 0, NextUsn = journal?.NextUsn ?? 0 };
                // Results appear while it builds, unless an older complete index is still showing.
                if (Index == null) Index = index;

                if (volume != null && ReadMft(volume, index))
                {
                    index.Source = "mft";
                }
                else
                {
                    if (volume != null) Log.Info($"File index for {Letter}: couldn't read the MFT; walking folders instead");
                    index.Lock.EnterWriteLock();
                    try { index.Clear(); }
                    finally { index.Lock.ExitWriteLock(); }
                    Walk(index);
                    index.Source = "walk";
                }

                index.Lock.EnterWriteLock();
                try { index.FinishBuild(); }
                finally { index.Lock.ExitWriteLock(); }
                Index = index;
                Log.Info($"File index for {Letter}: {index.Count:N0} entries ({index.DistinctNames:N0} distinct names, {index.PoolBytes / 1048576.0:F1} MB of names, " +
                         $"{index.MemoryBytes / 1048576.0:F0} MB in all) by {index.Source} in {started.Elapsed.TotalSeconds:F1} s");
                Building = false;
                SaveIfDirty(force: true);
                RaiseChanged();
                CompactIfIdle();
            }
            finally
            {
                Building = false;
                BackgroundMode(false);
            }
        }

        bool ReadMft(SafeFileHandle volume, VolumeIndex index)
        {
            var lastChanged = Environment.TickCount64;
            return Ntfs.EnumerateMft(volume, batch =>
            {
                index.Lock.EnterWriteLock();
                try
                {
                    foreach (var entry in batch) index.Upsert(entry.Frn, entry.ParentFrn, entry.Attributes, entry.Name);
                }
                finally
                {
                    index.Lock.ExitWriteLock();
                }
                BuildCount = index.Count;
                if (Environment.TickCount64 - lastChanged > 1000)
                {
                    lastChanged = Environment.TickCount64;
                    RaiseChanged();
                }
            }, stop, WaitWhileGaming);
        }

        /// <summary>Lists every folder from the root down, a few at a time, recording file IDs as it goes.</summary>
        void Walk(VolumeIndex index)
        {
            var rootPath = Letter + @":\";
            var pending = new ConcurrentStack<string>();
            pending.Push(rootPath);
            var busy = 0;
            long lastChanged = Environment.TickCount64;
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
            var workers = new Thread[threads];
            for (var t = 0; t < threads; t++)
            {
                workers[t] = new Thread(() =>
                {
                    try
                    {
                        BackgroundMode(true);
                        WalkFolders(index, rootPath, pending, ref busy, ref lastChanged);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"File index for {Letter}: walking folders failed", ex);
                    }
                }) { IsBackground = true, Name = $"DeskShell file index walk {Letter}:", Priority = ThreadPriority.BelowNormal };
                workers[t].Start();
            }
            foreach (var worker in workers) worker.Join();
            stop.ThrowIfCancellationRequested();
        }

        void WalkFolders(VolumeIndex index, string rootPath, ConcurrentStack<string> pending, ref int busy, ref long lastChanged)
        {
            var buffer = new byte[64 * 1024];
            var children = new List<(long Id, FileAttributes Attributes, string Name)>();
            while (true)
            {
                WaitWhileGaming();
                Interlocked.Increment(ref busy);
                if (!pending.TryPop(out var folder))
                {
                    // Done when nobody is listing a folder (which could add more) and none are left.
                    var stillBusy = Interlocked.Decrement(ref busy);
                    if (stillBusy == 0 && pending.IsEmpty) return;
                    Thread.Sleep(5);
                    continue;
                }
                children.Clear();
                if (Ntfs.ListFolder(folder, buffer, children, out var folderId) && folderId >= 0)
                {
                    // Files in WinSxS are mostly second names (hard links) of files elsewhere, like System32: keep those.
                    var linkStore = folder.Contains("\\WinSxS", StringComparison.OrdinalIgnoreCase);
                    index.Lock.EnterWriteLock();
                    try
                    {
                        if (folder == rootPath) index.Upsert(folderId, folderId, FileAttributes.Directory, "");
                        foreach (var child in children) index.Upsert(child.Id, folderId, child.Attributes, child.Name, replaceLink: !linkStore);
                    }
                    finally
                    {
                        index.Lock.ExitWriteLock();
                    }
                    foreach (var child in children)
                    {
                        // Junctions and symlinks are listed but not followed (they'd loop, or repeat other folders).
                        if ((child.Attributes & FileAttributes.Directory) != 0 && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                            pending.Push(folder.EndsWith('\\') ? folder + child.Name : folder + "\\" + child.Name);
                    }
                    BuildCount = index.Count;
                }
                Interlocked.Decrement(ref busy);
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastChanged) > 1000)
                {
                    Interlocked.Exchange(ref lastChanged, now);
                    RaiseChanged();
                }
            }
        }

        /// <summary>
        /// Keeps the index current from the change journal until DeskShell stops. The drive is opened
        /// only for each read, so nothing holds it open (a USB disk can still be ejected).
        /// </summary>
        void Follow(uint serial)
        {
            var buffer = new byte[1 << 16];
            var changes = new List<Ntfs.Change>();
            var lastSave = Environment.TickCount64;
            var failures = 0;
            while (!stop.IsCancellationRequested)
            {
                var index = Index!;
                bool caughtUp;
                using (var root = Ntfs.OpenRoot(Letter))
                {
                    if (root.IsInvalid)
                    {
                        if (failures++ == 0) Log.Info($"File index for {Letter}: the drive can't be opened right now; waiting for it");
                        stop.WaitHandle.WaitOne(30_000);
                        continue;
                    }
                    if (failures > 0 && Ntfs.VolumeSerial(Letter) != serial)
                    {
                        Log.Info($"File index for {Letter}: a different drive has this letter now; it's indexed from scratch next time");
                        Delete = true;
                        return;
                    }
                    failures = 0;
                    changes.Clear();
                    var start = index.NextUsn;
                    var next = Ntfs.ReadJournal(root, false, index.JournalId, start, Ntfs.ReasonCreate | Ntfs.ReasonDelete | Ntfs.ReasonRenameNew, changes, buffer);
                    if (next == null)
                    {
                        Log.Info($"File index for {Letter}: the change journal no longer reaches back to the index; rebuilding");
                        using var volume = Ntfs.TryOpenVolume(Letter);
                        Build(root, volume, serial);
                        continue;
                    }
                    if (changes.Count > 0) Apply(root, index, changes);
                    index.NextUsn = next.Value;
                    caughtUp = next.Value == start;
                }

                // Saved now and then (and when DeskShell quits). Not often: the system drive changes every second, and if
                // DeskShell is ended without saving, the journal still has everything since the last save.
                if (Environment.TickCount64 - lastSave > 2 * 3600_000 && !Services.GameModeSignal.Active)
                {
                    lastSave = Environment.TickCount64;
                    if (index.CompactionDue)
                    {
                        index.Lock.EnterWriteLock();
                        try { index.CompactNames(); }
                        finally { index.Lock.ExitWriteLock(); }
                    }
                    SaveIfDirty(force: false);
                }
                // More waiting: read on. While gaming, catch up only now and then (the journal keeps everything meanwhile).
                if (Services.GameModeSignal.Active) stop.WaitHandle.WaitOne(30_000);
                else if (caughtUp) stop.WaitHandle.WaitOne(1000);
            }
        }

        void Apply(SafeFileHandle root, VolumeIndex index, List<Ntfs.Change> changes)
        {
            // The journal has a record per write; only the latest per file matters (remembering a rename).
            var latest = new Dictionary<long, Ntfs.Change>();
            foreach (var change in changes)
            {
                if (latest.TryGetValue(change.Frn, out var earlier))
                    latest[change.Frn] = change with { Reason = change.Reason | (earlier.Reason & Ntfs.ReasonRenameNew) };
                else latest[change.Frn] = change;
            }

            // Work out what's new (and needs its name) under the read lock; look names up without any lock.
            var work = new List<(Ntfs.Change Change, string? Name)>(latest.Count);
            var newFolders = new HashSet<long>(); // made in this same batch: their contents count as known too
            // Records deleted in this batch: a new file may reuse one straight away (a file replaced by a new copy).
            var deleted = latest.Values.Where(c => (c.Reason & Ntfs.ReasonDelete) != 0).Select(c => c.Frn & Ntfs.RecordMask).ToHashSet();
            index.Lock.EnterReadLock();
            try
            {
                foreach (var change in latest.Values)
                {
                    var slot = index.SlotOf(change.Frn);
                    if ((change.Reason & Ntfs.ReasonDelete) != 0)
                    {
                        if (slot >= 0) work.Add((change, null));
                        continue;
                    }
                    var parent = (int)(change.ParentFrn & Ntfs.RecordMask);
                    // Still being written: nothing about its name or place changed.
                    if (slot >= 0 && index.ParentRecord(slot) == parent && (change.Reason & Ntfs.ReasonRenameNew) == 0 &&
                        !deleted.Contains(change.Frn & Ntfs.RecordMask)) continue;
                    // Only entries in folders we know (others are in places we can't see).
                    if (index.SlotOf(change.ParentFrn) < 0 && !newFolders.Contains(change.ParentFrn & Ntfs.RecordMask)) continue;
                    if ((change.Attributes & FileAttributes.Directory) != 0) newFolders.Add(change.Frn & Ntfs.RecordMask);
                    work.Add((change, change.Name));
                }
            }
            finally
            {
                index.Lock.ExitReadLock();
            }
            if (work.Count == 0) return;

            for (var i = 0; i < work.Count; i++)
            {
                var (change, name) = work[i];
                if ((change.Reason & Ntfs.ReasonDelete) == 0 && name == null)
                    work[i] = (change, Ntfs.NameById(root, change.Frn));
            }

            index.Lock.EnterWriteLock();
            try
            {
                foreach (var (change, name) in work)
                {
                    if ((change.Reason & Ntfs.ReasonDelete) != 0) index.Remove(change.Frn);
                    else if (name != null) index.Upsert(change.Frn, change.ParentFrn, change.Attributes, name);
                }
                index.MergeIfDue();
            }
            finally
            {
                index.Lock.ExitWriteLock();
            }
            RaiseChanged();
        }

        void SaveIfDirty(bool force)
        {
            var index = Index;
            if (index == null || !index.Complete || Building || !index.Dirty && !force) return;
            try
            {
                index.Lock.EnterReadLock();
                try { index.Save(SnapshotPath); }
                finally { index.Lock.ExitReadLock(); }
            }
            catch (Exception ex)
            {
                Log.Error($"File index for {Letter}: couldn't save its snapshot", ex);
            }
        }

        void WaitWhileGaming()
        {
            while (Services.GameModeSignal.Active && !stop.IsCancellationRequested) stop.WaitHandle.WaitOne(5000);
            stop.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Once every drive is built: building leaves hundreds of MB of spent buffers (arrays that grew,
    /// sort keys) on the large-object heap; compact it and give the memory back to Windows.
    /// </summary>
    static void CompactIfIdle()
    {
        lock (Gate)
            if (Drives.Any(d => d.Building)) return;
        Compact();
    }

    /// <summary>A full, compacting collection (large objects too), then the working set handed back.</summary>
    public static void Compact()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
    }

    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    /// <summary>Background priority for this thread, CPU and disk: building never competes with what you're doing.</summary>
    static void BackgroundMode(bool on) => SetThreadPriority(GetCurrentThread(), on ? 0x00010000 : 0x00020000);

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern bool SetThreadPriority(IntPtr thread, int priority);
}
