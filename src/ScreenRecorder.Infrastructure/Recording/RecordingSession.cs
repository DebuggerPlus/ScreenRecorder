using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Core.Enums;
using ScreenRecorder.Infrastructure.Encoding;

namespace ScreenRecorder.Infrastructure.Recording;

public class RecordingSession : IRecordingSession
{
    private readonly IVideoCapture _videoCapture;
    private readonly IAudioCapture _audioCapture;
    private readonly FFmpegEncoder _encoder;
    private readonly ILoggerService _logger;
    private RecordingState _state = RecordingState.Idle;
    private Stopwatch _stopwatch = new();
    private System.Timers.Timer _timer;
    private string? _tempAudioPath;
    private string? _finalVideoPath;

    public RecordingSession(IVideoCapture videoCapture, IAudioCapture audioCapture, FFmpegEncoder encoder, ILoggerService logger)
    {
        _videoCapture = videoCapture;
        _audioCapture = audioCapture;
        _encoder = encoder;
        _logger = logger;
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (s, e) => Tick?.Invoke(_stopwatch.Elapsed);
    }

    public RecordingState State { get => _state; private set { _state = value; StateChanged?.Invoke(_state); } }
    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public event Action<RecordingState>? StateChanged;
    public event Action<TimeSpan>? Tick;

    public async Task StartAsync(RecordingOptions options, CancellationToken ct)
    {
        _finalVideoPath = options.OutputPath;
        _tempAudioPath = Path.ChangeExtension(options.OutputPath, ".wav");
        
        // 1. Стартуем видео (без аудио внутри FFmpeg)
        _encoder.Start(options.OutputPath + ".tmp.mp4", options.Source.Width, options.Source.Height, options.FramesPerSecond, options.VideoBitrateKbps, null);
        await _videoCapture.StartAsync(options, ct);

        // 2. Стартуем аудио отдельно через NAudio
        if (options.SystemAudioDeviceId != "None")
        {
            await _audioCapture.StartAsync(options.SystemAudioDeviceId ?? "default", _tempAudioPath, ct);
        }

        _stopwatch.Restart();
        _timer.Start();
        State = RecordingState.Recording;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        State = RecordingState.Stopping;
        _timer.Stop();
        _stopwatch.Stop();

        await _videoCapture.StopAsync(ct);
        await _audioCapture.StopAsync();
        _encoder.Stop();

        // 3. Склеиваем видео и аудио
        if (File.Exists(_tempAudioPath))
        {
            _logger.Log("Merging video and audio...");
            var tempVideo = _finalVideoPath + ".tmp.mp4";
            var args = $"-y -i \"{tempVideo}\" -i \"{_tempAudioPath}\" -c:v copy -c:a aac \"{_finalVideoPath}\"";
            
            var process = Process.Start(new ProcessStartInfo("ffmpeg", args) { CreateNoWindow = true, UseShellExecute = false });
            process?.WaitForExit();
            
            try { File.Delete(tempVideo); File.Delete(_tempAudioPath); } catch {}
            _logger.Log("Merge complete.");
        }
        else
        {
            File.Move(_finalVideoPath + ".tmp.mp4", _finalVideoPath, true);
        }

        State = RecordingState.Idle;
    }

    public Task PauseAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
}