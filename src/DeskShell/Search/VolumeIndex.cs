using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskShell.Search;

/// <summary>
/// Every file and folder name on one NTFS drive, stored compactly: no object per file, just a few
/// parallel arrays (parent folder's MFT record, name offset, flags) and one pool of UTF-8 names in
/// which each distinct name is stored once. A list of entries kept sorted by name means results
/// come out in name order without sorting them each time. About 25 bytes per file plus its
/// (shared) name.
/// </summary>
public sealed class VolumeIndex
{
    /// <summary><see cref="Recycled"/>: in the Recycle Bin (left out of results).</summary>
    public const byte InUse = 1, IsDir = 2, IsHidden = 4, IsSystem = 8, Unsorted = 16, Recycled = 32;
    const int RootRecord = 5;

    public VolumeIndex(char drive) => Drive = drive;

    public char Drive { get; }
    public uint Serial { get; set; }
    public ulong JournalId { get; set; }
    public long NextUsn { get; set; }
    /// <summary>"mft" (read straight from the drive), "walk" (folders listed one by one) or "" (none yet).</summary>
    public string Source { get; set; } = "";
    /// <summary>Fully built (a walk or MFT read finished), not just partly filled.</summary>
    public bool Complete { get; set; }
    /// <summary>Changed since the snapshot on disk.</summary>
    public bool Dirty { get; set; }

    public readonly ReaderWriterLockSlim Lock = new(LockRecursionPolicy.NoRecursion);

    // Per entry ("slot"); a free slot has flags 0.
    int[] _record = new int[1024];    // MFT record number (the low part of the file reference)
    int[] _parent = new int[1024];    // the parent folder's MFT record number
    int[] _name = new int[1024];      // offset of the name in the pool
    byte[] _flags = new byte[1024];
    int _slots;                       // slots used so far (free ones included)
    readonly Stack<int> _free = new();
    int _count;

    // record number → slot, open addressing (value = slot + 1, 0 = empty)
    int[] _table = new int[2048];

    // Names: [length byte][utf-8 bytes]; a length of 255 is followed by a 2-byte real length.
    byte[] _pool = new byte[1 << 16];
    int _poolUsed = 1;                // offset 0 is the empty name
    // While building (and compacting): distinct names, pool offset + 1, open addressing by hash, so
    // each is stored once. Afterwards it's dropped to save memory; names from changes are appended.
    int[]? _names = new int[4096];
    int _nameCount;
    long _appended;                   // bytes appended since the pool was last compacted

    // Entries in name order; new or renamed ones wait in _pending until merged in.
    int[] _sorted = Array.Empty<int>();
    int _sortedCount;
    readonly List<int> _pending = new();
    long _lastMerge = Environment.TickCount64;

    public int Count => _count;
    public int PendingCount => _pending.Count;

    public long MemoryBytes => (long)_record.Length * 13 + _table.Length * 4L + _pool.Length + (_names?.Length ?? 0) * 4L + _sorted.Length * 4L + _pending.Capacity * 4L;

    // ---------- names ----------

    public ReadOnlySpan<byte> NameBytes(int slot) => NameAt(_name[slot]);

    ReadOnlySpan<byte> NameAt(int offset)
    {
        int length = _pool[offset];
        if (length < 255) return new ReadOnlySpan<byte>(_pool, offset + 1, length);
        length = BinaryPrimitives.ReadUInt16LittleEndian(_pool.AsSpan(offset + 1));
        return new ReadOnlySpan<byte>(_pool, offset + 3, length);
    }

    public string Name(int slot) => Encoding.UTF8.GetString(NameBytes(slot));
    public byte Flags(int slot) => _flags[slot];
    public int Record(int slot) => _record[slot];
    public int ParentRecord(int slot) => _parent[slot];
    public bool IsLive(int slot) => slot >= 0 && slot < _slots && (_flags[slot] & InUse) != 0;

