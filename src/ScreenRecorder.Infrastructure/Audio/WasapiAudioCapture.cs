using System;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Interfaces;
using NAudio.Wave;

namespace ScreenRecorder.Infrastructure.Audio;

public class WasapiAudioCapture : IAudioCapture
{
    private IWaveIn? _captureDevice;
    private WaveFileWriter? _writer;
    private readonly ILoggerService _logger;

    public WasapiAudioCapture(ILoggerService logger)
    {
        _logger = logger;
    }

    public Task StartAsync(string deviceId, string outputPath, CancellationToken ct)
    {
        try
        {
            _logger.Log($"NAudio: Starting capture for {deviceId}");
            
            // Если ID содержит системные признаки - используем Loopback, иначе WaveIn (для микрофона)
            if (deviceId.Contains("System") || deviceId.ToLower().Contains("speaker") || deviceId.ToLower().Contains("output"))
            {
                _captureDevice = new WasapiLoopbackCapture();
            }
            else
            {
                _captureDevice = new WaveInEvent(); // Самый надежный для микрофона в .NET
            }

            _writer = new WaveFileWriter(outputPath, _captureDevice.WaveFormat);

            _captureDevice.DataAvailable += (s, e) =>
            {
                _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            };

            _captureDevice.RecordingStopped += (s, e) =>
            {
                _writer?.Dispose();
                _writer = null;
                _captureDevice?.Dispose();
                _captureDevice = null;
            };

            _captureDevice.StartRecording();
            _logger.Log("NAudio: Capture started.");
        }
        catch (Exception ex)
        {
            _logger.Log($"NAudio Error: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _captureDevice?.StopRecording();
        _logger.Log("NAudio: Capture stopped.");
        return Task.CompletedTask;
    }
}