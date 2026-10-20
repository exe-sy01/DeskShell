using System.IO;
using System.Xml.Linq;
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
    static readonly string Source = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");
    static readonly string Copy = Path.Combine(Path.GetTempPath(), "DeskShell", "wpn");

    public static Task<List<ToastItem>> ReadAsync(int max = 20) => Task.Run(() =>
    {
        var items = new List<ToastItem>();
        try
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

            using var db = new SqliteConnection($"Data Source={Path.Combine(Copy, "wpndatabase.db")};Pooling=False");
            db.Open();
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
