using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Enums;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace ScreenRecorder.Infrastructure.Audio;

public class WasapiAudioCapture : IAudioCapture
{
    private WasapiLoopbackCapture? _loopbackCapture;
    private WasapiCapture? _micCapture; // WasapiIn is sometimes not found, WasapiCapture is more generic
    private readonly ILogger<WasapiAudioCapture> _logger;
    
    public WasapiAudioCapture(ILogger<WasapiAudioCapture> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(AudioSourceMode mode, string? systemDeviceId, string? microphoneDeviceId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting audio capture: {Mode}", mode);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _loopbackCapture?.StopRecording();
        _loopbackCapture?.Dispose();
        _micCapture?.StopRecording();
        _micCapture?.Dispose();
        return Task.CompletedTask;
    }
}