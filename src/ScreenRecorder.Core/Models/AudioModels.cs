using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ScreenRecorder.Core.Models;

/// <summary>Способ захвата звука с устройства.</summary>
public enum AudioCaptureApi
{
    /// <summary>WASAPI shared, по событию драйвера (рекомендуется).</summary>
    WasapiEvent,
    /// <summary>WASAPI shared, опрос по таймеру. Иногда стабильнее на виртуальных драйверах (iVCam).</summary>
    WasapiPolling,
    /// <summary>WASAPI exclusive. Минимум преобразований, но устройство должно поддерживать формат.</summary>
    WasapiExclusive
}

public enum AudioBitDepth { Pcm16, Pcm24, Float32 }

public enum AudioChannelMode { Auto, Mono, Stereo, LeftOnly, RightOnly }

public enum AudioTestContainer { Wav, Flac, Mp3, M4a }

/// <summary>Десятиполосный эквалайзер. Полосы фиксированы, меняется только усиление.</summary>
public sealed class EqualizerSettings
{
    public bool Enabled { get; set; }
    public string Preset { get; set; } = "Flat";
    public float[] BandGainsDb { get; set; } = new float[EqualizerPresets.BandCount];

    public EqualizerSettings Clone() => new()
    {
        Enabled = Enabled,
        Preset = Preset,
        BandGainsDb = (float[])BandGainsDb.Clone()
    };

    public void Normalize()
    {
        var fixedBands = new float[EqualizerPresets.BandCount];
        if (BandGainsDb != null)
            for (int i = 0; i < Math.Min(BandGainsDb.Length, fixedBands.Length); i++)
                fixedBands[i] = Math.Clamp(float.IsFinite(BandGainsDb[i]) ? BandGainsDb[i] : 0f, -12f, 12f);
        BandGainsDb = fixedBands;
        if (string.IsNullOrWhiteSpace(Preset)) Preset = "Flat";
    }
}

public static class EqualizerPresets
{
    public const int BandCount = 10;
    public const string Custom = "Custom";

