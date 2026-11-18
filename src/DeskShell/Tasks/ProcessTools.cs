using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskShell.Tasks;

/// <summary>
/// Everything worth knowing about one process, for a details panel. Anything Windows wouldn't tell
/// us (usually because the process belongs to another user or is protected) is null.
/// </summary>
public sealed record ProcessDetails(
    int Pid,
    int? ParentPid,
    string Name,
    string? Path,
    string? CommandLine,
    DateTime? Started,
    string? User,
    bool? Elevated,
    ProcessPriorityClass? Priority,
    long? WorkingSet,
    long? PrivateBytes,
    long? PeakWorkingSet,
    int? Handles,
    int? Threads,
    bool? Is32Bit,
    string? Description,
    string? Company,
    string? Version);

/// <summary>
/// The things Task Manager can do to a process beyond ending it: priority, suspend and resume,
/// ending a whole tree, and reading its command line and other details. Nothing here throws:
/// when Windows says no (another user's or a protected process) you get null or false, so the
/// caller can offer to try again as administrator.
/// </summary>
public static class ProcessTools
{
    const uint PROCESS_TERMINATE = 0x0001, PROCESS_SET_INFORMATION = 0x0200, PROCESS_SUSPEND_RESUME = 0x0800,
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004), STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023),
        STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    const int ERROR_INVALID_PARAMETER = 87;

    /// <summary>
    /// The processes we suspended, with their start time so a reused process ID isn't mistaken for
    /// one of them. Windows has no simple "is this suspended" flag, so this is the reliable half of
    /// <see cref="IsSuspended"/>.
    /// </summary>
    static readonly Dictionary<int, long> SuspendedByUs = new();
    static readonly Dictionary<string, string> Accounts = new();
    static readonly object Gate = new();

    // ---------- priority ----------

    /// <summary>Sets the process's priority. "Realtime" quietly becomes "High" unless we're administrator (Windows' rule).</summary>
    public static bool SetPriority(int pid, ProcessPriorityClass priority)
    {
        try
        {
            using var handle = Open(pid, PROCESS_SET_INFORMATION);
            return handle != null && SetPriorityClass(handle, (uint)priority);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The process's priority, or null if Windows won't say.</summary>
    public static ProcessPriorityClass? GetPriority(int pid)
    {
        try
        {
            using var handle = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION);
            if (handle == null) return null;
            var value = GetPriorityClass(handle);
            return value == 0 ? null : (ProcessPriorityClass)value;
        }
        catch
        {
            return null;
        }
    }

    // ---------- suspend and resume ----------

    /// <summary>
    /// Freezes every thread of the process. Suspending twice would need two resumes, so a process
    /// we already suspended is left alone. DeskShell never suspends itself (it would freeze the screen).
    /// </summary>
    public static bool Suspend(int pid)
    {
        if (pid == Environment.ProcessId) return false;
        try
        {
            using var handle = Open(pid, PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION);
            if (handle == null) return false;
            var started = CreationTime(handle) ?? 0;
            lock (Gate)
                if (SuspendedByUs.TryGetValue(pid, out var known) && known == started) return true;
            if (NtSuspendProcess(handle) < 0) return false;
            lock (Gate) SuspendedByUs[pid] = started;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Lets a suspended process run again.</summary>
    public static bool Resume(int pid)
    {
        try
        {
            using var handle = Open(pid, PROCESS_SUSPEND_RESUME);
            if (handle == null) return false;
            if (NtResumeProcess(handle) < 0) return false;
            lock (Gate) SuspendedByUs.Remove(pid);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best effort: true if we suspended it, or if every one of its threads is waiting because it was
    /// suspended (which also catches Store apps Windows has put to sleep).
    /// </summary>
    public static bool IsSuspended(int pid)
    {
        try
        {
            lock (Gate)
                if (SuspendedByUs.TryGetValue(pid, out var started))
                {
                    using var handle = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION);
                    if (handle != null && (CreationTime(handle) ?? 0) == started) return true;
                    SuspendedByUs.Remove(pid); // gone, or the ID now belongs to someone else
                }

            using var process = Process.GetProcessById(pid);
            var threads = process.Threads;
            if (threads.Count == 0) return false;
            foreach (ProcessThread thread in threads)
                if (thread.ThreadState != System.Diagnostics.ThreadState.Wait || thread.WaitReason != ThreadWaitReason.Suspended)
                    return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- ending ----------

    /// <summary>Ends one process at once. True if it's gone (including "it was already gone").</summary>
    public static bool Kill(int pid)
    {
        if (pid <= 4) return false; // Idle and System
        try
        {
            var handle = OpenProcess(PROCESS_TERMINATE, false, pid);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                return error == ERROR_INVALID_PARAMETER; // no such process any more
            }
            using (handle) return TerminateProcess(handle, 1);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ends a process and everything it started, children first so nothing restarts its parent's
    /// work in between. <paramref name="all"/> is the current process list (e.g. from ProcessSampler).
    /// A child that started before its "parent" isn't really its child (Windows reuses process IDs),
    /// so it's spared. DeskShell itself is skipped; end it with App.Quit instead.
    /// Returns the processes that couldn't be ended (usually administrator ones); empty means all done.
    /// </summary>
    public static List<int> KillTree(int pid, IReadOnlyList<(int Pid, int Parent)> all)
    {
        var denied = new List<int>();
        try
        {
            var children = all.Where(p => p.Pid != p.Parent).ToLookup(p => p.Parent, p => p.Pid);
            var order = new List<int>();
            var seen = new HashSet<int>();
            void Walk(int id, long? parentStarted)
            {
                if (!seen.Add(id)) return;
                var started = CreationTime(id);
                if (parentStarted is long ps && started is long s && s < ps) return; // an older process with a reused parent ID
                foreach (var child in children[id]) Walk(child, started);
                order.Add(id);
            }
            Walk(pid, null);

            foreach (var id in order)
            {
                if (id == Environment.ProcessId || id <= 4) continue;
                if (!Kill(id)) denied.Add(id);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't end the process tree of {pid}", ex);
        }
        return denied;
    }

    /// <summary>
    /// Ends processes through Windows' administrator prompt (taskkill). <paramref name="tree"/> also
    /// ends their children. False if you said no or it failed.
    /// </summary>
    public static async Task<bool> KillAsAdministratorAsync(IEnumerable<int> pids, bool tree = false)
    {
        var list = pids.Where(p => p > 4 && p != Environment.ProcessId).Distinct().ToList();
        if (list.Count == 0) return true;
        var args = "/F " + (tree ? "/T " : "") + string.Join(" ", list.Select(p => $"/PID {p}"));
        return await RunElevatedAsync(System.IO.Path.Combine(Environment.SystemDirectory, "taskkill.exe"), args) == 0;
    }

    /// <summary>
    /// Runs a program as administrator (Windows asks first), hidden, and waits for it. Like
    /// ToolsMenu.Run with admin: true, but you get the exit code back, so the caller knows when to look
    /// again and whether it worked. Null means "No" at the prompt or it couldn't start.
    /// </summary>
    public static async Task<int?> RunElevatedAsync(string file, string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null) return null;
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null; // ERROR_CANCELLED: "No" at the prompt
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't run {System.IO.Path.GetFileName(file)} as administrator", ex);
            return null;
        }
    }

    // ---------- reading ----------

    /// <summary>
    /// The command line the process was started with. Read through ProcessCommandLineInformation,
    /// which (unlike reading the process's memory) works without administrator for your own processes.
    /// </summary>
    public static string? CommandLine(int pid)
    {
        try
        {
            using var handle = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION);
            return handle == null ? null : CommandLine(handle);
        }
        catch
        {
            return null;
        }
    }

    static string? CommandLine(SafeProcessHandle handle)
    {
        var size = 2048;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(handle, 60 /* ProcessCommandLineInformation */, buffer, size, out var needed);
                if (status is STATUS_INFO_LENGTH_MISMATCH or STATUS_BUFFER_TOO_SMALL or STATUS_BUFFER_OVERFLOW)
                {
                    size = Math.Max(needed, size * 2);
                    continue;
                }
                if (status < 0) return null;
                // The buffer starts with a UNICODE_STRING (length in bytes, then a pointer into the same buffer).
                var length = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return length == 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    /// <summary>
    /// Path, command line, owner, memory and the rest, for a details panel. Slow-ish (a few file and
    /// account lookups), so call it on a background thread. Null only if the process has gone.
    /// </summary>
    public static ProcessDetails? Details(int pid)
    {
        try
        {
            Process process;
            try
            {
                process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return null; // gone
            }

            using (process)
            {
                // These come from Windows' process list, so they work even when we can't open the process.
                string name = "";
                int? threads = null, handles = null;
                long? workingSet = null, privateBytes = null, peak = null;
                try { name = process.ProcessName; } catch { }
                try { threads = process.Threads.Count; } catch { }
                try { handles = process.HandleCount; } catch { }
                try
                {
                    workingSet = process.WorkingSet64;
                    peak = process.PeakWorkingSet64;
                    privateBytes = process.PrivateMemorySize64;
                }
                catch
                {
                    // not available
                }

                string? path = null, commandLine = null, user = null;
                DateTime? started = null;
                bool? elevated = null, is32Bit = null;
                int? parent = null;
                ProcessPriorityClass? priority = null;

                using var handle = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION);
                if (handle != null)
                {
                    path = ImagePath(handle);
                    commandLine = CommandLine(handle);
                    if (CreationTime(handle) is long created && created > 0)
                        try { started = DateTime.FromFileTime(created); } catch { }
                    (user, elevated) = Owner(handle);
                    parent = ParentOf(handle);
                    var p = GetPriorityClass(handle);
                    if (p != 0) priority = (ProcessPriorityClass)p;
                    if (IsWow64Process2(handle, out var machine, out _)) is32Bit = machine != 0; // 0: not running under WOW64

                    var counters = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
                    if (GetProcessMemoryInfo(handle, ref counters, (int)counters.cb))
                    {
                        workingSet = (long)counters.WorkingSetSize;
                        peak = (long)counters.PeakWorkingSetSize;
                        privateBytes = (long)counters.PrivateUsage;
                    }
                }

                string? description = null, company = null, version = null;
                if (path != null)
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(path);
                        description = Clean(info.FileDescription);
                        company = Clean(info.CompanyName);
                        version = Clean(info.ProductVersion) ?? Clean(info.FileVersion);
                    }
                    catch
                    {
                        // unreadable file
                    }

                var fileName = path != null ? System.IO.Path.GetFileName(path) : name.Length > 0 ? name + ".exe" : pid.ToString();
                return new ProcessDetails(pid, parent, fileName, path, commandLine, started, user, elevated, priority,
                    workingSet, privateBytes, peak, handles, threads, is32Bit, description, company, version);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't read details of process {pid}", ex);
            return null;
        }
    }

    // ---------- helpers ----------

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>A handle with the given rights, or null if Windows says no (or the process has gone).</summary>
    static SafeProcessHandle? Open(int pid, uint access)
    {
        if (pid <= 0) return null;
        var handle = OpenProcess(access, false, pid);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    static long? CreationTime(SafeProcessHandle handle) =>
        GetProcessTimes(handle, out var created, out _, out _, out _) ? created : null;

    static long? CreationTime(int pid)
    {
        using var handle = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION);
        return handle == null ? null : CreationTime(handle);
    }

    static string? ImagePath(SafeProcessHandle handle)
    {
        var size = 1024;
        var text = new StringBuilder(size);
        return QueryFullProcessImageName(handle, 0, text, ref size) ? text.ToString() : null;
    }

    /// <summary>The parent process ID from PROCESS_BASIC_INFORMATION (its sixth pointer-sized field).</summary>
    static int? ParentOf(SafeProcessHandle handle)
    {
        var size = IntPtr.Size * 6;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return NtQueryInformationProcess(handle, 0 /* ProcessBasicInformation */, buffer, size, out _) >= 0
                ? (int)Marshal.ReadIntPtr(buffer, IntPtr.Size * 5)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Who the process runs as (DOMAIN\name) and whether it's elevated, from its access token.</summary>
    static (string? User, bool? Elevated) Owner(SafeProcessHandle handle)
    {
        if (!OpenProcessToken(handle, TOKEN_QUERY, out var token)) return (null, null);
        try
        {
            string? user = null;
            bool? elevated = null;

            GetTokenInformation(token, 1 /* TokenUser */, IntPtr.Zero, 0, out var needed);
            if (needed > 0)
            {
                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (GetTokenInformation(token, 1, buffer, needed, out _)) user = AccountName(Marshal.ReadIntPtr(buffer));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            var elevation = Marshal.AllocHGlobal(4);
            try
            {
                if (GetTokenInformation(token, 20 /* TokenElevation */, elevation, 4, out _)) elevated = Marshal.ReadInt32(elevation) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(elevation);
            }
            return (user, elevated);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>A SID as DOMAIN\name, remembered because looking up domain accounts can be slow.</summary>
    static string? AccountName(IntPtr sid)
    {
        if (sid == IntPtr.Zero) return null;
        string key;
        try
        {
            key = new SecurityIdentifier(sid).Value;
        }
        catch
        {
            return null;
        }
        lock (Gate)
            if (Accounts.TryGetValue(key, out var known)) return known;

        int nameLength = 256, domainLength = 256;
        var name = new StringBuilder(nameLength);
        var domain = new StringBuilder(domainLength);
        var result = LookupAccountSid(null, sid, name, ref nameLength, domain, ref domainLength, out _)
            ? domain.Length > 0 ? $"{domain}\\{name}" : name.ToString()
            : key;
        lock (Gate) Accounts[key] = result;
        return result;
    }

    // ---------- Windows ----------

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetPriorityClass(SafeProcessHandle process, uint priority);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetPriorityClass(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref PROCESS_MEMORY_COUNTERS_EX counters, int size);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LookupAccountSidW")]
    static extern bool LookupAccountSid(string? system, IntPtr sid, StringBuilder name, ref int nameLength, StringBuilder domain, ref int domainLength, out int use);
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(SafeProcessHandle process);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(SafeProcessHandle process);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, IntPtr info, int length, out int returned);
}
