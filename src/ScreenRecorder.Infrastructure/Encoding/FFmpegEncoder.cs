using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Infrastructure.Encoding;

/// <summary>
/// Пишет сырые BGRA-кадры в stdin ffmpeg. Параметры кодирования берутся из VideoEncodingSettings
/// (внешний профиль), а не из констант.
/// </summary>
public class FFmpegEncoder
{
    private Process? _process;
    private readonly ILoggerService _uiLogger;
    private long _framesWritten;
    private bool _exitLogged;

    public FFmpegEncoder(ILoggerService uiLogger)
    {
        _uiLogger = uiLogger;
    }

    public bool IsRunning => _process is { HasExited: false };
    public long FramesWritten => _framesWritten;

    public async Task WriteFrameAsync(byte[] data)
    {
        if (_process != null && !_process.HasExited)
        {
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(data, 0, data.Length);
                await _process.StandardInput.BaseStream.FlushAsync();
                _framesWritten++;
            }
            catch (Exception ex)
            {
                if (!_exitLogged)
                {
                    _exitLogged = true;
                    _uiLogger.Log($"FFmpeg: кадры больше не пишутся: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Запускает ffmpeg. Аргументы передаются токенами — пути с пробелами не ломаются.</summary>
    public async Task StartAsync(string outputPath, int width, int height, VideoEncodingSettings video, bool lowPriority = true)
    {
        Stop(); // защита от повторного старта: без неё два ffmpeg писали в один файл

        width = (width / 2) * 2;
        height = (height / 2) * 2;
        video.Normalize();

        var args = video.BuildArgs(width, height, outputPath);
        _uiLogger.Log($"FFmpeg Start: {width}x{height} @ {video.Fps}fps — {video.Describe()}");
        _uiLogger.Log($"FFmpeg args: {video.DescribeArgs(width, height, outputPath)}");

        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _framesWritten = 0;
        _exitLogged = false;
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (s, e) => { if (e.Data != null) _uiLogger.Log($"FFmpeg: {e.Data}"); };
        _process.Start();
        _process.BeginErrorReadLine();

        if (lowPriority)
        {
            // Кодирование не должно отнимать процессорное время у потока захвата звука.
            try { _process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) { _uiLogger.Log($"FFmpeg: could not lower priority: {ex.Message}"); }
        }

        // Если энкодер/параметры не поддержаны (например h264_qsv без драйвера), ffmpeg падает сразу.
        await Task.Delay(250);
        if (_process.HasExited)
            _uiLogger.Log($"FFmpeg: энкодер завершился сразу (код {_process.ExitCode}) — видео записано не будет. Проверьте профиль.");
    }

    public void Stop()
    {
        if (_process == null) return;
        try
        {
            if (!_process.HasExited)
            {
                try { _process.StandardInput.Close(); } catch { }
                _process.WaitForExit(4000);
                if (!_process.HasExited)
                {
                    _uiLogger.Log("FFmpeg: не завершился сам, прерываю (файл может быть недописан).");
                    _process.Kill(true);
                    _process.WaitForExit(2000);
                }
            }
            _uiLogger.Log($"FFmpeg: записано кадров {_framesWritten}, код выхода {(_process.HasExited ? _process.ExitCode : -1)}");
        }
        catch (Exception ex) { _uiLogger.Log($"FFmpeg stop: {ex.Message}"); }
        finally
        {
            try { _process.Dispose(); } catch { }
            _process = null;
        }
    }
}