    public static readonly float[] BandFrequencies = { 31.5f, 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
    public static readonly string[] BandLabels = { "31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };

    public static readonly IReadOnlyDictionary<string, float[]> All = new Dictionary<string, float[]>
    {
        ["Flat"]                 = new float[] {  0,  0,  0,  0,  0,  0,  0,  0,  0,  0 },
        ["Voice clarity"]        = new float[] { -8, -6, -3,  0,  0,  1,  3,  4,  2,  0 },
        ["Reduce rumble"]        = new float[] { -12, -9, -5, -2,  0,  0,  0,  0,  0,  0 },
        ["Reduce hiss"]          = new float[] {  0,  0,  0,  0,  0,  0, -1, -3, -6, -9 },
        ["Warm"]                 = new float[] {  2,  3,  3,  2,  1,  0, -1, -2, -2, -3 },
        ["Bright"]               = new float[] {  0,  0,  0,  0,  0,  1,  2,  3,  5,  6 },
    };

    /// <summary>Имена пресетов в порядке показа (Custom последним).</summary>
    public static IReadOnlyList<string> Names { get; } = All.Keys.Append(Custom).ToList();
}

/// <summary>
/// Все настройки захвата и обработки звука, влияющие на качество записи.
/// Сериализуется в settings.json. Перед использованием вызывать Normalize().
/// </summary>
public sealed class AudioCaptureSettings
{
    // --- Захват ---
    public AudioCaptureApi Api { get; set; } = AudioCaptureApi.WasapiEvent;
    public int BufferMs { get; set; } = 100;
    /// <summary>false = формат берётся из драйвера (mix format). true = запросить формат ниже.</summary>
    public bool RequestCustomDriverFormat { get; set; }
    public int DriverSampleRate { get; set; } = 48000;
    public AudioBitDepth DriverBitDepth { get; set; } = AudioBitDepth.Pcm16;
    public int DriverChannels { get; set; } = 2;
    public bool BoostThreadPriority { get; set; } = true;
    public bool LowerEncoderPriority { get; set; } = true;

    // --- Обработка и выходной формат ---
    /// <summary>0 = как у источника.</summary>
    public int OutputSampleRate { get; set; }
    public AudioBitDepth OutputBitDepth { get; set; } = AudioBitDepth.Pcm16;
    public AudioChannelMode ChannelMode { get; set; } = AudioChannelMode.Auto;
    public float InputGainDb { get; set; }
    /// <summary>0 = выключен.</summary>
    public int HighPassHz { get; set; }
    public bool Dither { get; set; } = true;
    public EqualizerSettings Equalizer { get; set; } = new();

    // --- Итоговая AAC-дорожка в MP4 ---
    public int AacBitrateKbps { get; set; } = 192;

    public static readonly int[] AllowedDriverRates = { 8000, 16000, 22050, 32000, 44100, 48000, 88200, 96000 };

    public AudioCaptureSettings Clone() => new()
    {
        Api = Api, BufferMs = BufferMs,
        RequestCustomDriverFormat = RequestCustomDriverFormat,
        DriverSampleRate = DriverSampleRate, DriverBitDepth = DriverBitDepth, DriverChannels = DriverChannels,
        BoostThreadPriority = BoostThreadPriority, LowerEncoderPriority = LowerEncoderPriority,
        OutputSampleRate = OutputSampleRate, OutputBitDepth = OutputBitDepth, ChannelMode = ChannelMode,
        InputGainDb = InputGainDb, HighPassHz = HighPassHz, Dither = Dither,
        Equalizer = (Equalizer ?? new EqualizerSettings()).Clone(),
        AacBitrateKbps = AacBitrateKbps
    };

    /// <summary>Приводит значения к допустимым (на случай повреждённого JSON).</summary>
    public void Normalize()
    {
        BufferMs = Math.Clamp(BufferMs, 10, 1000);
        DriverSampleRate = AllowedDriverRates.OrderBy(r => Math.Abs(r - DriverSampleRate)).First();
        DriverChannels = Math.Clamp(DriverChannels, 1, 2);
        OutputSampleRate = OutputSampleRate <= 0 ? 0 : Math.Clamp(OutputSampleRate, 8000, 192000);
        InputGainDb = float.IsFinite(InputGainDb) ? Math.Clamp(InputGainDb, -30f, 30f) : 0f;
        HighPassHz = HighPassHz <= 0 ? 0 : Math.Clamp(HighPassHz, 20, 500);
        AacBitrateKbps = Math.Clamp(AacBitrateKbps, 64, 512);
        Equalizer ??= new EqualizerSettings();
        Equalizer.Normalize();
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"api={Api}, buffer={BufferMs}ms");
        sb.Append(RequestCustomDriverFormat
            ? $", driver={DriverSampleRate}Hz/{DriverBitDepth}/{DriverChannels}ch"
            : ", driver=mix format");
        sb.Append($", out={(OutputSampleRate == 0 ? "src" : OutputSampleRate + "Hz")}/{OutputBitDepth}/{ChannelMode}");
        sb.Append(CultureInfo.InvariantCulture, $", gain={InputGainDb:0.#}dB, hp={(HighPassHz == 0 ? "off" : HighPassHz + "Hz")}");
        sb.Append($", eq={(Equalizer.Enabled ? Equalizer.Preset : "off")}, dither={Dither}, boost={BoostThreadPriority}, aac={AacBitrateKbps}k");
        return sb.ToString();
    }
}

/// <summary>Запрос на запись звука в WAV.</summary>
public sealed record AudioCaptureRequest(
    string DeviceId,
    string OutputPath,
    AudioCaptureSettings Settings,
    string? RawCopyPath = null);

/// <summary>Уровень сигнала за последний интервал. Значения в dBFS.</summary>
public readonly record struct AudioLevel(float PeakDb, float RmsDb, int Clipped);

public sealed record AudioCaptureStats(
    long Packets,
    double MaxGapMs,
    double AvgGapMs,
    int LateCallbacks,
    int QueueDepth,
    int MaxQueueDepth,
    double ElapsedSec);

public sealed record AudioCaptureReport(
    string DeviceName,
    string ApiUsed,
    string DriverFormat,
    string OutputFormat,
    long Packets,
    double MaxGapMs,
    double AvgGapMs,
    int LateCallbacks,
    double PaddedMs,
    double DurationSec,
    int MaxQueueDepth,
    bool ThreadBoosted,
    string? Notes)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Device:        {DeviceName}");
        sb.AppendLine($"API:           {ApiUsed}");
        sb.AppendLine($"Driver format: {DriverFormat}");
        sb.AppendLine($"Output format: {OutputFormat}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Duration:      {DurationSec:F2} s, packets: {Packets}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Packet gap:    avg {AvgGapMs:F1} ms, max {MaxGapMs:F0} ms, late callbacks: {LateCallbacks}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Silence added: {PaddedMs:F0} ms, max queue: {MaxQueueDepth}, thread boosted: {(ThreadBoosted ? "yes" : "no")}"));
        if (!string.IsNullOrWhiteSpace(Notes)) sb.AppendLine($"Notes:         {Notes}");
        return sb.ToString();
    }
}

