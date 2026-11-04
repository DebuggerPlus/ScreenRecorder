using ScreenRecorder.Core.Enums;
namespace ScreenRecorder.Core.Models;

public sealed record CaptureSource(string Id, string Name, CaptureMode Mode, int Width, int Height);
public sealed record AudioDevice(string Id, string Name, bool IsInput);
public sealed record RecordingOptions(
    CaptureSource Source, 
    AudioSourceMode AudioMode, 
    string? SystemAudioDeviceId, 
    string? MicrophoneDeviceId, 
    string OutputPath, 
    int FramesPerSecond, 
    int VideoBitrateKbps);