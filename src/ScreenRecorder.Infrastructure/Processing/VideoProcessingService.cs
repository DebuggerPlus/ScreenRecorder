using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Infrastructure.Processing;

/// <summary>
/// Обработка готового видео/аудио файла через ffmpeg: шумоподавление, фильтры, эквалайзер, уровень.
/// Видео-поток по умолчанию копируется, перекодируется только звук.
/// Прогресс отдаётся из вывода ffmpeg (-progress pipe:1), прерывание — kill процесса.
/// </summary>
public sealed class VideoProcessingService : IVideoProcessor
{
    private readonly ILoggerService _logger;
    private Process? _process;
    private long _lastReportTicks;

    public VideoProcessingService(ILoggerService logger) => _logger = logger;

    public bool IsRunning
    {
        get
        {
            var p = _process;
            try { return p != null && !p.HasExited; }
            catch { return false; }
        }
    }

    public void Cancel()
    {
        var p = _process;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                _logger.Log("Video processing: cancel requested, killing ffmpeg.");
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Log("Video processing: cancel failed — " + ex.Message);
        }
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return MediaInfo.Empty(path ?? "");
        try
        {
            var psi = new ProcessStartInfo("ffprobe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in new[]
                     {
                         "-v", "error", "-print_format", "json", "-show_format", "-show_streams",
                         "-show_entries",
                         "format=duration,size:stream=codec_type,codec_name,width,height,r_frame_rate,sample_rate,channels,duration",
                         path
                     })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null) return MediaInfo.Empty(path);
            string json = await proc.StandardOutput.ReadToEndAsync();
            string err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                _logger.Log($"ffprobe '{path}' exit {proc.ExitCode}: {err.Trim()}");
                return MediaInfo.Empty(path);
            }
            return ParseProbe(path, json);
        }
        catch (Exception ex)
        {
            _logger.Log($"ffprobe failed for '{path}': {ex.Message}");
            return MediaInfo.Empty(path);
        }
    }

    internal static MediaInfo ParseProbe(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double duration = 0, size = 0;
        if (root.TryGetProperty("format", out var fmt))
        {
            duration = ReadDouble(fmt, "duration");
            size = ReadDouble(fmt, "size");
        }

        bool hasVideo = false, hasAudio = false;
        string videoCodec = "", audioCodec = "";
        int width = 0, height = 0, channels = 0, sampleRate = 0;
        double fps = 0, audioDuration = 0;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && !hasVideo)
                {
                    hasVideo = true;
                    videoCodec = ReadString(s, "codec_name");
                    width = (int)ReadDouble(s, "width");
                    height = (int)ReadDouble(s, "height");
                    fps = ParseRate(ReadString(s, "r_frame_rate"));
                }
                else if (type == "audio" && !hasAudio)
                {
                    hasAudio = true;
                    audioCodec = ReadString(s, "codec_name");
                    channels = (int)ReadDouble(s, "channels");
                    sampleRate = (int)ReadDouble(s, "sample_rate");
                    audioDuration = ReadDouble(s, "duration");
                }
            }
        }

        return new MediaInfo(path, duration, (long)size, hasVideo, videoCodec, width, height, fps,
            hasAudio, audioCodec, channels, sampleRate, audioDuration);
    }

    private static double ReadDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }

    private static string ReadString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double ParseRate(string rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            return num / den;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
    }

    public async Task<VideoProcessingResult> RunAsync(VideoProcessingSettings settings, IProgress<VideoProcessingProgress>? progress, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        settings.Normalize();
        var tail = new Queue<string>();
        string filterChain = settings.BuildFilterChain();

        VideoProcessingResult Fail(string error)
        {
            _logger.Log("Video processing failed: " + error);
            return new VideoProcessingResult(false, settings.OutputPath, 0, sw.Elapsed, filterChain, string.Join(Environment.NewLine, tail), error);
        }

        if (string.IsNullOrWhiteSpace(settings.InputPath) || !File.Exists(settings.InputPath))
            return Fail("входной файл не найден: " + settings.InputPath);
        if (string.IsNullOrWhiteSpace(settings.OutputPath))
            return Fail("не задан выходной файл");
        if (string.Equals(Path.GetFullPath(settings.InputPath), Path.GetFullPath(settings.OutputPath), StringComparison.OrdinalIgnoreCase))
            return Fail("выходной файл совпадает с входным — выберите другое имя");

        // Абсолютные пути: ffmpeg запускается с рабочей папкой = папка приложения (для модели arnndn).
        settings.InputPath = Path.GetFullPath(settings.InputPath);
        settings.OutputPath = Path.GetFullPath(settings.OutputPath);

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(settings.OutputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return Fail("не удалось создать папку результата: " + ex.Message);
        }

        double total = settings.LimitSeconds > 0 ? settings.LimitSeconds : 0;
        var info = await ProbeAsync(settings.InputPath, ct);
        if (info.IsValid)
        {
            settings.InputHasVideo = info.HasVideo;
            settings.InputHasAudio = info.HasAudio;
        }
        if (total <= 0) total = info.DurationSec;
        _logger.Log($"Video processing: input {info.ToText()}");

        var args = settings.BuildArguments();
        _logger.Log("Video processing: " + settings.BuildCommandLine());

        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Чтобы относительные пути внутри фильтров (модель arnndn) искались от папки приложения,
            // а не от случайной текущей папки процесса.
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return Fail("не удалось запустить ffmpeg (он должен быть в PATH): " + ex.Message);
        }

        if (_process == null) return Fail("не удалось запустить ffmpeg");

        bool cancelled = false;
        using var reg = ct.Register(() => { cancelled = true; Cancel(); });

        var proc = _process;
        _lastReportTicks = 0;
        var pos = TimeSpan.Zero;

        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            var line = e.Data;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                var raw = line.Substring("out_time_us=".Length).Trim();
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us > 0)
                {
                    pos = TimeSpan.FromSeconds(us / 1_000_000.0);
                    _lastReportTicks = Report(progress, total, pos, sw, _lastReportTicks, "encoding", false);
                }
            }
            else if (line.StartsWith("progress=", StringComparison.Ordinal))
            {
                if (line.EndsWith("end", StringComparison.Ordinal))
                    _lastReportTicks = Report(progress, total, pos, sw, _lastReportTicks, "finalizing", true);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            tail.Enqueue(e.Data);
            while (tail.Count > 40) tail.Dequeue();
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();
        proc.WaitForExit(); // дождаться окончания вычитывания асинхронных потоков

        int exit = proc.ExitCode;
        _process = null;

        if (cancelled || ct.IsCancellationRequested)
            return Fail("обработка прервана пользователем");

        bool ok = exit == 0 && File.Exists(settings.OutputPath) && new FileInfo(settings.OutputPath).Length > 0;
        double outDuration = 0;
        if (ok)
        {
            var outInfo = await ProbeAsync(settings.OutputPath, CancellationToken.None);
            outDuration = outInfo.DurationSec;
            _logger.Log($"Video processing: done in {sw.Elapsed.TotalSeconds:0.0}s -> {settings.OutputPath} ({outInfo.ToText()})");
        }
        else
        {
            _logger.Log($"Video processing: ffmpeg exit {exit}, result not written");
        }

        if (!ok && exit == 0) exit = -1;
        return new VideoProcessingResult(
            ok,
            settings.OutputPath,
            outDuration,
            sw.Elapsed,
            filterChain,
            string.Join(Environment.NewLine, tail),
            ok ? "" : (exit == 0 ? "результат не создан" : $"ffmpeg завершился с кодом {exit}"));
    }

    private static long Report(IProgress<VideoProcessingProgress>? progress, double total, TimeSpan pos,
        Stopwatch sw, long lastTicks, string phase, bool force)
    {
        if (progress == null) return lastTicks;
        long now = sw.ElapsedTicks;
        if (!force && now - lastTicks < Stopwatch.Frequency / 4) return lastTicks; // не чаще 4 раз в секунду
        double percent = total > 0 ? Math.Min(100.0, pos.TotalSeconds / total * 100.0) : 0;
        progress.Report(new VideoProcessingProgress(percent, sw.Elapsed, pos, phase));
        return now;
    }
}
