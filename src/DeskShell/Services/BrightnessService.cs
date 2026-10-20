using System.Management;
using System.Runtime.InteropServices;
using ManagedShell.AppBar;

namespace DeskShell.Services;

/// <summary>
/// Screen brightness. Laptop panels go through WMI; external monitors through DDC/CI,
/// which the monitor has to support (most do, some have it switched off in their menu).
/// All calls are slow, so they run off the UI thread.
/// </summary>
internal static class BrightnessService
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Average brightness 0-100 across monitors, or null if none can be controlled.</summary>
    public static Task<int?> GetAsync() => Task.Run(async () =>
    {
        await Gate.WaitAsync();
        try
        {
            var values = new List<int>();
            if (ReadLaptop() is { } laptop) values.Add(laptop);
            values.AddRange(ForEachExternal(m =>
                GetMonitorBrightness(m, out var min, out var cur, out var max) && max > min
                    ? (int?)(int)Math.Round((cur - min) * 100.0 / (max - min))
                    : null));
            return values.Count == 0 ? (int?)null : (int)values.Average();
        }
        finally
        {
            Gate.Release();
        }
    });

    public static Task SetAsync(int percent) => Task.Run(async () =>
    {
        percent = Math.Clamp(percent, 0, 100);
        await Gate.WaitAsync();
        try
        {
            WriteLaptop(percent);
            ForEachExternal(m =>
            {
                if (GetMonitorBrightness(m, out var min, out _, out var max) && max > min)
                    SetMonitorBrightness(m, (uint)(min + (max - min) * percent / 100.0));
                return (int?)null;
            });
        }
        finally
        {
            Gate.Release();
        }
    });

    static int? ReadLaptop()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in searcher.Get())
                return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch
        {
            // Desktops have no WMI brightness class.
        }
        return null;
    }

    static void WriteLaptop(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in searcher.Get())
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
        }
        catch
        {
        }
    }

    static List<int> ForEachExternal(Func<IntPtr, int?> action)
    {
        var results = new List<int>();
        foreach (var screen in AppBarScreen.FromAllScreens())
        {
            try
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(screen.HMonitor, out var count) || count == 0) continue;
                var monitors = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(screen.HMonitor, count, monitors)) continue;
                try
                {
                    foreach (var m in monitors)
                        if (action(m.hPhysicalMonitor) is { } value) results.Add(value);
                }
                finally
                {
                    DestroyPhysicalMonitors(count, monitors);
                }
            }
            catch (Exception ex)
            {
                Log.Error("DDC/CI brightness call failed", ex);
            }
        }
        return results;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
}
