using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using ScreenRecorder.Core.Enums;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Infrastructure.Encoding;

namespace ScreenRecorder.Infrastructure.Audio;

/// <summary>
/// Запись только звука (без видео и без ffmpeg-видеоконвейера) для проверки чистоты захвата.
/// Использует собственный экземпляр WasapiAudioCapture, поэтому не мешает основной записи,
/// но запускается только когда основная запись не идёт.
/// </summary>
public sealed class AudioTestService : IAudioTestService
{
    private readonly ILoggerService _logger;
    private readonly IRecordingSession _session;

    private WasapiAudioCapture? _capture;
    private AudioTestOptions? _options;
    private string? _processedPath, _rawPath;

    public AudioTestService(ILoggerService logger, IRecordingSession session)
    {
        _logger = logger;
        _session = session;
    }

    public bool IsRunning => _capture?.IsRunning == true;
    public event Action<AudioLevel>? Level;
    public AudioCaptureStats? GetLiveStats() => _capture?.GetStats();

    public async Task<string?> StartAsync(AudioTestOptions o)
    {
        if (IsRunning) return "A test is already running.";
        if (_session.State != RecordingState.Idle) return "Stop the screen recording before running an audio test.";
        if (string.IsNullOrWhiteSpace(o.DeviceId)) return "Select an audio device first.";

        try { Directory.CreateDirectory(o.OutputFolder); }
        catch (Exception ex) { return $"Cannot use test folder: {ex.Message}"; }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _processedPath = Path.Combine(o.OutputFolder, $"audiotest_{stamp}.wav");
        _rawPath = o.SaveRawCopy ? Path.Combine(o.OutputFolder, $"audiotest_{stamp}_raw.wav") : null;
        _options = o;

        var cap = new WasapiAudioCapture(_logger);
        cap.LevelMeasured += l => Level?.Invoke(l);
        _capture = cap;

        await cap.StartAsync(new AudioCaptureRequest(o.DeviceId, _processedPath, o.Settings, _rawPath), CancellationToken.None);
        if (!cap.IsRunning)
        {
            _capture = null;
            return cap.LastError ?? "Audio capture failed to start (see log).";
        }
        _logger.Log($"Audio test started: {_processedPath}");
        return null;
    }

    public async Task<AudioTestResult> StopAsync()
    {
        var cap = _capture;
        var o = _options;
        if (cap == null || o == null)
            return new AudioTestResult(null, null, null, null, null, null, "No test is running.");

        await cap.StopAsync();
        _capture = null;
        _logger.Log("Audio test stopped.");
        return await FinishAsync(_processedPath, _rawPath, cap.LastReport, o.Container, o.Settings.AacBitrateKbps, cap.LastError);
    }

    public async Task<AudioTestResult> ReprocessAsync(string rawWavPath, AudioCaptureSettings settings, AudioTestContainer container)
    {
        var s = settings.Clone();
        s.Normalize();
        string dir = Path.GetDirectoryName(Path.GetFullPath(rawWavPath))!;
        string baseName = Path.GetFileNameWithoutExtension(rawWavPath);
        if (baseName.EndsWith("_raw", StringComparison.OrdinalIgnoreCase)) baseName = baseName[..^4];
        string outPath = Path.Combine(dir, $"{baseName}_reprocessed_{DateTime.Now:HHmmss}.wav");

        try
        {
            await Task.Run(() =>
            {
                using var reader = new WaveFileReader(rawWavPath);
                var chain = new AudioProcessingChain(reader.WaveFormat, s);
                using var writer = new WaveFileWriter(outPath, chain.OutputFormat);
                var buf = new byte[reader.WaveFormat.BlockAlign * 4800];
                int n;
                while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    chain.Process(buf, n, (b, c) => writer.Write(b, 0, c));
            });
            _logger.Log($"Audio test: reprocessed '{Path.GetFileName(rawWavPath)}' with [{s.Describe()}]");
        }
        catch (Exception ex)
        {
            _logger.Log($"Audio reprocess failed: {ex.Message}");
            return new AudioTestResult(null, rawWavPath, null, null, null, null, $"Reprocess failed: {ex.Message}");
        }
        return await FinishAsync(outPath, null, null, container, s.AacBitrateKbps, null);
    }

    public Task<AudioAnalysis> AnalyzeFileAsync(string path) => AudioAnalyzer.AnalyzeAnyAsync(path);

    private async Task<AudioTestResult> FinishAsync(string? processed, string? raw, AudioCaptureReport? report,
        AudioTestContainer container, int aacKbps, string? error)
    {
        AudioAnalysis? pa = null, ra = null;
        string? converted = null;

        try { if (processed != null && File.Exists(processed)) pa = await Task.Run(() => AudioAnalyzer.AnalyzeWav(processed)); }
        catch (Exception ex) { error ??= "Analysis failed: " + ex.Message; }
        try { if (raw != null && File.Exists(raw)) ra = await Task.Run(() => AudioAnalyzer.AnalyzeWav(raw)); }
        catch (Exception ex) { error ??= "Raw analysis failed: " + ex.Message; }

        if (container != AudioTestContainer.Wav && processed != null && File.Exists(processed))
        {
            try { converted = await ConvertAsync(processed, container, aacKbps); }
            catch (Exception ex) { error = (error == null ? "" : error + " ") + "Conversion failed: " + ex.Message; }
        }

        return new AudioTestResult(processed, raw, converted, report, pa, ra, error);
    }

    private static async Task<string> ConvertAsync(string wavPath, AudioTestContainer container, int aacKbps)
    {
        string ext = container switch { AudioTestContainer.Flac => ".flac", AudioTestContainer.Mp3 => ".mp3", _ => ".m4a" };
        string outPath = Path.ChangeExtension(wavPath, ext);
        var args = new System.Collections.Generic.List<string> { "-y", "-i", wavPath, "-vn" };
        switch (container)
        {
            case AudioTestContainer.Flac: args.AddRange(new[] { "-c:a", "flac" }); break;
            case AudioTestContainer.Mp3: args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", $"{aacKbps}k" }); break;
            default: args.AddRange(new[] { "-c:a", "aac", "-b:a", $"{aacKbps}k" }); break;
        }
        args.Add(outPath);

        var (code, err) = await FfmpegRunner.RunAsync(args);
        if (code != 0 || !File.Exists(outPath))
        {
            var tail = string.Join(" | ", err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(2));
            throw new InvalidOperationException(tail.Length > 0 ? tail : $"ffmpeg exit code {code}");
        }
        return outPath;
    }
}
