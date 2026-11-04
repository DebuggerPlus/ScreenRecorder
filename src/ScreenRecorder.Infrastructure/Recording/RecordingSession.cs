using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Core.Enums;
using ScreenRecorder.Infrastructure.Encoding;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace ScreenRecorder.Infrastructure.Recording;

public class RecordingSession : IRecordingSession
{
    private readonly IVideoCapture _videoCapture;
    private readonly IAudioCapture _audioCapture;
    private readonly FFmpegEncoder _encoder;
    private readonly ILogger<RecordingSession> _logger;
    private RecordingState _state = RecordingState.Idle;
    private Stopwatch _stopwatch = new();
    private System.Timers.Timer _timer;

    public RecordingSession(IVideoCapture videoCapture, IAudioCapture audioCapture, FFmpegEncoder encoder, ILogger<RecordingSession> logger)
    {
        _videoCapture = videoCapture;
        _audioCapture = audioCapture;
        _encoder = encoder;
        _logger = logger;
        
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (s, e) => Tick?.Invoke(_stopwatch.Elapsed);
    }

    public RecordingState State 
    { 
        get => _state; 
        private set 
        { 
            _state = value; 
            StateChanged?.Invoke(_state); 
        } 
    }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public event Action<RecordingState>? StateChanged;
    public event Action<TimeSpan>? Tick;

    public async Task StartAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        if (State != RecordingState.Idle) return;

        State = RecordingState.Preparing;
        try 
        {
            _encoder.Start(options.OutputPath, options.Source.Width, options.Source.Height, options.FramesPerSecond, options.VideoBitrateKbps);
            await _videoCapture.StartAsync(options.Source, cancellationToken);
            await _audioCapture.StartAsync(options.AudioMode, options.SystemAudioDeviceId, options.MicrophoneDeviceId, cancellationToken);
            
            _stopwatch.Restart();
            _timer.Start();
            State = RecordingState.Recording;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording");
            State = RecordingState.Error;
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        if (State != RecordingState.Recording) return;
        
        await _videoCapture.PauseAsync(cancellationToken);
        _stopwatch.Stop();
        State = RecordingState.Paused;
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (State != RecordingState.Paused) return;

        await _videoCapture.ResumeAsync(cancellationToken);
        _stopwatch.Start();
        State = RecordingState.Recording;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (State != RecordingState.Recording && State != RecordingState.Paused) return;

        State = RecordingState.Stopping;
        _timer.Stop();
        _stopwatch.Stop();

        await _audioCapture.StopAsync(cancellationToken);
        await _videoCapture.StopAsync(cancellationToken);
        _encoder.Stop();

        State = RecordingState.Idle;
    }
}