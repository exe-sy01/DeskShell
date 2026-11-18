using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DeskShell.Claude;

/// <summary>
/// "DeskShell.exe --claude-hook Event": what Claude Code runs for each hook (installed by
/// <see cref="ClaudeSetup"/>). It passes the event to the running DeskShell over a named pipe and,
/// for a permission request, waits for your answer in the notch and prints it for Claude Code.
/// It never gets in Claude's way: if DeskShell isn't running, or you don't answer in time, it
/// exits at once with no output, and Claude carries on as if there were no hook.
/// </summary>
public static class ClaudeHook
{
    public static string PipeName => $"DeskShell.Claude.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    /// <summary>How long a permission request waits for the notch before Claude's own prompt takes over.</summary>
    public const int ApprovalWaitSeconds = 45;

    public static void Run(string[] args)
    {
        try
        {
            var name = args.Length > 1 ? args[1] : "";
            string input;
            using (var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8)) input = stdin.ReadToEnd();
            if (string.IsNullOrWhiteSpace(input)) return;

            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            try { pipe.Connect(250); }
            catch { return; } // DeskShell isn't running

            // One line out: the event; for permission requests, one line back: Claude Code's answer (or nothing).
            var line = JsonSerializer.Serialize(new { @event = name, payload = JsonDocument.Parse(input).RootElement });
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            if (name != "PermissionRequest") return;

            var reply = ReadLine(pipe, TimeSpan.FromSeconds(ApprovalWaitSeconds));
            if (string.IsNullOrWhiteSpace(reply)) return;
            using var stdout = Console.OpenStandardOutput();
            var answer = Encoding.UTF8.GetBytes(reply.Trim());
            stdout.Write(answer, 0, answer.Length);
            stdout.Flush();
        }
        catch
        {
            // Whatever went wrong, say nothing: Claude Code goes on as normal.
        }
    }

    static string? ReadLine(Stream pipe, TimeSpan wait)
    {
        var read = Task.Run(() =>
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            return reader.ReadLine();
        });
        return read.Wait(wait) ? read.Result : null;
    }
}
