using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskShell.Search;

/// <summary>One file or folder as NTFS reports it: its MFT record, its folder's record, attributes and name.</summary>
public readonly record struct NtfsEntry(long Frn, long ParentFrn, FileAttributes Attributes, string Name);

/// <summary>
/// The NTFS calls behind the file index. Everything here works without administrator rights where
/// Windows allows it: data drives can usually be opened directly and their Master File Table
/// listed; the system drive can't, so its folders are walked instead (still reading each entry's
/// file ID, so both end up the same). Changes come from the USN change journal, which any user may
/// read (names left out, so they're looked up by file ID).
/// </summary>
public static unsafe class Ntfs
{
    const uint FSCTL_ENUM_USN_DATA = 0x000900b3, FSCTL_QUERY_USN_JOURNAL = 0x000900f4,
        FSCTL_READ_USN_JOURNAL = 0x000900bb, FSCTL_READ_UNPRIVILEGED_USN_JOURNAL = 0x000903ab;
    const uint GENERIC_READ = 0x80000000, FILE_READ_ATTRIBUTES = 0x80, FILE_LIST_DIRECTORY = 1;
    const uint SHARE_ALL = 7, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const long RecordMask = 0xFFFFFFFFFFFF;

    public const uint ReasonCreate = 0x100, ReasonDelete = 0x200, ReasonRenameOld = 0x1000, ReasonRenameNew = 0x2000,
        ReasonBasicInfo = 0x8000, ReasonClose = 0x80000000;

