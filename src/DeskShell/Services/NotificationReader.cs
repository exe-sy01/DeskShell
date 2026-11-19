using System.IO;
using System.Xml.Linq;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace DeskShell.Services;

public sealed record ToastItem(string AppId, string Title, string Body, DateTime Arrived);

/// <summary>
/// Reads recent notifications from Windows' own notification store. Windows offers no public
/// API for this to normal apps, so we read a copy of the database (read-only, never modified).
/// Notifications can't be dismissed from here; the tile opens Windows' notification center for that.
/// </summary>
internal static class NotificationReader
{
    internal static readonly string Source = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");
    static readonly string Copy = Path.Combine(Path.GetTempPath(), "DeskShell", "wpn");

    static readonly object Gate = new();

    /// <summary>Copies Windows' notification database (it's in use) and opens the copy.</summary>
    static SqliteConnection OpenCopy()
    {
        Directory.CreateDirectory(Copy);
        foreach (var name in new[] { "wpndatabase.db", "wpndatabase.db-wal", "wpndatabase.db-shm" })
        {
            var from = Path.Combine(Source, name);
            var to = Path.Combine(Copy, name);
            if (File.Exists(from))
            {
                using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = new FileStream(to, FileMode.Create, FileAccess.Write);
                src.CopyTo(dst);
            }
            else if (File.Exists(to))
            {
                File.Delete(to);
            }
        }

        var db = new SqliteConnection($"Data Source={Path.Combine(Copy, "wpndatabase.db")};Pooling=False");
        db.Open();
        return db;
    }

    /// <summary>
    /// Store apps' taskbar badges (e.g. Xbox "13"), keyed by app id. Values are a number, or a
    /// word such as "alert" or "newMessage" for apps that show a symbol instead.
    /// </summary>
    public static Task<Dictionary<string, string>> ReadBadgesAsync() => Task.Run(() =>
    {
        var badges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (Gate)
        {
            try
            {
                using var db = OpenCopy();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    SELECT h.PrimaryId, n.Payload
                    FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId
                    WHERE n.Type = 'badge'
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                    try
                    {
                        var value = XDocument.Parse(PayloadText(reader.GetValue(1))).Root?.Attribute("value")?.Value;
                        if (!string.IsNullOrEmpty(value) && value != "none" && value != "0")
                            badges[reader.GetString(0)] = value;
                    }
                    catch
                    {
                        // not a badge we understand
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read app badges", ex);
            }
        }
        return badges;
    });

    /// <summary>Every app Windows has kept notifications for (their notification ids).</summary>
    public static Task<List<string>> ReadAppIdsAsync() => Task.Run(() =>
    {
        var ids = new List<string>();
        lock (Gate)
        try
        {
            using var db = OpenCopy();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT PrimaryId FROM NotificationHandler";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0)) ids.Add(reader.GetString(0));
        }
        catch (Exception ex)
        {
            Log.Error("Could not list notifying apps", ex);
        }
        return ids;
    });

    public static Task<List<ToastItem>> ReadAsync(int max = 20) => Task.Run(() =>
    {
        var items = new List<ToastItem>();
        // Discord's own notifications repeat what the AIChat plugin already shows, with profile pictures.
        var skipDiscord = DiscordLink.CoversNotifications;
        lock (Gate)
        try
        {
            using var db = OpenCopy();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT h.PrimaryId, n.Payload, n.ArrivalTime
                FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId
                WHERE n.Type = 'toast'
                ORDER BY n.ArrivalTime DESC
                LIMIT $max
                """;
            cmd.Parameters.AddWithValue("$max", max);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var appId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (skipDiscord && DiscordLink.IsDiscordApp(appId)) continue;
                var payload = reader.IsDBNull(1) ? "" : PayloadText(reader.GetValue(1));
                var arrived = reader.IsDBNull(2) ? DateTime.MinValue : DateTime.FromFileTimeUtc(reader.GetInt64(2)).ToLocalTime();
                var texts = ParseTexts(payload);
                if (texts.Count == 0) continue;
                items.Add(new ToastItem(appId, texts[0], string.Join(" ", texts.Skip(1)), arrived));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read notifications", ex);
        }
        return items;
    });

    static string PayloadText(object value) => value switch
    {
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        string s => s,
        _ => ""
    };

    static List<string> ParseTexts(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Descendants("text")
                .Select(t => t.Value.Trim())
                .Where(t => t.Length > 0)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}

/// <summary>
/// Raises <see cref="Arrived"/> on the UI thread when a new notification lands in Windows'
/// notification store (watched as files, so nothing polls while nothing arrives).
/// </summary>
internal static class NotificationWatcher
{
    static FileSystemWatcher? _watcher;
    static DispatcherTimer? _debounce;
    static DateTime _newest = DateTime.MinValue;
    static bool _primed;

    public static event Action<ToastItem>? Arrived;

    /// <summary>While true (game mode), changes are ignored.</summary>
    public static bool Paused { get; set; }

    public static void Start()
    {
        if (_watcher != null || !Directory.Exists(NotificationReader.Source)) return;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, async (_, _) =>
        {
            _debounce!.Stop();
            await CheckAsync();
        }, Dispatcher.CurrentDispatcher);
        _debounce.Stop();
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(CheckAsync); // learns the newest one, announces nothing
        try
        {
            _watcher = new FileSystemWatcher(NotificationReader.Source, "wpndatabase.db*")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) => _debounce.Dispatcher.InvokeAsync(Changed);
        }
        catch (Exception ex)
        {
            Log.Error("Can't watch for new notifications", ex);
        }

        // Windows writes a new notification to the store at once but tells file watchers several
        // seconds later; looking at the file's time and size each second (no reading) catches it at once.
        var wal = Path.Combine(NotificationReader.Source, "wpndatabase.db-wal");
        var last = (DateTime.MinValue, -1L);
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (Paused) return;
            try
            {
                var info = new FileInfo(wal);
                var now = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1L);
                if (now == last) return;
                var changed = last.Item2 != -1 || last.Item1 != DateTime.MinValue;
                last = now;
                if (changed) Changed();
            }
            catch
            {
                // the file is being replaced; look again next time
            }
        }, Dispatcher.CurrentDispatcher);
    }

    static DispatcherTimer? _poll;

    static void Changed()
    {
        if (Paused || _debounce == null) return;
        _debounce.Stop();
        _debounce.Start();
    }

    public static void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _poll?.Stop();
        _debounce?.Stop();
    }

    static async Task CheckAsync()
    {
        // A few, since entries without text are skipped
        var latest = (await NotificationReader.ReadAsync(5)).FirstOrDefault();
        // The first look only learns what's already there (even if that's nothing), so it announces nothing.
        var first = !_primed;
        _primed = true;
        if (latest == null) return;
        if (!first && latest.Arrived <= _newest) return;
        _newest = latest.Arrived;
        if (!first) Arrived?.Invoke(latest);
    }
}
