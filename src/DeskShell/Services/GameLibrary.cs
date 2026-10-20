using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagedShell.ShellFolders;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace DeskShell.Services;

/// <summary>An installed game, from whichever launcher installed it.</summary>
public sealed record Game(
    string Key,          // stable id, e.g. "steam:221100"
    string Name,
    string Source,       // Steam, Epic, Xbox, Battle.net, Ubisoft, EA, Riot, …
    string Launch,       // URI or program to start
    string? Arguments,
    string? CoverPath,   // portrait box art, when the launcher has one locally
    string? IconPath,    // program or .ico for an icon when there's no cover
    long LastPlayed);    // Unix seconds, 0 if unknown

/// <summary>
/// Every installed game in one list: Steam (all libraries), Epic, Xbox app / Game Pass, and games
/// that registered with Windows from Battle.net, Ubisoft, EA, Riot and Rockstar. Read in the
/// background; recently played first.
/// </summary>
public static class GameLibrary
{
    static List<Game> _games = new();
    static DateTime _scannedAt = DateTime.MinValue;
    static bool _scanning;
    static readonly Dictionary<string, ImageSource?> Images = new();

    public static IReadOnlyList<Game> Games => _games;

    /// <summary>Raised on the UI thread after a scan finds a different list.</summary>
    public static event Action? Changed;

    /// <summary>Rescans if the list is older than <paramref name="maxAge"/> (default 10 minutes).</summary>
    public static async Task RefreshAsync(TimeSpan? maxAge = null)
    {
        if (_scanning || DateTime.Now - _scannedAt < (maxAge ?? TimeSpan.FromMinutes(10))) return;
        _scanning = true;
        try
        {
            var found = await Task.Run(Scan);
            _scannedAt = DateTime.Now;
            if (found.Select(g => g.Key).SequenceEqual(_games.Select(g => g.Key))) return;
            _games = found;
            Log.Info($"Game library: {found.Count} games ({string.Join(", ", found.GroupBy(g => g.Source).Select(s => $"{s.Count()} {s.Key}"))})");
            Changed?.Invoke();
        }
        finally
        {
            _scanning = false;
        }
    }

