using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BfbbOnline.Shared;

/// <summary>
/// Wire protocol: TCP, UTF-8 JSON, one object per line, every object has "t" (type).
/// See docs/PROTOCOL.md.
/// </summary>
public static class Proto
{
    public const int Version = 2;   // 2: enemy/boss sync (np), race rewards (obj p)
    public const int DefaultPort = 25599;

    public static JsonObject Msg(string type) => new() { ["t"] = type };

    public static string? Type(JsonObject o) => o["t"]?.GetValue<string>();

    public static JsonArray Floats(params float[] v)
    {
        var a = new JsonArray();
        foreach (var f in v) a.Add(Finite(MathF.Round(f, 3)));
        return a;
    }

    /// <summary>JSON has no NaN/infinity (the game can briefly hold them, e.g. falling out of the world): 0 instead.</summary>
    public static float Finite(float f) => float.IsFinite(f) ? f : 0f;
}

/// <summary>A line-oriented JSON connection with a bounded send queue (drops state spam when slow).</summary>
public sealed class JsonConnection : IDisposable
{
    readonly TcpClient tcp;
    readonly NetworkStream stream;
    readonly Channel<string> outq = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    readonly CancellationTokenSource cts = new();
    public string Remote { get; }
    public event Action<JsonObject>? Received;
    public event Action<string>? Closed;
    int closed;

    public JsonConnection(TcpClient tcp)
    {
        this.tcp = tcp;
        tcp.NoDelay = true;
        stream = tcp.GetStream();
        Remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public static async Task<JsonConnection> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        return new JsonConnection(tcp);
    }

    public void Start()
    {
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
    }

    public void Send(JsonObject o) => Send(o.ToJsonString());

    public void Send(string line)
    {
        if (closed == 0) outq.Writer.TryWrite(line);
    }

    async Task ReadLoop()
    {
        string why = "closed";
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line == null) break;
                if (line.Length == 0) continue;
                if (line.Length > 4 << 20) { why = "line too long"; break; }
                JsonObject? o;
                try { o = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (o != null) Received?.Invoke(o);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            why = e.Message;
        }
        Close(why);
    }

    async Task WriteLoop()
    {
        var enc = new UTF8Encoding(false);
        try
        {
            await foreach (var line in outq.Reader.ReadAllAsync(cts.Token))
            {
                var b = enc.GetBytes(line + "\n");
                await stream.WriteAsync(b, cts.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            Close(e.Message);
        }
    }

    public void Close(string why)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        cts.Cancel();
        outq.Writer.TryComplete();
        try { tcp.Close(); } catch { }
        Closed?.Invoke(why);
    }

    public void Dispose() => Close("disposed");
}