    /// <summary>The name's offset in the pool: an existing copy while building, else appended.</summary>
    int Intern(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return 0;
        var length = bytes.Length;
        var hash = Hash(bytes);
        if (_names != null)
        {
            var mask = _names.Length - 1;
            for (var i = hash & mask; ; i = (i + 1) & mask)
            {
                var known = _names[i];
                if (known == 0) break;
                if (NameAt(known - 1).SequenceEqual(bytes)) return known - 1;
            }
        }

        var needed = length + (length < 255 ? 1 : 3);
        if (_poolUsed + needed > _pool.Length)
            Array.Resize(ref _pool, Math.Max(_pool.Length + (_pool.Length > 8_000_000 ? _pool.Length / 8 : _pool.Length / 2), _poolUsed + needed));
        var offset = _poolUsed;
        if (length < 255) _pool[offset] = (byte)length;
        else
        {
            _pool[offset] = 255;
            BinaryPrimitives.WriteUInt16LittleEndian(_pool.AsSpan(offset + 1), (ushort)length);
        }
        bytes.CopyTo(_pool.AsSpan(offset + (length < 255 ? 1 : 3)));
        _poolUsed += needed;
        _nameCount++;
        if (_names == null)
        {
            _appended += needed;
            return offset;
        }
        if (_nameCount * 4 > _names.Length * 3) GrowNames();
        AddName(offset, hash);
        return offset;
    }

    void AddName(int offset, int hash)
    {
        var names = _names!;
        var mask = names.Length - 1;
        var i = hash & mask;
        while (names[i] != 0) i = (i + 1) & mask;
        names[i] = offset + 1;
    }

    void GrowNames()
    {
        var old = _names!;
        _names = new int[old.Length * 2];
        foreach (var known in old)
            if (known != 0) AddName(known - 1, Hash(NameAt(known - 1)));
    }

    static int Hash(ReadOnlySpan<byte> bytes)
    {
        uint hash = 2166136261;
        foreach (var b in bytes) hash = (hash ^ b) * 16777619;
        return (int)(hash & 0x7FFFFFFF);
    }

    // ---------- record → slot ----------

    // Any table size works: the hash's top bits pick the bucket ("fast range"), so it needn't be a power of two.
    static int Home(int record, int size) => (int)(((ulong)((uint)record * 2654435761u) * (uint)size) >> 32);

    public int SlotOf(long frn)
    {
        var record = (int)(frn & Ntfs.RecordMask);
        var table = _table;
        for (var i = Home(record, table.Length); ; i = i + 1 == table.Length ? 0 : i + 1)
        {
            var value = table[i];
            if (value == 0) return -1;
            if (_record[value - 1] == record) return value - 1;
        }
    }

    void TableAdd(int slot)
    {
        var i = Home(_record[slot], _table.Length);
        while (_table[i] != 0) i = i + 1 == _table.Length ? 0 : i + 1;
        _table[i] = slot + 1;
    }

    void TableRemove(int slot)
    {
        var size = _table.Length;
        var i = Home(_record[slot], size);
        while (_table[i] != slot + 1)
        {
            if (_table[i] == 0) return;
            i = i + 1 == size ? 0 : i + 1;
        }
        // Backward-shift deletion keeps every probe chain intact without tombstones.
        var j = i;
        while (true)
        {
            j = j + 1 == size ? 0 : j + 1;
            if (_table[j] == 0) break;
            var home = Home(_record[_table[j] - 1], size);
            if (i <= j ? i < home && home <= j : i < home || home <= j) continue;
            _table[i] = _table[j];
            i = j;
        }
        _table[i] = 0;
    }

    void GrowTable()
    {
        _table = new int[_table.Length + Math.Max(2048, _table.Length / 2)];
        for (var slot = 0; slot < _slots; slot++)
            if ((_flags[slot] & InUse) != 0) TableAdd(slot);
    }

    // ---------- changes (call with the write lock held) ----------

