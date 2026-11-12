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
    /// <summary>Настройки окна обработки видео (шумоподавление и т.п.).</summary>
    public VideoProcessingSettings VideoProcessing { get; set; } = new();
    /// <summary>Аудио с телефона (PhoneAudioRecorder). По умолчанию выключено.</summary>
    public PhoneAudioSettings PhoneAudio { get; set; } = new();
    /// <summary>Имя файла последнего применённого профиля из configs\*.json.</summary>
    public string? LastProfileFile { get; set; }
    /// <summary>Версия схемы настроек окна обработки видео (для одноразовых миграций).</summary>
    public int VideoProcessingSchema { get; set; }
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
        Current.VideoProcessing ??= new VideoProcessingSettings();
        MigrateVideoProcessing();
        Current.VideoProcessing.Normalize();
        Current.PhoneAudio ??= new PhoneAudioSettings();
        Current.PhoneAudio.Normalize();
        if (string.IsNullOrWhiteSpace(Current.OutputFolder))
            Current.OutputFolder = new AppSettings().OutputFolder;
    }

    /// <summary>
    /// Одноразовая миграция настроек окна обработки видео.
    /// Схема 0 (то, что сохранялось до первого запуска с рецептами по умолчанию) открывала окно
    /// с полностью выключенным шумоподавлением: «Process» выдавал почти копию входного файла,
    /// и разницы на слух не было. Здесь пустая цепочка один раз заменяется рабочим рецептом.
    /// </summary>
    private void MigrateVideoProcessing()
    {
        if (Current.VideoProcessingSchema >= 1) return;
        var v = Current.VideoProcessing ?? new VideoProcessingSettings();
        if (!v.NoiseReduction.IsEnabled)
        {
            var keepInput = v.InputPath;
            var keepOutput = v.OutputPath;
            v.NoiseReduction = NoiseReductionSettings.CreateDefault();
            v.InputPath = keepInput;
            v.OutputPath = keepOutput;
        }
        Current.VideoProcessing = v;
        Current.VideoProcessingSchema = 1;
        Save();   // миграция сохраняется сразу — иначе она повторялась бы при каждом запуске
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions)); }
        catch { /* папка программы может быть недоступна для записи: настройки просто не сохранятся */ }
    }
}