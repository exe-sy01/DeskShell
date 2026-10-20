using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeskShell.Services;

/// <summary>
/// Remembers the last 30 pieces of text you copied, in memory only (nothing is written to disk).
/// Text that password managers mark as private is skipped.
/// </summary>
public sealed class ClipboardHistory : IDisposable
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    const int Max = 30;

    readonly HwndSource _source;
    string? _ignoreNext;

    public ObservableCollection<string> Items { get; } = new();

    public ClipboardHistory()
    {
        _source = new HwndSource(new HwndSourceParameters("DeskShellClipboard") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
            Dispatcher.CurrentDispatcher.BeginInvoke(Capture, DispatcherPriority.Background);
        return IntPtr.Zero;
    }

    void Capture()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") ||
                    Clipboard.ContainsData("Clipboard Viewer Ignore") ||
                    !Clipboard.ContainsText())
                    return;

                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;
                if (text == _ignoreNext)
                {
                    _ignoreNext = null;
                    return;
                }

                Items.Remove(text);
                Items.Insert(0, text);
                while (Items.Count > Max) Items.RemoveAt(Items.Count - 1);
                return;
            }
            catch (COMException)
            {
                // Another app has the clipboard open; try again shortly.
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the clipboard", ex);
                return;
            }
        }
    }

    /// <summary>Puts an earlier item back on the clipboard and moves it to the top.</summary>
    public void Restore(string text)
    {
        try
        {
            _ignoreNext = text;
            Clipboard.SetDataObject(text, true);
            Items.Remove(text);
            Items.Insert(0, text);
        }
        catch (Exception ex)
        {
            Log.Error("Could not set the clipboard", ex);
        }
    }

    public void Clear() => Items.Clear();

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }

    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
