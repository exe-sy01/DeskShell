namespace DeskShell;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The watchdog copy needs no UI, so it skips WPF entirely and stays tiny.
        if (args.Length == 2 && args[0] == "--watchdog" && int.TryParse(args[1], out var parentPid))
        {
            TaskbarGuard.RunWatchdog(parentPid);
            return;
        }
        if (args.Length > 0 && args[0] is "--files" or "--open")
        {
            Files.FilesHost.Run(args);
            return;
        }
        if (Installer.RunCommand(args)) return;

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
