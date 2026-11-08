using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorder.Core.Interfaces;

namespace ScreenRecorder.Infrastructure.Audio;

public class WasapiAudioCapture : IAudioCapture
{
    private IWaveIn? _capture;
    private WaveFileWriter? _writer;
    private WaveFormat? _format;
    private readonly ILoggerService _logger;
    private readonly Stopwatch _clock = new();
    private readonly object _lock = new();
    private long _writtenBytes;
    private TaskCompletionSource? _stopped;

    // Тишину в середине потока вставляем ТОЛЬКО для loopback (там пакеты реально не приходят при тишине).
    // Микрофон / виртуальный микрофон (iVCam) присылает данные непрерывно; нули посреди речи = треск.
    private bool _padGaps;
    private bool _first = true;

    // Диагностика
    private long _paddedBytes, _packets;
    private double _maxGapMs;
    private TimeSpan _lastPacketAt;

    public WasapiAudioCapture(ILoggerService logger) => _logger = logger;

    public Task StartAsync(string deviceId, string outputPath, CancellationToken ct)
    {
        try
        {
            _logger.Log($"Audio: starting for '{deviceId}'");
            using var enumerator = new MMDeviceEnumerator();
            var device = FindDevice(enumerator, deviceId);

            if (device == null)
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _logger.Log($"Audio: device not found, fallback to default output '{device.FriendlyName}'");
            }

            bool isRender = device.DataFlow == DataFlow.Render;
            _padGaps = isRender;

            // Формат НЕ навязываем: берём тот, в котором работает драйвер (mix format).
            _capture = isRender
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device, true, 50);   // event-driven, буфер 50 мс

            _format = _capture.WaveFormat;
            _logger.Log($"Audio: '{device.FriendlyName}' {(isRender ? "loopback" : "capture")} format: {_format}");

            _writer = new WaveFileWriter(outputPath, _format);
            _writtenBytes = 0; _paddedBytes = 0; _packets = 0; _maxGapMs = 0;
            _first = true;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;

            _clock.Restart();
            _lastPacketAt = TimeSpan.Zero;
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            _logger.Log($"Audio Error: {ex}");
        }
        return Task.CompletedTask;
    }

    private static MMDevice? FindDevice(MMDeviceEnumerator en, string idOrName)
    {
        try { return en.GetDevice(idOrName); } catch { }
        var all = en.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
        return all.FirstOrDefault(d => d.ID.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.FriendlyName.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.FriendlyName.Contains(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    private void OnData(object? s, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_writer == null || _format == null) return;

            var now = _clock.Elapsed;
            _packets++;
            _maxGapMs = Math.Max(_maxGapMs, (now - _lastPacketAt).TotalMilliseconds);
            _lastPacketAt = now;

            // loopback — пэддинг всегда; микрофон — только перед самым первым пакетом (задержка старта устройства)
            if (_padGaps || _first)
                PadSilenceTo(BytesAt(now) - e.BytesRecorded);
            _first = false;

            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _writtenBytes += e.BytesRecorded;
        }
    }

    private void OnStopped(object? s, StoppedEventArgs e)
    {
        lock (_lock)
        {
            if (e.Exception != null) _logger.Log($"Audio: stopped with error: {e.Exception.Message}");
            PadSilenceTo(BytesAt(_clock.Elapsed));      // хвост до реальной длительности
            _writer?.Dispose();                          // финализация WAV-заголовка
            _writer = null;
            _logger.Log($"Audio: {_packets} packets, max gap {_maxGapMs:F0} ms, padded {PaddedMs():F0} ms, " +
                        $"file {_writtenBytes / (double)(_format?.AverageBytesPerSecond ?? 1):F2} s");
        }
        _capture?.Dispose();
        _capture = null;
        _stopped?.TrySetResult();
    }

    public async Task StopAsync()
    {
        if (_capture == null) return;
        _capture.StopRecording();
        if (_stopped != null) await Task.WhenAny(_stopped.Task, Task.Delay(3000));
    }

    private double PaddedMs() => _format == null ? 0 : _paddedBytes * 1000.0 / _format.AverageBytesPerSecond;

    private long BytesAt(TimeSpan t)
    {
        if (_format == null) return 0;
        long b = (long)(t.TotalSeconds * _format.AverageBytesPerSecond);
        return b - b % _format.BlockAlign;
    }

    private void PadSilenceTo(long targetBytes)
    {
        if (_writer == null || _format == null) return;
        long gap = targetBytes - _writtenBytes;
        if (gap <= BytesAt(TimeSpan.FromMilliseconds(50))) return;

        gap -= gap % _format.BlockAlign;
        var zeros = new byte[16 * 1024 - (16 * 1024 % _format.BlockAlign)];
        while (gap > 0)
        {
            int n = (int)Math.Min(gap, zeros.Length);
            _writer.Write(zeros, 0, n);
            _writtenBytes += n; _paddedBytes += n;
            gap -= n;
        }
    }
}
