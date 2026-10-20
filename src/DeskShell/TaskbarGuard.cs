using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ManagedShell.AppBar;
using static ManagedShell.Interop.NativeMethods;

namespace DeskShell;

/// <summary>
/// Makes sure the Windows taskbar always comes back, even if DeskShell crashes or is killed.
/// The main process records the taskbar's original state in a marker file and starts a
/// small watchdog copy of itself. A clean exit deletes the marker; if the watchdog sees the
/// main process die while the marker still exists, it restores the taskbar.
/// </summary>
internal static class TaskbarGuard
{
    static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskShell", "taskbar-hidden.txt");

    /// <summary>Call before hiding the taskbar.</summary>
    public static void Arm(ExplorerHelper explorer)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            // If a marker survives from a crash, it already holds the user's real state; keep it.
            if (!File.Exists(MarkerPath))
                File.WriteAllText(MarkerPath, ((int)explorer.GetTaskbarState()).ToString());

            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}") { UseShellExecute = false });
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
        if (!File.Exists(MarkerPath)) return;

        Log.Info("DeskShell exited without cleaning up; restoring the Windows taskbar");
        Restore();
    }

    public static void Restore()
    {
        try
        {
            if (File.Exists(MarkerPath) && int.TryParse(File.ReadAllText(MarkerPath), out var state))
                new ExplorerHelper().SetTaskbarState((ABState)state);

            var tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero) Native.ShowWindow(tray, Native.SW_SHOW);

            var secondary = IntPtr.Zero;
            while ((secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                Native.ShowWindow(secondary, Native.SW_SHOW);

            // Ask tray apps to re-add their icons to Explorer's tray.
            var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            PostMessage(Native.HWND_BROADCAST, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
