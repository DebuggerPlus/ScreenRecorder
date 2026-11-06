using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using ScreenRecorder.Core.Interfaces;

namespace ScreenRecorder.Infrastructure.Audio;

public class WasapiAudioCapture : IAudioCapture
{
    private IWaveIn? _captureDevice;
    private WaveFileWriter? _writer;
    private WaveFormat? _format;
    private readonly ILoggerService _logger;
    private readonly Stopwatch _clock = new();
    private readonly object _lock = new();
    private long _writtenBytes;
    private TaskCompletionSource? _stopped;

    public WasapiAudioCapture(ILoggerService logger) => _logger = logger;

    public Task StartAsync(string deviceId, string outputPath, CancellationToken ct)
    {
        try
        {
            _logger.Log($"NAudio: Starting capture for {deviceId}");
            var id = deviceId.ToLowerInvariant();

            if (deviceId.Contains("System") || id.Contains("speaker") || id.Contains("output"))
                _captureDevice = new WasapiLoopbackCapture();
            else
                // WaveInEvent по умолчанию = 8 кГц моно -> глухой звук. Задаём нормальный формат.
                _captureDevice = new WaveInEvent { WaveFormat = new WaveFormat(48000, 16, 1) };

            _format = _captureDevice.WaveFormat;
            _writer = new WaveFileWriter(outputPath, _format);
            _writtenBytes = 0;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _captureDevice.DataAvailable += (s, e) =>
            {
                lock (_lock)
                {
                    if (_writer == null) return;
                    // Данные e.Buffer закончились "сейчас" -> начались на (elapsed - длительность буфера).
                    // Если до этого места в файле дыра (loopback молчит при тишине) — забиваем тишиной.
                    PadSilenceTo(BytesAt(_clock.Elapsed) - e.BytesRecorded);
                    _writer.Write(e.Buffer, 0, e.BytesRecorded);
                    _writtenBytes += e.BytesRecorded;
                }
            };

            _captureDevice.RecordingStopped += (s, e) =>
            {
                lock (_lock)
                {
                    PadSilenceTo(BytesAt(_clock.Elapsed));   // дотянуть до реальной длительности
                    _writer?.Dispose();                      // тут финализируется WAV-заголовок
                    _writer = null;
                }
                _captureDevice?.Dispose();
                _captureDevice = null;
                _stopped?.TrySetResult();
            };

            _clock.Restart();
            _captureDevice.StartRecording();
            _logger.Log($"NAudio: Capture started ({_format}).");
        }
        catch (Exception ex)
        {
            _logger.Log($"NAudio Error: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_captureDevice == null) return;
        _captureDevice.StopRecording();
        // StopRecording асинхронный: ждём, пока файл реально закрыт, иначе ffmpeg склеит недописанный WAV
        if (_stopped != null) await Task.WhenAny(_stopped.Task, Task.Delay(3000));
        _logger.Log("NAudio: Capture stopped.");
    }

    private long BytesAt(TimeSpan t)
    {
        if (_format == null) return 0;
        long b = (long)(t.TotalSeconds * _format.AverageBytesPerSecond);
        return b - b % _format.BlockAlign;
    }

    // Допускаем 50 мс разброса, чтобы не вставлять тишину из-за джиттера буферов
    private void PadSilenceTo(long targetBytes)
    {
        if (_writer == null || _format == null) return;
        long tolerance = BytesAt(TimeSpan.FromMilliseconds(50));
        long gap = targetBytes - _writtenBytes;
        if (gap <= tolerance) return;

        gap -= gap % _format.BlockAlign;
        var zeros = new byte[32 * 1024 - (32 * 1024 % _format.BlockAlign)];
        while (gap > 0)
        {
            int n = (int)Math.Min(gap, zeros.Length);
            _writer.Write(zeros, 0, n);     // нули = тишина и для PCM16, и для float32
            _writtenBytes += n;
            gap -= n;
        }
    }
}