    /// <summary>
    /// Adds the entry or updates it (a rename or move). Records under NTFS's own hidden metadata are
    /// skipped. A file with several names (hard links) has one entry: <paramref name="replaceLink"/>
    /// false keeps the name it already has when this one is in another folder.
    /// </summary>
    public void Upsert(long frn, long parentFrn, FileAttributes attributes, string name, bool replaceLink = true)
    {
        var record = (int)(frn & Ntfs.RecordMask);
        var parent = (int)(parentFrn & Ntfs.RecordMask);
        if (record < 16 && record != RootRecord || parent == 11 /* $Extend */) return;
        if (record == RootRecord)
        {
            name = "";
            parent = RootRecord;
        }
        Span<byte> utf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(name.Length)];
        var bytes = utf8[..Encoding.UTF8.GetBytes(name, utf8)];

        var flags = (byte)(InUse | ((attributes & FileAttributes.Directory) != 0 ? IsDir : 0) |
                           ((attributes & FileAttributes.Hidden) != 0 ? IsHidden : 0) | ((attributes & FileAttributes.System) != 0 ? IsSystem : 0));
        // In the Recycle Bin: its folder at the root, and everything under it (when the parent's known already;
        // a full build sorts out the rest in FinishBuild).
        var parentSlot = SlotOf(parent);
        if (parentSlot >= 0 && parentSlot != SlotOf(record) && (_flags[parentSlot] & Recycled) != 0 ||
            parent == RootRecord && bytes.Length == 12 && VolumeIndex.CompareNames(bytes, "$recycle.bin"u8) == 0)
            flags |= Recycled;

        var slot = SlotOf(record);
        if (slot >= 0)
        {
            if (!replaceLink && _parent[slot] != parent) return;
            _parent[slot] = parent;
            var renamed = !NameBytes(slot).SequenceEqual(bytes);
            if (renamed) _name[slot] = Intern(bytes);
            _flags[slot] = (byte)(flags | (_flags[slot] & Unsorted));
            if (renamed) MarkUnsorted(slot);
            Dirty = true;
            return;
        }

