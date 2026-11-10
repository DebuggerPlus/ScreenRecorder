using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenRecorder.App.Services;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ScreenRecorder.App.ViewModels;

/// <summary>Пункт выпадающего списка: подпись + значение.</summary>
public sealed record Option<T>(string Label, T Value);

public sealed class AudioDeviceItem
{
    public AudioDevice Device { get; }
    public AudioDeviceItem(AudioDevice device) => Device = device;
    public string Display => (Device.IsInput ? "[Input] " : "[System audio / loopback] ") + Device.Name;
}

public partial class EqBandViewModel : ObservableObject
{
    public string Label { get; }
    [ObservableProperty] private float _gainDb;
    public EqBandViewModel(string label) => Label = label;
}

/// <summary>
/// Окно настроек звука: источник, формат захвата, обработка, эквалайзер и тестовая запись без видео.
/// Все изменения сразу сохраняются в settings.json и используются при следующей записи.
/// </summary>
public partial class AudioSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IAudioDeviceService _devices;
    private readonly IAudioTestService _test;
    private readonly ILoggerService _logger;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statsTimer;
    private bool _loading;
    private bool _applyingPreset;
    private bool _deviceTouched; // пользователь сам менял устройство в этом окне
    private DateTime _testStartedAt;

    private static readonly HashSet<string> PersistedProperties = new()
    {
        nameof(Api), nameof(BufferMs), nameof(RequestCustomDriverFormat), nameof(DriverSampleRate),
        nameof(DriverBitDepth), nameof(DriverChannels), nameof(BoostThreadPriority), nameof(LowerEncoderPriority),
        nameof(OutputSampleRate), nameof(OutputBitDepth), nameof(ChannelMode), nameof(InputGainDb),
        nameof(HighPassHz), nameof(Dither), nameof(AacBitrateKbps), nameof(EqEnabled), nameof(EqPreset),
        nameof(SaveRawCopy), nameof(TestContainer), nameof(AutoStopSeconds)
    };

    // ----- Списки выбора -----
    public ObservableCollection<AudioDeviceItem> Devices { get; } = new();
    public ObservableCollection<EqBandViewModel> Bands { get; } = new();

    public IReadOnlyList<Option<AudioCaptureApi>> Apis { get; } = new[]
    {
        new Option<AudioCaptureApi>("WASAPI shared, event-driven (recommended)", AudioCaptureApi.WasapiEvent),
        new Option<AudioCaptureApi>("WASAPI shared, polling (try for iVCam / virtual drivers)", AudioCaptureApi.WasapiPolling),
        new Option<AudioCaptureApi>("WASAPI exclusive (driver must accept the format)", AudioCaptureApi.WasapiExclusive),
    };
    public IReadOnlyList<Option<int>> DriverSampleRates { get; } =
        AudioCaptureSettings.AllowedDriverRates.Select(r => new Option<int>($"{r} Hz", r)).ToList();
    public IReadOnlyList<Option<int>> DriverChannelOptions { get; } = new[]
        { new Option<int>("Mono (1 ch)", 1), new Option<int>("Stereo (2 ch)", 2) };
    public IReadOnlyList<Option<AudioBitDepth>> BitDepths { get; } = new[]
    {
        new Option<AudioBitDepth>("16-bit PCM", AudioBitDepth.Pcm16),
        new Option<AudioBitDepth>("24-bit PCM", AudioBitDepth.Pcm24),
        new Option<AudioBitDepth>("32-bit float", AudioBitDepth.Float32),
    };
    public IReadOnlyList<Option<int>> OutputSampleRates { get; } = new[]
    {
        new Option<int>("Same as source", 0), new Option<int>("8000 Hz", 8000), new Option<int>("16000 Hz", 16000),
        new Option<int>("22050 Hz", 22050), new Option<int>("32000 Hz", 32000), new Option<int>("44100 Hz", 44100),
        new Option<int>("48000 Hz", 48000), new Option<int>("88200 Hz", 88200), new Option<int>("96000 Hz", 96000),
    };
    public IReadOnlyList<Option<AudioChannelMode>> ChannelModes { get; } = new[]
    {
        new Option<AudioChannelMode>("Auto (as source, max 2)", AudioChannelMode.Auto),
        new Option<AudioChannelMode>("Mono (mix all channels)", AudioChannelMode.Mono),
        new Option<AudioChannelMode>("Stereo", AudioChannelMode.Stereo),
        new Option<AudioChannelMode>("Left channel only", AudioChannelMode.LeftOnly),
        new Option<AudioChannelMode>("Right channel only", AudioChannelMode.RightOnly),
    };
    public IReadOnlyList<Option<int>> HighPassOptions { get; } = new[]
    {
        new Option<int>("Off", 0), new Option<int>("60 Hz", 60), new Option<int>("80 Hz", 80),
        new Option<int>("100 Hz", 100), new Option<int>("150 Hz", 150), new Option<int>("200 Hz", 200),
    };
    public IReadOnlyList<Option<int>> AacBitrates { get; } = new[]
    {
        new Option<int>("96 kbps", 96), new Option<int>("128 kbps", 128), new Option<int>("160 kbps", 160),
        new Option<int>("192 kbps", 192), new Option<int>("256 kbps", 256), new Option<int>("320 kbps", 320),
    };
    public IReadOnlyList<Option<AudioTestContainer>> Containers { get; } = new[]
    {
        new Option<AudioTestContainer>("WAV only", AudioTestContainer.Wav),
        new Option<AudioTestContainer>("WAV + FLAC", AudioTestContainer.Flac),
        new Option<AudioTestContainer>("WAV + MP3", AudioTestContainer.Mp3),
        new Option<AudioTestContainer>("WAV + M4A (AAC)", AudioTestContainer.M4a),
    };
    public IReadOnlyList<Option<int>> AutoStopOptions { get; } = new[]
    {
        new Option<int>("Until I press Stop", 0), new Option<int>("5 seconds", 5), new Option<int>("10 seconds", 10),
        new Option<int>("30 seconds", 30), new Option<int>("60 seconds", 60),
    };
    public IReadOnlyList<string> EqPresetNames { get; } = EqualizerPresets.Names;

    // ----- Источник -----
    [ObservableProperty] private AudioDeviceItem? _selectedDevice;
    [ObservableProperty] private string _deviceFormatText = "";

    // ----- Захват -----
    [ObservableProperty] private AudioCaptureApi _api;
    [ObservableProperty] private int _bufferMs;
    [ObservableProperty] private bool _requestCustomDriverFormat;
    [ObservableProperty] private int _driverSampleRate;
    [ObservableProperty] private AudioBitDepth _driverBitDepth;
    [ObservableProperty] private int _driverChannels;
    [ObservableProperty] private bool _boostThreadPriority;
    [ObservableProperty] private bool _lowerEncoderPriority;

    // ----- Обработка -----
    [ObservableProperty] private int _outputSampleRate;
    [ObservableProperty] private AudioBitDepth _outputBitDepth;
    [ObservableProperty] private AudioChannelMode _channelMode;
    [ObservableProperty] private float _inputGainDb;
    [ObservableProperty] private int _highPassHz;
    [ObservableProperty] private bool _dither;
    [ObservableProperty] private int _aacBitrateKbps;

    // ----- Эквалайзер -----
    [ObservableProperty] private bool _eqEnabled;
    [ObservableProperty] private string _eqPreset = "Flat";

    // ----- Тест -----
    [ObservableProperty] private bool _saveRawCopy = true;
    [ObservableProperty] private AudioTestContainer _testContainer = AudioTestContainer.Wav;
    [ObservableProperty] private int _autoStopSeconds;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isTesting;
    [ObservableProperty] private double _peakPercent;
    [ObservableProperty] private double _rmsPercent;
    [ObservableProperty] private bool _clipWarning;
    [ObservableProperty] private string _levelText = "Peak -∞ dBFS   RMS -∞ dBFS";
    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private string _statusText = "Ready. Pick a device and press Record test.";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProcessed))] private string? _lastProcessedPath;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRaw))] private string? _lastRawPath;

    public bool IsIdle => !IsTesting;
    public bool HasProcessed => !string.IsNullOrEmpty(LastProcessedPath) && File.Exists(LastProcessedPath);
    public bool HasRaw => !string.IsNullOrEmpty(LastRawPath) && File.Exists(LastRawPath);
    public string TestFolder => Path.Combine(_settings.Current.OutputFolder, "AudioTests");

    public AudioSettingsViewModel(SettingsService settings, IAudioDeviceService devices,
        IAudioTestService test, ILoggerService logger)
    {
        _settings = settings;
        _devices = devices;
        _test = test;
        _logger = logger;

        for (int i = 0; i < EqualizerPresets.BandCount; i++)
        {
            var band = new EqBandViewModel(EqualizerPresets.BandLabels[i]);
            band.PropertyChanged += OnBandChanged;
            Bands.Add(band);
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); ApplyToSettings(); };
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _statsTimer.Tick += OnStatsTick;

        _test.Level += OnLevel;
        LoadFromSettings();
        RefreshDevices();
    }

    // ----- Загрузка и сохранение -----
    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var a = _settings.Current.Audio;
            a.Normalize();
            Api = a.Api; BufferMs = a.BufferMs;
            RequestCustomDriverFormat = a.RequestCustomDriverFormat;
            DriverSampleRate = a.DriverSampleRate; DriverBitDepth = a.DriverBitDepth; DriverChannels = a.DriverChannels;
            BoostThreadPriority = a.BoostThreadPriority; LowerEncoderPriority = a.LowerEncoderPriority;
            OutputSampleRate = a.OutputSampleRate; OutputBitDepth = a.OutputBitDepth; ChannelMode = a.ChannelMode;
            InputGainDb = a.InputGainDb; HighPassHz = a.HighPassHz; Dither = a.Dither; AacBitrateKbps = a.AacBitrateKbps;
            EqEnabled = a.Equalizer.Enabled; EqPreset = a.Equalizer.Preset;
            for (int i = 0; i < Bands.Count; i++) Bands[i].GainDb = a.Equalizer.BandGainsDb[i];
        }
        finally { _loading = false; }
    }

    private void ApplyToSettings()
    {
        var a = _settings.Current.Audio;
        a.Api = Api; a.BufferMs = BufferMs;
        a.RequestCustomDriverFormat = RequestCustomDriverFormat;
        a.DriverSampleRate = DriverSampleRate; a.DriverBitDepth = DriverBitDepth; a.DriverChannels = DriverChannels;
        a.BoostThreadPriority = BoostThreadPriority; a.LowerEncoderPriority = LowerEncoderPriority;
        a.OutputSampleRate = OutputSampleRate; a.OutputBitDepth = OutputBitDepth; a.ChannelMode = ChannelMode;
        a.InputGainDb = InputGainDb; a.HighPassHz = HighPassHz; a.Dither = Dither; a.AacBitrateKbps = AacBitrateKbps;
        a.Equalizer.Enabled = EqEnabled; a.Equalizer.Preset = EqPreset;
        for (int i = 0; i < Bands.Count; i++) a.Equalizer.BandGainsDb[i] = Bands[i].GainDb;
        a.Normalize();
        // Устройство пишем только если его меняли в этом окне: иначе простое открытие окна
        // могло бы подменить выбор на главной странице (например, "No Audio").
        if (_deviceTouched && SelectedDevice != null) _settings.Current.LastAudioDevice = SelectedDevice.Device.Name;
        _settings.Save();
    }

    private void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }

    /// <summary>Немедленно сохраняет настройки (перед тестом и при закрытии окна).</summary>
    public void FlushSave() { _saveTimer.Stop(); ApplyToSettings(); }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName == null) return;
        if (PersistedProperties.Contains(e.PropertyName)) ScheduleSave();
    }

    partial void OnSelectedDeviceChanged(AudioDeviceItem? value)
    {
        DeviceFormatText = value == null ? "" : _devices.DescribeMixFormat(value.Device.Id);
        if (!_loading && value != null) { _deviceTouched = true; ScheduleSave(); }
    }

    // ----- Эквалайзер -----
    private void OnBandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || _applyingPreset) return;
        if (e.PropertyName == nameof(EqBandViewModel.GainDb))
        {
            EqPreset = EqualizerPresets.Custom;
            ScheduleSave();
        }
    }

    partial void OnEqPresetChanged(string value)
    {
        if (_loading || _applyingPreset) return;
        if (EqualizerPresets.All.TryGetValue(value, out var gains))
        {
            _applyingPreset = true;
            try { for (int i = 0; i < Bands.Count; i++) Bands[i].GainDb = gains[i]; }
            finally { _applyingPreset = false; }
            if (value != "Flat") EqEnabled = true;
        }
    }

    [RelayCommand] private void ResetEqualizer() => EqPreset = "Flat";

    // ----- Устройства -----
    [RelayCommand]
    private void RefreshDevices()
    {
        string? keepId = SelectedDevice?.Device.Id;
        string? keepName = SelectedDevice?.Device.Name ?? _settings.Current.LastAudioDevice;
        _loading = true;
        try
        {
            Devices.Clear();
            foreach (var d in _devices.GetDevices()) Devices.Add(new AudioDeviceItem(d));
            SelectedDevice = Devices.FirstOrDefault(d => d.Device.Id == keepId)
                          ?? Devices.FirstOrDefault(d => d.Device.Name == keepName)
                          ?? Devices.FirstOrDefault();
        }
        finally { _loading = false; }
        if (SelectedDevice != null) DeviceFormatText = _devices.DescribeMixFormat(SelectedDevice.Device.Id);
    }

    [RelayCommand]
    private async Task ProbeFormatsAsync()
    {
        if (SelectedDevice == null) { StatusText = "Select a device first."; return; }
        StatusText = "Probing device formats...";
        ResultText = await _devices.ProbeFormatsAsync(SelectedDevice.Device.Id);
        StatusText = "Probe finished.";
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        if (MessageBox.Show("Reset all audio settings to defaults?", "Audio settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _settings.Current.Audio = new AudioCaptureSettings();
        _settings.Save();
        LoadFromSettings();
    }

    // ----- Тестовая запись -----
    [RelayCommand]
    private async Task StartTestAsync()
    {
        if (IsTesting) return;
        if (SelectedDevice == null) { StatusText = "Select a device first."; return; }

        FlushSave();
        ClipWarning = false; PeakPercent = 0; RmsPercent = 0; StatsText = "";
        StatusText = "Starting...";
        var options = new AudioTestOptions(SelectedDevice.Device.Id, TestFolder,
            _settings.Current.Audio.Clone(), SaveRawCopy, TestContainer);

        string? error = await _test.StartAsync(options);
        if (error != null)
        {
            StatusText = "Could not start: " + error;
            ResultText = "ERROR: " + error;
            return;
        }
        _testStartedAt = DateTime.Now;
        IsTesting = true;
        _statsTimer.Start();
        StatusText = "Recording test... speak into the microphone, then press Stop.";
    }

    [RelayCommand]
    private async Task StopTestAsync()
    {
        if (!IsTesting) return;
        _statsTimer.Stop();
        StatusText = "Finishing and analysing...";
        var r = await _test.StopAsync();
        IsTesting = false;
        PeakPercent = 0; RmsPercent = 0;

        LastProcessedPath = r.ProcessedPath;
        LastRawPath = r.RawPath;
        ResultText = r.ToText();
        StatusText = r.Error == null ? "Test finished. Use Play buttons to listen." : "Test finished with errors, see results.";
        RefreshFileButtons();
    }

    [RelayCommand]
    private async Task ReprocessRawAsync()
    {
        if (!HasRaw) { StatusText = "Record a test with 'Save raw copy' first."; return; }
        FlushSave();
        StatusText = "Applying current settings to the raw recording...";
        var r = await _test.ReprocessAsync(LastRawPath!, _settings.Current.Audio.Clone(), TestContainer);
        if (r.ProcessedPath != null) LastProcessedPath = r.ProcessedPath;
        ResultText = "Raw recording re-processed with the current settings:" + Environment.NewLine + r.ToText();
        StatusText = r.Error == null ? "Done. Press 'Play processed' to listen." : "Re-processing failed, see results.";
        RefreshFileButtons();
    }

    [RelayCommand]
    private async Task AnalyzeFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a recording to analyse (e.g. a Bandicam file to compare)",
            Filter = "Audio/video|*.wav;*.mp4;*.m4a;*.mp3;*.flac;*.mkv;*.avi;*.ogg|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        StatusText = "Analysing " + Path.GetFileName(dialog.FileName) + "...";
        try
        {
            var a = await _test.AnalyzeFileAsync(dialog.FileName);
            ResultText = a.ToText() + Environment.NewLine + ResultText;
            StatusText = "Analysis finished.";
        }
        catch (Exception ex)
        {
            ResultText = "Analysis failed: " + ex.Message + Environment.NewLine + ResultText;
            StatusText = "Analysis failed.";
        }
    }

    [RelayCommand] private void PlayProcessed() => OpenFile(LastProcessedPath);
    [RelayCommand] private void PlayRaw() => OpenFile(LastRawPath);

    [RelayCommand]
    private void OpenTestFolder()
    {
        try
        {
            Directory.CreateDirectory(TestFolder);
            Process.Start(new ProcessStartInfo(TestFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText = "Cannot open folder: " + ex.Message; }
    }

    private void OpenFile(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText = "Cannot open file: " + ex.Message; }
    }

    private void RefreshFileButtons()
    {
        OnPropertyChanged(nameof(HasProcessed));
        OnPropertyChanged(nameof(HasRaw));
    }

    // Вызывается из фонового потока записи ~10 раз в секунду.
    private void OnLevel(AudioLevel level)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            PeakPercent = ToPercent(level.PeakDb);
            RmsPercent = ToPercent(level.RmsDb);
            if (level.Clipped > 0) ClipWarning = true;
            LevelText = $"Peak {Fmt(level.PeakDb)} dBFS   RMS {Fmt(level.RmsDb)} dBFS";
        }));
    }

    private static double ToPercent(float db) => Math.Clamp((db + 60.0) / 60.0 * 100.0, 0, 100);
    private static string Fmt(float db) => db <= -119f ? "-∞" : db.ToString("F1");

    private void OnStatsTick(object? sender, EventArgs e)
    {
        var s = _test.GetLiveStats();
        if (s != null)
        {
            StatsText = $"{s.ElapsedSec:F1} s  |  packets {s.Packets}  |  max gap {s.MaxGapMs:F0} ms (avg {s.AvgGapMs:F1})  |  " +
                        $"late {s.LateCallbacks}  |  queue {s.QueueDepth} (max {s.MaxQueueDepth})";
        }
        if (IsTesting && AutoStopSeconds > 0 && (DateTime.Now - _testStartedAt).TotalSeconds >= AutoStopSeconds)
            _ = StopTestAsync();
    }

    /// <summary>Вызывается при закрытии окна.</summary>
    public void Cleanup()
    {
        _statsTimer.Stop();
        _test.Level -= OnLevel;
        FlushSave();
        if (IsTesting) _ = _test.StopAsync();
    }
}
