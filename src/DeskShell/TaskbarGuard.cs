using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskShell;

/// <summary>
/// Makes sure the Windows taskbar always comes back, even if DeskShell crashes or is killed.
/// The main process writes a marker file and starts a small watchdog copy of itself. A clean
/// exit deletes the marker; if the watchdog sees the main process die while the marker is still
/// its parent's, it shows the taskbar again and gives Windows back the screen space.
/// DeskShell never changes the taskbar's own settings, so there is nothing else to undo.
/// </summary>
internal static class TaskbarGuard
{
    static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskShell", "taskbar-hidden.txt");

    /// <summary>Call before hiding the taskbar.</summary>
    public static void Arm()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, Environment.ProcessId.ToString());
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--watchdog {Environment.ProcessId}") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the taskbar watchdog", ex);
        }
    }

    /// <summary>Call after a clean shutdown has restored the taskbar.</summary>
    public static void Disarm()
    {
        try { File.Delete(MarkerPath); } catch { }
    }

    /// <summary>Entry point for the watchdog process.</summary>
    public static void RunWatchdog(int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Parent already gone.
        }

        // Give a clean shutdown a moment to delete the marker.
        Thread.Sleep(1500);
        // Only act if the marker is still our parent's; a newer DeskShell may already own it.
        if (MarkerOwner() != parentPid) return;

        Log.Info("DeskShell exited without cleaning up; restoring the Windows taskbar");
        Restore();
    }

    public static void Restore()
    {
        try
        {
            TaskbarHider.ShowAll();
            ResetWorkAreas();

            // Ask tray apps to re-add their icons to Explorer's tray.
            PostMessage(Native.HWND_BROADCAST, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Restoring the taskbar failed", ex);
        }
        finally
        {
            Disarm();
        }
    }

    /// <summary>Hands every monitor's full area back and tells Explorer, so it re-reserves its taskbar space.</summary>
    public static void ResetWorkAreas()
    {
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var full = info.rcMonitor;
                SystemParametersInfo(0x002F /* SPI_SETWORKAREA */, 0, ref full, 0x0002 /* SPIF_SENDCHANGE */);
            }
            return true;
        }, IntPtr.Zero);
    }

    static int MarkerOwner()
    {
        try { return File.Exists(MarkerPath) && int.TryParse(File.ReadAllText(MarkerPath).Trim(), out var pid) ? pid : 0; }
        catch { return 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, ref RECT rect, int winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
