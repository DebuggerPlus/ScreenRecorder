using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Enums;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Infrastructure.Encoding;

namespace ScreenRecorder.Infrastructure.Recording;

public class RecordingSession : IRecordingSession
{
    private readonly IVideoCapture _videoCapture;
    private readonly IAudioCapture _audioCapture;
    private readonly FFmpegEncoder _encoder;
    private readonly ILoggerService _logger;
    private RecordingState _state = RecordingState.Idle;
    private readonly Stopwatch _stopwatch = new();
    private readonly System.Timers.Timer _timer;
    private string? _tempAudioPath;
    private AudioCaptureSettings _audioSettings = new();
    private VideoEncodingSettings _videoSettings = new();
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
        // Раньше двойной старт (двойной клик по кнопке) поднимал ВТОРОЙ ffmpeg и второй цикл захвата
        // в тот же файл — в логе за 11.11 это видно как два START подряд, и mp4 в итоге не собрался.
        if (_state is RecordingState.Preparing or RecordingState.Recording or RecordingState.Stopping)
        {
            _logger.Log($"START ignored: запись уже идёт (state={_state})");
            return;
        }

        State = RecordingState.Preparing;
        _finalVideoPath = options.OutputPath;
        _tempAudioPath = Path.ChangeExtension(options.OutputPath, ".wav");
        _audioSettings = options.AudioSettings?.Clone() ?? new AudioCaptureSettings();
        _audioSettings.Normalize();
        _videoSettings = options.Video?.Clone() ?? new VideoEncodingSettings
        {
            Fps = options.FramesPerSecond > 0 ? options.FramesPerSecond : 30,
            RateControl = VideoRateControl.Bitrate,
            BitrateKbps = options.VideoBitrateKbps > 0 ? options.VideoBitrateKbps : 12000
        };
        _videoSettings.Normalize();

        _logger.Log($"Recording profile: {_videoSettings.Describe()}");
        _logger.Log($"Audio settings: {_audioSettings.Describe()}");

        try
        {
            // 1. Стартуем видео (без аудио внутри FFmpeg)
            await _encoder.StartAsync(options.OutputPath + ".tmp.mp4", options.Source.Width, options.Source.Height, _videoSettings, _audioSettings.LowerEncoderPriority);
            await _videoCapture.StartAsync(options, ct);

            // 2. Стартуем аудио отдельно через NAudio
            if (!string.IsNullOrEmpty(options.SystemAudioDeviceId) && options.SystemAudioDeviceId != "None")
            {
                await _audioCapture.StartAsync(new AudioCaptureRequest(options.SystemAudioDeviceId, _tempAudioPath, _audioSettings), ct);
            }

            _stopwatch.Restart();
            _timer.Start();
            State = RecordingState.Recording;
        }
        catch (Exception ex)
        {
            _logger.Log($"START FAILED: {ex.Message}");
            try { _encoder.Stop(); } catch { }
            State = RecordingState.Idle;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_state != RecordingState.Recording)
        {
            _logger.Log($"STOP ignored: запись не идёт (state={_state})");
            return;
        }

        State = RecordingState.Stopping;
        _timer.Stop();
        _stopwatch.Stop();

        try { await _videoCapture.StopAsync(ct); } catch (Exception ex) { _logger.Log($"video stop: {ex.Message}"); }
        try { await _audioCapture.StopAsync(); } catch (Exception ex) { _logger.Log($"audio stop: {ex.Message}"); }
        try { _encoder.Stop(); } catch (Exception ex) { _logger.Log($"encoder stop: {ex.Message}"); }

        var tempVideo = _finalVideoPath + ".tmp.mp4";

        if (!File.Exists(tempVideo))
        {
            _logger.Log($"STOP: {tempVideo} не создан — видео нет (проверьте профиль и лог FFmpeg).");
            State = RecordingState.Idle;
            return;
        }

        // 3. Склеиваем видео и аудио
        if (!string.IsNullOrEmpty(_tempAudioPath) && File.Exists(_tempAudioPath))
        {
            _logger.Log("Merging video and audio...");
            // Шумоподавление при съёмке: та же цепочка, что в окне обработки, применяется к записанному WAV
            // здесь, на склейке. В живой поток NAudio её не вставить (afftdn/arnndn — фильтры ffmpeg).
            var denoiseChain = RecordingDenoise.BuildChain(_audioSettings);
            if (denoiseChain.Length > 0)
                _logger.Log($"Denoise on record: recipe '{_audioSettings.DenoiseRecipe}' -> -af {denoiseChain}");
            try
            {
                var (code, err) = await FfmpegRunner.RunAsync(RecordingDenoise.BuildMergeArgs(
                    tempVideo, _tempAudioPath, _finalVideoPath!, _audioSettings.AacBitrateKbps, denoiseChain));

                if (code != 0 && denoiseChain.Length > 0)
                {
                    // Фильтр не должен стоить нам звука: повторяем склейку без него.
                    _logger.Log($"Merge with denoise FAILED (ffmpeg exit {code}) — retrying without filters.");
                    (code, err) = await FfmpegRunner.RunAsync(RecordingDenoise.BuildMergeArgs(
                        tempVideo, _tempAudioPath, _finalVideoPath!, _audioSettings.AacBitrateKbps, null));
                }

                if (code == 0 && File.Exists(_finalVideoPath!))
                {
                    try { File.Delete(tempVideo); } catch { }
                    if (_audioSettings.KeepRawAudio)
                        _logger.Log($"Raw audio kept (before denoise): {_tempAudioPath}");
                    else
                        try { File.Delete(_tempAudioPath); } catch { }
                    _logger.Log(denoiseChain.Length > 0 ? "Merge complete (denoise applied)." : "Merge complete.");
                }
                else
                {
                    // Не теряем запись: оставляем видео без звука и исходный WAV рядом.
                    _logger.Log($"Merge FAILED (ffmpeg exit {code}). Video saved without audio; WAV kept: {_tempAudioPath}");
                    var tail = string.Join(" | ", err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(2));
                    if (tail.Length > 0) _logger.Log($"FFmpeg: {tail}");
                    try { File.Move(tempVideo, _finalVideoPath!, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Merge error: {ex.Message}. Video saved without audio; WAV kept: {_tempAudioPath}");
                try { File.Move(tempVideo, _finalVideoPath!, true); } catch { }
            }
        }
        else
        {
            try { File.Move(tempVideo, _finalVideoPath!, true); }
            catch (Exception ex) { _logger.Log($"Could not finalize {_finalVideoPath}: {ex.Message}"); }
        }

        State = RecordingState.Idle;
    }

    public Task PauseAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
}
