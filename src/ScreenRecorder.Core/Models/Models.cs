namespace ScreenRecorder.Core.Models;
using ScreenRecorder.Core.Enums;

public sealed record CaptureSource(string Id, string Name, CaptureMode Mode, int Width, int Height, int X = 0, int Y = 0);
public sealed record AudioDevice(string Id, string Name, bool IsInput);
public enum LogoPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center
}

public sealed record RecordingOptions(
    CaptureSource Source, 
    AudioSourceMode AudioMode, 
    string? SystemAudioDeviceId, 
    string? MicrophoneDeviceId, 
    string OutputPath, 
    int FramesPerSecond, 
    int VideoBitrateKbps,
    bool ShowCursor = true,
    bool ShowLogo = false,
    string? LogoPath = null,
    string LogoText = "RECORDER",
    float LogoOpacity = 0.5f,
    float LogoScale = 1.0f,
    LogoPosition LogoPosition = LogoPosition.TopRight);
