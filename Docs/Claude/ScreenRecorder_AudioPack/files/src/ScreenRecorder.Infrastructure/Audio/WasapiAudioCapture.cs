using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Infrastructure.Audio;

/// <summary>
/// Захват звука через WASAPI в WAV.
/// Поток захвата NAudio делает минимум работы: копирует пакет в очередь и считает тайминги.
/// Конвертация, фильтры, ресемплинг и запись на диск выполняются в отдельном потоке записи,
/// поэтому медленный диск, ресемплер или нагрузка на UI не приводят к потере пакетов устройства.
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    private readonly struct Chunk
    {
        public readonly byte[]? Data;
        public readonly int Length;
        public readonly long PadBytes;
        private Chunk(byte[]? data, int length, long pad) { Data = data; Length = length; PadBytes = pad; }
        public static Chunk Audio(byte[] data, int length) => new(data, length, 0);
        public static Chunk Silence(long bytes) => new(null, 0, bytes);
    }

    private readonly ILoggerService _logger;
    private readonly Stopwatch _clock = new();

    private WasapiCapture? _capture;
    private BlockingCollection<Chunk>? _queue;
    private TaskCompletionSource? _finished;
    private AudioCaptureSettings _settings = new();
    private WaveFormat? _format;

    // Состояние, которое пишет только поток захвата.
    private bool _padGaps, _first, _boosted, _boostedOk;
    private long _acceptedBytes, _paddedBytes, _packets;
    private double _maxGapMs, _gapSumMs;
    private int _lateCallbacks;
    private volatile int _maxDepth;
    private TimeSpan _lastPacketAt;
    private double _lateThresholdMs;

    private string _deviceName = "?";
    private string _apiUsed = "";
    private bool _gcChanged;
    private GCLatencyMode _prevGcMode;

    public WasapiAudioCapture(ILoggerService logger) => _logger = logger;

    public bool IsRunning => _capture != null;
    public string? LastError { get; private set; }
    public AudioCaptureReport? LastReport { get; private set; }
    public event Action<AudioLevel>? LevelMeasured;

    public AudioCaptureStats? GetStats()
    {
        if (_queue == null && LastReport != null) return null;
        long p = _packets;
        return new AudioCaptureStats(p, _maxGapMs, p > 1 ? _gapSumMs / (p - 1) : 0, _lateCallbacks,
            _queue?.Count ?? 0, _maxDepth, _clock.Elapsed.TotalSeconds);
    }

    public Task StartAsync(AudioCaptureRequest req, CancellationToken ct)
    {
        if (_capture != null)
        {
            _logger.Log("Audio: capture already running");
            return Task.CompletedTask;
        }

        LastError = null;
        LastReport = null;
        var settings = req.Settings.Clone();
        settings.Normalize();
        _settings = settings;

        try
        {
            _logger.Log($"Audio: starting for '{req.DeviceId}'");
            _logger.Log($"Audio settings: {settings.Describe()}");

            using var enumerator = new MMDeviceEnumerator();
            var device = FindDevice(enumerator, req.DeviceId);
            if (device == null)
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _logger.Log($"Audio: device not found, fallback to default output '{device.FriendlyName}'");
            }
            _deviceName = device.FriendlyName;
            bool isRender = device.DataFlow == DataFlow.Render;

            // Основная попытка по настройкам пользователя, затем безопасный запасной вариант.
            var attempts = new System.Collections.Generic.List<(AudioCaptureApi Api, bool Custom)>
            {
                (settings.Api, settings.RequestCustomDriverFormat)
            };
            if (!isRender && (settings.Api != AudioCaptureApi.WasapiEvent || settings.RequestCustomDriverFormat))
                attempts.Add((AudioCaptureApi.WasapiEvent, false));

            foreach (var attempt in attempts)
            {
                if (TryStart(device, isRender, attempt.Api, attempt.Custom, req, settings)) return Task.CompletedTask;
            }

            LastError ??= "Could not start audio capture (see log)";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.Log($"Audio Error: {ex}");
        }
        return Task.CompletedTask;
    }

    private bool TryStart(MMDevice device, bool isRender, AudioCaptureApi api, bool custom,
        AudioCaptureRequest req, AudioCaptureSettings settings)
    {
        WasapiCapture? cap = null;
        WaveFileWriter? writer = null, rawWriter = null;
        BlockingCollection<Chunk>? queue = null;
        try
        {
            cap = isRender ? new WasapiLoopbackCapture(device) : CreateMicCapture(device, api, custom, settings);
            _apiUsed = isRender ? "WASAPI loopback (system audio)" :
                $"WASAPI {(api == AudioCaptureApi.WasapiExclusive ? "exclusive" : "shared")}, " +
                $"{(api == AudioCaptureApi.WasapiPolling ? "polling" : "event-driven")}, buffer {settings.BufferMs} ms";

            var fmt = cap.WaveFormat;
            _format = fmt;
            var chain = new AudioProcessingChain(fmt, settings);
            writer = CreateWriter(req.OutputPath, chain.OutputFormat);
            if (!string.IsNullOrEmpty(req.RawCopyPath))
                rawWriter = CreateWriter(req.RawCopyPath, AudioFormatHelper.ToStandard(fmt));

            _logger.Log($"Audio: '{_deviceName}' {_apiUsed}; driver {AudioFormatHelper.Describe(fmt)} -> " +
                        $"output {AudioFormatHelper.Describe(chain.OutputFormat)}");

            // Сброс счётчиков.
            _padGaps = isRender;
            _first = true; _boosted = false; _boostedOk = false;
            _acceptedBytes = 0; _paddedBytes = 0; _packets = 0;
            _maxGapMs = 0; _gapSumMs = 0; _lateCallbacks = 0; _maxDepth = 0;
            _lastPacketAt = TimeSpan.Zero;
            _lateThresholdMs = Math.Max(60, settings.BufferMs * 1.5);

            queue = new BlockingCollection<Chunk>(new ConcurrentQueue<Chunk>());
            _queue = queue;
            _finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (settings.BoostThreadPriority)
            {
                _prevGcMode = GCSettings.LatencyMode;
                try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; _gcChanged = true; } catch { }
            }

            var w = writer; var r = rawWriter; var q = queue; var fin = _finished;

            cap.DataAvailable += OnData;
            cap.RecordingStopped += OnStopped;
            _capture = cap;
            _clock.Restart();
            cap.StartRecording();

            // Поток записи стартует только после успешного StartRecording: при ошибке старта
            // нет «висящих» потоков, а пакеты, пришедшие раньше, просто ждут в очереди.
            var thread = new Thread(() => WriterLoop(q, w, r, chain, fmt, fin))
            {
                IsBackground = true,
                Name = "AudioWriter",
                Priority = ThreadPriority.AboveNormal
            };
            thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.Log($"Audio: start failed ({(isRender ? "loopback" : $"{api}, custom={custom}")}): {ex.Message}");
            try { queue?.CompleteAdding(); } catch { }
            _capture = null;
            _queue = null;
            _finished = null;
            try { cap?.Dispose(); } catch { }
            try { writer?.Dispose(); } catch { }
            try { rawWriter?.Dispose(); } catch { }
            RestoreGc();
            return false;
        }
    }

    private WasapiCapture CreateMicCapture(MMDevice device, AudioCaptureApi api, bool custom, AudioCaptureSettings s)
    {
        bool exclusive = api == AudioCaptureApi.WasapiExclusive;
        bool eventSync = api != AudioCaptureApi.WasapiPolling;
        var cap = new WasapiCapture(device, eventSync, s.BufferMs);
        if (exclusive) cap.ShareMode = AudioClientShareMode.Exclusive;

        WaveFormat? wanted = custom ? BuildDriverFormat(s) : null;
        var check = wanted ?? cap.WaveFormat;
        if (exclusive && !device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, check))
        {
            cap.Dispose();
            throw new NotSupportedException(
                $"Exclusive mode: driver does not accept {AudioFormatHelper.Describe(check)}");
        }
        // В shared-режиме NAudio включает AutoConvertPcm: Windows сама приведёт поток к запрошенному формату.
        if (wanted != null) cap.WaveFormat = wanted;
        return cap;
    }

    private static WaveFormat BuildDriverFormat(AudioCaptureSettings s) => s.DriverBitDepth switch
    {
        AudioBitDepth.Pcm16 => new WaveFormat(s.DriverSampleRate, 16, s.DriverChannels),
        AudioBitDepth.Pcm24 => new WaveFormat(s.DriverSampleRate, 24, s.DriverChannels),
        _ => WaveFormat.CreateIeeeFloatWaveFormat(s.DriverSampleRate, s.DriverChannels)
    };

    private static WaveFileWriter CreateWriter(string path, WaveFormat fmt)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 18);
        return new WaveFileWriter(fs, fmt);
    }

    private static MMDevice? FindDevice(MMDeviceEnumerator en, string idOrName)
    {
        try { return en.GetDevice(idOrName); } catch { }
        var all = en.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
        return all.FirstOrDefault(d => d.ID.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.FriendlyName.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.FriendlyName.Contains(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    // ----- Поток захвата NAudio: только копирование и тайминги -----
    private void OnData(object? s, WaveInEventArgs e)
    {
        var q = _queue;
        var fmt = _format;
        if (q == null || fmt == null || e.BytesRecorded <= 0) return;

        try
        {
            if (!_boosted)
            {
                _boosted = true;
                if (_settings.BoostThreadPriority) BoostCurrentThread();
            }

            var now = _clock.Elapsed;
            _packets++;
            if (_packets > 1)
            {
                double gap = (now - _lastPacketAt).TotalMilliseconds;
                _gapSumMs += gap;
                if (gap > _maxGapMs) _maxGapMs = gap;
                if (!_padGaps && gap > _lateThresholdMs) _lateCallbacks++;
            }
            _lastPacketAt = now;

            // Loopback: пакеты не приходят в тишине, заполняем паузы нулями по часам.
            // Микрофон: только задержка старта устройства (перед первым пакетом); нули посреди речи дают треск.
            if (_padGaps || _first)
            {
                long target = BytesAt(now, fmt) - e.BytesRecorded;
                long gapBytes = target - _acceptedBytes;
                if (gapBytes > BytesAt(TimeSpan.FromMilliseconds(50), fmt))
                {
                    gapBytes -= gapBytes % fmt.BlockAlign;
                    q.Add(Chunk.Silence(gapBytes));
                    _acceptedBytes += gapBytes;
                    _paddedBytes += gapBytes;
                }
            }
            _first = false;

            var buf = ArrayPool<byte>.Shared.Rent(e.BytesRecorded);
            Buffer.BlockCopy(e.Buffer, 0, buf, 0, e.BytesRecorded);
            q.Add(Chunk.Audio(buf, e.BytesRecorded));
            _acceptedBytes += e.BytesRecorded;

            int depth = q.Count;
            if (depth > _maxDepth) _maxDepth = depth;
        }
        catch (InvalidOperationException) { /* очередь уже закрыта при остановке */ }
    }

    private void BoostCurrentThread()
    {
        try
        {
            uint index = 0;
            _boostedOk = AvSetMmThreadCharacteristics("Pro Audio", ref index) != IntPtr.Zero;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            _logger.Log($"Audio: capture thread boosted (MMCSS Pro Audio: {(_boostedOk ? "ok" : "unavailable")})");
        }
        catch (Exception ex)
        {
            _logger.Log($"Audio: could not boost capture thread: {ex.Message}");
        }
    }

    private void OnStopped(object? s, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            LastError = e.Exception.Message;
            _logger.Log($"Audio: stopped with error: {e.Exception.Message}");
        }

        var q = _queue;
        var fmt = _format;
        try
        {
            // Хвост тишины до реальной длительности записи (как и раньше).
            if (q != null && fmt != null && !q.IsAddingCompleted)
            {
                long gap = BytesAt(_clock.Elapsed, fmt) - _acceptedBytes;
                if (gap > BytesAt(TimeSpan.FromMilliseconds(50), fmt))
                {
                    gap -= gap % fmt.BlockAlign;
                    q.Add(Chunk.Silence(gap));
                    _paddedBytes += gap;
                    _acceptedBytes += gap;
                }
            }
        }
        catch (InvalidOperationException) { }
        try { q?.CompleteAdding(); } catch { }
        try { _capture?.Dispose(); } catch { }
    }

    public async Task StopAsync()
    {
        var cap = _capture;
        var fin = _finished;
        if (cap == null || fin == null) return;

        try { cap.StopRecording(); } catch { }
        if (await Task.WhenAny(fin.Task, Task.Delay(5000)) != fin.Task)
        {
            _logger.Log("Audio: RecordingStopped not received within 5 s, finalizing anyway");
            try { _queue?.CompleteAdding(); } catch { }
            await Task.WhenAny(fin.Task, Task.Delay(5000));
        }
    }

    // ----- Поток записи: конвертация, фильтры, ресемплинг, диск -----
    private void WriterLoop(BlockingCollection<Chunk> q, WaveFileWriter writer, WaveFileWriter? raw,
        AudioProcessingChain chain, WaveFormat fmt, TaskCompletionSource fin)
    {
        try
        {
            Action<byte[], int> sink = (b, n) => writer.Write(b, 0, n);
            int zeroLen = 16 * 1024;
            zeroLen -= zeroLen % fmt.BlockAlign;
            if (zeroLen < fmt.BlockAlign) zeroLen = fmt.BlockAlign;
            var zeros = new byte[zeroLen];
            var levelClock = Stopwatch.StartNew();

            foreach (var c in q.GetConsumingEnumerable())
            {
                if (c.Data != null)
                {
                    raw?.Write(c.Data, 0, c.Length);
                    chain.Process(c.Data, c.Length, sink);
                    ArrayPool<byte>.Shared.Return(c.Data);
                }
                else
                {
                    long remaining = c.PadBytes;
                    while (remaining > 0)
                    {
                        int n = (int)Math.Min(remaining, zeros.Length);
                        raw?.Write(zeros, 0, n);
                        chain.Process(zeros, n, sink);
                        remaining -= n;
                    }
                }

                if (levelClock.ElapsedMilliseconds >= 100)
                {
                    levelClock.Restart();
                    try { LevelMeasured?.Invoke(chain.TakeLevel()); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.Log($"Audio writer error: {ex}");
            // Очищаем очередь, чтобы поток захвата не копил память.
            try { foreach (var _ in q.GetConsumingEnumerable()) { } } catch { }
        }
        finally
        {
            try { writer.Dispose(); } catch (Exception ex) { _logger.Log($"Audio: closing WAV failed: {ex.Message}"); }
            try { raw?.Dispose(); } catch { }
            FinishReport(fmt, chain);
            _capture = null;
            _queue = null;
            RestoreGc();
            fin.TrySetResult();
        }
    }

    private void FinishReport(WaveFormat fmt, AudioProcessingChain chain)
    {
        _clock.Stop();
        double paddedMs = _paddedBytes * 1000.0 / fmt.AverageBytesPerSecond;
        double durationSec = _acceptedBytes / (double)fmt.AverageBytesPerSecond;
        double avgGap = _packets > 1 ? _gapSumMs / (_packets - 1) : 0;

        string? notes = null;
        if (!_padGaps && (_lateCallbacks > 0 || _maxGapMs > _settings.BufferMs * 3))
            notes = $"Packets arrived late {_lateCallbacks} time(s), max gap {_maxGapMs:F0} ms: the capture thread was " +
                    "starved or the driver stalled. Try a larger buffer, polling mode, or a lower load (FPS/encoder).";
        if (LastError != null) notes = (notes == null ? "" : notes + " ") + $"Error: {LastError}";

        LastReport = new AudioCaptureReport(_deviceName, _apiUsed, AudioFormatHelper.Describe(fmt),
            AudioFormatHelper.Describe(chain.OutputFormat), _packets, _maxGapMs, avgGap, _lateCallbacks,
            paddedMs, durationSec, _maxDepth, _boostedOk, notes);

        _logger.Log($"Audio: {_packets} packets, max gap {_maxGapMs:F0} ms, late {_lateCallbacks}, " +
                    $"padded {paddedMs:F0} ms, file {durationSec:F2} s");
    }

    private void RestoreGc()
    {
        if (!_gcChanged) return;
        _gcChanged = false;
        try { GCSettings.LatencyMode = _prevGcMode; } catch { }
    }

    private static long BytesAt(TimeSpan t, WaveFormat f)
    {
        long b = (long)(t.TotalSeconds * f.AverageBytesPerSecond);
        return b - b % f.BlockAlign;
    }
}
