namespace ScreenRecorder.Core.Models;

public sealed record RecordingFile(
    string Path,
    string Name,
    DateTime CreatedAt,
    string Size);