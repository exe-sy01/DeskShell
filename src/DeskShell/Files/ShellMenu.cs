using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static DeskShell.Files.ShellNative;

namespace DeskShell.Files;

/// <summary>
/// Windows' full right-click menu for files or a folder's background ("Show more options"), with
/// everything apps add to it: 7-Zip, Open with, Send to, Git and so on.
/// </summary>
public static class ShellMenu
{
    const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120, WM_MENUSELECT = 0x11F;
    static IContextMenu2? _menu2;
    static IContextMenu3? _menu3;

    /// <summary>Submenus such as "Send to" and "Open with" are filled in by the shell through the window's messages.</summary>
    public static void Attach(HwndSource source) => source.AddHook(Hook);

    static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_menu2 == null && _menu3 == null) return IntPtr.Zero;
        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR or WM_MENUSELECT)) return IntPtr.Zero;
        try
        {
            if (_menu3 != null && _menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
            {
                handled = true;
                return result;
            }
            if (_menu2 != null && _menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        catch
        {
            // a broken shell extension
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Shows the menu for <paramref name="items"/> (all in one folder), or for <paramref name="folder"/>'s
    /// background if there are none. <paramref name="ownVerb"/> gets first say on the chosen verb
    /// ("rename", "open"…) and returns true if it handled it.
    /// </summary>
    public static void Show(IntPtr hwnd, string folder, IReadOnlyList<string> items, Point screen, bool extended, Func<string, bool> ownVerb)
    {
        using var menu = Create(hwnd, folder, items);
        if (menu == null) return;
        var popup = CreatePopupMenu();
        try
        {
            var flags = CMF_NORMAL | CMF_EXPLORE | (items.Count > 0 ? CMF_CANRENAME : 0) | (extended ? CMF_EXTENDEDVERBS : 0);
            if (menu.Menu.QueryContextMenu(popup, 0, 1, 0x7FFF, flags) < 0) return;
            _menu2 = menu.Menu as IContextMenu2;
            _menu3 = menu.Menu as IContextMenu3;
            int chosen;
            try { chosen = TrackPopupMenuEx(popup, TPM_RETURNCMD | TPM_RIGHTBUTTON, (int)screen.X, (int)screen.Y, hwnd, IntPtr.Zero); }
            finally { _menu2 = null; _menu3 = null; }
            if (chosen <= 0) return;
            var verb = Verb(menu.Menu, chosen - 1);
            if (verb != null && ownVerb(verb)) return;
            Invoke(menu.Menu, hwnd, folder, new IntPtr(chosen - 1), IntPtr.Zero, screen);
        }
        catch (Exception ex)
        {
            Log.Error("Shell menu failed", ex);
        }
        finally
        {
            DestroyMenu(popup);
        }
    }

    /// <summary>Runs one of the menu's commands by name, e.g. "openas" (Open with) or "properties".</summary>
    public static void InvokeVerb(IntPtr hwnd, string folder, IReadOnlyList<string> items, string verb)
    {
        using var menu = Create(hwnd, folder, items);
        if (menu == null) return;
        var popup = CreatePopupMenu();
        var ansi = Marshal.StringToHGlobalAnsi(verb);
        var wide = Marshal.StringToHGlobalUni(verb);
        try
        {
            menu.Menu.QueryContextMenu(popup, 0, 1, 0x7FFF, CMF_NORMAL);
            Invoke(menu.Menu, hwnd, folder, ansi, wide, null);
        }
        catch (Exception ex)
        {
            Log.Error($"Shell verb {verb} failed", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(ansi);
            Marshal.FreeHGlobal(wide);
            DestroyMenu(popup);
        }
    }

    static void Invoke(IContextMenu menu, IntPtr hwnd, string folder, IntPtr verb, IntPtr verbW, Point? at)
    {
        var directory = Marshal.StringToHGlobalUni(folder);
        try
        {
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE,
                hwnd = hwnd,
                lpVerb = verb,
                lpVerbW = verbW == IntPtr.Zero ? verb : verbW,
                lpDirectoryW = directory,
                nShow = 1 // SW_SHOWNORMAL
            };
            if (at is { } point)
            {
                info.fMask |= CMIC_MASK_PTINVOKE;
                info.ptInvoke = new POINT { X = (int)point.X, Y = (int)point.Y };
            }
            if ((WindowManagement.WmNative.GetAsyncKeyState(0x11) & 0x8000) != 0) info.fMask |= CMIC_MASK_CONTROL_DOWN;
            if ((WindowManagement.WmNative.GetAsyncKeyState(0x10) & 0x8000) != 0) info.fMask |= CMIC_MASK_SHIFT_DOWN;
            var result = menu.InvokeCommand(ref info);
            if (result < 0 && result != unchecked((int)0x800704C7)) Log.Error($"Shell command failed: {result:X}");
        }
        finally
        {
            Marshal.FreeHGlobal(directory);
        }
    }

    static string? Verb(IContextMenu menu, int id)
    {
        var buffer = Marshal.AllocHGlobal(520);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return menu.GetCommandString(new UIntPtr((uint)id), GCS_VERBW, IntPtr.Zero, buffer, 260) == 0 ? Marshal.PtrToStringUni(buffer) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    sealed class MenuHandle : IDisposable
    {
        public IContextMenu Menu = null!;
        public readonly List<IntPtr> Pidls = new();
        public readonly List<object> Com = new();

        public void Dispose()
        {
            foreach (var o in Com) Marshal.ReleaseComObject(o);
            foreach (var p in Pidls) ILFree(p);
        }
    }

    [DllImport("shell32.dll")] static extern IntPtr ILFindLastID(IntPtr pidl);

    static MenuHandle? Create(IntPtr hwnd, string folder, IReadOnlyList<string> items)
    {
        var handle = new MenuHandle();
        try
        {
            var menuIid = IID_IContextMenu;
            IntPtr menuPtr;
            if (items.Count > 0)
            {
                foreach (var path in items)
                {
                    if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0) continue;
                    handle.Pidls.Add(pidl);
                }
                if (handle.Pidls.Count == 0) { handle.Dispose(); return null; }
                var folderIid = IID_IShellFolder;
                if (SHBindToParent(handle.Pidls[0], ref folderIid, out var parentPtr, out _) != 0) { handle.Dispose(); return null; }
                var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
                Marshal.Release(parentPtr);
                handle.Com.Add(parent);
                var children = handle.Pidls.Select(ILFindLastID).ToArray();
                if (parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref menuIid, IntPtr.Zero, out menuPtr) != 0) { handle.Dispose(); return null; }
            }
            else
            {
                if (SHParseDisplayName(folder, IntPtr.Zero, out var pidl, 0, out _) != 0) { handle.Dispose(); return null; }
                handle.Pidls.Add(pidl);
                if (SHGetDesktopFolder(out var desktopPtr) != 0) { handle.Dispose(); return null; }
                var desktop = (IShellFolder)Marshal.GetObjectForIUnknown(desktopPtr);
                Marshal.Release(desktopPtr);
                handle.Com.Add(desktop);
                var folderIid = IID_IShellFolder;
                if (desktop.BindToObject(pidl, IntPtr.Zero, ref folderIid, out var folderPtr) != 0) { handle.Dispose(); return null; }
                var shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
                Marshal.Release(folderPtr);
                handle.Com.Add(shellFolder);
                if (shellFolder.CreateViewObject(hwnd, ref menuIid, out menuPtr) != 0) { handle.Dispose(); return null; }
            }
            handle.Menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            Marshal.Release(menuPtr);
            handle.Com.Add(handle.Menu);
            return handle;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't get the shell menu", ex);
            handle.Dispose();
            return null;
        }
    }
}
