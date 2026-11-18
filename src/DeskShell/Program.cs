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
        // Claude Code runs this for each hook: pass it to the running DeskShell and get out of the way.
        if (args.Length > 0 && args[0] == "--claude-hook")
        {
            Claude.ClaudeHook.Run(args);
            return;
        }
        if (args.Length == 2 && args[0] == "--restart" && int.TryParse(args[1], out var oldPid))
        {
            Services.GraphicsResetWatcher.RunReplacement(oldPid);
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
