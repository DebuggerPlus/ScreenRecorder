using System;
using System.IO;
using System.Text.Json;

namespace ScreenRecorder.App.Services;

public class AppSettings
{
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder");
}

public class SettingsService
{
    private readonly string _path;
    public AppSettings Current { get; private set; }

    public SettingsService()
    {
        _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        Load();
    }

    public void Load()
    {
        if (File.Exists(_path))
            Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        else
            Current = new AppSettings();
    }

    public void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(Current));
    }
}