using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ScreenRecorder.Core.Models;

/// <summary>Тип шума для спектрального шумоподавления (afftdn, опция nt).</summary>
public enum NoiseType { White = 0, Vinyl = 1, Shellac = 2 }

/// <summary>Что делать с уровнем после обработки.</summary>
public enum LevelTarget
{
    Off = 0,
    /// <summary>Выравнивание громкости по EBU R128 до -16 LUFS (loudnorm).</summary>
    Loudness = 1,
    /// <summary>Ограничитель -1 dBFS, чтобы не было клиппинга (alimiter).</summary>
    Limiter = 2,
    /// <summary>Нормализация речи (speechnorm) — для голосовых записей.</summary>
    Speech = 3
}

public enum OutputContainer { Mp4 = 0, Mkv = 1 }

/// <summary>
/// Настройки шумоподавления. Каждый блок включается отдельно и складывается в цепочку ffmpeg:
/// обрезка спектра -> спектральный (afftdn) -> нейросетевой для речи (arnndn) -> гейт (agate).
/// Всё сериализуется в settings.json.
/// </summary>
public sealed class NoiseReductionSettings
{
    // --- Обрезка спектра: гул вентилятора, рокот, шипение ---
    /// <summary>Срез снизу, Гц. 0 = выключен. По умолчанию 100 Гц — рабочий рецепт «Fan + voice (RNN)».</summary>
    public int HighPassHz { get; set; } = 100;
    /// <summary>Срез сверху, Гц. 0 = выключен.</summary>
    public int LowPassHz { get; set; }

    // --- Спектральное шумоподавление (afftdn) ---
    public bool SpectralEnabled { get; set; }
    /// <summary>Оценённый уровень шума, дБ (nf). Ключевой параметр: ставится по уровню шума в паузах.</summary>
    public float NoiseFloorDb { get; set; } = -40f;
    /// <summary>На сколько дБ гасить шум (nr), 0.01..97.</summary>
    public float ReductionDb { get; set; } = 16f;
    /// <summary>Отслеживать изменение шума во времени (tn).</summary>
    public bool TrackNoise { get; set; }
    /// <summary>Отслеживать остаток после вычитания (tr).</summary>
    public bool TrackResidual { get; set; }
    /// <summary>Остаточный уровень: ниже него шум не убирается (rf), -80..-20. Меньше = чище, но больше артефактов.</summary>
    public float ResidualFloorDb { get; set; } = -60f;
    /// <summary>Тип шума (nt).</summary>
    public NoiseType Type { get; set; } = NoiseType.White;

    // --- Нейросетевое шумоподавление речи (arnndn, модель rnnoise) ---
    /// <summary>По умолчанию включено: рабочий рецепт «Fan + voice (RNN)».</summary>
    public bool RnnEnabled { get; set; } = true;
    /// <summary>Файл модели. Относительный путь считается от папки приложения.</summary>
    public string RnnModel { get; set; } = DefaultRnnModel;
    /// <summary>Доля обработанного сигнала в выходе: 1 = только модель, 0 = вход не менять (-1..1).</summary>
    public float RnnMix { get; set; } = 1f;

    // --- Гейт (agate): давит шум в паузах ---
    public bool GateEnabled { get; set; }
    /// <summary>Порог срабатывания, дБ (выше — режет больше).</summary>
    public float GateThresholdDb { get; set; } = -45f;
    /// <summary>Жёсткость закрытия.</summary>
    public float GateRatio { get; set; } = 2f;
    /// <summary>На сколько дБ давить сигнал ниже порога.</summary>
    public float GateRangeDb { get; set; } = -30f;

    public const string DefaultRnnModel = "models/rnnoise/bd.rnnn";

    /// <summary>
    /// Рецепт, который ставится при первом запуске и по кнопке «Reset settings».
    /// Причина: с полностью выключенным шумоподавлением «Process» даёт копию входного файла,
    /// и пользователь не слышит никакой разницы (проверено на живой записи).
    /// </summary>
    public const string DefaultRecipe = "Fan + voice (RNN)";

