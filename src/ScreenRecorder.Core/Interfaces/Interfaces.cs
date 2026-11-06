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
    Task StartAsync(string deviceId, string outputPath, CancellationToken ct);
    Task StopAsync();
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

public interface ILoggerService {
    ObservableCollection<string> LogEntries { get; }
    void Log(string message);
}