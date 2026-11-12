using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.App.Services;

/// <summary>Профиль из папки configs вместе с путём к его файлу.</summary>
public sealed class ProfileEntry
{
    public ProfileEntry(string filePath, EncodingProfile profile)
    {
        FilePath = filePath;
        Profile = profile;
    }

    public string FilePath { get; }
    public EncodingProfile Profile { get; }
    public string Name => Profile.Name;
    public string FileName => Path.GetFileName(FilePath);
    public string DisplayName => $"{Profile.Name} [{FileName}]";
    public string ShortDescription => Profile.Description ?? string.Empty;
}

/// <summary>
/// Внешние профили кодирования: файлы *.json в папке configs рядом с программой.
/// Пользователь кладёт/правит файлы сам, приложение читает список, показывает его в UI и применяет выбранный.
/// </summary>
public sealed class ProfileService
{
    private readonly ILoggerService _logger;

    public string Folder { get; }

    public ProfileService(ILoggerService logger)
    {
        _logger = logger;
        Folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs");
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // русский текст в файле остаётся читаемым
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Читает все configs\*.json. Битые файлы пропускаются с записью в лог.</summary>
    public List<ProfileEntry> Load()
    {
        EnsureDefaults();
        var list = new List<ProfileEntry>();
        foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<EncodingProfile>(File.ReadAllText(file), ReadOptions);
                if (profile == null) { _logger.Log($"Profile: {Path.GetFileName(file)} — пустой файл, пропущен"); continue; }
                profile.Normalize();
                list.Add(new ProfileEntry(file, profile));
            }
            catch (Exception ex)
            {
                _logger.Log($"Profile: {Path.GetFileName(file)} не прочитан: {ex.Message}");
            }
        }
        if (list.Count == 0) _logger.Log($"Profile: в {Folder} нет валидных *.json");
        else _logger.Log($"Profile: загружено {list.Count} шт. из {Folder}");
        return list;
    }

    public void Save(ProfileEntry entry)
    {
        entry.Profile.Normalize();
        File.WriteAllText(entry.FilePath, JsonSerializer.Serialize(entry.Profile, WriteOptions));
        _logger.Log($"Profile: сохранён {entry.FileName}");
    }

    /// <summary>Создаёт новый файл профиля. Возвращает null, если файл уже есть.</summary>
    public ProfileEntry? SaveAs(string fileName, EncodingProfile profile)
    {
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) fileName += ".json";
        var path = Path.Combine(Folder, fileName);
        if (File.Exists(path)) return null;
        profile.Normalize();
        File.WriteAllText(path, JsonSerializer.Serialize(profile, WriteOptions));
        _logger.Log($"Profile: создан {fileName}");
        return new ProfileEntry(path, profile);
    }

    public bool Exists(string fileName)
    {
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) fileName += ".json";
        return File.Exists(Path.Combine(Folder, fileName));
    }

    /// <summary>Пишет встроенные варианты, если пользователь ещё ничего не положил в configs.</summary>
    public void EnsureDefaults()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (Directory.GetFiles(Folder, "*.json").Length > 0) return;
            foreach (var (file, profile) in Defaults())
                File.WriteAllText(Path.Combine(Folder, file), JsonSerializer.Serialize(profile, WriteOptions));
            _logger.Log($"Profile: созданы встроенные варианты в {Folder}");
        }
        catch (Exception ex)
        {
            _logger.Log($"Profile: не удалось создать {Folder}: {ex.Message}");
        }
    }

    /// <summary>Готовые варианты для теста. Аудио: усиление +15 dB, формат берётся у драйвера, выход float32 без потерь.</summary>
    public static IEnumerable<(string File, EncodingProfile Profile)> Defaults()
    {
        yield return ("01-balanced.json", new EncodingProfile
        {
            Name = "Balanced",
            Description = "Базовое улучшение: x264 veryfast, CRF 20, 30 fps, gain +15 dB. Цель — заметно лучше прежних 8000 kbps/ultrafast при умеренной нагрузке CPU.",
            Video = new VideoEncodingSettings { Fps = 30, Encoder = "libx264", Preset = "veryfast", RateControl = VideoRateControl.Crf, Crf = 20, PixelFormat = "yuv420p", KeyframeSeconds = 2 },
            Audio = StandardAudio(15f, 192)
        });

        yield return ("02-max-quality.json", new EncodingProfile
        {
            Name = "Max quality",
            Description = "x264 fast, CRF 16, 60 fps, yuv444p (максимально резкий текст), gain +15 dB, AAC 256k. Тяжёлый: следите за CPU и дропами кадров.",
            Video = new VideoEncodingSettings { Fps = 60, Encoder = "libx264", Preset = "fast", RateControl = VideoRateControl.Crf, Crf = 16, PixelFormat = "yuv444p", KeyframeSeconds = 2 },
            Audio = StandardAudio(15f, 256)
        });

        yield return ("03-low-cpu.json", new EncodingProfile
        {
            Name = "Low CPU",
            Description = "x264 ultrafast, постоянный битрейт 12 Mbps, 30 fps. Для слабого/загруженного CPU: качество выше прежнего за счёт битрейта, а не пресета.",
            Video = new VideoEncodingSettings { Fps = 30, Encoder = "libx264", Preset = "ultrafast", RateControl = VideoRateControl.Bitrate, BitrateKbps = 12000, PixelFormat = "yuv420p", KeyframeSeconds = 2 },
            Audio = StandardAudio(15f, 192)
        });

        yield return ("04-screen-text.json", new EncodingProfile
        {
            Name = "Screen text 30",
            Description = "x264 veryfast, CRF 18, 30 fps, yuv420p — компромисс: резкий текст и совместимый файл. Рекомендуется как основной для записи экрана.",
            Video = new VideoEncodingSettings { Fps = 30, Encoder = "libx264", Preset = "veryfast", RateControl = VideoRateControl.Crf, Crf = 18, PixelFormat = "yuv420p", KeyframeSeconds = 2 },
            Audio = StandardAudio(15f, 256)
        });

        yield return ("05-gpu-qsv.json", new EncodingProfile
        {
            Name = "GPU QuickSync",
            Description = "h264_qsv (Intel Quick Sync), 20 Mbps, 30 fps, nv12. Разгружает CPU. Работает только если QSV доступен драйвером; проверьте лог после первого старта.",
            Video = new VideoEncodingSettings { Fps = 30, Encoder = "h264_qsv", Preset = "veryfast", RateControl = VideoRateControl.Bitrate, BitrateKbps = 20000, PixelFormat = "nv12", KeyframeSeconds = 2 },
            Audio = StandardAudio(15f, 192)
        });
    }

    /// <summary>
    /// gain: входное усиление (замеры показали пик −21…−28 dBFS — это главный резерв качества).
    /// Формат драйвера не запрашиваем: берём mix format (32-bit float), выход тоже float32 — ни одного лишнего преобразования.
    /// </summary>
    private static AudioCaptureSettings StandardAudio(float gainDb, int aacKbps) => new()
    {
        Api = AudioCaptureApi.WasapiEvent,
        BufferMs = 100,
        RequestCustomDriverFormat = false,
        OutputSampleRate = 0,
        OutputBitDepth = AudioBitDepth.Float32,
        ChannelMode = AudioChannelMode.Auto,
        InputGainDb = gainDb,
        HighPassHz = 0,
        Dither = false,
        AacBitrateKbps = aacKbps
    };
}
