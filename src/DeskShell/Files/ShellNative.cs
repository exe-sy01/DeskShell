using System.Runtime.InteropServices;

namespace DeskShell.Files;

/// <summary>The Windows shell calls the file explorer needs: icons, thumbnails, context menus and file operations.</summary>
internal static class ShellNative
{
    // ---------- file info and the system image list ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    public const uint SHGFI_TYPENAME = 0x400, SHGFI_SYSICONINDEX = 0x4000, SHGFI_USEFILEATTRIBUTES = 0x10;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    public const int SHIL_LARGE = 0, SHIL_EXTRALARGE = 2, SHIL_JUMBO = 4;

    [DllImport("shell32.dll", EntryPoint = "#727")]
    public static extern int SHGetImageList(int list, ref Guid riid, out IImageList images);

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IImageList
    {
        [PreserveSig] int Add(IntPtr image, IntPtr mask, out int index);
        [PreserveSig] int ReplaceIcon(int i, IntPtr icon, out int index);
        [PreserveSig] int SetOverlayImage(int image, int overlay);
        [PreserveSig] int Replace(int i, IntPtr image, IntPtr mask);
        [PreserveSig] int AddMasked(IntPtr image, int mask, out int index);
        [PreserveSig] int Draw(IntPtr parameters);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr icon);
    }

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

    // ---------- thumbnails ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DIBSECTION
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
        public BITMAPINFOHEADER dsBmih;
        public int dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public int dsOffset;
    }

    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr obj, int size, out DIBSECTION section);

    // ---------- pidls, folders and context menus ----------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")] public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr folder, out IntPtr childPidl);
    [DllImport("shell32.dll")] public static extern int SHGetDesktopFolder(out IntPtr folder);
    [DllImport("shell32.dll")] public static extern void ILFree(IntPtr pidl);

    public static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eaten, out IntPtr pidl, IntPtr attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumerator);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr owner, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetAttributesOf(uint count, IntPtr pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, out IntPtr result);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint firstId, uint lastId, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint firstId, uint lastId, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint firstId, uint lastId, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    public const uint CMF_NORMAL = 0, CMF_EXPLORE = 4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    public const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000, CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;
    public const uint GCS_VERBW = 4;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2;

    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    // ---------- file operations ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    public const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    public const ushort FOF_RENAMEONCOLLISION = 0x8, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string a, string b);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] out string path);

    public static string KnownFolder(string guid, Environment.SpecialFolder fallback)
    {
        var id = new Guid(guid);
        return SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0 ? path : Environment.GetFolderPath(fallback);
    }

    // ---------- shortcuts ----------

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkObject { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr findData, uint flags);
    }

    /// <summary>Where a .lnk shortcut points, or null.</summary>
    public static string? LinkTarget(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLinkObject();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(lnk, 0);
            var path = new System.Text.StringBuilder(1024);
            return ((IShellLinkW)link).GetPath(path, path.Capacity, IntPtr.Zero, 0) == 0 && path.Length > 0 ? path.ToString() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SHGetPathFromIDListW(IntPtr pidl, System.Text.StringBuilder path);

    /// <summary>"shell:Downloads", "::{GUID}" and the like as a folder path, if they are one.</summary>
    public static string? ShellPathToFolder(string name)
    {
        if (SHParseDisplayName(name, IntPtr.Zero, out var pidl, 0, out _) != 0) return null;
        try
        {
            var path = new System.Text.StringBuilder(1024);
            return SHGetPathFromIDListW(pidl, path) && path.Length > 0 ? path.ToString() : null;
        }
        finally
        {
            ILFree(pidl);
        }
    }

    // ---------- dark Win32 menus ----------

    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

    /// <summary>Makes the shell's own context menus dark, like the rest of the window (Windows 10 1903 and later).</summary>
    public static void UseDarkMenus()
    {
        try
        {
            SetPreferredAppMode(2); // force dark
            FlushMenuThemes();
        }
        catch
        {
            // older Windows: light menus
        }
    }
}
