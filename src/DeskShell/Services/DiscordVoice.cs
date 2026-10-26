using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DeskShell.Services;

public sealed record VoicePeer(string Id, string Name, string? Avatar, bool Speaking, bool Muted, bool Deafened);

public sealed record VoiceCall(string Channel, string? Server, IReadOnlyList<VoicePeer> Peers);

/// <summary>
/// The Discord voice call you're in, as reported by the AIChat Vencord plugin (deskshell.ts),
/// which sends a "voice" message over a WebSocket to 127.0.0.1:47390 whenever the call changes:
/// who's in it, who's speaking, who's muted. Only Discord's own pages may connect (checked by
/// their Origin), so a website open in a browser can't feed it. Nothing is sent back.
/// </summary>
public static class DiscordVoice
{
    public const int Port = 47390;
    const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    static readonly string[] AllowedOrigins = ["https://discord.com", "https://canary.discord.com", "https://ptb.discord.com"];

    static TcpListener? _listener;
    static CancellationTokenSource? _stop;
    static Dispatcher? _ui;
    static int _nextClient;
    // Each connected Discord (stable, PTB, Canary) reports its own call; the latest one wins.
    static readonly Dictionary<int, (VoiceCall? Call, long At)> _clients = new();

    /// <summary>The call you're in, or null.</summary>
    public static VoiceCall? Current { get; private set; }

    /// <summary>Raised on the UI thread when the call changes (joined, left, someone speaks…).</summary>
    public static event Action? Changed;

    public static void Start()
    {
        _ui = Dispatcher.CurrentDispatcher;
        _stop = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            Log.Error($"Can't listen for Discord voice calls on port {Port}", ex);
            _listener = null;
            return;
        }
        _ = AcceptLoopAsync(_stop.Token);
    }

    public static void Stop()
    {
        _stop?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    static async Task AcceptLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(stop); }
            catch { return; }
            var id = Interlocked.Increment(ref _nextClient);
            _ = Task.Run(() => ServeAsync(client, id, stop));
        }
    }

    static async Task ServeAsync(TcpClient client, int id, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                if (!await HandshakeAsync(stream, stop)) return;
                Log.Info("Discord connected for voice calls");
                await ReadMessagesAsync(stream, text => OnMessage(id, text), stop);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Discord closed or reloaded
            }
            catch (Exception ex)
            {
                Log.Error("Discord voice connection failed", ex);
            }
            finally
            {
                lock (_clients) _clients.Remove(id);
                Publish();
            }
        }
    }

    /// <summary>The WebSocket opening handshake (RFC 6455), refusing anything that isn't Discord.</summary>
    static async Task<bool> HandshakeAsync(NetworkStream stream, CancellationToken stop)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length) return false;
            var read = await stream.ReadAsync(buffer.AsMemory(length), stop);
            if (read == 0) return false;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n")) break;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
        string? Header(string name) => lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim())
            .FirstOrDefault();

        var origin = Header("Origin");
        var key = Header("Sec-WebSocket-Key");
        if (key == null || origin == null || !AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"), stop);
            return false;
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), stop);
        return true;
    }

    /// <summary>Reads WebSocket frames, answering pings and calling <paramref name="onText"/> for each text message.</summary>
    static async Task ReadMessagesAsync(NetworkStream stream, Action<string> onText, CancellationToken stop)
    {
        var message = new MemoryStream();
        var head = new byte[14];
        while (!stop.IsCancellationRequested)
        {
            await ReadExactAsync(stream, head, 2, stop);
            var fin = (head[0] & 0x80) != 0;
            var opcode = head[0] & 0x0F;
            var masked = (head[1] & 0x80) != 0;
            long length = head[1] & 0x7F;
            if (length == 126)
            {
                await ReadExactAsync(stream, head, 2, stop);
                length = (head[0] << 8) | head[1];
            }
            else if (length == 127)
            {
                await ReadExactAsync(stream, head, 8, stop);
                length = 0;
                for (var i = 0; i < 8; i++) length = (length << 8) | head[i];
            }
            if (length > 1_000_000) return; // far more than a call ever needs
            var mask = new byte[4];
            if (masked) await ReadExactAsync(stream, mask, 4, stop);
            var payload = new byte[length];
            await ReadExactAsync(stream, payload, (int)length, stop);
            if (masked)
                for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

            switch (opcode)
            {
                case 0x0 or 0x1: // continuation / text
                    message.Write(payload);
                    if (message.Length > 1_000_000) return;
                    if (fin)
                    {
                        onText(Encoding.UTF8.GetString(message.ToArray()));
                        message.SetLength(0);
                    }
                    break;
                case 0x8: // close
                    await stream.WriteAsync(new byte[] { 0x88, 0x00 }, stop);
                    return;
                case 0x9: // ping → pong with the same payload (server frames are unmasked)
                    if (payload.Length <= 125)
                    {
                        var pong = new byte[2 + payload.Length];
                        pong[0] = 0x8A;
                        pong[1] = (byte)payload.Length;
                        payload.CopyTo(pong, 2);
                        await stream.WriteAsync(pong, stop);
                    }
                    break;
            }
        }
    }

    static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken stop)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), stop);
            if (read == 0) throw new IOException("closed");
            offset += read;
        }
    }

    static void OnMessage(int client, string text)
    {
        VoiceCall? call;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "voice") return;
            call = ParseCall(root);
        }
        catch (JsonException)
        {
            return;
        }
        lock (_clients) _clients[client] = (call, Environment.TickCount64);
        Publish();
    }

    static VoiceCall? ParseCall(JsonElement root)
    {
        if (!root.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.Object) return null;
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        var peers = new List<VoicePeer>();
        if (root.TryGetProperty("peers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in list.EnumerateArray().Take(50))
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var avatar = Str(p, "avatar");
                if (avatar != null && !avatar.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) avatar = null;
                peers.Add(new VoicePeer(
                    Str(p, "id") ?? "",
                    Str(p, "name") ?? "Someone",
                    avatar,
                    Bool(p, "speaking"),
                    Bool(p, "muted") || Bool(p, "selfMuted"),
                    Bool(p, "deafened") || Bool(p, "selfDeafened")));
            }
        }
        var server = root.TryGetProperty("server", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        return new VoiceCall(Str(channel, "name") ?? "Voice", server, peers);
    }

    static void Publish()
    {
        VoiceCall? call;
        lock (_clients)
            call = _clients.Values.Where(c => c.Call != null).OrderByDescending(c => c.At).Select(c => c.Call).FirstOrDefault();
        _ui?.InvokeAsync(() =>
        {
            Current = call;
            Changed?.Invoke();
        });
    }
}
