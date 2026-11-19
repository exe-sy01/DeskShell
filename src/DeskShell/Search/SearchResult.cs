namespace DeskShell.Search;

/// <summary>
/// The matches of one search: each drive's matching entries, already in name order. They're merged
/// into one list only as far as it's looked at, so a search that matches millions of files costs
/// no more than the scan (nobody scrolls through millions).
/// </summary>
public sealed class SearchResult
{
    readonly List<FileIndex.Hit> _merged = new();
    readonly int[] _positions;

    public SearchResult(SearchQuery query, List<(VolumeIndex Volume, int[] Slots)> parts)
    {
        Query = query;
        Parts = parts.Where(p => p.Slots.Length > 0).ToArray();
        _positions = new int[Parts.Length];
        Count = Parts.Sum(p => p.Slots.Length);
    }

    public SearchQuery Query { get; }
    public (VolumeIndex Volume, int[] Slots)[] Parts { get; }
    public int Count { get; }

    public FileIndex.Hit this[int index]
    {
        get
        {
            if (index >= _merged.Count) MergeTo(index + 1);
            return _merged[index];
        }
    }

    /// <summary>Every hit, in name order (merges the rest).</summary>
    public List<FileIndex.Hit> All()
    {
        MergeTo(Count);
        return _merged;
    }

    void MergeTo(int wanted)
    {
        lock (_merged)
        {
            if (_merged.Count >= wanted) return;
            // Ahead in good-sized steps, so scrolling doesn't merge row by row.
            wanted = Math.Min(Count, Math.Max(wanted, _merged.Count + 2000));
            if (Parts.Length == 1)
            {
                var (volume, slots) = Parts[0];
                for (var i = _merged.Count; i < wanted; i++) _merged.Add(new FileIndex.Hit(volume, slots[i]));
                return;
            }
            foreach (var part in Parts) part.Volume.Lock.EnterReadLock();
            try
            {
                while (_merged.Count < wanted)
                {
                    var best = -1;
                    for (var i = 0; i < Parts.Length; i++)
                    {
                        if (_positions[i] >= Parts[i].Slots.Length) continue;
                        if (best < 0 || Compare(i, best) < 0) best = i;
                    }
                    _merged.Add(new FileIndex.Hit(Parts[best].Volume, Parts[best].Slots[_positions[best]++]));
                }
            }
            finally
            {
                foreach (var part in Parts) part.Volume.Lock.ExitReadLock();
            }
        }
    }

    int Compare(int a, int b)
    {
        var (va, sa) = Parts[a];
        var (vb, sb) = Parts[b];
        var slotA = sa[_positions[a]];
        var slotB = sb[_positions[b]];
        // A slot freed since the search compares as empty; it's skipped when shown.
        var nameA = va.IsLive(slotA) ? va.NameBytes(slotA) : default;
        var nameB = vb.IsLive(slotB) ? vb.NameBytes(slotB) : default;
        return VolumeIndex.CompareNames(nameA, nameB);
    }
}
