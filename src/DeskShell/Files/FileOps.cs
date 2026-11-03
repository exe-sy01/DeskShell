using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DeskShell.Files;

/// <summary>
/// Copying, moving and deleting go through Windows' own file operation, so they get its progress
/// window, "replace or skip" questions, the Recycle Bin and Ctrl+Z in Explorer. Each runs on its
/// own thread so the window stays responsive.
/// </summary>
public static class FileOps
{
    public static void Copy(IntPtr owner, IReadOnlyList<string> files, string toFolder) => Run(owner, ShellNative.FO_COPY, files, toFolder);
    public static void Move(IntPtr owner, IReadOnlyList<string> files, string toFolder) => Run(owner, ShellNative.FO_MOVE, files, toFolder);

    /// <summary>To the Recycle Bin, or gone for good if <paramref name="permanently"/> (Windows asks first).</summary>
    public static void Delete(IntPtr owner, IReadOnlyList<string> files, bool permanently) =>
        Run(owner, ShellNative.FO_DELETE, files, null, permanently ? (ushort)0 : (ushort)(ShellNative.FOF_ALLOWUNDO | ShellNative.FOF_WANTNUKEWARNING));

    static void Run(IntPtr owner, uint operation, IReadOnlyList<string> files, string? to, ushort? flags = null)
    {
        if (files.Count == 0) return;
        // Copying something into the folder it's already in makes "name - Copy", like Explorer.
        var sameFolder = to != null && files.All(f => string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), to.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (sameFolder && operation == ShellNative.FO_MOVE) return;
        var thread = new Thread(() =>
        {
            try
            {
                var op = new ShellNative.SHFILEOPSTRUCT
                {
                    hwnd = owner,
                    wFunc = operation,
                    pFrom = string.Join('\0', files) + '\0',
                    pTo = to == null ? null : to + '\0',
                    fFlags = flags ?? (ushort)(ShellNative.FOF_ALLOWUNDO | (sameFolder ? ShellNative.FOF_RENAMEONCOLLISION : 0))
                };
                var result = ShellNative.SHFileOperation(ref op);
                if (result != 0 && op.fAnyOperationsAborted == 0 && result != 1223 /* cancelled */)
                    Log.Error($"File operation {operation} failed with {result:X}");
            }
            catch (Exception ex)
            {
                Log.Error("File operation failed", ex);
            }
        }) { IsBackground = false, Name = "DeskShell file operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Renames in place; returns an error message or null.</summary>
    public static string? Rename(string path, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return null;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A file name can't contain any of these characters: \\ / : * ? \" < > |";
        var target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (string.Equals(target, path, StringComparison.Ordinal)) return null;
        try
        {
            var caseOnly = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) return $"There's already something called \"{newName}\" here.";
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Creates "New folder", "New folder (2)"… and returns its path.</summary>
    public static string NewFolder(string parent)
    {
        var name = "New folder";
        for (var n = 2; Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name)); n++) name = $"New folder ({n})";
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates an empty file such as "New Text Document.txt" and returns its path.</summary>
    public static string NewFile(string parent, string baseName, string extension)
    {
        var name = baseName + extension;
        for (var n = 2; File.Exists(Path.Combine(parent, name)) || Directory.Exists(Path.Combine(parent, name)); n++) name = $"{baseName} ({n}){extension}";
        var path = Path.Combine(parent, name);
        using (File.Create(path)) { }
        return path;
    }

    // ---------- clipboard, shared with Explorer and other apps ----------

    const string DropEffect = "Preferred DropEffect";

    public static void ToClipboard(IReadOnlyList<string> files, bool cut)
    {
        if (files.Count == 0) return;
        var list = new StringCollection();
        list.AddRange(files.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(DropEffect, new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5))); // move or copy|link
        try { Clipboard.SetDataObject(data, true); }
        catch (Exception ex) { Log.Error("Couldn't use the clipboard", ex); }
    }

    /// <summary>Files on the clipboard and whether they were cut.</summary>
    public static (List<string> Files, bool Cut) FromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return (new(), false);
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            var cut = Clipboard.GetData(DropEffect) is MemoryStream stream && stream.Length >= 4 && (BitConverter.ToInt32(stream.ToArray(), 0) & 2) != 0;
            return (files, cut);
        }
        catch
        {
            return (new(), false);
        }
    }

    public static void Paste(IntPtr owner, string folder)
    {
        var (files, cut) = FromClipboard();
        if (files.Count == 0) return;
        if (cut)
        {
            Move(owner, files, folder);
            try { Clipboard.Clear(); } catch { /* someone else has it open */ }
        }
        else Copy(owner, files, folder);
    }

    // ---------- opening ----------

    /// <summary>Opens a file with its app (or runs it); false if Windows couldn't.</summary>
    public static bool Open(string path, string? verb = null, bool admin = false)
    {
        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" };
            if (admin) start.Verb = "runas";
            else if (verb != null) start.Verb = verb;
            Process.Start(start);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return true; // "No" at the permission prompt
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
            return false;
        }
    }

    public static void Properties(string path) => ShellNative.SHObjectProperties(IntPtr.Zero, 2 /* SHOP_FILEPATH */, path, null);

    /// <summary>Windows Terminal (or PowerShell) in this folder.</summary>
    public static void TerminalHere(string folder)
    {
        var terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        try
        {
            if (File.Exists(terminal)) Process.Start(new ProcessStartInfo(terminal, $"-d \"{folder.TrimEnd('\\')}\\\\\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder });
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't open a terminal", ex);
        }
    }
}
