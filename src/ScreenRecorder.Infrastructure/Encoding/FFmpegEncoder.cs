using System.Diagnostics;
using System.IO;
using ScreenRecorder.Core.Interfaces;
using System.Threading.Tasks;
using System;

namespace ScreenRecorder.Infrastructure.Encoding;

public class FFmpegEncoder
{
    private Process? _process;
    private readonly ILoggerService _uiLogger;

    public FFmpegEncoder(ILoggerService uiLogger)
    {
        _uiLogger = uiLogger;
    }

    public async Task WriteFrameAsync(byte[] data)
    {
        if (_process != null && !_process.HasExited)
        {
            try { 
                await _process.StandardInput.BaseStream.WriteAsync(data, 0, data.Length); 
                await _process.StandardInput.BaseStream.FlushAsync();
            }
            catch { }
        }
    }

    public void Start(string outputPath, int width, int height, int fps, int bitrate, string? audioDeviceName = null)
    {
        width = (width / 2) * 2;
        height = (height / 2) * 2;

        // Использование -re для чтения входного потока со скоростью реального времени (native frame rate)
        var args = $"-y -f rawvideo -pixel_format bgra -video_size {width}x{height} -framerate {fps} -i - " +
                   $"-c:v libx264 -pix_fmt yuv420p -preset ultrafast -crf 23 -b:v {bitrate}k " +
                   $"-r {fps} -vsync cfr \"{outputPath}\"";
        
        _uiLogger.Log($"FFmpeg Start: {width}x{height} @ {fps}fps");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _process.ErrorDataReceived += (s, e) => { if (e.Data != null) _uiLogger.Log($"FFmpeg: {e.Data}"); };
        _process.Start();
        _process.BeginErrorReadLine();
    }

    public void Stop()
    {
        if (_process != null && !_process.HasExited)
        {
            _process.StandardInput.Close();
            _process.WaitForExit(3000);
            if (!_process.HasExited) _process.Kill();
            _process.Dispose();
            _process = null;
        }
    }
}