    /// <summary>Настройки по умолчанию — сразу рабочий рецепт, а не пустая цепочка.</summary>
    public static NoiseReductionSettings CreateDefault() => Recipe(DefaultRecipe);

    /// <summary>Полностью выключенная цепочка: база для рецептов («All off») и для замеров «до/после».</summary>
    public static NoiseReductionSettings CreateEmpty()
    {
        // new NoiseReductionSettings() = рабочий рецепт, поэтому все блоки гасим явно.
        var n = new NoiseReductionSettings();
        n.HighPassHz = 0;
        n.LowPassHz = 0;
        n.SpectralEnabled = false;
        n.RnnEnabled = false;
        n.GateEnabled = false;
        return n;
    }

    public static readonly int[] HighPassChoices = { 0, 40, 60, 80, 100, 150, 200, 300 };
    public static readonly int[] LowPassChoices = { 0, 8000, 12000, 16000, 20000 };

    /// <summary>Готовые рецепты для кнопок в окне обработки.</summary>
    public static readonly string[] Recipes =
    {
        "Fan / hum",
        "Fan + voice (RNN)",
        "Voice, quiet room (RNN)",
        "Pauses -> silence (gate)",
        "All off"
    };

    public static NoiseReductionSettings Recipe(string name)
    {
        // База всегда «всё выключено»: рецепт не должен зависеть от дефолтов класса.
        var n = CreateEmpty();
        switch (name)
        {
            case "Fan / hum":
                n.HighPassHz = 150;
                n.SpectralEnabled = true;
                n.NoiseFloorDb = -35; n.ReductionDb = 18; n.ResidualFloorDb = -70; n.TrackNoise = false;
                break;
            case "Fan + voice (RNN)":
                n.HighPassHz = 100;
                n.RnnEnabled = true;
                break;
            case "Voice, quiet room (RNN)":
                n.RnnEnabled = true;
                break;
            case "Pauses -> silence (gate)":
                n.GateEnabled = true;
                n.GateThresholdDb = -45; n.GateRatio = 2; n.GateRangeDb = -30;
                break;
            case "All off":
                break;
        }
        return n;
    }

    public NoiseReductionSettings Clone() => (NoiseReductionSettings)MemberwiseClone();

    /// <summary>Есть ли хоть один включённый блок.</summary>
    public bool IsEnabled => HighPassHz > 0 || LowPassHz > 0 || SpectralEnabled || RnnEnabled || GateEnabled;

    public void Normalize()
    {
        if (!Enum.IsDefined(Type)) Type = NoiseType.White;

        HighPassHz = HighPassHz <= 0 ? 0 : Math.Clamp(HighPassHz, 20, 2000);
        LowPassHz = LowPassHz <= 0 ? 0 : Math.Clamp(LowPassHz, 1000, 22000);
        // Не даём срезам схлопнуться: если верхний ниже нижнего — выключаем верхний.
        if (LowPassHz > 0 && HighPassHz > 0 && LowPassHz <= HighPassHz) LowPassHz = 0;

        NoiseFloorDb = Clamp(NoiseFloorDb, -80f, -20f, -40f);
        ReductionDb = Clamp(ReductionDb, 0.01f, 97f, 16f);
        ResidualFloorDb = Clamp(ResidualFloorDb, -80f, -20f, -60f);
        RnnMix = Clamp(RnnMix, -1f, 1f, 1f);
        if (string.IsNullOrWhiteSpace(RnnModel)) RnnModel = DefaultRnnModel;
        GateThresholdDb = Clamp(GateThresholdDb, -80f, -5f, -45f);
        GateRatio = Clamp(GateRatio, 1f, 20f, 2f);
        GateRangeDb = Clamp(GateRangeDb, -80f, 0f, -30f);
    }

