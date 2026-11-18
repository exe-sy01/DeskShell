using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DeskShell.Files;

/// <summary>The file explorer's own preferences, in %APPDATA%\DeskShell\files.json.</summary>
public sealed class FilesPreferences
{
    public List<string> Pins { get; set; } = new();
    /// <summary>"details" or "tiles".</summary>
    public string View { get; set; } = "details";
    public bool? ShowHidden { get; set; }
    public bool? ShowExtensions { get; set; }
    public string SortBy { get; set; } = "Name";
    public bool SortDescending { get; set; }
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 700;
    public bool Maximized { get; set; }
    public double SidebarWidth { get; set; } = 220;
    /// <summary>"preview", "details" or null: the panel on the right.</summary>
    public string? SidePanel { get; set; }
    public double SidePanelWidth { get; set; } = 320;
    /// <summary>Work out folders' sizes in the details list (in the background).</summary>
    public bool FolderSizes { get; set; }
    /// <summary>Folders that remember the grid view (pictures and so on), by path.</summary>
    public List<string> TileFolders { get; set; } = new();
    public List<string> DetailFolders { get; set; } = new();
}

/// <summary>Loads and saves the preferences; hidden-file and extension settings start from Explorer's.</summary>
public static class FileView
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskShell", "files.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static FilesPreferences Prefs { get; private set; } = new();

    public static bool ShowHidden => Prefs.ShowHidden ?? ExplorerSetting("Hidden", 2) == 1;
    public static bool ShowExtensions => Prefs.ShowExtensions ?? ExplorerSetting("HideFileExt", 1) == 0;

    static int ExplorerSetting(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath)) Prefs = JsonSerializer.Deserialize<FilesPreferences>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read the file explorer's preferences", ex);
        }
        if (Prefs.Pins.Count == 0 && !File.Exists(FilePath)) Prefs.Pins = DefaultPins();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Prefs, Json));
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save the file explorer's preferences", ex);
        }
    }

    /// <summary>Raised when pins change, so every window's sidebar updates.</summary>
    public static event Action? PinsChanged;

    static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool IsPinned(string path) => Prefs.Pins.Any(p => Same(p, path));

    public static void SetPinned(string path, bool pinned)
    {
        Prefs.Pins.RemoveAll(p => Same(p, path));
        if (pinned) Prefs.Pins.Add(path);
        Save();
        PinsChanged?.Invoke();
    }

    public static void MovePin(string path, int by)
    {
        var index = Prefs.Pins.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        var to = index + by;
        if (index < 0 || to < 0 || to >= Prefs.Pins.Count) return;
        (Prefs.Pins[index], Prefs.Pins[to]) = (Prefs.Pins[to], Prefs.Pins[index]);
        Save();
        PinsChanged?.Invoke();
    }

    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string Downloads => ShellNative.KnownFolder("374DE290-123F-4565-9164-39C4925E467B", Environment.SpecialFolder.UserProfile);
    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public static string Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public static string Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static List<string> DefaultPins() => new[] { Desktop, Downloads, Documents, Pictures, Music, Videos }
        .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A Fluent icon for well-known folders.</summary>
    public static string GlyphFor(string path)
    {
        bool Is(string known) => string.Equals(path.TrimEnd('\\'), known.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (Is(Desktop)) return "";
        if (Is(Downloads)) return "";
        if (Is(Documents)) return "";
        if (Is(Pictures)) return "";
        if (Is(Music)) return "";
        if (Is(Videos)) return "";
        if (Is(Profile)) return "";
        return "";
    }

    /// <summary>Pictures and videos open as tiles unless you've picked otherwise for that folder.</summary>
    public static bool TilesFor(string folder)
    {
        if (Prefs.TileFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return true;
        if (Prefs.DetailFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return false;
        bool Under(string root) => !string.IsNullOrEmpty(root) && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        if (Under(Pictures) || Under(Videos)) return true;
        return Prefs.View == "tiles";
    }

    public static void RememberView(string folder, bool tiles)
    {
        Prefs.TileFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        Prefs.DetailFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        (tiles ? Prefs.TileFolders : Prefs.DetailFolders).Add(folder);
        // Keep the lists from growing forever.
        if (Prefs.TileFolders.Count > 300) Prefs.TileFolders.RemoveAt(0);
        if (Prefs.DetailFolders.Count > 300) Prefs.DetailFolders.RemoveAt(0);
        Save();
    }
}
