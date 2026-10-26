using System.Windows.Controls;
using DeskShell.Services;
using ManagedShell.WindowsTasks;

namespace DeskShell;

/// <summary>The right-click menu of a pinned app, file or folder (dock and island).</summary>
public static class PinMenu
{
    public static void Fill(ContextMenu menu, DockPinItem item)
    {
        menu.Items.Clear();
        MenuItem Add(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
            return mi;
        }

        // Its open windows first, to switch straight to one.
        foreach (var window in item.Windows)
        {
            var w = window;
            Add((w.State == ApplicationWindow.WindowState.Active ? "●  " : "     ") + Trim(w.Title), () => WindowManagement.WmNative.Activate(w.Handle));
        }
        if (item.Windows.Count > 0) menu.Items.Add(new Separator());

        Add(item.IsRunning ? "Open new window" : "Open", () => DockPins.Launch(item.Pin));
        var index = DockPins.Items.IndexOf(item);
        if (index > 0) Add("Move left", () => DockPins.Move(item, index - 1));
        if (index < DockPins.Items.Count - 1) Add("Move right", () => DockPins.Move(item, index + 1));
        Add("Unpin", () => DockPins.Remove(item));
        if (item.Windows.Count > 0)
            Add(item.Windows.Count == 1 ? "Close window" : $"Close all {item.Windows.Count} windows", () =>
            {
                foreach (var w in item.Windows.ToList()) w.Close();
            });
    }

    static string Trim(string? text) => string.IsNullOrEmpty(text) ? "(untitled)" : text.Length > 60 ? text[..57] + "…" : text;
}