    /// <summary>Добавляет фильтры шумоподавления в цепочку ffmpeg (-af).</summary>
    public void AppendFilters(List<string> chain)
    {
        Normalize();
        if (HighPassHz > 0) chain.Add($"highpass=f={HighPassHz}:poles=2");
        if (LowPassHz > 0) chain.Add($"lowpass=f={LowPassHz}:poles=2");

        if (SpectralEnabled)
        {
            chain.Add($"afftdn=nr={Num(ReductionDb)}:nf={Num(NoiseFloorDb)}" +
                      $":tn={(TrackNoise ? 1 : 0)}:tr={(TrackResidual ? 1 : 0)}:rf={Num(ResidualFloorDb)}:nt={TypeChar()}");
        }

        if (RnnEnabled)
        {
            // arnndn работает на 48 кГц; при другом входе ffmpeg вставит ресемплер сам.
            chain.Add($"arnndn=m={EscapeFilterPath(RnnModel)}:mix={Num(RnnMix, 2)}");
        }

        if (GateEnabled)
        {
            chain.Add($"agate=threshold={Num(DbToLinear(GateThresholdDb), 6)}:ratio={Num(GateRatio, 2)}:" +
                      $"range={Num(DbToLinear(GateRangeDb), 6)}:attack=10:release=200:makeup=1");
        }
    }

    public string Describe()
    {
        Normalize();
        var parts = new List<string>
        {
            HighPassHz > 0 ? $"HP {HighPassHz} Hz" : "HP off",
            LowPassHz > 0 ? $"LP {LowPassHz} Hz" : "LP off"
        };
        if (SpectralEnabled)
            parts.Add($"spectral nr={Num(ReductionDb)}dB nf={Num(NoiseFloorDb)}dB rf={Num(ResidualFloorDb)}dB " +
                      $"tn={(TrackNoise ? 1 : 0)} tr={(TrackResidual ? 1 : 0)} nt={TypeChar()}");
        if (RnnEnabled) parts.Add($"rnn mix={Num(RnnMix, 2)} model={RnnModel}");
        if (GateEnabled)
            parts.Add($"gate thr={Num(GateThresholdDb)}dB ratio={Num(GateRatio)} range={Num(GateRangeDb)}dB");
        if (!SpectralEnabled && !RnnEnabled && !GateEnabled) parts.Add("denoise off");
        return string.Join(", ", parts);
    }

    private char TypeChar() => Type switch
    {
        NoiseType.Vinyl => 'v',
        NoiseType.Shellac => 's',
        _ => 'w'
    };

    private static double DbToLinear(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>Путь для значения фильтра: прямые слэши, двоеточие диска экранировано.</summary>
    public static string EscapeFilterPath(string path)
        => string.IsNullOrWhiteSpace(path) ? path : path.Replace("\\", "/").Replace(":", "\\:");

    private static float Clamp(float value, float min, float max, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    internal static string Num(double value, int digits = 3)
        => value.ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);
}

/// <summary>
/// Полный набор настроек обработки готового файла: файлы, видео-поток, аудио-обработка.
/// </summary>
public sealed class VideoProcessingSettings
{
    public string InputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";

    /// <summary>Заполняется из ffprobe перед запуском (влияет на -map и на копирование видео).</summary>
    public bool InputHasVideo { get; set; } = true;
    public bool InputHasAudio { get; set; } = true;

    /// <summary>true = видео копируется без перекодирования (быстро, без потери качества).</summary>
    public bool CopyVideoStream { get; set; } = true;
    public int Crf { get; set; } = 20;
    public string Preset { get; set; } = "medium";

    public OutputContainer Container { get; set; } = OutputContainer.Mp4;
    public int AacBitrateKbps { get; set; } = 192;

    public float GainDb { get; set; }
    public LevelTarget Level { get; set; } = LevelTarget.Off;

    /// <summary>Для предпросмотра: с какой секунды и сколько секунд брать (0 = весь файл).</summary>
    public int StartSeconds { get; set; }
    public int LimitSeconds { get; set; }

    public NoiseReductionSettings NoiseReduction { get; set; } = new();
    public EqualizerSettings Equalizer { get; set; } = new();

    public static readonly string[] VideoPresets =
        { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };

