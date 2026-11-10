using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Infrastructure.Encoding;

namespace ScreenRecorder.Infrastructure.Audio;

/// <summary>
/// Простой анализатор WAV: ищет клиппинг, «цифровую тишину» посреди записи (потерянные пакеты)
/// и резкие скачки сигнала (щелчки). Это эвристика: она хорошо подходит для сравнения двух записей
/// одного и того же источника (например, ScreenRecorder и Bandicam), но не заменяет прослушивание.
/// Пороги вынесены в константы.
/// </summary>
public static class AudioAnalyzer
{
    private const float ClickAbsoluteMin = 0.03f;   // минимальный скачок между соседними сэмплами
    private const float ClickRelativeFactor = 12f;  // во сколько раз скачок выше локального среднего
    private const double ClickRefractoryMs = 2.0;   // не считаем один щелчок несколько раз
    private const double DropoutMinMs = 2.0;        // минимальная длина «цифровой тишины»

    public static AudioAnalysis AnalyzeWav(string path, string? displayName = null)
    {
        using var reader = new WaveFileReader(path);
        var fmt = reader.WaveFormat;
        int ch = fmt.Channels;
        int rate = fmt.SampleRate;
        int frameBytes = fmt.BlockAlign;

        const int framesPerRead = 8192;
        var bytes = new byte[frameBytes * framesPerRead];
        var dec = new float[framesPerRead * ch];

        long total = 0, clipped = 0;
        double sumSq = 0, sum = 0;
        float peak = 0;

        int winLen = Math.Max(1, rate / 50); // окно 20 мс
        double winSq = 0; int winCnt = 0;
        var windowsDb = new List<float>();

        float prev = 0, ema = 0;
        float alpha = 1f / Math.Max(1f, 0.005f * rate);
        long lastClick = -1_000_000;
        long refractory = (long)(ClickRefractoryMs * rate / 1000.0);
        long warmup = rate / 50;
        int clicks = 0;

        long zeroRun = 0, minZeroRun = (long)(DropoutMinMs * rate / 1000.0);
        bool seenSignal = false;
        int dropouts = 0;

        int n;
        while ((n = reader.Read(bytes, 0, bytes.Length)) > 0)
        {
            int frames = n / frameBytes;
            if (frames == 0) break;
            AudioFormatHelper.Decode(bytes, frames * ch, fmt, dec);

            for (int f = 0; f < frames; f++)
            {
                float x;
                if (ch == 1) x = dec[f];
                else
                {
                    float s = 0;
                    for (int c = 0; c < ch; c++) s += dec[f * ch + c];
                    x = s / ch;
                }

                float a = Math.Abs(x);
                if (a > peak) peak = a;
                if (a >= 0.999f) clipped++;
                sumSq += (double)x * x;
                sum += x;

                winSq += (double)x * x;
                if (++winCnt >= winLen)
                {
                    windowsDb.Add((float)(10.0 * Math.Log10(Math.Max(winSq / winCnt, 1e-12))));
                    winSq = 0; winCnt = 0;
                }

                // Цифровая тишина посреди записи (не в начале и не в конце).
                if (a < 1e-9f) zeroRun++;
                else
                {
                    if (seenSignal && zeroRun >= minZeroRun) dropouts++;
                    zeroRun = 0;
                    seenSignal = true;
                }

                // Щелчки: скачок между соседними сэмплами сильно выше среднего скачка за последние ~5 мс.
                float d = Math.Abs(x - prev);
                if (total > warmup && d > Math.Max(ClickAbsoluteMin, ClickRelativeFactor * ema)
                    && total - lastClick > refractory)
                {
                    clicks++;
                    lastClick = total;
                }
                ema += alpha * (d - ema);
                prev = x;
                total++;
            }
        }

        double duration = rate > 0 ? total / (double)rate : 0;
        double rms = total > 0 ? Math.Sqrt(sumSq / total) : 0;
        double dc = total > 0 ? sum / total : 0;
        double noiseFloor = -120;
        if (windowsDb.Count > 0)
        {
            windowsDb.Sort();
            noiseFloor = windowsDb[(int)(windowsDb.Count * 0.10)];
        }
        double perMin = duration > 1 ? clicks / (duration / 60.0) : 0;

        string verdict;
        if (total == 0) verdict = "File is empty.";
        else if (dropouts > 0 || perMin > 15)
            verdict = "Likely audible defects (dropouts or crackle). Compare with a known-good recording of the same source.";
        else if (perMin > 3)
            verdict = "Some suspicious clicks. Compare with a known-good recording of the same source.";
        else
            verdict = "No obvious defects detected by this heuristic.";
        if (total > 0 && clipped > total * 0.0001) verdict += " Signal clips: lower the input gain.";

        return new AudioAnalysis(displayName ?? Path.GetFileName(path), duration, rate,
            peak > 1e-6f ? 20.0 * Math.Log10(peak) : -120, rms > 1e-6 ? 20.0 * Math.Log10(rms) : -120,
            noiseFloor, clipped, dc, dropouts, clicks, perMin, verdict);
    }

    /// <summary>Анализирует WAV напрямую, остальные форматы (mp4, m4a, mp3, flac…) сначала конвертирует через ffmpeg.</summary>
    public static async Task<AudioAnalysis> AnalyzeAnyAsync(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            return await Task.Run(() => AnalyzeWav(path));
        }

        string tmp = Path.Combine(Path.GetTempPath(), "sr_analyze_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var (code, err) = await FfmpegRunner.RunAsync(new[]
            {
                "-y", "-i", path, "-vn", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", tmp
            });
            if (code != 0 || !File.Exists(tmp))
                throw new InvalidOperationException("ffmpeg could not read the audio: " + Tail(err));
            return await Task.Run(() => AnalyzeWav(tmp, Path.GetFileName(path)));
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(2));
    }
}
