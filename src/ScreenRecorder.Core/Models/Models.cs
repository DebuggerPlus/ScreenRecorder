using ScreenRecorder.Core.Enums;
namespace ScreenRecorder.Core.Models;

public sealed record CaptureSource(string Id, string Name, CaptureMode Mode, int Width, int Height, int X = 0, int Y = 0);
public sealed record AudioDevice(string Id, string Name, bool IsInput);
public sealed record RecordingOptions(
    CaptureSource Source, 
    AudioSourceMode AudioMode, 
    string? SystemAudioDeviceId, 
    string? MicrophoneDeviceId, 
    string OutputPath, 
    int FramesPerSecond, 
    int VideoBitrateKbps,
    bool ShowCursor = true);