    public static readonly int[] AacBitrates = { 96, 128, 160, 192, 256, 320 };

    public VideoProcessingSettings Clone() => new()
    {
        InputPath = InputPath,
        OutputPath = OutputPath,
        InputHasVideo = InputHasVideo,
        InputHasAudio = InputHasAudio,
        CopyVideoStream = CopyVideoStream,
        Crf = Crf,
        Preset = Preset,
        Container = Container,
        AacBitrateKbps = AacBitrateKbps,
        GainDb = GainDb,
        Level = Level,
        StartSeconds = StartSeconds,
        LimitSeconds = LimitSeconds,
        NoiseReduction = (NoiseReduction ?? new NoiseReductionSettings()).Clone(),
        Equalizer = (Equalizer ?? new EqualizerSettings()).Clone()
    };

    public void Normalize()
    {
        Crf = Math.Clamp(Crf, 14, 32);
        if (!VideoPresets.Contains(Preset)) Preset = "medium";
        AacBitrateKbps = Math.Clamp(AacBitrateKbps, 64, 512);
        GainDb = float.IsFinite(GainDb) ? Math.Clamp(GainDb, -30f, 30f) : 0f;
        StartSeconds = Math.Clamp(StartSeconds, 0, 24 * 3600);
        LimitSeconds = Math.Clamp(LimitSeconds, 0, 24 * 3600);
        if (!Enum.IsDefined(Container)) Container = OutputContainer.Mp4;
        if (!Enum.IsDefined(Level)) Level = LevelTarget.Off;
        NoiseReduction ??= new NoiseReductionSettings();
        NoiseReduction.Normalize();
        Equalizer ??= new EqualizerSettings();
        Equalizer.Normalize();
        if (LimitSeconds == 0) StartSeconds = 0;
        OutputPath = FixExtension(OutputPath, Container);
    }

    public static string FixExtension(string path, OutputContainer container)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var ext = container == OutputContainer.Mkv ? ".mkv" : ".mp4";
        return Path.ChangeExtension(path, ext);
    }

    /// <summary>Цепочка фильтров ffmpeg (-af). Пустая строка = фильтров нет.</summary>
    public string BuildFilterChain()
    {
        Normalize();
        var chain = new List<string>();
        NoiseReduction.AppendFilters(chain);

        if (Equalizer.Enabled)
        {
            for (int i = 0; i < EqualizerPresets.BandCount; i++)
            {
                float gain = Equalizer.BandGainsDb[i];
                if (Math.Abs(gain) < 0.05f) continue;
                chain.Add($"equalizer=f={NoiseReductionSettings.Num(EqualizerPresets.BandFrequencies[i])}:t=q:w=1:g={NoiseReductionSettings.Num(gain)}");
            }
        }

        if (Math.Abs(GainDb) >= 0.05f) chain.Add($"volume={NoiseReductionSettings.Num(GainDb)}dB");

        switch (Level)
        {
            case LevelTarget.Loudness: chain.Add("loudnorm=I=-16:TP=-1.5:LRA=11"); break;
            case LevelTarget.Limiter: chain.Add("alimiter=limit=0.891:attack=5:release=50"); break;
            case LevelTarget.Speech: chain.Add("speechnorm=e=6.25:r=0.0001:l=1"); break;
        }

        return chain.Count == 0 ? "" : string.Join(",", chain);
    }

