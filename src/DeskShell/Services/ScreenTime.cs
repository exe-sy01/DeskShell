using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DeskShell.Services;

/// <summary>
/// How long each app has been in front today, counted every 10 seconds while you're at the PC
/// (or watching/playing something fullscreen). Kept on this PC only, in %APPDATA%\DeskShell\screentime.json.
/// </summary>
public static class ScreenTime
{
    public sealed class AppTime
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public int Seconds { get; set; }
    }

    sealed class Data
    {
        public string Date { get; set; } = "";
        public Dictionary<string, AppTime> Apps { get; set; } = new();
        /// <summary>Seconds of use in each hour of today.</summary>
        public int[] Hours { get; set; } = new int[24];
        /// <summary>Earlier days' totals, by date, last 14 days.</summary>
        public Dictionary<string, int> Days { get; set; } = new();
    }

    const int Step = 10;
    static readonly string FilePath = System.IO.Path.Combine(Settings.Folder, "screentime.json");
    static readonly object Gate = new();
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);
    static Data _data = new();
    static Timer? _timer;
    static int _unsaved;

    public static void Start()
    {
        if (_timer != null) return;
        try
        {
            if (File.Exists(FilePath)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read screen time", ex);
        }
        RollOver();
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(Step), TimeSpan.FromSeconds(Step));
    }

    public static void Save()
    {
        string json;
        lock (Gate) json = JsonSerializer.Serialize(_data);
        try
        {
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save screen time", ex);
        }
    }

    public static int TodaySeconds
    {
        get { lock (Gate) return _data.Apps.Values.Sum(a => a.Seconds); }
    }

    public static List<AppTime> TopApps(int count)
    {
        lock (Gate)
            return _data.Apps.Values.OrderByDescending(a => a.Seconds).Take(count)
                .Select(a => new AppTime { Name = a.Name, Path = a.Path, Seconds = a.Seconds }).ToList();
    }

    /// <summary>Seconds of use in each hour of today.</summary>
    public static int[] Hours
    {
        get { lock (Gate) return (int[])_data.Hours.Clone(); }
    }

    /// <summary>The last 7 days, oldest first, today last.</summary>
    public static List<(DateTime Day, int Seconds)> Week()
    {
        var today = DateTime.Today;
        lock (Gate)
            return Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).Select(d =>
                (d, d == today ? _data.Apps.Values.Sum(a => a.Seconds) : _data.Days.GetValueOrDefault(d.ToString("yyyy-MM-dd")))).ToList();
    }

    public static string Format(int seconds) =>
        seconds < 60 ? "<1m" : seconds < 3600 ? $"{seconds / 60}m" : $"{seconds / 3600}h {seconds % 3600 / 60}m";

    static void RollOver()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        lock (Gate)
        {
            if (_data.Date == today) return;
            if (_data.Date.Length > 0) _data.Days[_data.Date] = _data.Apps.Values.Sum(a => a.Seconds);
            foreach (var old in _data.Days.Keys.Where(d => string.CompareOrdinal(d, DateTime.Today.AddDays(-14).ToString("yyyy-MM-dd")) < 0).ToList())
                _data.Days.Remove(old);
            _data.Date = today;
            _data.Apps.Clear();
            _data.Hours = new int[24];
        }
    }

    static void Tick()
    {
        try
        {
            RollOver();
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            // Away from the PC (no input for 2 minutes) doesn't count, unless something fullscreen is on.
            var input = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            var idle = GetLastInputInfo(ref input) ? (uint)Environment.TickCount - input.dwTime : 0;
            if (idle > 120_000 && App.Fullscreen?.ForegroundFullscreen != true) return;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return;
            var path = ImagePath((int)pid);
            if (path == null) return;
            var exe = System.IO.Path.GetFileNameWithoutExtension(path);
            if (exe.Equals("LockApp", StringComparison.OrdinalIgnoreCase) || exe.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)) return;

            lock (Gate)
            {
                if (!_data.Apps.TryGetValue(path, out var app))
                    _data.Apps[path] = app = new AppTime { Name = NameOf(path), Path = path };
                app.Seconds += Step;
                _data.Hours[DateTime.Now.Hour] += Step;
            }
            if (++_unsaved >= 12) // every 2 minutes
            {
                _unsaved = 0;
                Save();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Screen time check failed", ex);
        }
    }

    static string NameOf(string path)
    {
        if (Names.TryGetValue(path, out var name)) return name;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim() : System.IO.Path.GetFileNameWithoutExtension(path);
        }
        catch
        {
            name = System.IO.Path.GetFileNameWithoutExtension(path);
        }
        // Some apps describe themselves at length ("Windows Explorer Shell Experience…"); keep it short.
        if (name.Length > 32) name = System.IO.Path.GetFileNameWithoutExtension(path);
        return Names[path] = name;
    }

    static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var text = new StringBuilder(1024);
            var size = text.Capacity;
            return QueryFullProcessImageName(h, 0, text, ref size) ? text.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
