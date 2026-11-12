using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenRecorder.App.Services;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.App.ViewModels;

/// <summary>
/// Окно обработки готового видео: шумоподавление (с настройками), эквалайзер, уровень.
/// Видео-поток копируется, перекодируется только звук.
/// </summary>
public partial class VideoProcessingViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IVideoProcessor _processor;
    private readonly IAudioTestService _analysis;
    private readonly ILoggerService _logger;
    private readonly DispatcherTimer _saveTimer;

    private CancellationTokenSource? _cts;
    private bool _loading;
    private bool _applyingPreset;
    private bool _suggestingOutput;
    private bool _hasVideo = true;
    private bool _hasAudio = true;

    public VideoProcessingViewModel(SettingsService settings, IVideoProcessor processor,
        IAudioTestService analysis, ILoggerService logger)
    {
        _settings = settings;
        _processor = processor;
        _analysis = analysis;
        _logger = logger;

        for (int i = 0; i < EqualizerPresets.BandCount; i++)
        {
            var band = new EqBandViewModel(EqualizerPresets.BandLabels[i]);
            band.PropertyChanged += OnBandChanged;
            Bands.Add(band);
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); ApplyToSettings(); };

        LoadFromSettings();

        // Последняя запись — самый частый вход для этого окна.
        if (string.IsNullOrWhiteSpace(InputPath)) InputPath = FindLatestRecording() ?? "";

        RefreshCommandText();
    }

    // ----- Списки выбора -----
    public ObservableCollection<EqBandViewModel> Bands { get; } = new();

    public IReadOnlyList<Option<NoiseType>> NoiseTypes { get; } = new[]
    {
        new Option<NoiseType>("White", NoiseType.White),
        new Option<NoiseType>("Vinyl", NoiseType.Vinyl),
        new Option<NoiseType>("Shellac", NoiseType.Shellac),
    };

    public IReadOnlyList<Option<LevelTarget>> LevelTargets { get; } = new[]
    {
        new Option<LevelTarget>("Off", LevelTarget.Off),
        new Option<LevelTarget>("Loudness, EBU R128 -16 LUFS (loudnorm)", LevelTarget.Loudness),
        new Option<LevelTarget>("Peak limiter -1 dBFS (alimiter)", LevelTarget.Limiter),
        new Option<LevelTarget>("Speech normalizer (speechnorm)", LevelTarget.Speech),
    };

    public IReadOnlyList<Option<int>> HighPassOptions { get; } =
        NoiseReductionSettings.HighPassChoices.Select(hz => new Option<int>(hz == 0 ? "Off" : $"{hz} Hz", hz)).ToList();

    public IReadOnlyList<Option<int>> LowPassOptions { get; } =
        NoiseReductionSettings.LowPassChoices.Select(hz => new Option<int>(hz == 0 ? "Off" : $"{hz} Hz", hz)).ToList();

    public IReadOnlyList<Option<int>> BitrateOptions { get; } =
        VideoProcessingSettings.AacBitrates.Select(k => new Option<int>($"{k} kbps", k)).ToList();

    public IReadOnlyList<Option<OutputContainer>> ContainerOptions { get; } = new[]
    {
        new Option<OutputContainer>("MP4", OutputContainer.Mp4),
        new Option<OutputContainer>("MKV", OutputContainer.Mkv),
    };

    public IReadOnlyList<string> VideoPresetNames { get; } = VideoProcessingSettings.VideoPresets;
    public IReadOnlyList<string> EqPresetNames { get; } = EqualizerPresets.Names;
    public IReadOnlyList<string> Recipes { get; } = NoiseReductionSettings.Recipes;

    // ----- Файлы -----
    [ObservableProperty] private string _inputPath = "";
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private string _inputInfoText = "no file selected";

    // ----- Видео / контейнер -----
    [ObservableProperty] private bool _copyVideoStream = true;
    [ObservableProperty] private int _crf = 20;
    [ObservableProperty] private string _videoPreset = "medium";
    [ObservableProperty] private OutputContainer _container = OutputContainer.Mp4;
    [ObservableProperty] private int _aacBitrateKbps = 192;

    // ----- Обрезка спектра -----
    [ObservableProperty] private int _highPassHz;
    [ObservableProperty] private int _lowPassHz;

    // ----- Спектральное шумоподавление (afftdn) -----
    [ObservableProperty] private bool _spectralEnabled;
    [ObservableProperty] private double _noiseFloorDb = -40;
    [ObservableProperty] private double _reductionDb = 16;
    [ObservableProperty] private bool _trackNoise;
    [ObservableProperty] private bool _trackResidual;
    [ObservableProperty] private double _residualFloorDb = -60;
    [ObservableProperty] private NoiseType _noiseType = NoiseType.White;

    // ----- Нейросетевое шумоподавление речи (arnndn) -----
    [ObservableProperty] private bool _rnnEnabled;
    [ObservableProperty] private string _rnnModel = NoiseReductionSettings.DefaultRnnModel;
    [ObservableProperty] private double _rnnMix = 1;

    // ----- Гейт (agate) -----
    [ObservableProperty] private bool _gateEnabled;
    [ObservableProperty] private double _gateThresholdDb = -45;
    [ObservableProperty] private double _gateRatio = 2;
    [ObservableProperty] private double _gateRangeDb = -30;

    // ----- Эквалайзер и уровень -----
    [ObservableProperty] private bool _eqEnabled;
    [ObservableProperty] private string _eqPreset = "Flat";
    [ObservableProperty] private double _gainDb;
    [ObservableProperty] private LevelTarget _level = LevelTarget.Off;

    // ----- Предпросмотр -----
    [ObservableProperty] private int _startSeconds = 10;
    [ObservableProperty] private int _limitSeconds = 10;

    // ----- Состояние -----
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _autoAnalyze = true;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _statusText = "Pick a file, set the noise reduction, then press Process.";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _commandText = "";

    /// <summary>Предупреждение над кнопкой Process: пустая цепочка фильтров = копия файла.</summary>
    [ObservableProperty] private string _warningText = "";
    public bool HasWarning => WarningText.Length > 0;

    /// <summary>Задать предупреждение над кнопкой Process и сразу обновить его видимость.</summary>
    private void SetWarning(string text)
    {
        WarningText = text;
        OnPropertyChanged(nameof(HasWarning));
    }
    [ObservableProperty] private string _analysisText = "";
    [ObservableProperty] private string _lastOutputPath = "";
    [ObservableProperty] private string _previewPath = "";

    public bool HasOutput => !string.IsNullOrEmpty(LastOutputPath) && File.Exists(LastOutputPath);
    public bool HasPreview => !string.IsNullOrEmpty(PreviewPath) && File.Exists(PreviewPath);
    public bool ShowReencodeOptions => !CopyVideoStream;
    public bool CanRun => !IsRunning;

    private static readonly HashSet<string> PersistedProperties = new()
    {
        nameof(CopyVideoStream), nameof(Crf), nameof(VideoPreset), nameof(Container), nameof(AacBitrateKbps),
        nameof(GainDb), nameof(Level), nameof(StartSeconds), nameof(LimitSeconds),
        nameof(HighPassHz), nameof(LowPassHz),
        nameof(SpectralEnabled), nameof(NoiseFloorDb), nameof(ReductionDb), nameof(TrackNoise),
        nameof(TrackResidual), nameof(ResidualFloorDb), nameof(NoiseType),
        nameof(RnnEnabled), nameof(RnnModel), nameof(RnnMix),
        nameof(GateEnabled), nameof(GateThresholdDb), nameof(GateRatio), nameof(GateRangeDb),
        nameof(EqEnabled), nameof(EqPreset),
    };

    // ----- Загрузка / сохранение -----
    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var v = _settings.Current.VideoProcessing;
            v.Normalize();
            var n = v.NoiseReduction;

            InputPath = v.InputPath;
            OutputPath = v.OutputPath;
            CopyVideoStream = v.CopyVideoStream; Crf = v.Crf; VideoPreset = v.Preset;
            Container = v.Container; AacBitrateKbps = v.AacBitrateKbps;
            GainDb = v.GainDb; Level = v.Level; StartSeconds = v.StartSeconds; LimitSeconds = v.LimitSeconds > 0 ? v.LimitSeconds : 10;

            HighPassHz = n.HighPassHz; LowPassHz = n.LowPassHz;
            SpectralEnabled = n.SpectralEnabled; NoiseFloorDb = n.NoiseFloorDb; ReductionDb = n.ReductionDb;
            TrackNoise = n.TrackNoise; TrackResidual = n.TrackResidual; ResidualFloorDb = n.ResidualFloorDb; NoiseType = n.Type;
            RnnEnabled = n.RnnEnabled; RnnModel = n.RnnModel; RnnMix = n.RnnMix;
            GateEnabled = n.GateEnabled; GateThresholdDb = n.GateThresholdDb; GateRatio = n.GateRatio; GateRangeDb = n.GateRangeDb;

            EqEnabled = v.Equalizer.Enabled; EqPreset = v.Equalizer.Preset;
            for (int i = 0; i < Bands.Count; i++) Bands[i].GainDb = v.Equalizer.BandGainsDb[i];
        }
        finally { _loading = false; }
    }

    private void ApplyToSettings()
    {
        var v = _settings.Current.VideoProcessing;
        v.InputPath = InputPath;
        v.OutputPath = OutputPath;
        v.CopyVideoStream = CopyVideoStream; v.Crf = Crf; v.Preset = VideoPreset;
        v.Container = Container; v.AacBitrateKbps = AacBitrateKbps;
        v.GainDb = (float)GainDb; v.Level = Level; v.StartSeconds = StartSeconds; v.LimitSeconds = LimitSeconds;

        var n = v.NoiseReduction;
        n.HighPassHz = HighPassHz; n.LowPassHz = LowPassHz;
        n.SpectralEnabled = SpectralEnabled; n.NoiseFloorDb = (float)NoiseFloorDb; n.ReductionDb = (float)ReductionDb;
        n.TrackNoise = TrackNoise; n.TrackResidual = TrackResidual; n.ResidualFloorDb = (float)ResidualFloorDb; n.Type = NoiseType;
        n.RnnEnabled = RnnEnabled; n.RnnModel = RnnModel; n.RnnMix = (float)RnnMix;
        n.GateEnabled = GateEnabled; n.GateThresholdDb = (float)GateThresholdDb; n.GateRatio = (float)GateRatio; n.GateRangeDb = (float)GateRangeDb;

        v.Equalizer.Enabled = EqEnabled; v.Equalizer.Preset = EqPreset;
        for (int i = 0; i < Bands.Count; i++) v.Equalizer.BandGainsDb[i] = Bands[i].GainDb;

        v.Normalize();
        _settings.Save();
    }

    private void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }

    /// <summary>Немедленно сохраняет настройки (перед прогоном и при закрытии окна).</summary>
    public void FlushSave() { _saveTimer.Stop(); ApplyToSettings(); }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == null) return;
        if (e.PropertyName == nameof(IsRunning)) OnPropertyChanged(nameof(CanRun));
        if (_loading) return;
        if (PersistedProperties.Contains(e.PropertyName)) { ScheduleSave(); RefreshCommandText(); }
    }

    // ----- Сборка настроек -----
    private VideoProcessingSettings BuildSettings(bool preview)
    {
        var v = _settings.Current.VideoProcessing;
        return new VideoProcessingSettings
        {
            InputPath = InputPath,
            OutputPath = preview ? PreviewOutputPath() : OutputPath,
            InputHasVideo = _hasVideo,
            InputHasAudio = _hasAudio,
            CopyVideoStream = CopyVideoStream,
            Crf = Crf,
            Preset = VideoPreset,
            Container = Container,
            AacBitrateKbps = AacBitrateKbps,
            GainDb = (float)GainDb,
            Level = Level,
            StartSeconds = preview ? StartSeconds : 0,
            LimitSeconds = preview ? Math.Max(1, LimitSeconds) : 0,
            NoiseReduction = new NoiseReductionSettings
            {
                HighPassHz = HighPassHz, LowPassHz = LowPassHz,
                SpectralEnabled = SpectralEnabled, NoiseFloorDb = (float)NoiseFloorDb, ReductionDb = (float)ReductionDb,
                TrackNoise = TrackNoise, TrackResidual = TrackResidual, ResidualFloorDb = (float)ResidualFloorDb, Type = NoiseType,
                RnnEnabled = RnnEnabled, RnnModel = RnnModel, RnnMix = (float)RnnMix,
                GateEnabled = GateEnabled, GateThresholdDb = (float)GateThresholdDb, GateRatio = (float)GateRatio, GateRangeDb = (float)GateRangeDb
            },
            Equalizer = v.Equalizer.Clone()
        };
    }

    private string PreviewOutputPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ScreenRecorder");
        var name = Path.GetFileNameWithoutExtension(InputPath);
        if (string.IsNullOrWhiteSpace(name)) name = "preview";
        var ext = Container == OutputContainer.Mkv ? ".mkv" : ".mp4";
        return Path.Combine(dir, $"preview_{name}_{DateTime.Now:HHmmss}{ext}");
    }

    private void RefreshCommandText()
    {
        try
        {
            var s = BuildSettings(false);
            s.OutputPath = string.IsNullOrWhiteSpace(OutputPath)
                ? (Container == OutputContainer.Mkv ? "<output>.mkv" : "<output>.mp4")
                : OutputPath;
            CommandText = s.BuildCommandLine();
            SetWarning(string.IsNullOrWhiteSpace(InputPath)
                ? "Входной файл не выбран — укажите запись, которую нужно обработать."
                : !File.Exists(InputPath)
                    ? $"Входной файл не найден: {InputPath} — файл удалили или перенесли. Выберите файл заново."
                    : s.BuildFilterChain().Length == 0
                        ? "Ни один блок обработки не включён: на выходе будет почти копия входного файла, "
                          + "разницы на слух не будет. Возьмите готовый рецепт выше или включите шумоподавление."
                        : "");
        }
        catch (Exception ex) { CommandText = "cannot build command: " + ex.Message; }
        OnPropertyChanged(nameof(HasWarning));
    }

    // ----- Файлы -----
    partial void OnInputPathChanged(string value)
    {
        if (!_loading)
        {
            _suggestingOutput = true;
            try { if (!string.IsNullOrWhiteSpace(value)) OutputPath = SuggestOutput(value); }
            finally { _suggestingOutput = false; }
            _ = RefreshProbeAsync();
        }
    }

    partial void OnOutputPathChanged(string value)
    {
        if (_loading || _suggestingOutput) return;
        ScheduleSave();
        RefreshCommandText();
    }

    partial void OnContainerChanged(OutputContainer value)
    {
        if (!_loading && !string.IsNullOrWhiteSpace(OutputPath))
            OutputPath = VideoProcessingSettings.FixExtension(OutputPath, value);
    }

    partial void OnLastOutputPathChanged(string value) => OnPropertyChanged(nameof(HasOutput));
    partial void OnPreviewPathChanged(string value) => OnPropertyChanged(nameof(HasPreview));
    partial void OnCopyVideoStreamChanged(bool value) => OnPropertyChanged(nameof(ShowReencodeOptions));

    private string SuggestOutput(string input)
    {
        try
        {
            var dir = Path.GetDirectoryName(input) ?? "";
            var name = Path.GetFileNameWithoutExtension(input);
            var ext = Container == OutputContainer.Mkv ? ".mkv" : ".mp4";
            return Path.Combine(dir, name + "_processed" + ext);
        }
        catch { return ""; }
    }

    private string? FindLatestRecording()
    {
        try
        {
            var folder = _settings.Current.OutputFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, "*.mp4")
                .Concat(Directory.EnumerateFiles(folder, "*.mkv"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    [RelayCommand]
    private void BrowseInput()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a video or audio file to process",
            Filter = "Video/audio|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.m4a;*.mp3;*.wav;*.flac;*.aac;*.ogg|All files|*.*"
        };
        if (dialog.ShowDialog() == true) InputPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Where to save the processed file",
            Filter = Container == OutputContainer.Mkv ? "Matroska|*.mkv|MP4|*.mp4" : "MP4|*.mp4|Matroska|*.mkv",
            FileName = string.IsNullOrWhiteSpace(OutputPath) ? "processed.mp4" : Path.GetFileName(OutputPath),
            InitialDirectory = string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetDirectoryName(OutputPath)
        };
        if (dialog.ShowDialog() == true) OutputPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick an rnnoise model (.rnnn)",
            Filter = "rnnoise model|*.rnnn|All files|*.*"
        };
        if (dialog.ShowDialog() == true) RnnModel = dialog.FileName;
    }

    [RelayCommand]
    private void UseLatestRecording()
    {
        var latest = FindLatestRecording();
        if (latest == null) { StatusText = "No recordings found in the output folder."; return; }
        InputPath = latest;
    }

    [RelayCommand]
    private async Task RefreshProbeAsync()
    {
        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
        {
            InputInfoText = "file not found";
            _hasVideo = true; _hasAudio = true;
            return;
        }
        InputInfoText = "reading...";
        try
        {
            var info = await _processor.ProbeAsync(InputPath);
            _hasVideo = info.IsValid ? info.HasVideo : true;
            _hasAudio = info.IsValid ? info.HasAudio : true;
            InputInfoText = info.ToText();
        }
        catch (Exception ex) { InputInfoText = "probe failed: " + ex.Message; }
    }

    /// <summary>Измеряет уровень шума в файле и подставляет его в «Noise floor» спектрального режима.</summary>
    [RelayCommand]
    private async Task MeasureNoiseFloorAsync()
    {
        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
        {
            StatusText = "Pick an existing input file first.";
            return;
        }
        IsRunning = true;
        StatusText = "Measuring noise floor...";
        try
        {
            var a = await _analysis.AnalyzeFileAsync(InputPath);
            NoiseFloorDb = Math.Round(a.NoiseFloorDb, 1);
            SpectralEnabled = true;
            StatusText = $"Noise floor measured: {a.NoiseFloorDb:0.0} dBFS (RMS {a.RmsDb:0.0}, peak {a.PeakDb:0.0}). " +
                         "Spectral mode switched on with this floor.";
        }
        catch (Exception ex) { StatusText = "measure failed: " + ex.Message; }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private void ApplyRecipe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _loading = true;
        try
        {
            var n = NoiseReductionSettings.Recipe(name);
            HighPassHz = n.HighPassHz; LowPassHz = n.LowPassHz;
            SpectralEnabled = n.SpectralEnabled; NoiseFloorDb = n.NoiseFloorDb; ReductionDb = n.ReductionDb;
            TrackNoise = n.TrackNoise; TrackResidual = n.TrackResidual; ResidualFloorDb = n.ResidualFloorDb;
            RnnEnabled = n.RnnEnabled; RnnModel = n.RnnModel; RnnMix = n.RnnMix;
            GateEnabled = n.GateEnabled; GateThresholdDb = n.GateThresholdDb; GateRatio = n.GateRatio; GateRangeDb = n.GateRangeDb;
        }
        finally { _loading = false; }
        ScheduleSave();
        RefreshCommandText();
        StatusText = "Recipe applied: " + name;
    }

    // ----- Прогон -----
    [RelayCommand]
    private async Task ProcessAsync() => await RunAsync(false);

    [RelayCommand]
    private async Task PreviewAsync() => await RunAsync(true);

    private async Task RunAsync(bool preview)
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
        {
            StatusText = "Pick an existing input file first.";
            SetWarning(string.IsNullOrWhiteSpace(InputPath)
                ? "Входной файл не выбран — укажите запись, которую нужно обработать."
                : $"Входной файл не найден: {InputPath} — файл удалили или перенесли. Выберите файл заново.");
            _logger.Log($"Video processing window: отказано — входного файла нет: {InputPath}");
            return;
        }
        if (!preview && string.IsNullOrWhiteSpace(OutputPath))
        {
            StatusText = "Set the output file first.";
            SetWarning("Не задан выходной файл — нажмите «…» рядом с Output и укажите, куда писать результат.");
            _logger.Log("Video processing window: отказано — не задан выходной файл");
            return;
        }
        var output = preview ? PreviewOutputPath() : OutputPath;
        if (!preview && File.Exists(output))
        {
            var answer = MessageBox.Show($"Overwrite?\n{output}", "Process video", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText = "Cancelled: output file already exists — pick another name.";
                _logger.Log($"Video processing window: отказано — выходной файл {output} уже есть, перезапись отклонена");
                return;
            }
            _logger.Log($"Video processing window: перезаписываю существующий {output}");
        }

        FlushSave();
        var settings = BuildSettings(preview);
        settings.OutputPath = output;

        IsRunning = true;
        ProgressPercent = 0;
        ProgressText = "";
        ResultText = "";
        LogText = "";
        AnalysisText = "";
        StatusText = preview ? "Rendering preview..." : "Processing...";
        _logger.Log($"Video processing window: {(preview ? "preview" : "full run")} — {settings.Describe()}");
        _logger.Log("Video processing window: " + settings.BuildCommandLine());

        var progress = new Progress<VideoProcessingProgress>(p =>
        {
            ProgressPercent = p.Percent;
            ProgressText = $"{p.Position:mm\\:ss\\.ff} processed, {p.Elapsed.TotalSeconds:0.0} s elapsed ({p.Phase})";
        });

        _cts = new CancellationTokenSource();
        try
        {
            var result = await _processor.RunAsync(settings, progress, _cts.Token);
            if (result.Success)
            {
                if (preview) { PreviewPath = result.OutputPath; StatusText = "Preview ready — listen to it before the full run."; }
                else { LastOutputPath = result.OutputPath; StatusText = "Done."; }
                ResultText = result.ToText() + Environment.NewLine + "filters: " + result.FilterChain;
                if (AutoAnalyze) await AnalyzePairAsync(settings.InputPath, result.OutputPath);
            }
            else
            {
                StatusText = "Failed.";
                ResultText = result.ToText();
            }
            LogText = result.LogTail;
        }
        catch (Exception ex)
        {
            StatusText = "Failed: " + ex.Message;
            ResultText = ex.ToString();
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            ProgressPercent = 0;
        }

        RefreshCommandText();   // предупреждение над кнопкой пересчитывается после прогона
    }

    [RelayCommand]
    private void Cancel()
    {
        try { _cts?.Cancel(); _processor.Cancel(); StatusText = "Cancelling..."; }
        catch (Exception ex) { StatusText = "Cancel failed: " + ex.Message; }
    }

    /// <summary>Сравнивает исходник и результат по уровню и шуму (ffmpeg + AudioAnalyzer).</summary>
    [RelayCommand]
    private async Task AnalyzeResultAsync()
    {
        var target = HasOutput ? LastOutputPath : (HasPreview ? PreviewPath : "");
        if (string.IsNullOrEmpty(target)) { AnalysisText = "Nothing to analyse yet — process the file first."; return; }
        await AnalyzePairAsync(InputPath, target);
    }

    private async Task AnalyzePairAsync(string input, string output)
    {
        if (!File.Exists(input) || !File.Exists(output)) return;
        try
        {
            AnalysisText = "analysing input and output...";
            var before = await _analysis.AnalyzeFileAsync(input);
            var after = await _analysis.AnalyzeFileAsync(output);
            AnalysisText =
                $"noise floor  {before.NoiseFloorDb,7:F1} -> {after.NoiseFloorDb,7:F1} dBFS ({after.NoiseFloorDb - before.NoiseFloorDb:+0.0;-0.0} dB)" + Environment.NewLine +
                $"RMS          {before.RmsDb,7:F1} -> {after.RmsDb,7:F1} dBFS ({after.RmsDb - before.RmsDb:+0.0;-0.0} dB)" + Environment.NewLine +
                $"peak         {before.PeakDb,7:F1} -> {after.PeakDb,7:F1} dBFS" + Environment.NewLine +
                $"clipped      {before.ClippedSamples,7} -> {after.ClippedSamples,7}";
        }
        catch (Exception ex) { AnalysisText = "analysis failed: " + ex.Message; }
    }

    [RelayCommand] private void PlayPreview() => OpenFile(PreviewPath);
    [RelayCommand] private void PlayOutput() => OpenFile(LastOutputPath);

    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            var path = HasOutput ? LastOutputPath : (HasPreview ? PreviewPath : OutputPath);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
        }
        catch (Exception ex) { StatusText = "Cannot open folder: " + ex.Message; }
    }

    [RelayCommand]
    private void CopyCommand()
    {
        try { Clipboard.SetText(CommandText); StatusText = "ffmpeg command copied to clipboard."; }
        catch (Exception ex) { StatusText = "Copy failed: " + ex.Message; }
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        if (MessageBox.Show("Reset video processing settings to defaults?", "Process video",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _settings.Current.VideoProcessing = new VideoProcessingSettings
        {
            NoiseReduction = NoiseReductionSettings.CreateDefault()
        };
        _settings.Save();
        LoadFromSettings();
        RefreshCommandText();
        StatusText = "Defaults restored.";
    }

    // ----- Эквалайзер -----
    private void OnBandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || _applyingPreset) return;
        if (e.PropertyName == nameof(EqBandViewModel.GainDb))
        {
            EqPreset = EqualizerPresets.Custom;
            ScheduleSave();
            RefreshCommandText();
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

    private void OpenFile(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else StatusText = "File not found: " + path;
        }
        catch (Exception ex) { StatusText = "Cannot open file: " + ex.Message; }
    }

    /// <summary>Вызывается при закрытии окна.</summary>
    public void Cleanup()
    {
        FlushSave();
        if (IsRunning) _processor.Cancel();
    }
}
