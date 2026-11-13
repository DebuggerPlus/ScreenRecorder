using System.Collections.Generic;

namespace ScreenRecorder.Core.Models;

/// <summary>
/// Шумоподавление при съёмке. Звук при записи идёт через NAudio в WAV, а тяжёлые фильтры (afftdn,
/// arnndn) — ffmpeg-ные, в живой поток их не вставить. Поэтому выбранный рецепт применяется к WAV
/// в момент склейки с видео: записанный файл сразу получается чистым.
/// Здесь только сборка параметров — её проверяет самотест, без ffmpeg и без устройств.
/// </summary>
public static class RecordingDenoise
{
    /// <summary>Цепочка фильтров для -af. Пустая строка = фильтровать нечего (выключено или пустой рецепт).</summary>
    public static string BuildChain(AudioCaptureSettings? settings)
    {
        if (settings is null || !settings.DenoiseOnRecord) return string.Empty;

        // Recipe() безопасен для неизвестного имени: вернёт пустую (выключенную) цепочку.
        var n = NoiseReductionSettings.Recipe(settings.DenoiseRecipe);
        if (n is null || !n.IsEnabled) return string.Empty;

        var chain = new List<string>();
        n.AppendFilters(chain);
        return chain.Count == 0 ? string.Empty : string.Join(",", chain);
    }

    /// <summary>
    /// Аргументы ffmpeg для склейки записанного видео с записанным WAV: видео копируется без перекодирования,
    /// звук перекодируется в AAC — при заданной цепочке с шумоподавлением.
    /// </summary>
    public static string[] BuildMergeArgs(string tempVideo, string tempAudio, string output, int aacKbps, string? chain)
    {
        var args = new List<string>
        {
            "-y", "-i", tempVideo, "-i", tempAudio,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy"
        };

        if (!string.IsNullOrWhiteSpace(chain))
        {
            args.Add("-af");
            args.Add(chain!);
        }

        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add($"{aacKbps}k");
        args.Add("-shortest");
        args.Add(output);
        return args.ToArray();
    }
}
