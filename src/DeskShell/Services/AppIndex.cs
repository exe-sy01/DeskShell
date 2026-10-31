using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using ManagedShell.ShellFolders;

namespace DeskShell.Services;

public sealed record AppResult(string Name, string Target, ImageSource? Icon);

/// <summary>
/// Every app Windows knows about (desktop and Store), read from the same "Apps" folder the
/// Start menu uses. Each app is identified and launched by its AppsFolder id.
/// </summary>
public sealed class AppIndex
{
    sealed record Entry(string Name, string Target);

    volatile List<Entry> _entries = new();
    readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the app list in the background; call again to refresh.</summary>
    public void Start()
    {
        var thread = new Thread(() =>
        {
            try
            {
                _entries = Enumerate();
                Log.Info($"Found {_entries.Count} apps");
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the Apps folder", ex);
            }
        }) { IsBackground = true, Name = "DeskShell app index" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static List<Entry> Enumerate()
    {
        var result = new List<Entry>();
        var iid = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out var folder);
        var bhid = BHID_EnumItems;
        var enumIid = typeof(IEnumShellItems).GUID;
        folder.BindToHandler(IntPtr.Zero, ref bhid, ref enumIid, out var enumObj);
        var items = (IEnumShellItems)enumObj;
        while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
        {
            try
            {
                item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name);
                item.GetDisplayName(SIGDN_PARENTRELATIVEPARSING, out var target);
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(target))
                    result.Add(new Entry(name, target));
            }
            catch
            {
                // skip items that can't describe themselves
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        return result.DistinctBy(e => e.Target, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Icon for an app, loaded on first use (call on the UI thread).</summary>
    public ImageSource? IconFor(string target)
    {
        if (_icons.TryGetValue(target, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            using var item = new ShellItem($"shell:AppsFolder\\{target}");
            item.AllowAsync = false; // load now, so the icon is ready before the item is released
            icon = item.LargeIcon;
        }
        catch
        {
            // no icon
        }
        _icons[target] = icon;
        return icon;
    }

    /// <summary>Every app by name, without loading icons.</summary>
    public List<(string Name, string Target)> AllApps() =>
        _entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Select(e => (e.Name, e.Target)).ToList();

    long _lastRead = Environment.TickCount64;

    /// <summary>Picks up newly installed apps, at most every ten minutes.</summary>
    public void RefreshIfOld()
    {
        if (Environment.TickCount64 - _lastRead < 600_000) return;
        _lastRead = Environment.TickCount64;
        Start();
    }

    public List<AppResult> Search(string query, int max = 8) =>
        SearchNames(query, max).Select(a => new AppResult(a.Name, a.Target, IconFor(a.Target))).ToList();

    /// <summary>Best matches first, without loading icons.</summary>
    public List<(string Name, string Target)> SearchNames(string query, int max = 8)
    {
        query = query.Trim();
        if (query.Length == 0) return new();

        var scored = _entries
            .Select(e => (Entry: e, Score: Score(e.Name, query)))
            .Where(x => x.Score > 0)
            .ToList();
        // Loose "letters in order" matches only show when nothing matches properly.
        if (scored.Any(x => x.Score >= 60)) scored.RemoveAll(x => x.Score < 60);

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name.Length)
            .Take(max)
            .Select(x => (x.Entry.Name, x.Entry.Target))
            .ToList();
    }

    /// <summary>Finds an app by its id, e.g. to label a notification or show a pinned icon.</summary>
    public AppResult? Find(string target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        var entry = _entries.FirstOrDefault(e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase));
        return entry == null ? null : new AppResult(entry.Name, entry.Target, IconFor(entry.Target));
    }

    /// <summary>First app whose id starts with the given text (e.g. a package family plus "!").</summary>
    public AppResult? FindByPrefix(string prefix)
    {
        var entry = _entries.FirstOrDefault(e => e.Target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return entry == null ? null : new AppResult(entry.Name, entry.Target, IconFor(entry.Target));
    }

    static int Score(string name, string query)
    {
        var n = name.ToLowerInvariant();
        var q = query.ToLowerInvariant();
        if (n == q) return 100;
        if (n.StartsWith(q)) return 90;
        var words = n.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q))) return 80;
        if (q.Length >= 2 && string.Concat(words.Select(w => w[0])).StartsWith(q)) return 70;
        if (n.Contains(q)) return 60;

        // Letters in order, e.g. "vsc" → "Visual Studio Code".
        var i = 0;
        foreach (var ch in n)
            if (i < q.Length && ch == q[i]) i++;
        return i == q.Length && q.Length >= 3 ? 30 : 0;
    }

    public static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{target}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not launch {target}", ex);
        }
    }

    // ---------- Shell interop ----------

    const uint SIGDN_NORMALDISPLAY = 0;
    const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
        void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare([MarshalAs(UnmanagedType.Interface)] IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem item, out uint fetched);
    }
}
