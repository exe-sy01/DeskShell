using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeskShell.Files;

/// <summary>
/// The file explorer runs in its own DeskShell process ("DeskShell.exe --files"), so a misbehaving
/// shell extension or thumbnail handler can't take the island and taskbar down with it. That
/// process stays in the background once started, so later windows open instantly; DeskShell and
/// folder links hand it requests over a named pipe.
/// </summary>
public static class FilesHost
{
    public const string QuitEventName = "DeskShell.Files.QuitRequest";
    static string Suffix => $"{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";
    static string PipeName => "DeskShell.Files." + Suffix;

    public sealed record Request(string? Path, string? Select, bool NewWindow, bool Background);

    /// <summary>The running explorer process's app, when this is that process.</summary>
    public static FilesApp? App { get; private set; }

    // ---------- the asking side (DeskShell, or a folder link) ----------

    /// <summary>Opens a folder (or Home) in DeskShell's explorer, starting it if needed. Safe to call from the UI thread.</summary>
    public static void Open(string? path = null, string? select = null, bool newWindow = false) =>
        Task.Run(() =>
        {
            var request = new Request(path, select, newWindow, false);
            if (TrySend(request)) return;
            Start(request);
        });

    /// <summary>Starts the explorer process in the background, so the first window opens fast.</summary>
    public static void Preload() =>
        Task.Run(() =>
        {
            if (!IsRunning) Start(new Request(null, null, false, true));
        });

    static bool IsRunning => File.Exists(@"\\.\pipe\" + PipeName);

    static void Start(Request request)
    {
        var args = new List<string> { "--files" };
        if (request.Background) args.Add("--background");
        if (request.NewWindow) args.Add("--new-window");
        if (request.Select != null) { args.Add("--select"); args.Add(request.Select); }
        if (request.Path != null) args.Add(request.Path);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            Process.Start(start);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't start the file explorer", ex);
        }
    }

    static bool TrySend(Request request)
    {
        if (!IsRunning) return false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            // We were just asked by the user, so let the explorer bring its window to the front.
            AllowSetForegroundWindow(-1);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(JsonSerializer.Serialize(request));
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't reach the file explorer", ex);
            return false;
        }
    }

    /// <summary>Windows' own File Explorer, for places that aren't folders (Recycle Bin, Control Panel…).</summary>
    public static void OpenInExplorer(string target)
    {
        // If Explorer sent it straight back to us (we're Windows' folder opener), stop rather than loop.
        lock (Handed)
        {
            var now = Environment.TickCount64;
            if (Handed.TryGetValue(target, out var last) && now - last < 4000)
            {
                Log.Error($"{target} came back from Explorer; not handing it over again");
                return;
            }
            Handed[target] = now;
        }
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = false, ArgumentList = { target } });
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't open {target} in Explorer", ex);
        }
    }

    static readonly Dictionary<string, long> Handed = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    // ---------- the explorer process ----------

    /// <summary>
    /// "DeskShell.exe --files [--background] [--new-window] [--select file] [folder]", and
    /// "DeskShell.exe --open folder" from Windows when DeskShell opens all folders.
    /// </summary>
    public static void Run(string[] args)
    {
        string? path = null, select = null;
        bool newWindow = false, background = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--new-window": newWindow = true; break;
                case "--background": background = true; break;
                case "--select" when i + 1 < args.Length: select = args[++i]; break;
                default: path = args[i]; break;
            }
        }
        if (select != null && path == null) path = Path.GetDirectoryName(select);
        var request = new Request(path, select, newWindow, background);

        if (TrySend(request)) return;
        using var mutex = new Mutex(true, "DeskShell.Files." + Suffix, out var first);
        if (!first)
        {
            // Another one is just starting; give it a moment to listen.
            for (var i = 0; i < 30; i++)
            {
                Thread.Sleep(100);
                if (TrySend(request)) return;
            }
            return;
        }

        Log.Prefix = "[Files] ";
        var app = new FilesApp();
        App = app;
        app.Startup += (_, _) => app.Begin(request, PipeName);
        app.Run();
        GC.KeepAlive(mutex);
    }
}

