using ScreenRecorder.Core.Enums;
using ScreenRecorder.Core.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenRecorder.Core.Interfaces;

public interface ICaptureSourceProvider {
    Task<IReadOnlyList<CaptureSource>> GetSourcesAsync(CaptureMode mode, CancellationToken cancellationToken);
}

public interface IVideoCapture {
    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IAudioCapture {
    bool IsRunning { get; }
    string? LastError { get; }
    AudioCaptureReport? LastReport { get; }
    /// <summary>Уровень сигнала (после обработки), примерно 10 раз в секунду. Вызывается из фонового потока.</summary>
    event Action<AudioLevel>? LevelMeasured;
    AudioCaptureStats? GetStats();
    Task StartAsync(AudioCaptureRequest request, CancellationToken ct);
    Task StopAsync();
}

public interface IAudioDeviceService {
    IReadOnlyList<AudioDevice> GetDevices();
    /// <summary>Короткое описание формата устройства, например "48000 Hz, 32-bit float, 2 ch".</summary>
    string DescribeMixFormat(string deviceId);
    /// <summary>Перебирает типовые форматы и сообщает, какие драйвер принимает в shared/exclusive режиме.</summary>
    Task<string> ProbeFormatsAsync(string deviceId);
}

public interface IAudioTestService {
    bool IsRunning { get; }
    event Action<AudioLevel>? Level;
    AudioCaptureStats? GetLiveStats();
    /// <summary>Запускает запись звука без видео. Возвращает текст ошибки или null при успехе.</summary>
    Task<string?> StartAsync(AudioTestOptions options);
    Task<AudioTestResult> StopAsync();
    /// <summary>Прогоняет ранее записанный raw-файл через текущую цепочку обработки.</summary>
    Task<AudioTestResult> ReprocessAsync(string rawWavPath, AudioCaptureSettings settings, AudioTestContainer container);
    /// <summary>Анализирует любой аудио/видеофайл (не-WAV предварительно конвертируется через ffmpeg).</summary>
    Task<AudioAnalysis> AnalyzeFileAsync(string path);
}

public interface IRecordingSession {
    RecordingState State { get; }
    TimeSpan Elapsed { get; }
    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    event Action<RecordingState>? StateChanged;
    event Action<TimeSpan>? Tick;
}

public interface IVideoProcessor {
    bool IsRunning { get; }
    /// <summary>Читает параметры файла через ffprobe (нет ffprobe — вернёт MediaInfo.Empty).</summary>
    Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default);
    /// <summary>Обрабатывает файл целиком (или отрезок, если задан LimitSeconds) и пишет новый файл.</summary>
    Task<VideoProcessingResult> RunAsync(VideoProcessingSettings settings, IProgress<VideoProcessingProgress>? progress, CancellationToken ct = default);
    /// <summary>Прерывает текущий прогон (kill процесса ffmpeg).</summary>
    void Cancel();
}

public interface ILoggerService {
    ObservableCollection<string> LogEntries { get; }
    void Log(string message);
}