        slot = _free.Count > 0 ? _free.Pop() : NewSlot();
        _record[slot] = record;
        _parent[slot] = parent;
        _name[slot] = Intern(bytes);
        _flags[slot] = (byte)(flags | (_flags[slot] & Unsorted));
        MarkUnsorted(slot);
        if (++_count * 4 > _table.Length * 3) GrowTable();
        else TableAdd(slot);
        Dirty = true;
    }

    int NewSlot()
    {
        if (_slots == _record.Length)
        {
            var size = _record.Length + Math.Max(1024, _record.Length > 1_000_000 ? _record.Length / 8 : _record.Length / 2);
            Array.Resize(ref _record, size);
            Array.Resize(ref _parent, size);
            Array.Resize(ref _name, size);
            Array.Resize(ref _flags, size);
        }
        return _slots++;
    }

    void MarkUnsorted(int slot)
    {
        if ((_flags[slot] & Unsorted) != 0) return;
        _flags[slot] |= Unsorted;
        _pending.Add(slot);
    }

    public void Remove(long frn)
    {
        var slot = SlotOf(frn);
        if (slot < 0) return;
        TableRemove(slot);
        _flags[slot] &= Unsorted; // free, but remember it may still sit in _pending
        _free.Push(slot);
        _count--;
        Dirty = true;
    }

    /// <summary>Empties the index (before rebuilding it).</summary>
    public void Clear()
    {
        _record = new int[1024];
        _parent = new int[1024];
        _name = new int[1024];
        _flags = new byte[1024];
        _slots = 0;
        _count = 0;
        _free.Clear();
        _table = new int[2048];
        _pool = new byte[1 << 16];
        _poolUsed = 1;
        _names = new int[4096];
        _nameCount = 0;
        _appended = 0;
        _sorted = Array.Empty<int>();
        _sortedCount = 0;
        _pending.Clear();
        Complete = false;
        Dirty = true;
    }

    /// <summary>Folds new and renamed entries into the sorted list when enough have piled up (call with the write lock held).</summary>
    public void MergeIfDue(bool force = false)
    {
        if (_pending.Count == 0) return;
        if (!force && _pending.Count < 4000 && Environment.TickCount64 - _lastMerge < 60_000) return;
        _lastMerge = Environment.TickCount64;

        var add = new List<int>(_pending.Count);
        foreach (var slot in _pending)
            if ((_flags[slot] & InUse) != 0) add.Add(slot);
        var added = add.ToArray();
        SortByName(added);

        // One pass: the old list without stale entries (freed, or waiting in pending), merged with the new ones.
        var merged = new int[_count + 16];
        int a = 0, b = 0, n = 0;
        while (true)
        {
            while (a < _sortedCount && (_flags[_sorted[a]] & (InUse | Unsorted)) != InUse) a++;
            if (a >= _sortedCount && b >= added.Length) break;
            int next;
            if (a >= _sortedCount) next = added[b++];
            else if (b >= added.Length) next = _sorted[a++];
            else next = CompareSlots(_sorted[a], added[b]) <= 0 ? _sorted[a++] : added[b++];
            if (n == merged.Length) Array.Resize(ref merged, n * 2);
            merged[n++] = next;
        }
        foreach (var slot in _pending) _flags[slot] &= unchecked((byte)~Unsorted);
        _pending.Clear();
        if (_pending.Capacity > 65536) _pending.TrimExcess();
        _sorted = merged;
        _sortedCount = n;
    }

    /// <summary>After a full build: sort everything once (call with the write lock held).</summary>
    public void FinishBuild()
    {
        var all = new int[_count];
        var n = 0;
        for (var slot = 0; slot < _slots; slot++)
        {
            _flags[slot] &= unchecked((byte)~Unsorted);
            if ((_flags[slot] & InUse) != 0) all[n++] = slot;
        }
        if (n != all.Length) Array.Resize(ref all, n);
        _pending.Clear();
        _pending.TrimExcess();
        MarkRecycled();
        SortByName(all);
        _sorted = all;
        _sortedCount = n;
        Trim();
        // The distinct-name table only saves space while thousands of names arrive at once.
        _names = null;
        _appended = 0;
        RightSizeTable();
        Complete = true;
        Dirty = true;
    }

    /// <summary>Flags everything under a Recycle Bin folder (the MFT lists children before their folders, so this is done once at the end).</summary>
    void MarkRecycled()
    {
        var state = new byte[_slots]; // 0 unknown, 1 no, 2 yes
        var chain = new List<int>();
        for (var slot = 0; slot < _slots; slot++)
        {
            if ((_flags[slot] & InUse) == 0 || state[slot] != 0) continue;
            chain.Clear();
            var current = slot;
            byte result = 1;
            while (true)
            {
                if (state[current] != 0) { result = state[current]; break; }
                chain.Add(current);
                if (_record[current] == RootRecord || chain.Count > 256) break;
                if (_parent[current] == RootRecord && CompareNames(NameBytes(current), "$recycle.bin"u8) == 0) { result = 2; break; }
                var parent = SlotOf(_parent[current]);
                if (parent < 0 || parent == current) break;
                current = parent;
            }
            foreach (var s in chain)
            {
                state[s] = result;
                if (result == 2) _flags[s] |= Recycled;
                else _flags[s] &= unchecked((byte)~Recycled);
            }
        }
    }

    /// <summary>The record lookup at about 70% full: roomy enough to stay fast, no bigger.</summary>
    void RightSizeTable()
    {
        var size = Math.Max(2048, (int)(_count / 0.7));
        if (Math.Abs(size - _table.Length) < size / 10) return;
        _table = new int[size];
        for (var slot = 0; slot < _slots; slot++)
            if ((_flags[slot] & InUse) != 0) TableAdd(slot);
    }

    /// <summary>Gives back the spare room arrays grew into while building.</summary>
    void Trim()
    {
        var size = Math.Max(1024, _slots + _slots / 32);
        if (size < _record.Length)
        {
            Array.Resize(ref _record, size);
            Array.Resize(ref _parent, size);
            Array.Resize(ref _name, size);
            Array.Resize(ref _flags, size);
        }
        var pool = Math.Max(1 << 16, _poolUsed + _poolUsed / 32);
        if (pool < _pool.Length) Array.Resize(ref _pool, pool);
    }

    // ---------- order ----------

    static readonly byte[] Fold = BuildFold();

    static byte[] BuildFold()
    {
        var fold = new byte[256];
        for (var i = 0; i < 256; i++) fold[i] = (byte)(i is >= 'A' and <= 'Z' ? i + 32 : i);
        return fold;
    }

    public static int CompareNames(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var diff = Fold[a[i]] - Fold[b[i]];
            if (diff != 0) return diff;
        }
        return a.Length - b.Length;
    }

    public int CompareSlots(int x, int y) => CompareNames(NameBytes(x), NameBytes(y));

    /// <summary>Sorts slots by name: first by an 8-byte prefix (fast, primitive sort), then ties by the whole name.</summary>
    void SortByName(int[] slots)
    {
        if (slots.Length < 2) return;
        var keys = new ulong[slots.Length];
        for (var i = 0; i < slots.Length; i++) keys[i] = Prefix(NameBytes(slots[i]));
        Array.Sort(keys, slots);
        var comparer = Comparer<int>.Create(CompareSlots);
        for (var start = 0; start < slots.Length;)
        {
            var end = start + 1;
            while (end < slots.Length && keys[end] == keys[start]) end++;
            if (end - start > 1) Array.Sort(slots, start, end - start, comparer);
            start = end;
        }
    }

    static ulong Prefix(ReadOnlySpan<byte> name)
    {
        ulong key = 0;
        for (var i = 0; i < 8; i++) key = (key << 8) | (i < name.Length ? Fold[name[i]] : 0u);
        return key;
    }

    // ---------- reading (call with the read lock held) ----------

    /// <summary>Slots whose names match, in name order.</summary>
    public int[] Find(Func<VolumeIndex, int, bool> match, CancellationToken cancel)
    {
        var result = Scan(_sorted, _sortedCount, (InUse | Unsorted), InUse, match, cancel);
        if (_pending.Count == 0) return result;

        // Entries not merged in yet: matched separately, then merged in by name.
        var extra = new List<int>();
        foreach (var slot in _pending)
            if ((_flags[slot] & InUse) != 0 && match(this, slot)) extra.Add(slot);
        cancel.ThrowIfCancellationRequested();
        if (extra.Count == 0) return result;
        var sorted = extra.ToArray();
        SortByName(sorted);
        var merged = new int[result.Length + sorted.Length];
        int a = 0, b = 0, n = 0;
        while (a < result.Length || b < sorted.Length)
        {
            if (b >= sorted.Length || a < result.Length && CompareSlots(result[a], sorted[b]) <= 0) merged[n++] = result[a++];
            else merged[n++] = sorted[b++];
        }
        return merged;
    }

    /// <summary>The slots of an earlier result that still match (typing more narrows what's there already).</summary>
    public int[] Filter(int[] slots, Func<VolumeIndex, int, bool> match, CancellationToken cancel) =>
        Scan(slots, slots.Length, InUse, InUse, match, cancel);

    /// <summary>Tests slots on all cores, a chunk each, keeping their order.</summary>
    int[] Scan(int[] slots, int count, byte mask, byte want, Func<VolumeIndex, int, bool> match, CancellationToken cancel)
    {
        const int chunk = 1 << 14;
        var chunks = (count + chunk - 1) / chunk;
        if (chunks == 0) return Array.Empty<int>();
        var found = new List<int>[chunks];
        var limit = _slots;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = cancel }, c =>
        {
            var list = new List<int>();
            var end = Math.Min(count, (c + 1) * chunk);
            for (var i = c * chunk; i < end; i++)
            {
                var slot = slots[i];
                if (slot < limit && (_flags[slot] & mask) == want && match(this, slot)) list.Add(slot);
            }
            found[c] = list;
        });
        var total = 0;
        foreach (var list in found) total += list.Count;
        var result = new int[total];
        var at = 0;
        foreach (var list in found)
        {
            CollectionsMarshal.AsSpan(list).CopyTo(result.AsSpan(at));
            at += list.Count;
        }
        return result;
    }

    /// <summary>The full path ("C:\Users\…\name").</summary>
    public string PathOf(int slot)
    {
        Span<char> buffer = stackalloc char[4096];
        var length = WritePath(slot, buffer);
        return length < 0 ? Drive + @":\" + Name(slot) : new string(buffer[..length]);
    }

    /// <summary>The folder the entry is in ("C:\Users\…").</summary>
    public string FolderOf(int slot)
    {
        var parent = SlotOf(_parent[slot]);
        if (parent < 0 || _record[parent] == RootRecord) return Drive + @":\";
        return PathOf(parent);
    }

    /// <summary>Writes the full path into <paramref name="buffer"/>; returns its length, or -1 if it doesn't fit.</summary>
    public int WritePath(int slot, Span<char> buffer)
    {
        Span<int> chain = stackalloc int[256];
        var depth = 0;
        var current = slot;
        while (current >= 0 && _record[current] != RootRecord && depth < chain.Length)
        {
            chain[depth++] = current;
            var parent = SlotOf(_parent[current]);
            if (parent == current) break;
            current = parent;
        }
        if (buffer.Length < 3) return -1;
        buffer[0] = Drive;
        buffer[1] = ':';
        buffer[2] = '\\';
        var length = 3;
        for (var i = depth - 1; i >= 0; i--)
        {
            var name = NameBytes(chain[i]);
            if (length + name.Length + 1 > buffer.Length) return -1;
            length += Encoding.UTF8.GetChars(name, buffer[length..]);
            if (i > 0) buffer[length++] = '\\';
        }
        return length;
    }

    /// <summary>Whether the entry's folder chain reaches the drive's root (entries in folders we never saw don't).</summary>
    public bool Reachable(int slot)
    {
        for (var depth = 0; depth < 256; depth++)
        {
            if (_record[slot] == RootRecord) return true;
            var parent = SlotOf(_parent[slot]);
            if (parent < 0 || parent == slot) return false;
            slot = parent;
        }
        return false;
    }

    // ---------- snapshot on disk ----------

    const int Magic = 0x58444653; // "SFDX"
    const int FormatVersion = 1;

    public void Save(string path)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(Drive);
            writer.Write(Serial);
            writer.Write(JournalId);
            writer.Write(NextUsn);
            writer.Write(Source);
            writer.Write(Complete);
            writer.Write(_slots);
            writer.Write(_poolUsed);
            writer.Write(_sortedCount);
            stream.Write(MemoryMarshal.AsBytes(_record.AsSpan(0, _slots)));
            stream.Write(MemoryMarshal.AsBytes(_parent.AsSpan(0, _slots)));
            stream.Write(MemoryMarshal.AsBytes(_name.AsSpan(0, _slots)));
            // Unsorted entries go back to pending on load; free slots stay free.
            stream.Write(_flags.AsSpan(0, _slots));
            stream.Write(_pool.AsSpan(0, _poolUsed));
            stream.Write(MemoryMarshal.AsBytes(_sorted.AsSpan(0, _sortedCount)));
        }
        File.Move(temp, path, overwrite: true);
        Dirty = false;
    }

    public static VolumeIndex? Load(string path, char drive)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion) return null;
            var index = new VolumeIndex(reader.ReadChar());
            if (index.Drive != drive) return null;
            index.Serial = reader.ReadUInt32();
            index.JournalId = reader.ReadUInt64();
            index.NextUsn = reader.ReadInt64();
            index.Source = reader.ReadString();
            index.Complete = reader.ReadBoolean();
            var slots = reader.ReadInt32();
            var poolUsed = reader.ReadInt32();
            var sortedCount = reader.ReadInt32();
            var size = Math.Max(1024, slots + slots / 32);
            index._record = new int[size];
            index._parent = new int[size];
            index._name = new int[size];
            index._flags = new byte[size];
            stream.ReadExactly(MemoryMarshal.AsBytes(index._record.AsSpan(0, slots)));
            stream.ReadExactly(MemoryMarshal.AsBytes(index._parent.AsSpan(0, slots)));
            stream.ReadExactly(MemoryMarshal.AsBytes(index._name.AsSpan(0, slots)));
            stream.ReadExactly(index._flags.AsSpan(0, slots));
            index._pool = new byte[Math.Max(1 << 16, poolUsed + poolUsed / 32)];
            stream.ReadExactly(index._pool.AsSpan(0, poolUsed));
            index._poolUsed = poolUsed;
            index._sorted = new int[sortedCount];
            stream.ReadExactly(MemoryMarshal.AsBytes(index._sorted.AsSpan()));
            index._sortedCount = sortedCount;
            index._slots = slots;
            index.Rebuild();
            index.Dirty = false;
            return index;
        }
        catch (Exception ex)
        {
            Log.Error($"File index for {drive}: couldn't read its snapshot", ex);
            return null;
        }
    }

    /// <summary>After loading: the record lookup and the free list from the arrays.</summary>
    void Rebuild()
    {
        _count = 0;
        for (var slot = 0; slot < _slots; slot++)
            if ((_flags[slot] & InUse) != 0) _count++;
        _table = new int[2048];
        RightSizeTable();
        _free.Clear();
        for (var slot = _slots - 1; slot >= 0; slot--)
        {
            if ((_flags[slot] & Unsorted) != 0) _pending.Add(slot);
            if ((_flags[slot] & InUse) == 0) _free.Push(slot);
        }
        _names = null;
        _nameCount = 0;
        for (var offset = 1; offset < _poolUsed; offset += Stored(offset)) _nameCount++;
    }

    int Stored(int offset) => _pool[offset] < 255 ? _pool[offset] + 1 : BinaryPrimitives.ReadUInt16LittleEndian(_pool.AsSpan(offset + 1)) + 3;

    /// <summary>Enough names came and went since the last compaction to make <see cref="CompactNames"/> worthwhile.</summary>
    public bool CompactionDue => _appended > Math.Max(4 << 20, _poolUsed / 8);

    /// <summary>
    /// Rewrites the name pool keeping only names in use, each once (names from changes are appended,
    /// so renames and repeats pile up). Call with the write lock held.
    /// </summary>
    public void CompactNames()
    {
        _appended = 0;
        var before = _poolUsed;
        var oldPool = _pool;
        // Old offset → new offset, for the names in use, in pool order (so a binary search finds them).
        var used = new System.Collections.BitArray(_poolUsed);
        for (var slot = 0; slot < _slots; slot++)
            if ((_flags[slot] & InUse) != 0) used[_name[slot]] = true;
        var oldOffsets = new List<int>();
        var newOffsets = new List<int>();
        _pool = new byte[Math.Max(1 << 16, _poolUsed)];
        _poolUsed = 1;
        _names = new int[4096];
        _nameCount = 0;
        for (var offset = 1; offset < before;)
        {
            var stored = oldPool[offset] < 255 ? oldPool[offset] + 1 : BinaryPrimitives.ReadUInt16LittleEndian(oldPool.AsSpan(offset + 1)) + 3;
            if (used[offset])
            {
                var length = oldPool[offset] < 255 ? oldPool[offset] : stored - 3;
                oldOffsets.Add(offset);
                newOffsets.Add(Intern(oldPool.AsSpan(offset + stored - length, length)));
            }
            offset += stored;
        }
        for (var slot = 0; slot < _slots; slot++)
            _name[slot] = (_flags[slot] & InUse) == 0 || _name[slot] == 0 ? 0 : newOffsets[oldOffsets.BinarySearch(_name[slot])];
        _names = null;
        _appended = 0;
        Array.Resize(ref _pool, Math.Max(1 << 16, _poolUsed + _poolUsed / 32));
        Log.Info($"File index for {Drive}: names compacted from {before / 1024:N0} KB to {_poolUsed / 1024:N0} KB");
        Dirty = true;
    }

    public long PoolBytes => _poolUsed;
    public int DistinctNames => _nameCount;
}
