using System;
using System.IO;
using System.Text.Json;

namespace ScreenRecorder.App.Services;

public class AppSettings
{
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder");
    public string? LastAudioDevice { get; set; }
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
    }

    public void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(Current));
    }
}