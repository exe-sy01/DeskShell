using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace DeskShell;

/// <summary>
/// Per-user install, no admin needed: copies DeskShell to %LOCALAPPDATA%\Programs\DeskShell, adds a
/// Start menu shortcut and an entry in Settings → Apps → Installed apps. Run as
/// "DeskShell.exe --install" (or pick Install in settings) and "DeskShell.exe --uninstall".
/// Settings in %APPDATA%\DeskShell are left alone, so a reinstall picks them up again.
/// </summary>
public static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskShell";
    const string QuitEventName = "DeskShell.QuitRequest";

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DeskShell");

    public static string InstalledExe => Path.Combine(InstallDir, "DeskShell.exe");

    static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DeskShell.lnk");

    public static Version Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => Version.ToString(3);

    /// <summary>This process is the installed copy.</summary>
    public static bool IsInstalledCopy =>
        string.Equals(Path.GetDirectoryName(Environment.ProcessPath), InstallDir, StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>Handles --install / --update / --uninstall. True means the command ran and the process should exit.</summary>
    public static bool RunCommand(string[] args)
    {
        if (args.Length == 0) return false;
        switch (args[0])
        {
            case "--install":
                Install(quiet: false);
                return true;
            case "--update":
                Install(quiet: true);
                return true;
            case "--uninstall":
                Uninstall();
                return true;
            case "--apply-island" when args.Length > 1:
                ApplyIsland(args[1]);
                return true;
            default:
                return false;
        }
    }

    static void Install(bool quiet)
    {
        try
        {
            var source = AppContext.BaseDirectory.TrimEnd('\\');
            if (!string.Equals(source, InstallDir, StringComparison.OrdinalIgnoreCase))
            {
                // A running DeskShell (and its watchdog) holds the files open.
                if (!QuitRunningCopies())
                {
                    Message("DeskShell is still running and didn't quit. Quit it (Ctrl+Alt+Shift+Q) and run the installer again.", MessageBoxImage.Warning);
                    return;
                }
                CopyFolder(source, InstallDir);
            }
            CreateShortcut();
            Register();
            Log.Info($"Installed DeskShell {VersionText} to {InstallDir}");
            if (quiet || Ask($"DeskShell {VersionText} is installed. You'll find it in the Start menu and in Installed apps.\n\nStart it now?"))
                Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Install failed", ex);
            Message($"DeskShell couldn't be installed: {ex.Message}", MessageBoxImage.Error);
        }
    }

    static void Uninstall()
    {
        if (!Ask("Remove DeskShell? Your Windows taskbar comes back. Your settings stay in %APPDATA%\\DeskShell in case you reinstall.")) return;
        try
        {
            if (!QuitRunningCopies())
            {
                Message("DeskShell is still running and didn't quit. Quit it (Ctrl+Alt+Shift+Q) and try again.", MessageBoxImage.Warning);
                return;
            }
            StartupRegistration.Apply(false);
            Files.FolderHandler.Apply(false, InstalledExe);
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            // This exe may be inside the folder, so a short-lived cmd deletes it after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            });
            Log.Info("Uninstalled DeskShell");
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall failed", ex);
            Message($"DeskShell couldn't be removed completely: {ex.Message}", MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Applies an Island Workshop settings file (what its "Copy settings" gives) and restarts DeskShell
    /// with it. A running copy is asked to quit first, so it can't save over the change.
    /// </summary>
    static void ApplyIsland(string path)
    {
        var wasRunning = Process.GetProcessesByName("DeskShell").Any(p => p.Id != Environment.ProcessId);
        if (!QuitRunningCopies())
        {
            Log.Error("Island settings not applied: DeskShell didn't quit");
            return;
        }
        var settings = Settings.Load();
        var problem = IslandWorkshop.Apply(File.ReadAllText(path), settings);
        if (problem != null)
        {
            Log.Error("Island settings not applied: " + problem);
        }
        else
        {
            settings.Save();
            Log.Info("Applied island settings from " + path);
        }
        if (wasRunning)
            Process.Start(new ProcessStartInfo(IsInstalled ? InstalledExe : Environment.ProcessPath!) { UseShellExecute = true });
    }

    /// <summary>Asks a running DeskShell to quit (cleanly, so it brings the taskbar back) and waits for it.</summary>
    static bool QuitRunningCopies()
    {
        if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
            using (quit) quit.Set();
        if (EventWaitHandle.TryOpenExisting(Files.FilesHost.QuitEventName, out var quitFiles))
            using (quitFiles) quitFiles.Set();
        var me = Environment.ProcessId;
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline)
        {
            var others = Process.GetProcessesByName("DeskShell").Where(p => p.Id != me).ToList();
            if (others.Count == 0) return true;
            foreach (var p in others) p.Dispose();
            Thread.Sleep(200);
        }
        // One that's stuck while quitting is ended; its watchdog brings Windows' taskbar back.
        foreach (var p in Process.GetProcessesByName("DeskShell").Where(p => p.Id != me))
        {
            try
            {
                Log.Error($"DeskShell {p.Id} didn't quit in time; ending it");
                p.Kill();
                p.WaitForExit(3000);
            }
            catch
            {
                // already gone
            }
            p.Dispose();
        }
        Thread.Sleep(1000);
        return !Process.GetProcessesByName("DeskShell").Any(p => p.Id != me);
    }

    /// <summary>Called by the running app: quits when an installer or updater asks it to.</summary>
    public static void ListenForQuitRequests(Action quit)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => quit(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = InstalledExe;
        link.WorkingDirectory = InstallDir;
        link.Description = "DeskShell";
        link.Save();
    }

    static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "DeskShell");
        key.SetValue("DisplayVersion", VersionText);
        key.SetValue("Publisher", "DeskShell");
        key.SetValue("DisplayIcon", $"{InstalledExe},0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var size = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
    }

    static bool Ask(string text) =>
        MessageBox.Show(text, "DeskShell", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    static void Message(string text, MessageBoxImage image) =>
        MessageBox.Show(text, "DeskShell", MessageBoxButton.OK, image);
}