    /// <summary>Fixed NTFS drives, as letters.</summary>
    public static List<char> FixedNtfsDrives()
    {
        var list = new List<char>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady && drive.DriveFormat == "NTFS" && drive.Name.Length >= 2)
                    list.Add(char.ToUpperInvariant(drive.Name[0]));
            }
            catch
            {
                // a drive that went away
            }
        }
        return list;
    }

    public static uint VolumeSerial(char drive)
    {
        return GetVolumeInformation($@"{drive}:\", null, 0, out var serial, out _, out _, null, 0) ? serial : 0;
    }

    /// <summary>A handle to the drive's root folder: enough for the journal and for opening files by ID.</summary>
    public static SafeFileHandle OpenRoot(char drive) =>
        CreateFile($@"{drive}:\", FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

    /// <summary>The volume itself, for reading its MFT. Null where Windows needs administrator rights for it (usually C:).</summary>
    public static SafeFileHandle? TryOpenVolume(char drive)
    {
        var handle = CreateFile($@"\\.\{drive}:", GENERIC_READ, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    public readonly record struct Journal(ulong Id, long FirstUsn, long NextUsn);

    public static Journal? QueryJournal(SafeFileHandle handle)
    {
        var data = stackalloc byte[80];
        if (!DeviceIoControl(handle, FSCTL_QUERY_USN_JOURNAL, null, 0, data, 80, out _, IntPtr.Zero)) return null;
        return new Journal(*(ulong*)data, *(long*)(data + 8), *(long*)(data + 16));
    }

    /// <summary>
    /// Lists every file and folder on the volume straight from the MFT, in batches. Needs a volume
    /// handle from <see cref="TryOpenVolume"/>. Returns false if Windows refused.
    /// </summary>
    public static bool EnumerateMft(SafeFileHandle volume, Action<List<NtfsEntry>> batch, CancellationToken cancel, Action? pause = null)
    {
        var buffer = new byte[1 << 20];
        var input = stackalloc long[3];
        input[0] = 0;
        input[1] = 0;
        input[2] = long.MaxValue;
        fixed (byte* b = buffer)
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                pause?.Invoke();
                if (!DeviceIoControl(volume, FSCTL_ENUM_USN_DATA, input, 24, b, buffer.Length, out var read, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    return error == 38; // ERROR_HANDLE_EOF: the end of the MFT
                }
                input[0] = *(long*)b;
                var list = new List<NtfsEntry>(8192);
                for (var offset = 8; offset < read;)
                {
                    var record = b + offset;
                    var length = *(int*)record;
                    if (length <= 0) break;
                    if (*(ushort*)(record + 4) == 2) list.Add(ReadV2(record));
                    offset += length;
                }
                batch(list);
            }
        }
    }

    static NtfsEntry ReadV2(byte* record)
    {
        var nameLength = *(ushort*)(record + 56);
        var nameOffset = *(ushort*)(record + 58);
        return new NtfsEntry(*(long*)(record + 8), *(long*)(record + 16), (FileAttributes)(*(uint*)(record + 52)),
            new string((char*)(record + nameOffset), 0, nameLength / 2));
    }

    // ---------- walking folders (where the MFT can't be read) ----------

    /// <summary>The file ID (MFT reference) of a folder or file, from an open handle.</summary>
    public static long FileId(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var info) ? ((long)info.FileIndexHigh << 32) | info.FileIndexLow : -1;

    /// <summary>
    /// Every entry directly inside <paramref name="path"/>, with file IDs. Returns false if the folder
    /// can't be opened (no access, gone).
    /// </summary>
    public static bool ListFolder(string path, byte[] buffer, List<(long Id, FileAttributes Attributes, string Name)> into, out long folderId)
    {
        folderId = -1;
        using var handle = CreateFile(@"\\?\" + path, FILE_LIST_DIRECTORY, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        folderId = FileId(handle);
        fixed (byte* b = buffer)
        {
            var infoClass = 11; // FileIdBothDirectoryRestartInfo, then FileIdBothDirectoryInfo
            while (GetFileInformationByHandleEx(handle, infoClass, b, buffer.Length))
            {
                infoClass = 10;
                for (var entry = b; ; )
                {
                    var next = *(int*)entry;
                    var nameLength = *(int*)(entry + 60);
                    var name = new string((char*)(entry + 104), 0, nameLength / 2);
                    if (name is not ("." or ".."))
                        into.Add((*(long*)(entry + 96), (FileAttributes)(*(uint*)(entry + 56)), name));
                    if (next == 0) break;
                    entry += next;
                }
            }
        }
        return true;
    }

    // ---------- the change journal ----------

    public readonly record struct Change(long Usn, long Frn, long ParentFrn, uint Reason, FileAttributes Attributes, string? Name);

    /// <summary>
    /// Reads changes after <paramref name="startUsn"/>. With a volume handle names are included;
    /// with only a root handle (no administrator rights) they're left out. Returns the next USN to
    /// read from, or null if the journal no longer goes back that far (or was replaced): rebuild.
    /// </summary>
    public static long? ReadJournal(SafeFileHandle handle, bool privileged, ulong journalId, long startUsn, uint reasons, List<Change> into, byte[] buffer)
    {
        var input = stackalloc byte[40];
        *(long*)input = startUsn;
        *(uint*)(input + 8) = reasons;
        *(uint*)(input + 12) = 0;     // every change, not only at close
        *(ulong*)(input + 16) = 0;    // timeout
        *(ulong*)(input + 24) = 0;    // don't wait
        *(ulong*)(input + 32) = journalId;
        fixed (byte* b = buffer)
        {
            if (!DeviceIoControl(handle, privileged ? FSCTL_READ_USN_JOURNAL : FSCTL_READ_UNPRIVILEGED_USN_JOURNAL, input, 40, b, buffer.Length, out var read, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                // ERROR_JOURNAL_ENTRY_DELETED (1181), ERROR_JOURNAL_NOT_ACTIVE (1179), ERROR_JOURNAL_DELETE_IN_PROGRESS (1178), ERROR_INVALID_PARAMETER (87)
                if (error is 1181 or 1179 or 1178 or 87) return null;
                throw new Win32Exception(error);
            }
            var next = *(long*)b;
            for (var offset = 8; offset < read;)
            {
                var record = b + offset;
                var length = *(int*)record;
                if (length <= 0) break;
                if (*(ushort*)(record + 4) == 2)
                {
                    var nameLength = *(ushort*)(record + 56);
                    var nameOffset = *(ushort*)(record + 58);
                    into.Add(new Change(*(long*)(record + 24), *(long*)(record + 8), *(long*)(record + 16), *(uint*)(record + 40),
                        (FileAttributes)(*(uint*)(record + 52)), nameLength > 0 ? new string((char*)(record + nameOffset), 0, nameLength / 2) : null));
                }
                offset += length;
            }
            return next;
        }
    }

    /// <summary>The current name of a file by its ID (for journal entries that come without one). Null if it can't be opened.</summary>
    public static string? NameById(SafeFileHandle root, long frn)
    {
        var descriptor = stackalloc byte[24];
        *(int*)descriptor = 24;
        *(int*)(descriptor + 4) = 0; // FileIdType
        *(long*)(descriptor + 8) = frn;
        *(long*)(descriptor + 16) = 0;
        using var handle = OpenFileById(root, descriptor, FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
        if (handle.IsInvalid) return null;
        var buffer = stackalloc byte[4 + 2 * 1100];
        if (!GetFileInformationByHandleEx(handle, 2 /* FileNameInfo */, buffer, 4 + 2 * 1100)) return null;
        var length = *(int*)buffer / 2;
        var path = new ReadOnlySpan<char>(buffer + 4, Math.Min(length, 1100));
        var slash = path.LastIndexOf('\\');
        return slash < 0 ? path.ToString() : path[(slash + 1)..].ToString();
    }

    // ---------- native ----------

    // FILETIMEs are two DWORDs, so nothing in here is 8-byte aligned.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, byte* fileId, uint access, uint share, IntPtr security, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, void* buffer, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetVolumeInformation(string root, char[]? name, int nameSize, out uint serial, out uint maxComponent, out uint flags, char[]? fs, int fsSize);
}
