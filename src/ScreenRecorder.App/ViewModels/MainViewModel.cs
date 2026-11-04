using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenRecorder.Core.Enums;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using ScreenRecorder.App.Services;
using ScreenRecorder.App.Views;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ScreenRecorder.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IRecordingSession _session;
    private readonly ICaptureSourceProvider _sourceProvider;
    private readonly ILoggerService _logger;
    private readonly SettingsService _settings;
    private string? _lastFilePath;

    [ObservableProperty] private RecordingState _state;
    [ObservableProperty] private string _timerDisplay = "00:00:00";
    [ObservableProperty] private CaptureMode _selectedCaptureMode = CaptureMode.Display;
    public ObservableCollection<CaptureSource> AvailableSources { get; } = new();
    [ObservableProperty] private CaptureSource? _selectedSource;

    public MainViewModel(IRecordingSession session, ICaptureSourceProvider sourceProvider, ILoggerService logger, SettingsService settings)
    {
        _session = session;
        _sourceProvider = sourceProvider;
        _logger = logger;
        _settings = settings;
        _session.StateChanged += s => { State = s; _logger.Log($"Session State: {s}"); };
        _session.Tick += t => TimerDisplay = t.ToString(@"hh\:mm\:ss");
        _ = RefreshSourcesAsync();
    }

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (SelectedCaptureMode == CaptureMode.Region)
        {
            var selector = new RegionSelectionWindow();
            if (selector.ShowDialog() == true)
            {
                var r = selector.SelectedRegion;
                SelectedSource = new CaptureSource("Region", "Selected Area", CaptureMode.Region, (int)r.Width, (int)r.Height);
                _logger.Log($"Area selected: {r.Width}x{r.Height} at {r.X},{r.Y}");
            }
            else return;
        }

        if (SelectedSource == null) { _logger.Log("Error: No source selected."); return; }
        
        if (!Directory.Exists(_settings.Current.OutputFolder)) Directory.CreateDirectory(_settings.Current.OutputFolder);
        _lastFilePath = Path.Combine(_settings.Current.OutputFolder, $"record_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        var options = new RecordingOptions(SelectedSource, AudioSourceMode.None, null, null, _lastFilePath, 30, 8000);
        _logger.Log($"Starting recording to: {_lastFilePath}");
        await _session.StartAsync(options, CancellationToken.None);
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        _logger.Log("Stopping recording...");
        await _session.StopAsync(CancellationToken.None);
        _logger.Log("Recording stopped and saved.");
    }

    [RelayCommand]
    private void PlayLastRecording()
    {
        if (!string.IsNullOrEmpty(_lastFilePath) && File.Exists(_lastFilePath))
        {
            _logger.Log($"Playing: {_lastFilePath}");
            Process.Start(new ProcessStartInfo(_lastFilePath) { UseShellExecute = true });
        }
        else _logger.Log("No recording to play.");
    }

    [RelayCommand]
    private async Task RefreshSourcesAsync()
    {
        var sources = await _sourceProvider.GetSourcesAsync(SelectedCaptureMode, CancellationToken.None);
        AvailableSources.Clear();
        foreach (var s in sources) AvailableSources.Add(s);
        SelectedSource = AvailableSources.FirstOrDefault();
        _logger.Log($"Sources refreshed for mode {SelectedCaptureMode}. Found {sources.Count}.");
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _settings.Current.OutputFolder };
        if (dialog.ShowDialog() == true)
        {
            _settings.Current.OutputFolder = dialog.FolderName;
            _settings.Save();
            _logger.Log($"Output folder changed to: {dialog.FolderName}");
        }
    }
}