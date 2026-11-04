using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using ScreenRecorder.Core.Interfaces;
using System.Threading.Tasks;

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
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(data, 0, data.Length);
                await _process.StandardInput.BaseStream.FlushAsync();
            }
            catch (System.Exception ex)
            {
                _uiLogger.Log($"FFmpeg Write Error: {ex.Message}");
            }
        }
    }

    public void Start(string outputPath, int width, int height, int fps, int bitrate)
    {
        width = (width / 2) * 2;
        height = (height / 2) * 2;

        // Используем аргументы списком для безопасности путей с пробелами
        var args = $"-y -f rawvideo -pixel_format bgra -video_size {width}x{height} -framerate {fps} -i - -c:v libx264 -pix_fmt yuv420p -preset ultrafast -b:v {bitrate}k \"{outputPath}\"";
        
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
        
        try 
        {
            _process.Start();
            _process.BeginErrorReadLine();
            _uiLogger.Log($"FFmpeg process started for {width}x{height}");
        }
        catch (System.Exception ex)
        {
            _uiLogger.Log($"CRITICAL: Could not start FFmpeg: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_process != null && !_process.HasExited)
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(3000))
            {
                _process.Kill();
            }
            _process.Dispose();
            _process = null;
            _uiLogger.Log("FFmpeg process stopped.");
        }
    }
}