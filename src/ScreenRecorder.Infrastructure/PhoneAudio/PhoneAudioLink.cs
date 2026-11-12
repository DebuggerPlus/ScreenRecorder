using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ScreenRecorder.Infrastructure.PhoneAudio;

/// <summary>
/// Control channel to the phone's AndroidAudioRecorder service (line-delimited JSON).
/// The reply to StartAsync() is the phone's READY signal: only after it arrives
/// should the video recording be started, so audio and video begin together.
/// </summary>
public sealed class PhoneAudioLink : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _readerTask;
    private TaskCompletionSource<JsonObject>? _pending;

    public string Host { get; }
    public int Port { get; }
    public bool Connected => _client?.Connected == true;

    /// <summary>Unsolicited messages from the phone ({"event":...}).</summary>
    public event Action<JsonObject>? EventReceived;

    public PhoneAudioLink(string host, int port = 47653)
    {
        Host = host;
        Port = port;
    }

    public async Task<JsonObject> ConnectAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        await client.ConnectAsync(Host, Port, cts.Token).ConfigureAwait(false);
        _client = client;
        _stream = client.GetStream();
        _readerTask = Task.Run(ReadLoopAsync);
        var status = await SendAsync(new JsonObject { ["cmd"] = "ping" }, ct).ConfigureAwait(false);
        return status;
    }

    public async Task<JsonObject> SendAsync(JsonObject command, CancellationToken ct = default)
    {
        if (_stream == null) throw new InvalidOperationException("not connected");
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            var data = System.Text.Encoding.UTF8.GetBytes(command.ToJsonString() + "\n");
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"no reply from phone for cmd '{command["cmd"]}'");
            }
            finally
            {
                _pending = null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var stream = _stream!;
        var sb = new StringBuilder();
        var one = new byte[1];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(one.AsMemory(0, 1), _cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                if (one[0] == (byte)'\n')
                {
                    var line = sb.ToString();
                    sb.Clear();
                    if (line.Length == 0) continue;
                    JsonObject json;
                    try { json = JsonNode.Parse(line)!.AsObject(); }
                    catch { continue; }
                    if (json.ContainsKey("event") && json["event"] is not null)
                        EventReceived?.Invoke(json);
                    else
                        _pending?.TrySetResult(json);
                }
                else if (one[0] != (byte)'\r')
                {
                    sb.Append((char)one[0]);
                }
            }
        }
        catch (Exception) { /* connection closed */ }
    }

    public async Task<JsonObject> StartAsync(
        string baseName,
        int uploadPort,
        string? pcHost = null,
        int sampleRate = 44100,
        int channels = 1,
        int bitrateKbps = 192,
        int quality = 5,
        CancellationToken ct = default)
    {
        var cmd = new JsonObject
        {
            ["cmd"] = "start",
            ["name"] = baseName,
            ["pcPort"] = uploadPort,
            ["sampleRate"] = sampleRate,
            ["channels"] = channels,
            ["bitrateKbps"] = bitrateKbps,
            ["quality"] = quality
        };
        if (!string.IsNullOrWhiteSpace(pcHost)) cmd["pcHost"] = pcHost;
        return await SendAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches a recorded file from the phone over a DEDICATED connection.
    /// The PC is the one connecting, so this works even when Windows Firewall
    /// blocks the phone's inbound push to the upload port (the usual case).
    /// </summary>
    public async Task<string> PullFileAsync(string name, string destPath, CancellationToken ct = default)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(Host, Port, ct).ConfigureAwait(false);
        using var stream = client.GetStream();

        var cmd = System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["cmd"] = "pull", ["name"] = name }.ToJsonString() + "\n");
        await stream.WriteAsync(cmd, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        // The phone broadcasts {"event":...} lines to every client, so skip anything
        // that is not the actual reply to our "pull" command.
        var sb = new StringBuilder();
        var one = new byte[1];
        JsonObject? reply = null;
        for (var lines = 0; lines < 200 && reply == null; lines++)
        {
            sb.Clear();
            while (true)
            {
                var n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (n <= 0) throw new IOException("connection closed before header");
                if (one[0] == (byte)'\n') break;
                if (one[0] != (byte)'\r') sb.Append((char)one[0]);
            }
            if (sb.Length == 0) continue;
            JsonObject? parsed = null;
            try { parsed = JsonNode.Parse(sb.ToString())?.AsObject(); } catch { }
            if (parsed != null && parsed.ContainsKey("ok")) reply = parsed;
        }

        var json = reply ?? throw new IOException("no reply to pull");
        if (json["ok"]?.GetValue<bool>() != true)
            throw new IOException(json["error"]?.ToString() ?? "pull refused");
        var total = json["bytes"]!.GetValue<long>();

        var dir = Path.GetDirectoryName(Path.GetFullPath(destPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        long got = 0;
        await using (var fs = File.Create(destPath))
        {
            var buf = new byte[1 << 16];
            while (got < total)
            {
                var want = (int)Math.Min(buf.Length, total - got);
                var n = await stream.ReadAsync(buf.AsMemory(0, want), ct).ConfigureAwait(false);
                if (n <= 0) break;
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                got += n;
            }
        }
        if (got != total)
        {
            try { File.Delete(destPath); } catch { }
            throw new IOException($"short read: {got}/{total} bytes");
        }
        return destPath;
    }

    public Task<JsonObject> StopAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["cmd"] = "stop" }, ct);

    public Task<JsonObject> StatusAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["cmd"] = "status" }, ct);

    public Task<JsonObject> ListAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["cmd"] = "list" }, ct);

    public void Dispose()
    {
        _cts.Cancel();
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
    }
}
