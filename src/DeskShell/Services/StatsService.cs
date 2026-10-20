using System.Runtime.InteropServices;

namespace DeskShell.Services;

public sealed record SystemStats(double Cpu, double Ram, double RamUsedGb, double RamTotalGb, double? Gpu);

/// <summary>CPU, memory and GPU load. Only sampled while the shell is open.</summary>
internal sealed class StatsService : IDisposable
{
    long _lastIdle, _lastTotal;
    IntPtr _query, _gpuCounter;

    public StatsService()
    {
        SampleCpu();
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0 &&
                PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) == 0)
                PdhCollectQueryData(_query);
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
        return new SystemStats(cpu, mem.dwMemoryLoad, used, total, SampleGpu());
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
            if (PdhCollectQueryData(_query) != 0) return null;
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
}
