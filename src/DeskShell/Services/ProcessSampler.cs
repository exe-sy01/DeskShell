using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskShell.Services;

/// <summary>One process at one moment. Rates are per second since the last sample.</summary>
public sealed record ProcessSample(int Pid, int ParentPid, string Name, string? Path, double Cpu, long Memory, double Io, double Gpu,
    int Threads, int Handles, long Started);

/// <summary>The whole machine at one moment.</summary>
public sealed record MachineSample(double Cpu, long MemoryUsed, long MemoryTotal, long Committed, long CommitLimit,
    double DiskActive, double DiskRead, double DiskWrite, double NetDown, double NetUp, double? Gpu,
    int Processes, int Threads, int Handles, IReadOnlyList<ProcessSample> List);

/// <summary>
/// Reads every process's CPU, memory and I/O in one call (no per-process handles, so it also sees
/// protected processes), plus GPU use from Windows' performance counters. Only runs while the
/// Task Manager is open.
/// </summary>
internal sealed class ProcessSampler : IDisposable
{
    readonly Dictionary<(int Pid, long Started), (long Cpu, long Io)> _last = new();
    readonly Dictionary<(int Pid, long Started), string?> _paths = new();
    readonly int _cores = Environment.ProcessorCount;
    long _lastTick;
    long _lastIdle, _lastTotal;
    long _lastNetDown, _lastNetUp;
    IntPtr _query, _gpu, _diskIdle, _diskRead, _diskWrite;
    byte[] _buffer = new byte[1 << 20];

