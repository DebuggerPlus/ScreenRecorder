using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ScreenRecorder.Infrastructure.PhoneAudio;

/// <summary>
/// Receives finished recordings from the phone.
/// Wire format: one JSON header line (name/bytes/sha256/...), then exactly `bytes` raw bytes.
/// </summary>
public sealed class PhoneUploadServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _waiters = new();
    private readonly Action<string> _log;

    public int Port { get; }
    public string Directory { get; }
    public string? LastReceivedPath { get; private set; }

    public PhoneUploadServer(int port, string directory, Action<string>? log = null)
    {
        Port = port;
        Directory = directory;
        _log = log ?? Console.WriteLine;
        System.IO.Directory.CreateDirectory(directory);
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        _log($"[upload] listening on 0.0.0.0:{Port} -> {Directory}");
    }

    /// <summary>Waits until a file with this name (or this base name) has been uploaded.</summary>
    public async Task<string?> WaitForFileAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        var key = Normalize(name);
        var tcs = _waiters.GetOrAdd(key, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static string Normalize(string name) => Path.GetFileNameWithoutExtension(name);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log($"[upload] accept failed: {ex.Message}"); break; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                var headerLine = await ReadLineAsync(stream).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(headerLine))
                {
                    _log($"[upload] {remote}: empty header");
                    return;
                }

                JsonObject header;
                try { header = JsonNode.Parse(headerLine)!.AsObject(); }
                catch (Exception ex)
                {
                    _log($"[upload] {remote}: bad header json: {ex.Message}");
                    return;
                }

                var name = Path.GetFileName(header["name"]?.GetValue<string>() ?? "recording.mp3");
                if (string.IsNullOrWhiteSpace(name)) name = "recording.mp3";
                var bytes = header["bytes"]?.GetValue<long>() ?? -1;
                var expectedSha = header["sha256"]?.GetValue<string>();

                var target = Path.Combine(Directory, name);
                var tmp = target + ".part";

                long written = 0;
                using (var sha = SHA256.Create())
                using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    while (bytes < 0 || written < bytes)
                    {
                        var want = bytes < 0 ? buffer.Length : (int)Math.Min(buffer.Length, bytes - written);
                        if (want <= 0) break;
                        var read = await stream.ReadAsync(buffer.AsMemory(0, want)).ConfigureAwait(false);
                        if (read <= 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        written += read;
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                    var actualSha = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
                    var ok = bytes < 0 || written == bytes;
                    if (ok && expectedSha != null)
                        ok = string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase);

                    await output.FlushAsync().ConfigureAwait(false);
                    output.Close();

                    if (!ok)
                    {
                        File.Delete(tmp);
                        await WriteLineAsync(stream, new JsonObject
                        {
                            ["ok"] = false,
                            ["error"] = $"size/hash mismatch: got {written}/{bytes} bytes"
                        }).ConfigureAwait(false);
                        _log($"[upload] {remote}: REJECTED {name} (got {written} of {bytes} bytes)");
                        return;
                    }

                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);
                }

                LastReceivedPath = target;
                await WriteLineAsync(stream, new JsonObject
                {
                    ["ok"] = true,
                    ["saved"] = target,
                    ["bytes"] = written
                }).ConfigureAwait(false);

                _log($"[upload] received {name} ({written} bytes) -> {target}");

                var key = Normalize(name);
                _waiters.GetOrAdd(key, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(target);
            }
        }
        catch (Exception ex)
        {
            _log($"[upload] {remote}: failed: {ex.Message}");
        }
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false);
            if (n <= 0) return sb.Length == 0 ? null : sb.ToString();
            if (one[0] == (byte)'\n') return sb.ToString();
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
            if (sb.Length > 65536) throw new InvalidDataException("header line too long");
        }
    }

    private static async Task WriteLineAsync(NetworkStream stream, JsonObject json)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString() + "\n");
        await stream.WriteAsync(data).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