/// <summary>The explorer process's WPF application: its windows, the request pipe and staying lean in the background.</summary>
public sealed class FilesApp : Application
{
    readonly List<FilesWindow> _windows = new();
    FileSystemWatcher? _settingsWatcher;
    FilesWindow? _lastActive;
    DispatcherTimer? _idle;

    public FilesApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DeskShell;component/Theme.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DeskShell;component/Files/FilesMenus.xaml") });
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled error in the file explorer", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal error in the file explorer", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Background task failed", e.Exception);
            e.SetObserved();
        };
    }

    public void Begin(FilesHost.Request first, string pipeName)
    {
        // This process doesn't own the settings: read the theme from the file, and follow it when it's saved.
        ThemeSettings? lastTheme = null;
        ThemeManager.Source = () => lastTheme = Settings.ReadOnly()?.Theme ?? lastTheme ?? new ThemeSettings();
        ThemeManager.Start();
        try
        {
            var watcher = new FileSystemWatcher(Settings.Folder, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
            var debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                ThemeManager.Refresh();
            }, Dispatcher);
            debounce.Stop();
            watcher.Changed += (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
            _settingsWatcher = watcher;
        }
        catch (Exception ex)
        {
            Log.Error("Can't follow theme changes", ex);
        }
        // Like the island, the explorer is always dark; menus opened from code use these app-wide colours.
        ThemeManager.Use(Resources);
        ShellNative.UseDarkMenus();
        FileView.Load();
        Handle(first);
        new Thread(() => Listen(pipeName)) { IsBackground = true, Name = "DeskShell files requests" }.Start();

        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, FilesHost.QuitEventName);
        ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => Dispatcher.BeginInvoke(Quit), null, Timeout.Infinite, true);

        // In the background with no windows: give memory back, and leave if DeskShell itself has gone.
        _idle = new DispatcherTimer(TimeSpan.FromMinutes(2), DispatcherPriority.Background, (_, _) =>
        {
            if (_windows.Count > 0) return;
            if (!Mutex.TryOpenExisting("DeskShell.SingleInstance", out var shell))
            {
                Quit();
                return;
            }
            shell.Dispose();
        }, Dispatcher);
        if (_windows.Count == 0) TrimSoon();
    }

    void Listen(string pipeName)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe);
                var line = reader.ReadLine();
                if (line == null) continue;
                var request = JsonSerializer.Deserialize<FilesHost.Request>(line);
                if (request != null) Dispatcher.BeginInvoke(() => Handle(request));
            }
            catch (Exception ex)
            {
                Log.Error("File explorer request failed", ex);
                Thread.Sleep(200);
            }
        }
    }

    void Handle(FilesHost.Request request)
    {
        if (request.Background) return;
        // Asked for a specific folder: a new tab in the window you used last. Otherwise a new window.
        var target = request.NewWindow || request.Path == null ? null : _lastActive ?? _windows.LastOrDefault();
        if (target == null)
        {
            OpenWindow(request.Path, request.Select);
            return;
        }
        target.OpenTab(request.Path, request.Select);
        Bring(target);
    }

    public void OpenWindow(string? path, string? select = null)
    {
        var window = new FilesWindow();
        _windows.Add(window);
        window.Activated += (_, _) => _lastActive = window;
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (_lastActive == window) _lastActive = _windows.LastOrDefault();
            if (_windows.Count == 0) TrimSoon();
        };
        if (_windows.Count > 1 && _lastActive is { WindowState: WindowState.Normal } previous)
        {
            // Cascade from the last window instead of stacking exactly on top of it.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = previous.Left + 32;
            window.Top = previous.Top + 32;
        }
        window.OpenTab(string.IsNullOrEmpty(path) ? null : path, select);
        window.Show();
        Bring(window);
    }

    static void Bring(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        WindowManagement.WmNative.Activate(new WindowInteropHelper(window).Handle);
    }

    void TrimSoon()
    {
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            if (_windows.Count > 0) return;
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            var info = GC.GetGCMemoryInfo();
            Log.Info($"Idle: managed heap {info.HeapSizeBytes / 1048576} MB, committed {info.TotalCommittedBytes / 1048576} MB");
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }, Dispatcher);
        timer.Start();
    }

    void Quit()
    {
        foreach (var window in _windows.ToList()) window.Close();
        Shutdown();
    }

    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
