using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace DeskShell.Services;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string PageUrl);

/// <summary>
/// Checks a GitHub repository's latest release (Settings.UpdateRepo, "owner/name") once a day. A
/// release whose tag is a newer version (v1.2.0) and that has a .zip of the published folder
/// attached is offered in the shell; installing it downloads the zip, quits DeskShell and lets the
/// new copy install itself over the old one. Nothing is checked while UpdateRepo is empty.
/// </summary>
public static class UpdateService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static System.Windows.Threading.DispatcherTimer? _timer;

    public static UpdateInfo? Available { get; private set; }

    static UpdateService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskShell/{Installer.VersionText}");

    public static void Start()
    {
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        // Not right at startup: there's enough going on then.
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => CheckAsync(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    public static void Stop() => _timer?.Stop();

    /// <summary>Returns a message for the settings window ("Up to date", "1.2.0 is available"…).</summary>
    public static async Task<string> CheckAsync()
    {
        var repo = App.Settings.UpdateRepo.Trim().Trim('/');
        if (repo.Length == 0) return "No update source set";
        if (GameMode.Active) return "Not checking during a game";
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (!response.IsSuccessStatusCode) return $"GitHub said {(int)response.StatusCode} for {repo}";
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return $"Latest release \"{tag}\" isn't a version number";
            var zip = root.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .FirstOrDefault(u => u != null && u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";

            Available = version > Installer.Version && zip != null ? new UpdateInfo(version, tag, zip, page) : null;
            if (version <= Installer.Version) return $"Up to date ({Installer.VersionText})";
            if (zip == null) return $"{tag} has no .zip attached";
            Log.Info($"Update available: {tag}");
            return $"{tag} is available";
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            return "Couldn't reach GitHub";
        }
    }

    /// <summary>Downloads the update and hands over to it. Returns an error message, or null once DeskShell is quitting.</summary>
    public static async Task<string?> InstallAsync()
    {
        var update = Available;
        if (update == null) return "No update available";
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), $"DeskShell-{update.Version}");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            var zipPath = Path.Combine(folder, "update.zip");
            await using (var download = await Http.GetStreamAsync(update.DownloadUrl))
            await using (var file = File.Create(zipPath))
                await download.CopyToAsync(file);

            var extracted = Path.Combine(folder, "files");
            ZipFile.ExtractToDirectory(zipPath, extracted);
            // The zip may hold the files directly or inside one folder.
            var exe = Directory.GetFiles(extracted, "DeskShell.exe", SearchOption.AllDirectories)
                .OrderBy(p => p.Length).FirstOrDefault();
            if (exe == null) return "The update didn't contain DeskShell.exe";

            Log.Info($"Installing update {update.Tag} from {exe}");
            // The new copy waits for this one to quit (it asks us to), copies itself over and starts.
            Process.Start(new ProcessStartInfo(exe, "--update") { UseShellExecute = false, WorkingDirectory = folder });
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            return $"Update failed: {ex.Message}";
        }
    }
}
