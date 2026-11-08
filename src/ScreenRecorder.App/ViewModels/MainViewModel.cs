using Forms = System.Windows.Forms;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenRecorder.Core.Enums;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using ScreenRecorder.App.Services;
using ScreenRecorder.App.Views;
using ScreenRecorder.Infrastructure.Capture;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;

namespace ScreenRecorder.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IRecordingSession _session;
    private readonly ICaptureSourceProvider _sourceProvider;
    private readonly ILoggerService _logger;
    private readonly SettingsService _settings;
    private string? _lastFilePath;
    private RecordingOverlayWindow? _overlay;
    private RegionSelectionWindow? _regionWindow;

    public ObservableCollection<RecordingFile> SavedFiles { get; } = new();
    public ObservableCollection<AudioDevice> AudioDevices { get; } = new();

    [ObservableProperty] private RecordingState _state;
    [ObservableProperty] private string _timerDisplay = "00:00:00";
    [ObservableProperty] private CaptureMode _selectedCaptureMode = CaptureMode.Display;
    [ObservableProperty] private CaptureSource? _selectedSource;
    [ObservableProperty] private AudioDevice? _selectedAudioDevice;
    [ObservableProperty] private bool _showCursor = true;
    [ObservableProperty] private bool _showLogo = false;
    [ObservableProperty] private string _logoText = "RECORDER";
    [ObservableProperty] private float _logoOpacity = 0.5f;
    [ObservableProperty] private float _logoScale = 1.0f;
    [ObservableProperty] private string? _logoPath;
    [ObservableProperty] private LogoPosition _selectedLogoPosition = LogoPosition.TopRight;
    [ObservableProperty] private string _currentView = "Home"; // Home, General, Video, Image, About

    [RelayCommand] private void SetView(string view) => CurrentView = view;

    [RelayCommand]
    private void SelectLogoFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dialog.ShowDialog() == true) LogoPath = dialog.FileName;
    }

    partial void OnSelectedAudioDeviceChanged(AudioDevice? value)
    {
        if (value != null)
        {
            _settings.Current.LastAudioDevice = value.Name;
            _settings.Save();
        }
    }

    public ObservableCollection<CaptureSource> AvailableSources { get; } = new();

    public MainViewModel(IRecordingSession session, ICaptureSourceProvider sourceProvider, ILoggerService logger, SettingsService settings)
    {
        _session = session;
        _sourceProvider = sourceProvider;
        _logger = logger;
        _settings = settings;

        // Загружаем настройки логотипа
        ShowLogo = _settings.Current.ShowLogo;
        LogoText = _settings.Current.LogoText;
        LogoOpacity = _settings.Current.LogoOpacity;
        LogoScale = _settings.Current.LogoScale;
        LogoPath = _settings.Current.LogoPath;
        SelectedLogoPosition = _settings.Current.LogoPosition;
        
        _session.StateChanged += s => { 
            State = s; 
            _logger.Log($"Session State: {s}"); 
            if (s == RecordingState.Idle) Application.Current.Dispatcher.Invoke(() => { _overlay?.Close(); _overlay = null; });
        };

        // Исправляем обновление счетчика: принудительно в UI потоке
        _session.Tick += t => {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => {
                TimerDisplay = t.ToString(@"hh\:mm\:ss");
            }));
        };
        
        _ = RefreshSourcesAsync();
        RefreshFiles();
        LoadAudioDevices();

        // Восстанавливаем последнее устройство
        if (!string.IsNullOrEmpty(_settings.Current.LastAudioDevice))
        {
            var device = AudioDevices.FirstOrDefault(d => d.Name == _settings.Current.LastAudioDevice);
            if (device != null) SelectedAudioDevice = device;
        }
    }

    [RelayCommand]
    public void SaveSettings()
    {
        _settings.Current.ShowLogo = ShowLogo;
        _settings.Current.LogoText = LogoText;
        _settings.Current.LogoOpacity = LogoOpacity;
        _settings.Current.LogoScale = LogoScale;
        _settings.Current.LogoPath = LogoPath;
        _settings.Current.LogoPosition = SelectedLogoPosition;
        _settings.Save();
    }

    partial void OnShowLogoChanged(bool value) => SaveSettings();
    partial void OnLogoTextChanged(string value) => SaveSettings();
    partial void OnLogoOpacityChanged(float value) => SaveSettings();
    partial void OnLogoScaleChanged(float value) => SaveSettings();
    partial void OnLogoPathChanged(string? value) => SaveSettings();
    partial void OnSelectedLogoPositionChanged(LogoPosition value) => SaveSettings();

    partial void OnSelectedCaptureModeChanged(CaptureMode value)
    {
        Application.Current.Dispatcher.Invoke(() => {
            if (value == CaptureMode.Region) {
                if (_regionWindow == null) _regionWindow = new RegionSelectionWindow();
                _regionWindow.Show();
            } else { _regionWindow?.Hide(); }
        });
    }

    private void LoadAudioDevices()
    {
        if (_sourceProvider is WindowsGraphicsCaptureSourceProvider provider)
        {
            var devices = provider.GetAudioDevices();
            AudioDevices.Clear();
            AudioDevices.Add(new AudioDevice("None", "No Audio", false));
            foreach (var d in devices) AudioDevices.Add(d);
            SelectedAudioDevice = AudioDevices.FirstOrDefault();
        }
    }

    
    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        Rect area;
        if (SelectedCaptureMode == CaptureMode.Region && _regionWindow != null)
        {
            // Переводим WPF координаты в пиксели с учетом масштабирования
            var source = PresentationSource.FromVisual(_regionWindow);
            double dpiX = 1.0, dpiY = 1.0;
            if (source?.CompositionTarget != null) {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }
            area = new Rect(_regionWindow.Left * dpiX, _regionWindow.Top * dpiY, _regionWindow.Width * dpiX, _regionWindow.Height * dpiY);
            _regionWindow.Hide();
            _overlay = new RecordingOverlayWindow(new Rect(_regionWindow.Left, _regionWindow.Top, _regionWindow.Width, _regionWindow.Height));
            _overlay.Show();
        }
        else
        {
            // Берем первичный монитор напрямую через WinForms (реальные пиксели)
            var screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
            area = new Rect(0, 0, screen.Bounds.Width, screen.Bounds.Height);
        }

        var capSource = new CaptureSource("Target", "Target", SelectedCaptureMode, (int)area.Width, (int)area.Height, (int)area.X, (int)area.Y);
        _lastFilePath = Path.Combine(_settings.Current.OutputFolder, $"record_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        if (!Directory.Exists(_settings.Current.OutputFolder)) Directory.CreateDirectory(_settings.Current.OutputFolder);

        string? audioId = SelectedAudioDevice?.Id;
 
        var options = new RecordingOptions(capSource, AudioSourceMode.System, audioId, null, _lastFilePath, 30, 8000, ShowCursor, ShowLogo, LogoPath, LogoText, LogoOpacity, LogoScale, SelectedLogoPosition);
        _logger.Log($"START: {capSource.Width}x{capSource.Height} at {capSource.X},{capSource.Y} Audio ID: {audioId}. Cursor: {ShowCursor}. Logo: {ShowLogo}");
        await _session.StartAsync(options, CancellationToken.None);
    }


    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        await _session.StopAsync(CancellationToken.None);
        if (SelectedCaptureMode == CaptureMode.Region) _regionWindow?.Show();
        RefreshFiles();
    }

    [RelayCommand] public void RefreshFiles() {
        SavedFiles.Clear();
        if (!Directory.Exists(_settings.Current.OutputFolder)) return;
        var files = Directory.GetFiles(_settings.Current.OutputFolder, "*.mp4")
            .Select(f => new RecordingFile(f, new FileInfo(f).Name, new FileInfo(f).CreationTime, $"{(new FileInfo(f).Length / 1024.0 / 1024.0):F2} MB"))
            .OrderByDescending(f => f.CreatedAt);
        foreach (var f in files) SavedFiles.Add(f);
    }

    [RelayCommand] private void PlayFile(RecordingFile file) { if (File.Exists(file.Path)) Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true }); }
    [RelayCommand] private void DeleteFile(RecordingFile file) { if (File.Exists(file.Path)) { try { File.Delete(file.Path); SavedFiles.Remove(file); } catch { } } }
    [RelayCommand] private void OpenRecordingsWindow() { RefreshFiles(); new RecordingsWindow(this).Show(); }
    [RelayCommand] private void PlayLastRecording() { if (!string.IsNullOrEmpty(_lastFilePath) && File.Exists(_lastFilePath)) Process.Start(new ProcessStartInfo(_lastFilePath) { UseShellExecute = true }); }
    
    [RelayCommand]
    private void RenameFile(RecordingFile file)
    {
        var oldPath = file.Path;
        var directory = Path.GetDirectoryName(oldPath);
        var extension = Path.GetExtension(oldPath);
        
        // В .NET 8/Windows лучше использовать Microsoft.VisualBasic.Interaction.InputBox напрямую без инстанса
        string newName = Microsoft.VisualBasic.Interaction.InputBox("Enter new name:", "Rename File", Path.GetFileNameWithoutExtension(oldPath));
        
        if (!string.IsNullOrWhiteSpace(newName))
        {
            var newPath = Path.Combine(directory!, newName + extension);
            if (!File.Exists(newPath))
            {
                try {
                    File.Move(oldPath, newPath);
                    RefreshFiles();
                } catch (Exception ex) {
                    MessageBox.Show($"Error renaming file: {ex.Message}");
                }
            }
            else
            {
                MessageBox.Show("File already exists.");
            }
        }
    }

    [RelayCommand] private void OpenSettings() {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog() == true) { _settings.Current.OutputFolder = dialog.FolderName; _settings.Save(); RefreshFiles(); }
    }
    [RelayCommand] private async Task RefreshSourcesAsync() {
        var sources = await _sourceProvider.GetSourcesAsync(SelectedCaptureMode, CancellationToken.None);
        AvailableSources.Clear();
        foreach (var s in sources) AvailableSources.Add(s);
        SelectedSource = AvailableSources.FirstOrDefault();
    }

    [RelayCommand] private void RefreshAudioSources() => LoadAudioDevices();

    private SyncTestWindow? _syncTestWindow;

    [RelayCommand]
    private void OpenSyncTest() 
    { 
        if (_syncTestWindow == null || !Application.Current.Windows.OfType<SyncTestWindow>().Any())
        {
            _syncTestWindow = new SyncTestWindow();
            _syncTestWindow.Closed += (s, e) => _syncTestWindow = null;
            _syncTestWindow.Show();
        }
        else
        {
            _syncTestWindow.Activate();
        }
    }
}