    /// <summary>Аргументы ffmpeg для текущих настроек (включая предпросмотр, если задан LimitSeconds).</summary>
    public List<string> BuildArguments()
    {
        Normalize();
        var args = new List<string> { "-hide_banner", "-y", "-nostdin" };

        if (LimitSeconds > 0 && StartSeconds > 0)
        {
            args.Add("-ss");
            args.Add(NoiseReductionSettings.Num(StartSeconds, 3));
        }

        args.Add("-i");
        args.Add(InputPath);

        args.Add("-map");
        args.Add("0:v:0?");
        args.Add("-map");
        args.Add("0:a:0?");

        var filter = InputHasAudio ? BuildFilterChain() : "";
        if (!string.IsNullOrEmpty(filter))
        {
            args.Add("-af");
            args.Add(filter);
        }

        if (InputHasVideo)
        {
            if (CopyVideoStream)
            {
                args.Add("-c:v");
                args.Add("copy");
            }
            else
            {
                args.Add("-c:v");
                args.Add("libx264");
                args.Add("-preset");
                args.Add(Preset);
                args.Add("-crf");
                args.Add(Crf.ToString(CultureInfo.InvariantCulture));
                args.Add("-pix_fmt");
                args.Add("yuv420p");
            }
        }

        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add($"{AacBitrateKbps}k");

        if (LimitSeconds > 0)
        {
            args.Add("-t");
            args.Add(NoiseReductionSettings.Num(LimitSeconds, 3));
        }

        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");

        if (Container == OutputContainer.Mp4)
        {
            args.Add("-movflags");
            args.Add("+faststart");
        }

        args.Add(OutputPath);
        return args;
    }

    public string BuildCommandLine()
    {
        var args = BuildArguments().Select(a => a.Contains(' ') ? "\"" + a + "\"" : a);
        return "ffmpeg " + string.Join(" ", args);
    }

    public string Describe()
    {
        Normalize();
        var sb = new StringBuilder();
        sb.Append(CopyVideoStream ? "video=copied" : $"video=x264 crf{Crf} {Preset}");
        sb.Append($", audio=aac {AacBitrateKbps}k");
        sb.Append($", {(Level == LevelTarget.Off ? "level=off" : "level=" + Level.ToString().ToLowerInvariant())}");
        sb.Append($", gain={(Math.Abs(GainDb) < 0.05f ? "0" : NoiseReductionSettings.Num(GainDb))}dB");
        sb.Append($", eq={(Equalizer.Enabled ? Equalizer.Preset : "off")}");
        sb.Append($", {NoiseReduction.Describe()}");
        return sb.ToString();
    }
}

/// <summary>Что удалось узнать о входном файле (ffprobe).</summary>
public sealed record MediaInfo(
    string Path,
    double DurationSec,
    long SizeBytes,
    bool HasVideo,
    string VideoCodec,
    int Width,
    int Height,
    double VideoFps,
    bool HasAudio,
    string AudioCodec,
    int Channels,
    int SampleRate,
    double AudioDurationSec)
{
    public static MediaInfo Empty(string path)
        => new(path, 0, 0, false, "", 0, 0, 0, false, "", 0, 0, 0);

    public bool IsValid => DurationSec > 0 || HasVideo || HasAudio;

    public string ToText()
    {
        if (!IsValid) return "не удалось прочитать файл (ffprobe)";
        var sb = new StringBuilder();
        sb.Append(DurationSec > 0
            ? TimeSpan.FromSeconds(DurationSec).ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture)
            : "?");
        sb.Append(CultureInfo.InvariantCulture, $", {SizeBytes / 1024.0 / 1024.0:0.0} MB");
        if (HasVideo) sb.Append(CultureInfo.InvariantCulture, $"; video {VideoCodec} {Width}x{Height} {VideoFps:0.##} fps");
        else sb.Append("; video нет");
        if (HasAudio) sb.Append(CultureInfo.InvariantCulture, $"; audio {AudioCodec} {Channels} ch {SampleRate} Hz");
        else sb.Append("; audio нет");
        return sb.ToString();
    }
}

public sealed record VideoProcessingProgress(double Percent, TimeSpan Elapsed, TimeSpan Position, string Phase);

public sealed record VideoProcessingResult(
    bool Success,
    string OutputPath,
    double OutputDurationSec,
    TimeSpan Elapsed,
    string FilterChain,
    string LogTail,
    string Error)
{
    public string ToText()
    {
        if (Success)
            return $"готово: {OutputPath} ({OutputDurationSec:0.00} с, {Elapsed.TotalSeconds:0.0} с работы)";
        return "ошибка: " + Error;
    }
}
