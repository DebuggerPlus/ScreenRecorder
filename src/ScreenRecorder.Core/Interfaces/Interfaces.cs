using ScreenRecorder.Core.Enums;
using ScreenRecorder.Core.Models;
namespace ScreenRecorder.Core.Interfaces;

public interface ICaptureSourceProvider {
    Task<IReadOnlyList<CaptureSource>> GetSourcesAsync(CaptureMode mode, CancellationToken cancellationToken);
}
public interface IVideoCapture {
    Task StartAsync(CaptureSource source, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
public interface IAudioCapture {
    Task StartAsync(AudioSourceMode mode, string? systemDeviceId, string? microphoneDeviceId, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
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