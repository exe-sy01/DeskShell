using System.Windows.Input;
using ManagedShell.WindowsTasks;

namespace DeskShell;

/// <summary>Commands used by the window buttons in both the dock and the shell.</summary>
public static class TaskCommands
{
    /// <summary>Click: focus the window, or minimise it if it's already in front.</summary>
    public static ICommand Activate { get; } = new Relay(o =>
    {
        if (o is not ApplicationWindow window) return;

        if (App.Instance.IsShellOpen)
        {
            // The shell itself has focus, so every window looks inactive; just bring it forward.
            App.Instance.HideShell();
            window.BringToFront();
        }
        else if (window.State == ApplicationWindow.WindowState.Active && window.CanMinimize)
        {
            window.Minimize();
        }
        else
        {
            window.BringToFront();
        }
    });

    public static ICommand Minimize { get; } = new Relay(o => (o as ApplicationWindow)?.Minimize());

    public static ICommand Close { get; } = new Relay(o => (o as ApplicationWindow)?.Close());

    sealed class Relay(Action<object?> run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run(parameter);
    }
}