/// <summary>Результат анализа готового аудиофайла на щелчки, провалы и клиппинг (эвристика).</summary>
public sealed record AudioAnalysis(
    string FileName,
    double DurationSec,
    int SampleRate,
    double PeakDb,
    double RmsDb,
    double NoiseFloorDb,
    long ClippedSamples,
    double DcOffset,
    int Dropouts,
    int Clicks,
    double ClicksPerMinute,
    string Verdict)
{
    public string ToText()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"File: {FileName}");
        sb.AppendLine(string.Create(c, $"  Length {DurationSec:F2} s, {SampleRate} Hz (analysed as mono)"));
        sb.AppendLine(string.Create(c, $"  Peak {PeakDb:F1} dBFS, RMS {RmsDb:F1} dBFS, noise floor {NoiseFloorDb:F1} dBFS, DC {DcOffset:F4}"));
        sb.AppendLine(string.Create(c, $"  Clipped samples: {ClippedSamples}, digital-silence dropouts: {Dropouts}"));
        sb.AppendLine(string.Create(c, $"  Suspected clicks: {Clicks} ({ClicksPerMinute:F1} per minute)"));
        sb.AppendLine($"  Verdict: {Verdict}");
        return sb.ToString();
    }
}

public sealed record AudioTestOptions(
    string DeviceId,
    string OutputFolder,
    AudioCaptureSettings Settings,
    bool SaveRawCopy,
    AudioTestContainer Container);

public sealed record AudioTestResult(
    string? ProcessedPath,
    string? RawPath,
    string? ConvertedPath,
    AudioCaptureReport? Report,
    AudioAnalysis? Processed,
    AudioAnalysis? Raw,
    string? Error)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        if (Error != null) sb.AppendLine($"ERROR: {Error}").AppendLine();
        if (Report != null) sb.AppendLine(Report.ToText());
        if (ProcessedPath != null) sb.AppendLine($"Processed: {ProcessedPath}");
        if (RawPath != null) sb.AppendLine($"Raw (unprocessed): {RawPath}");
        if (ConvertedPath != null) sb.AppendLine($"Converted: {ConvertedPath}");
        if (Processed != null) { sb.AppendLine(); sb.AppendLine("--- Processed ---"); sb.Append(Processed.ToText()); }
        if (Raw != null) { sb.AppendLine(); sb.AppendLine("--- Raw ---"); sb.Append(Raw.ToText()); }
        return sb.ToString();
    }
}
