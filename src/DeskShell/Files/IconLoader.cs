using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskShell.Files;

/// <summary>
/// Loads icons and thumbnails on background threads, newest request first, so the items on
/// screen fill in first. Icons shared by a file type are looked up once.
/// </summary>
public static class IconLoader
{
    static readonly ShellWorker Worker = new(2);
    static readonly object Gate = new();
    static readonly Dictionary<string, ImageSource> ByKey = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<int, ImageSource> ByIndex = new();
    static readonly HashSet<string> OwnIcon = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".msc", ".cpl", ".scr", ".appref-ms", ".website" };
    static Dispatcher UI => Application.Current.Dispatcher;

    /// <summary>The icon for files of this kind, if it can be shared; null for ones with their own icon.</summary>
    static string? SharedKey(FileItem item)
    {
        if (item.IsFolder)
            return (item.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0 ? null : "<folder>"; // customised folders
        var ext = item.Extension;
        return OwnIcon.Contains(ext) ? null : ext.Length == 0 ? "<file>" : ext;
    }

    public static ImageSource? Small(FileItem item, Action<ImageSource> loaded, Action dropped) => Small(item.Path, item.IsFolder, SharedKey(item), loaded, dropped);

    /// <summary>A 32px icon for any path (also used by the sidebar).</summary>
    public static ImageSource? Small(string path, bool isFolder, string? key, Action<ImageSource> loaded, Action? dropped = null)
    {
        if (key != null)
            lock (Gate)
            {
                if (ByKey.TryGetValue(key, out var known)) return known;
                // Another item of this kind is already being looked up: wait for that one.
                if (Pending.TryGetValue(key, out var waiting))
                {
                    waiting.Add(loaded);
                    return null;
                }
                Pending[key] = new List<Action<ImageSource>> { loaded };
            }

        Worker.Enqueue(() =>
        {
            var image = LookUp(path, isFolder, key) ?? LookUp(path, isFolder, key); // the shell occasionally fails once
            List<Action<ImageSource>> callbacks;
            lock (Gate)
            {
                if (key != null)
                {
                    callbacks = Pending.Remove(key, out var list) ? list : new();
                    if (image != null) ByKey[key] = image;
                }
                else callbacks = new() { loaded };
            }
            if (image == null)
            {
                if (dropped != null) UI.BeginInvoke(dropped);
                return;
            }
            UI.BeginInvoke(() => { foreach (var callback in callbacks) callback(image); }, DispatcherPriority.Background);
        }, () =>
        {
            if (key != null) lock (Gate) Pending.Remove(key);
            if (dropped != null) UI.BeginInvoke(dropped);
        });
        return null;
    }

    static readonly Dictionary<string, List<Action<ImageSource>>> Pending = new(StringComparer.OrdinalIgnoreCase);

    static ImageSource? LookUp(string path, bool isFolder, string? key)
    {
        var info = new ShellNative.SHFILEINFO();
        var flags = ShellNative.SHGFI_SYSICONINDEX;
        var attributes = isFolder ? ShellNative.FILE_ATTRIBUTE_DIRECTORY : ShellNative.FILE_ATTRIBUTE_NORMAL;
        if (key != null) flags |= ShellNative.SHGFI_USEFILEATTRIBUTES;
        var name = key != null && !isFolder ? "x" + (key.StartsWith('<') ? "" : key) : path;
        if (ShellNative.SHGetFileInfo(name, attributes, ref info, (uint)Marshal.SizeOf<ShellNative.SHFILEINFO>(), flags) == IntPtr.Zero) return null;
        ImageSource? image;
        lock (Gate) ByIndex.TryGetValue(info.iIcon, out image);
        if (image != null) return image;
        image = LoadIcon(name, attributes, flags & ShellNative.SHGFI_USEFILEATTRIBUTES);
        if (image != null) lock (Gate) ByIndex[info.iIcon] = image;
        return image;
    }

    /// <summary>The 32px icon, copied so it can be used on the UI thread.</summary>
    static ImageSource? LoadIcon(string path, uint attributes, uint extraFlags)
    {
        var info = new ShellNative.SHFILEINFO();
        if (ShellNative.SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShellNative.SHFILEINFO>(),
                0x100 /* SHGFI_ICON, large */ | extraFlags) == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var copy = new WriteableBitmap(source);
            copy.Freeze();
            return copy;
        }
        finally
        {
            ShellNative.DestroyIcon(info.hIcon);
        }
    }

    /// <summary>A thumbnail if the file has one, else its large icon.</summary>
    public static void Thumbnail(FileItem item, Action<ImageSource> loaded, Action dropped) =>
        Worker.Enqueue(() =>
        {
            var image = ShellImage(item.Path, 128);
            if (image != null) UI.BeginInvoke(() => loaded(image), DispatcherPriority.Background);
        }, () => UI.BeginInvoke(dropped));

    public static ImageSource? ShellImage(string path, int size)
    {
        var iid = ShellNative.IID_IShellItemImageFactory;
        if (ShellNative.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) != 0) return null;
        try
        {
            var factory = (ShellNative.IShellItemImageFactory)item;
            if (factory.GetImage(new ShellNative.SIZE { cx = size, cy = size }, 0 /* SIIGBF_RESIZETOFIT */, out var bitmap) != 0 || bitmap == IntPtr.Zero)
                return null;
            try { return FromHBitmap(bitmap); }
            finally { ShellNative.DeleteObject(bitmap); }
        }
        catch
        {
            return null; // a broken thumbnail handler
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>Copies a 32-bit shell bitmap, keeping its transparency (which Imaging.CreateBitmapSourceFromHBitmap drops).</summary>
    static BitmapSource? FromHBitmap(IntPtr bitmap)
    {
        if (ShellNative.GetObject(bitmap, Marshal.SizeOf<ShellNative.DIBSECTION>(), out var dib) == 0 || dib.bmBits == IntPtr.Zero || dib.bmBitsPixel != 32)
        {
            var plain = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var copy = new WriteableBitmap(plain);
            copy.Freeze();
            return copy;
        }
        int width = dib.bmWidth, height = Math.Abs(dib.bmHeight), stride = dib.bmWidthBytes;
        var pixels = new byte[stride * height];
        // Shell bitmaps say bottom-up but their rows are stored top-down (flipping them shows pictures upside down).
        var bottomUp = dib.dsBmih.biHeight < 0;
        for (var y = 0; y < height; y++)
            Marshal.Copy(dib.bmBits + (bottomUp ? height - 1 - y : y) * stride, pixels, y * stride, stride);

        // Photos often come back with every alpha byte zero; they're opaque.
        var anyAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}

/// <summary>A few STA threads for shell calls (thumbnail handlers expect one). Newest work first; old work is dropped past a limit.</summary>
public sealed class ShellWorker
{
    readonly LinkedList<(Action Work, Action? Dropped)> _queue = new();
    const int MaxQueued = 600;

    public ShellWorker(int threads)
    {
        for (var i = 0; i < threads; i++)
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "DeskShell files icons", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    /// <summary><paramref name="dropped"/> runs (on this thread) if the work is dropped for newer work.</summary>
    public void Enqueue(Action work, Action? dropped = null)
    {
        lock (_queue)
        {
            _queue.AddFirst((work, dropped));
            while (_queue.Count > MaxQueued)
            {
                _queue.Last!.Value.Dropped?.Invoke();
                _queue.RemoveLast();
            }
            Monitor.Pulse(_queue);
        }
    }

    void Run()
    {
        while (true)
        {
            Action work;
            lock (_queue)
            {
                while (_queue.Count == 0) Monitor.Wait(_queue);
                work = _queue.First!.Value.Work;
                _queue.RemoveFirst();
            }
            try { work(); }
            catch (Exception ex) { Log.Error("Icon loading failed", ex); }
        }
    }
}
