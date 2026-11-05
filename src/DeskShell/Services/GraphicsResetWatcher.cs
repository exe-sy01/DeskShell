using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskShell.Services;

/// <summary>
/// Notices when the graphics driver resets (a "driver timeout"). WPF doesn't always come back from
/// one: the island and docks stay blank or stop responding. This holds an empty device on the
/// graphics card, made through Windows' kernel graphics calls (no Direct3D, so no driver DLLs,
/// threads or memory). A driver reset marks every device on the card as reset, so when this one
/// is, DeskShell starts itself again with fresh windows.
/// </summary>
public static class GraphicsResetWatcher
{
    static Timer? _timer;
    static uint _device;
    static int _failures;
    static bool _fired;

    /// <summary>Raised once, on a timer thread, when the driver has reset.</summary>
    public static event Action? Reset;

    public static void Start() => _timer ??= new Timer(_ => Check(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    static void Check()
    {
        if (_fired) return;
        try
        {
            if (_device == 0)
            {
                // Couldn't make one yet (the card may be busy resetting); keep trying for a while.
                if (_failures < 12 && !Create()) _failures++;
                return;
            }
            var (status, state) = ExecutionState(_device);
            // 1 = active. Anything else (reset, hung, stopped, faulted), or the call failing because
            // the driver was restarted, means the card was reset under us.
            if (status == 0 && state == 1) return;
            _fired = true;
            Log.Info($"Graphics driver reset (status 0x{status:X8}, state {state}); restarting DeskShell");
            Reset?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Graphics reset check failed", ex);
        }
    }

    static bool Create()
    {
        var primary = MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(primary, ref info)) return false;

        var open = new OPENADAPTERFROMGDIDISPLAYNAME { DeviceName = info.szDevice };
        var status = D3DKMTOpenAdapterFromGdiDisplayName(ref open);
        var create = new CREATEDEVICE { hAdapter = open.hAdapter };
        if (status == 0) status = D3DKMTCreateDevice(ref create);
        if (status != 0 || create.hDevice == 0)
        {
            if (_failures == 0) Log.Error($"Graphics reset watcher couldn't start (0x{status:X8})");
            return false;
        }
        _device = create.hDevice;
        Log.Info($"Watching for graphics driver resets on {info.szDevice}");
        return true;
    }

    static (int Status, int State) ExecutionState(uint device)
    {
        // D3DKMT_GETDEVICESTATE: the device, the kind of state (1 = execution), then a union of answers.
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (var i = 0; i < 256; i += 8) Marshal.WriteInt64(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, (int)device);
            Marshal.WriteInt32(buffer, 4, 1);
            var status = D3DKMTGetDeviceState(buffer);
            return (status, Marshal.ReadInt32(buffer, 8));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Starts a helper copy that waits for this one to quit (ending it if it hangs), then starts
    /// DeskShell again once the screens have settled.
    /// </summary>
    public static void StartReplacement()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "--restart", Environment.ProcessId.ToString() } });
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't start DeskShell again", ex);
        }
    }

    /// <summary>"DeskShell.exe --restart pid": the helper's side.</summary>
    public static void RunReplacement(int pid)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            if (!old.WaitForExit(15000))
            {
                Log.Error("DeskShell didn't quit after the graphics reset; ending it");
                old.Kill();
                old.WaitForExit(5000);
            }
        }
        catch (ArgumentException)
        {
            // already gone
        }
        catch (Exception ex)
        {
            Log.Error("Waiting for DeskShell to quit failed", ex);
        }
        Thread.Sleep(2000); // let the driver and screens settle
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public int l1, t1, r1, b1, l2, t2, r2, b2;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OPENADAPTERFROMGDIDISPLAYNAME
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public uint hAdapter;
        public uint LuidLow;
        public int LuidHigh;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CREATEDEVICE
    {
        public ulong hAdapter; // a union with a pointer
        public uint Flags;
        public uint hDevice;
        public IntPtr pCommandBuffer;
        public uint CommandBufferSize;
        public IntPtr pAllocationList;
        public uint AllocationListSize;
        public IntPtr pPatchLocationList;
        public uint PatchLocationListSize;
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long point, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromGdiDisplayName(ref OPENADAPTERFROMGDIDISPLAYNAME args);
    [DllImport("gdi32.dll")] static extern int D3DKMTCreateDevice(ref CREATEDEVICE args);
    [DllImport("gdi32.dll")] static extern int D3DKMTGetDeviceState(IntPtr args);
}
