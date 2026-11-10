using System;
using System.IO;
using System.Text.Json;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.App.Services;

public class AppSettings
{
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder");
    public string? LastAudioDevice { get; set; }
    public bool ShowLogo { get; set; } = false;
    public string LogoText { get; set; } = "RECORDER";
    public float LogoOpacity { get; set; } = 0.5f;
    public float LogoScale { get; set; } = 1.0f;
    public string? LogoPath { get; set; }
    public LogoPosition LogoPosition { get; set; } = LogoPosition.TopRight;
    public AudioCaptureSettings Audio { get; set; } = new();
}

public class SettingsService
{
    private readonly string _path;
    public AppSettings Current { get; private set; } = new AppSettings();

    public SettingsService()
    {
        _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        Load();
    }

    public void Load()
    {
        try 
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { Current = new AppSettings(); }
        Current.Audio ??= new AudioCaptureSettings();
        Current.Audio.Normalize();
        if (string.IsNullOrWhiteSpace(Current.OutputFolder))
            Current.OutputFolder = new AppSettings().OutputFolder;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions)); }
        catch { /* папка программы может быть недоступна для записи: настройки просто не сохранятся */ }
    }
}