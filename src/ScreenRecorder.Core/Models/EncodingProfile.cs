using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace ScreenRecorder.Core.Models;

/// <summary>Способ управления битрейтом видео.</summary>
public enum VideoRateControl
{
    /// <summary>Постоянное качество: crf задаёт качество, битрейт плавает. Обычно лучший вариант для экрана.</summary>
    Crf,
    /// <summary>Постоянный битрейт: bitrateKbps — цель, maxRateKbps/bufferSizeKbps — потолок.</summary>
    Bitrate
}

/// <summary>
/// Параметры кодирования видео. Раньше были захардкожены в MainViewModel (30 fps, 8000 kbps,
/// preset ultrafast, crf 23 одновременно с -b:v — то есть crf игнорировался).
/// Теперь это часть внешнего профиля (configs\*.json).
/// </summary>
public sealed class VideoEncodingSettings
{
    /// <summary>Кадров в секунду, которые уходят в энкодер.</summary>
    public int Fps { get; set; } = 30;

    /// <summary>libx264 | h264_qsv | h264_nvenc | h264_amf</summary>
    public string Encoder { get; set; } = "libx264";

    /// <summary>ultrafast | superfast | veryfast | faster | fast | medium</summary>
    public string Preset { get; set; } = "veryfast";

    public VideoRateControl RateControl { get; set; } = VideoRateControl.Crf;

    /// <summary>0..51, меньше — лучше. Используется при RateControl = Crf.</summary>
    public int Crf { get; set; } = 20;

    /// <summary>Целевой битрейт, kbps. Используется при RateControl = Bitrate.</summary>
    public int BitrateKbps { get; set; } = 12000;

    /// <summary>Потолок битрейта, kbps. 0 = без ограничения (для Crf берётся только если задан явно).</summary>
    public int MaxRateKbps { get; set; }

    /// <summary>Размер буфера VBV, kbps. 0 = 2 * потолок.</summary>
    public int BufferSizeKbps { get; set; }

    /// <summary>yuv420p | yuv422p | yuv444p | nv12. yuv444p даёт самый резкий текст, но хуже совместимость.</summary>
    public string PixelFormat { get; set; } = "yuv420p";

    /// <summary>x264: film | animation | grain | stillimage. null = не задавать.</summary>
    public string? Tune { get; set; }

    /// <summary>Интервал ключевых кадров, секунды (0 = не задавать).</summary>
    public int KeyframeSeconds { get; set; } = 2;

    /// <summary>Число потоков энкодера, 0 = авто.</summary>
    public int Threads { get; set; }

    /// <summary>Дополнительные аргументы как есть. Например "-x264-params aq-mode=2".</summary>
    public string? ExtraArgs { get; set; }