    public ProcessSampler()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
            {
                if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpu) != 0) _gpu = IntPtr.Zero;
                if (PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out _diskIdle) != 0) _diskIdle = IntPtr.Zero;
                if (PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _diskRead) != 0) _diskRead = IntPtr.Zero;
                if (PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _diskWrite) != 0) _diskWrite = IntPtr.Zero;
                PdhCollectQueryData(_query);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Task Manager: performance counters unavailable", ex);
        }
        (_lastNetDown, _lastNetUp) = NetworkTotals();
        GetSystemTimes(out _lastIdle, out var kernel, out var user);
        _lastTotal = kernel + user;
    }

    public MachineSample Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastTick == 0 ? 1 : Math.Max(0.05, (now - _lastTick) / (double)Stopwatch.Frequency);
        _lastTick = now;

        // CPU for the whole machine
        GetSystemTimes(out var idle, out var kernel, out var user);
        var total = kernel + user;
        var dTotal = total - _lastTotal;
        var cpu = dTotal <= 0 ? 0 : Math.Clamp(100.0 * (dTotal - (idle - _lastIdle)) / dTotal, 0, 100);
        _lastIdle = idle;
        _lastTotal = total;

        // Counters: GPU per process, disk for the machine
        var gpuByPid = new Dictionary<int, double>();
        double? gpuTotal = null;
        double diskActive = 0, diskRead = 0, diskWrite = 0;
        if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
        {
            if (_gpu != IntPtr.Zero) gpuTotal = ReadGpu(gpuByPid);
            diskActive = Math.Clamp(100 - Value(_diskIdle), 0, 100);
            diskRead = Value(_diskRead);
            diskWrite = Value(_diskWrite);
        }

        // Network for the machine
        var (down, up) = NetworkTotals();
        var netDown = Math.Max(0, down - _lastNetDown) / seconds;
        var netUp = Math.Max(0, up - _lastNetUp) / seconds;
        _lastNetDown = down;
        _lastNetUp = up;

        // Every process
        var list = new List<ProcessSample>();
        var seen = new HashSet<(int, long)>();
        int threads = 0, handles = 0;
        foreach (var p in ReadProcesses())
        {
            threads += p.Threads;
            handles += p.Handles;
            if (p.Pid == 0) continue; // System Idle Process
            var key = (p.Pid, p.Started);
            seen.Add(key);
            var cpuTime = p.User + p.Kernel;
            var io = p.Read + p.Write + p.Other;
            double procCpu = 0, procIo = 0;
            if (_last.TryGetValue(key, out var before))
            {
                procCpu = Math.Clamp((cpuTime - before.Cpu) / (seconds * 10_000_000.0 * _cores) * 100, 0, 100);
                procIo = Math.Max(0, io - before.Io) / seconds;
            }
            _last[key] = (cpuTime, io);
            if (!_paths.TryGetValue(key, out var path)) _paths[key] = path = ImagePath(p.Pid);
            list.Add(new ProcessSample(p.Pid, p.Parent, p.Name, path, procCpu, p.Private, procIo,
                gpuByPid.TryGetValue(p.Pid, out var g) ? Math.Clamp(g, 0, 100) : 0, p.Threads, p.Handles, p.Started));
        }
        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _last.Remove(gone);
            _paths.Remove(gone);
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        var perf = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
        GetPerformanceInfo(out perf, perf.cb);
        var page = (long)perf.PageSize;

        return new MachineSample(cpu, (long)(mem.ullTotalPhys - mem.ullAvailPhys), (long)mem.ullTotalPhys,
            (long)perf.CommitTotal * page, (long)perf.CommitLimit * page,
            diskActive, diskRead, diskWrite, netDown, netUp, gpuTotal, list.Count, threads, handles, list);
    }

    // ---------- processes ----------

    readonly record struct RawProcess(int Pid, int Parent, string Name, long Private, long User, long Kernel, long Started,
        int Threads, int Handles, long Read, long Write, long Other);

    /// <summary>NtQuerySystemInformation(SystemProcessInformation): one snapshot of every process.</summary>
    List<RawProcess> ReadProcesses()
    {
        var result = new List<RawProcess>(400);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            try
            {
                var status = NtQuerySystemInformation(5, handle.AddrOfPinnedObject(), _buffer.Length, out var needed);
                if (status == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
                {
                    _buffer = new byte[Math.Max(needed, _buffer.Length) + (64 << 10)];
                    continue;
                }
                if (status != 0) return result;

                var b = handle.AddrOfPinnedObject();
                var offset = 0;
                while (true)
                {
                    var e = b + offset;
                    var nameLength = Marshal.ReadInt16(e, 0x38);
                    var namePtr = Marshal.ReadIntPtr(e, 0x40);
                    var pid = (int)Marshal.ReadIntPtr(e, 0x50);
                    var name = nameLength > 0 && namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr, nameLength / 2)
                        : pid == 0 ? "System Idle Process" : "System";
                    result.Add(new RawProcess(pid, (int)Marshal.ReadIntPtr(e, 0x58), name,
                        Marshal.ReadInt64(e, 0x08), Marshal.ReadInt64(e, 0x28), Marshal.ReadInt64(e, 0x30), Marshal.ReadInt64(e, 0x20),
                        Marshal.ReadInt32(e, 0x04), Marshal.ReadInt32(e, 0x60),
                        Marshal.ReadInt64(e, 0xE8), Marshal.ReadInt64(e, 0xF0), Marshal.ReadInt64(e, 0xF8)));
                    var next = Marshal.ReadInt32(e, 0);
                    if (next == 0) break;
                    offset += next;
                }
                return result;
            }
            finally
            {
                handle.Free();
            }
        }
        return result;
    }

    static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
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

    // ---------- counters ----------

    /// <summary>
    /// GPU use per process, as Task Manager counts it: each engine type's use added up, then the
    /// busiest engine type. The machine's figure is the busiest engine type over all processes.
    /// </summary>
    double ReadGpu(Dictionary<int, double> byPid)
    {
        var perEngine = new Dictionary<(int Pid, string Engine), double>();
        var engines = new Dictionary<string, double>();
        foreach (var (name, value) in Array(_gpu))
        {
            // pid_1234_luid_0x00000000_0x0000D1E5_phys_0_eng_0_engtype_3D
            if (!name.StartsWith("pid_")) continue;
            var end = name.IndexOf('_', 4);
            if (end < 0 || !int.TryParse(name.AsSpan(4, end - 4), out var pid)) continue;
            var typeAt = name.IndexOf("engtype_", StringComparison.Ordinal);
            var engine = typeAt < 0 ? "" : name[(typeAt + 8)..];
            perEngine[(pid, engine)] = perEngine.GetValueOrDefault((pid, engine)) + value;
            engines[engine] = engines.GetValueOrDefault(engine) + value;
        }
        foreach (var ((pid, _), value) in perEngine)
            byPid[pid] = Math.Max(byPid.GetValueOrDefault(pid), value);
        return Math.Clamp(engines.Count == 0 ? 0 : engines.Values.Max(), 0, 100);
    }

    static double Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return 0;
        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var value) == 0 && value.CStatus == 0 ? value.doubleValue : 0;
    }

    static List<(string Name, double Value)> Array(IntPtr counter)
    {
        var list = new List<(string, double)>();
        uint size = 0;
        PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
        if (size == 0) return list;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out var count, buffer) != 0) return list;
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.CStatus == 0) list.Add((Marshal.PtrToStringUni(item.szName) ?? "", item.doubleValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    static (long Down, long Up) NetworkTotals()
    {
        long down = 0, up = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                down += stats.BytesReceived;
                up += stats.BytesSent;
            }
        }
        catch
        {
            // an adapter vanished mid-read
        }
        return (down, up);
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
    struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr szName; public uint CStatus; public double doubleValue; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returned);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("psapi.dll")] static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
}