    static List<Game> Scan()
    {
        var games = new List<Game>();
        void Add(Func<IEnumerable<Game>> source, string name)
        {
            try { games.AddRange(source()); }
            catch (Exception ex) { Log.Error($"Couldn't read {name} games", ex); }
        }
        Add(Steam, "Steam");
        Add(Epic, "Epic");
        Add(Xbox, "Xbox");
        Add(Registered, "registered");

        // The same game can show up twice (e.g. a Steam game that also registered with Windows): keep the first.
        return games
            .GroupBy(g => Normalize(g.Name))
            .Select(g => g.First())
            .OrderByDescending(g => g.LastPlayed)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string Normalize(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "");

    static readonly Regex NotAGame = new(
        @"redistributable|soundtrack|dedicated server|\bserver$|\btools?\b|\bsdk\b|\bbenchmark\b|\bdemo server\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---------- Steam ----------

    static IEnumerable<Game> Steam()
    {
        var steam = (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', '\\');
        if (steam == null || !Directory.Exists(steam)) yield break;

        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));

        var cache = Path.Combine(steam, "appcache", "librarycache");
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest);
                string Field(string name) => Regex.Match(text, $"\"{name}\"\\s+\"([^\"]*)\"").Groups[1].Value;
                var id = Field("appid");
                var name = Field("name");
                if (id.Length == 0 || name.Length == 0 || NotAGame.IsMatch(name)) continue;
                if (id is "228980" or "431960") continue; // Steamworks redistributables, Wallpaper Engine
                long.TryParse(Field("LastPlayed"), out var lastPlayed);
                yield return new Game($"steam:{id}", name, "Steam", $"steam://rungameid/{id}", null, SteamCover(cache, id), null, lastPlayed);
            }
        }
    }

    /// <summary>Steam's portrait box art, in either its old or new cache layout.</summary>
    static string? SteamCover(string cache, string id)
    {
        var folder = Path.Combine(cache, id);
        if (!Directory.Exists(folder)) return null;
        foreach (var name in new[] { "library_600x900.jpg", "library_capsule.jpg" })
        {
            var match = Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories).FirstOrDefault();
            if (match != null) return match;
        }
        return null;
    }

    // ---------- Epic ----------

    static IEnumerable<Game> Epic()
    {
        var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var categories = root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                ? cats.EnumerateArray().Select(c => c.GetString() ?? "").ToList()
                : new List<string>();
            if (!categories.Contains("games")) continue; // skips engines, plugins, tools

            var name = Str("DisplayName");
            var exe = Str("LaunchExecutable");
            var location = Str("InstallLocation");
            var uri = $"com.epicgames.launcher://apps/{Str("CatalogNamespace")}%3A{Str("CatalogItemId")}%3A{Str("AppName")}?action=launch&silent=true";
            yield return new Game($"epic:{Str("AppName")}", name, "Epic", uri, null, null,
                exe.Length > 0 ? Path.Combine(location, exe) : null, 0);
        }
    }

    // ---------- Xbox app / Game Pass ----------

    static IEnumerable<Game> Xbox()
    {
        var manager = new PackageManager();
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            string location;
            try { location = package.InstalledLocation.Path; }
            catch { continue; }
            // Xbox app games install under "XboxGames" and carry a MicrosoftGame.config.
            var isGame = location.Contains(@"\XboxGames\", StringComparison.OrdinalIgnoreCase) ||
                         File.Exists(Path.Combine(location, "MicrosoftGame.config"));
            if (!isGame) continue;

            var entries = package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult();
            foreach (var entry in entries)
            {
                string? logo = null;
                try { logo = package.Logo?.LocalPath; } catch { }
                yield return new Game($"xbox:{entry.AppUserModelId}", entry.DisplayInfo.DisplayName, "Xbox",
                    $"shell:AppsFolder\\{entry.AppUserModelId}", null, null, logo, 0);
            }
        }
    }

    // ---------- Battle.net, Ubisoft, EA, Riot, Rockstar (from Windows' installed-apps list) ----------

    static readonly Regex GamePublishers = new(
        "Blizzard|Ubisoft|Electronic Arts|Riot Games|Rockstar|Activision|Bethesda|CD PROJEKT|Bungie|Respawn|2K|Bandai|Square Enix|Capcom|SEGA",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex Launchers = new(
        @"^(Battle\.net|Rockstar Games (Launcher|SDK)|Ubisoft Connect|EA app|Riot (Vanguard|Client)|Bethesda\.net Launcher|Social Club)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static IEnumerable<Game> Registered()
    {
        var roots = new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            using var uninstall = hive.OpenSubKey(path);
            if (uninstall == null) continue;
            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(keyName);
                var name = key?.GetValue("DisplayName") as string;
                var publisher = key?.GetValue("Publisher") as string ?? "";
                if (key == null || string.IsNullOrWhiteSpace(name) || !GamePublishers.IsMatch(publisher) ||
                    Launchers.IsMatch(name) || NotAGame.IsMatch(name))
                    continue;

                var icon = CleanPath(key.GetValue("DisplayIcon") as string);
                var uninstallCmd = key.GetValue("UninstallString") as string ?? "";
                var source = Regex.Match(publisher, GamePublishers.ToString(), RegexOptions.IgnoreCase).Value;
                if (source.Equals("Blizzard", StringComparison.OrdinalIgnoreCase)) source = "Battle.net";

                // Ubisoft: "Uplay Install 1234" → uplay://launch/1234/0
                var ubisoft = Regex.Match(keyName, @"^Uplay Install (\d+)$");
                if (ubisoft.Success)
                {
                    yield return new Game($"ubisoft:{ubisoft.Groups[1].Value}", name, "Ubisoft", $"uplay://launch/{ubisoft.Groups[1].Value}/0", null, null, icon, 0);
                    continue;
                }

                // Riot: the uninstaller is RiotClientServices.exe --uninstall-product=X; launch with --launch-product=X.
                var riot = Regex.Match(uninstallCmd, "\"?([^\"]*RiotClientServices\\.exe)\"?\\s+(.*)", RegexOptions.IgnoreCase);
                if (riot.Success)
                {
                    var args = riot.Groups[2].Value.Replace("--uninstall-", "--launch-");
                    yield return new Game($"riot:{keyName}", name, "Riot", riot.Groups[1].Value, args, null, icon, 0);
                    continue;
                }

                // Everything else: start the game's program (Battle.net and EA games hand over to their launcher themselves).
                if (icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon))
                    yield return new Game($"reg:{keyName}", name, source, icon, null, null, icon, 0);
            }
        }
    }

    /// <summary>"\"C:\\x\\game.exe\",0" → "C:\x\game.exe".</summary>
    static string? CleanPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('"');
        var comma = s.LastIndexOf(',');
        if (comma > 2 && int.TryParse(s[(comma + 1)..], out _)) s = s[..comma];
        return s.Trim('"').Replace('/', '\\');
    }

    // ---------- search, launch, pictures ----------

    public static List<Game> Search(string query, int max = 6)
    {
        var q = Normalize(query);
        if (q.Length == 0) return new List<Game>();
        return _games
            .Select(g => (Game: g, Score: Normalize(g.Name) is var n && n.StartsWith(q) ? 2 : n.Contains(q) ? 1 : 0))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Game.LastPlayed)
            .Take(max)
            .Select(x => x.Game)
            .ToList();
    }

    public static Game? Find(string key) => _games.FirstOrDefault(g => g.Key == key);

    public static void Launch(Game game)
    {
        try
        {
            if (game.Launch.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("explorer.exe", game.Launch) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(game.Launch, game.Arguments ?? "") { UseShellExecute = true });
            Log.Info($"Launched {game.Name} ({game.Source})");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not launch {game.Name}", ex);
        }
    }

    /// <summary>Box art if there is some, else the game's icon. Call on the UI thread.</summary>
    public static ImageSource? Picture(Game game, out bool isCover)
    {
        isCover = game.CoverPath != null;
        if (Images.TryGetValue(game.Key, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            if (game.CoverPath != null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 160;
                bmp.UriSource = new Uri(game.CoverPath);
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
            else if (game.IconPath != null && File.Exists(game.IconPath))
            {
                using var item = new ShellItem(game.IconPath);
                item.AllowAsync = false;
                image = item.ExtraLargeIcon ?? item.LargeIcon;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"No picture for {game.Name}", ex);
        }
        Images[game.Key] = image;
        return image;
    }
}