    public static readonly int[] AllowedFps = { 15, 20, 24, 25, 30, 48, 50, 60 };
    public static readonly string[] Encoders = { "libx264", "h264_qsv", "h264_nvenc", "h264_amf" };
    public static readonly string[] Presets = { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium" };
    public static readonly string[] PixelFormats = { "yuv420p", "yuv422p", "yuv444p", "nv12" };
    public static readonly string[] Tunes = { "(none)", "film", "animation", "grain", "stillimage" };

    public VideoEncodingSettings Clone() => new()
    {
        Fps = Fps, Encoder = Encoder, Preset = Preset, RateControl = RateControl,
        Crf = Crf, BitrateKbps = BitrateKbps, MaxRateKbps = MaxRateKbps, BufferSizeKbps = BufferSizeKbps,
        PixelFormat = PixelFormat, Tune = Tune, KeyframeSeconds = KeyframeSeconds, Threads = Threads, ExtraArgs = ExtraArgs
    };

    /// <summary>Приводит значения к допустимым (на случай правки JSON руками).</summary>
    public void Normalize()
    {
        if (Fps <= 0) Fps = 30;
        Fps = AllowedFps.OrderBy(f => Math.Abs(f - Fps)).First();
        if (string.IsNullOrWhiteSpace(Encoder) || !Encoders.Contains(Encoder, StringComparer.OrdinalIgnoreCase)) Encoder = "libx264";
        Encoder = Encoders.First(e => e.Equals(Encoder, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(Preset) || !Presets.Contains(Preset, StringComparer.OrdinalIgnoreCase)) Preset = "veryfast";
        Preset = Presets.First(p => p.Equals(Preset, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(PixelFormat) || !PixelFormats.Contains(PixelFormat, StringComparer.OrdinalIgnoreCase)) PixelFormat = "yuv420p";
        PixelFormat = PixelFormats.First(p => p.Equals(PixelFormat, StringComparison.OrdinalIgnoreCase));
        Crf = Math.Clamp(Crf, 0, 51);
        BitrateKbps = Math.Clamp(BitrateKbps, 500, 200000);
        MaxRateKbps = MaxRateKbps <= 0 ? 0 : Math.Clamp(MaxRateKbps, 500, 300000);
        BufferSizeKbps = BufferSizeKbps <= 0 ? 0 : Math.Clamp(BufferSizeKbps, 500, 600000);
        KeyframeSeconds = Math.Clamp(KeyframeSeconds, 0, 30);
        Threads = Math.Clamp(Threads, 0, 32);
        if (string.IsNullOrWhiteSpace(Tune) || Tune == "(none)") Tune = null;
    }

    /// <summary>Аргументы ffmpeg как отдельные токены (без склейки строк — пути с пробелами не ломаются).</summary>
    public List<string> BuildArgs(int width, int height, string outputPath)
    {
        var a = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "warning",
            "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", Inv($"{width}x{height}"),
            "-framerate", Inv(Fps),
            "-i", "-",
            "-c:v", Encoder,
            "-pix_fmt", PixelFormat
        };

        if (!string.IsNullOrWhiteSpace(Preset)) { a.Add("-preset"); a.Add(Preset); }
        if (!string.IsNullOrWhiteSpace(Tune)) { a.Add("-tune"); a.Add(Tune); }

        if (RateControl == VideoRateControl.Bitrate)
        {
            a.Add("-b:v"); a.Add(Inv($"{BitrateKbps}k"));
            int max = MaxRateKbps > 0 ? MaxRateKbps : (int)(BitrateKbps * 1.5);
            int buf = BufferSizeKbps > 0 ? BufferSizeKbps : max * 2;
            a.Add("-maxrate"); a.Add(Inv($"{max}k"));
            a.Add("-bufsize"); a.Add(Inv($"{buf}k"));
        }
        else
        {
            a.Add("-crf"); a.Add(Inv(Crf));
            if (MaxRateKbps > 0)
            {
                int buf = BufferSizeKbps > 0 ? BufferSizeKbps : MaxRateKbps * 2;
                a.Add("-maxrate"); a.Add(Inv($"{MaxRateKbps}k"));
                a.Add("-bufsize"); a.Add(Inv($"{buf}k"));
            }
        }

        if (KeyframeSeconds > 0)
        {
            a.Add("-g"); a.Add(Inv(Math.Max(1, Fps * KeyframeSeconds)));
        }
        if (Threads > 0) { a.Add("-threads"); a.Add(Inv(Threads)); }
        if (!string.IsNullOrWhiteSpace(ExtraArgs)) a.AddRange(SplitArgs(ExtraArgs!));

        a.Add("-fps_mode"); a.Add("cfr");
        a.Add(outputPath);
        return a;
    }

    /// <summary>Тот же список одной строкой — для показа в интерфейсе и в логе.</summary>
    public string DescribeArgs(int width, int height, string outputPath) =>
        string.Join(' ', BuildArgs(width, height, outputPath).Select(QuoteIfNeeded));

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(Inv($"{Encoder} {Preset}, {Fps}fps, {PixelFormat}, "));
        sb.Append(RateControl == VideoRateControl.Bitrate
            ? Inv($"bitrate {BitrateKbps}k")
            : Inv($"crf {Crf}"));
        if (MaxRateKbps > 0) sb.Append(Inv($", maxrate {MaxRateKbps}k"));
        if (!string.IsNullOrWhiteSpace(Tune)) sb.Append($", tune={Tune}");
        return sb.ToString();
    }

    /// <summary>Предупреждения о заведомо проблемных комбинациях.</summary>
    public List<string> Validate()
    {
        var w = new List<string>();
        if (Preset == "ultrafast" && RateControl == VideoRateControl.Crf)
            w.Add("ultrafast отключает deblocking/CABAC — на тексте будет блочность; для качества минимум superfast/veryfast.");
        if (Encoder != "libx264" && PixelFormat == "yuv444p")
            w.Add(Inv($"{Encoder} обычно не поддерживает yuv444p — используйте yuv420p/nv12."));
        if (Encoder == "h264_qsv" && PixelFormat == "yuv420p")
            w.Add("для h264_qsv эффективнее nv12.");
        if (RateControl == VideoRateControl.Crf && Crf > 26)
            w.Add(Inv($"crf {Crf} — заметная потеря деталей."));
        if (Fps >= 50 && Encoder == "libx264" && Preset == "veryfast")
            w.Add(Inv($"{Fps}fps на libx264/{Preset}: следите за загрузкой CPU, при отставании переключитесь на superfast/ultrafast."));
        return w;
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string QuoteIfNeeded(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    private static IEnumerable<string> SplitArgs(string line)
    {
        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}

/// <summary>
/// Внешний профиль записи: файл configs\*.json. Выбирается в приложении и применяется к записи.
/// Аудио необязательно: если null — берутся настройки из settings.json.
/// </summary>
public sealed class EncodingProfile
{
    public string Name { get; set; } = "Custom";
    public string? Description { get; set; }
    public VideoEncodingSettings Video { get; set; } = new();
    public AudioCaptureSettings? Audio { get; set; }

    public EncodingProfile Clone() => new()
    {
        Name = Name,
        Description = Description,
        Video = (Video ?? new VideoEncodingSettings()).Clone(),
        Audio = Audio?.Clone()
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Name)) Name = "Custom";
        Video ??= new VideoEncodingSettings();
        Video.Normalize();
        Audio?.Normalize();
    }
}
