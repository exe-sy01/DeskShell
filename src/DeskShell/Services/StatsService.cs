using System.Runtime.InteropServices;

namespace DeskShell.Services;

public sealed record SystemStats(double Cpu, double Ram, double RamUsedGb, double RamTotalGb, double? Gpu, double Disk = 0);

/// <summary>CPU, memory and GPU load. Only sampled while the shell is open.</summary>
internal sealed class StatsService : IDisposable
{
    long _lastIdle, _lastTotal;
    IntPtr _query, _gpuCounter, _diskIdle;
    readonly bool _withGpu;

    /// <param name="withGpu">GPU counters cost a little more to read; the notch only needs CPU, memory and disk.</param>
    public StatsService(bool withGpu = true)
    {
        _withGpu = withGpu;
        SampleCpu();
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
            {
                if (PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out _diskIdle) != 0) _diskIdle = IntPtr.Zero;
                if (!withGpu || PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
                    _gpuCounter = IntPtr.Zero;
                PdhCollectQueryData(_query);
            }
            else
                _gpuCounter = IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Log.Error("GPU counters unavailable", ex);
            _gpuCounter = IntPtr.Zero;
        }
    }

    public SystemStats Sample()
    {
        var cpu = SampleCpu();
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        var total = mem.ullTotalPhys / 1073741824.0;
        var used = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
        var collected = _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;
        return new SystemStats(cpu, mem.dwMemoryLoad, used, total, collected ? SampleGpu() : null, collected ? SampleDisk() : 0);
    }

    double SampleDisk()
    {
        if (_diskIdle == IntPtr.Zero) return 0;
        return PdhGetFormattedCounterValue(_diskIdle, PDH_FMT_DOUBLE, out _, out var value) == 0 && value.CStatus == 0
            ? Math.Clamp(100 - value.doubleValue, 0, 100) : 0;
    }

    double SampleCpu()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        long total = kernel + user; // kernel time includes idle time
        var dIdle = idle - _lastIdle;
        var dTotal = total - _lastTotal;
        _lastIdle = idle;
        _lastTotal = total;
        return dTotal <= 0 ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }

    double? SampleGpu()
    {
        if (_gpuCounter == IntPtr.Zero) return null;
        try
        {
            uint size = 0;
            PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
            if (size == 0) return 0;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref size, out var count, buffer) != 0) return null;
                var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                double sum = 0;
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                    if (item.CStatus == 0) sum += item.doubleValue;
                }
                return Math.Clamp(sum, 0, 100);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    const uint PDH_FMT_DOUBLE = 0x00000200;

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
}
