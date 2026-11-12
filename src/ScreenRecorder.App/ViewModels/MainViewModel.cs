using Forms = System.Windows.Forms;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenRecorder.Core.Enums;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using ScreenRecorder.App.Services;
using ScreenRecorder.App.Views;
using ScreenRecorder.Infrastructure.Capture;
using ScreenRecorder.Infrastructure.PhoneAudio;
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
    private readonly Func<AudioSettingsViewModel> _audioVmFactory;
    private AudioSettingsWindow? _audioWindow;
    private readonly Func<VideoProcessingViewModel> _videoVmFactory;
    private VideoProcessingWindow? _videoWindow;
    private readonly ProfileService _profiles;
    private readonly PhoneAudioService _phoneAudio;
    private VideoEncodingSettings _activeVideo = new();
    private AudioCaptureSettings? _activeAudio;

    public ObservableCollection<RecordingFile> SavedFiles { get; } = new();
    public ObservableCollection<AudioDevice> AudioDevices { get; } = new();

    // --- Внешние профили кодирования (configs\*.json) ---
    public ObservableCollection<ProfileEntry> Profiles { get; } = new();
    public ObservableCollection<int> FpsChoices { get; } = new(VideoEncodingSettings.AllowedFps);
    public ObservableCollection<string> EncoderChoices { get; } = new(VideoEncodingSettings.Encoders);
    public ObservableCollection<string> PresetChoices { get; } = new(VideoEncodingSettings.Presets);
    public ObservableCollection<string> PixelFormatChoices { get; } = new(VideoEncodingSettings.PixelFormats);
    public ObservableCollection<VideoRateControl> RateControlChoices { get; } = new(new[] { VideoRateControl.Crf, VideoRateControl.Bitrate });

    [ObservableProperty] private ProfileEntry? _selectedProfile;
    [ObservableProperty] private string _profileStatus = "Профиль не выбран.";
    [ObservableProperty] private string _profileArgsPreview = "";
    [ObservableProperty] private string _profileWarnings = "";
    [ObservableProperty] private int _profileFps = 30;
    [ObservableProperty] private string _profileEncoder = "libx264";
    [ObservableProperty] private string _profilePreset = "veryfast";
    [ObservableProperty] private VideoRateControl _profileRateControl = VideoRateControl.Crf;
    [ObservableProperty] private int _profileCrf = 20;
    [ObservableProperty] private int _profileBitrateKbps = 12000;
    [ObservableProperty] private string _profilePixelFormat = "yuv420p";
    [ObservableProperty] private bool _profileApplyAudio = true;
    [ObservableProperty] private float _profileGainDb;
    [ObservableProperty] private int _profileAacKbps = 192;

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
    [ObservableProperty] private string _outputFolder = "";

    // --- Аудио с телефона (PhoneAudioRecorder) ---
    [ObservableProperty] private bool _phoneAudioEnabled;
    [ObservableProperty] private string _phoneAudioHost = "";
    [ObservableProperty] private string _phoneAudioStatus = "Audio from phone: off";

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

    public MainViewModel(IRecordingSession session, ICaptureSourceProvider sourceProvider, ILoggerService logger, SettingsService settings,
        Func<AudioSettingsViewModel> audioVmFactory, ProfileService profiles, PhoneAudioService phoneAudio,
        Func<VideoProcessingViewModel> videoVmFactory)
    {
        _session = session;
        _sourceProvider = sourceProvider;
        _logger = logger;
        _settings = settings;
        _audioVmFactory = audioVmFactory;
        _profiles = profiles;
        _phoneAudio = phoneAudio;
        _videoVmFactory = videoVmFactory;
        OutputFolder = _settings.Current.OutputFolder;

        // Настройки аудио с телефона
        PhoneAudioEnabled = _settings.Current.PhoneAudio.Enabled;
        PhoneAudioHost = _settings.Current.PhoneAudio.Host;
        UpdatePhoneAudioStatus();

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
            StartRecordingCommand.NotifyCanExecuteChanged();
            StopRecordingCommand.NotifyCanExecuteChanged();
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
        ReloadProfiles();

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

    // ----- Аудио с телефона: сохранение настроек и статус -----
    partial void OnPhoneAudioEnabledChanged(bool value)
    {
        _settings.Current.PhoneAudio.Enabled = value;
        _settings.Save();
        UpdatePhoneAudioStatus();
    }

    partial void OnPhoneAudioHostChanged(string value)
    {
        _settings.Current.PhoneAudio.Host = value ?? "";
        _settings.Save();
    }

    private void UpdatePhoneAudioStatus()
    {
        PhoneAudioStatus = PhoneAudioEnabled
            ? (string.IsNullOrWhiteSpace(PhoneAudioHost)
                ? "Audio from phone: on — укажите IP телефона"
                : $"Audio from phone: on ({PhoneAudioHost})")
            : "Audio from phone: off";
    }


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
            // Запоминаем выбор до Clear(): иначе сброс на "No Audio" затирал LastAudioDevice в настройках.
            string? keep = SelectedAudioDevice?.Name ?? _settings.Current.LastAudioDevice;
            var devices = provider.GetAudioDevices();
            AudioDevices.Clear();
            AudioDevices.Add(new AudioDevice("None", "No Audio", false));
            foreach (var d in devices) AudioDevices.Add(d);
            SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Name == keep) ?? AudioDevices.FirstOrDefault();
        }
    }

    
    private bool CanStartRecording() => State == RecordingState.Idle;
    private bool CanStopRecording() => State == RecordingState.Recording;

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private async Task StartRecordingAsync()
    {
        if (State != RecordingState.Idle)
        {
            // Двойной клик по кнопке раньше поднимал вторую запись в тот же файл.
            _logger.Log($"START ignored: уже идёт запись (state={State})");
            return;
        }

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

        var audio = _activeAudio?.Clone() ?? _settings.Current.Audio.Clone();
        var options = new RecordingOptions(capSource, AudioSourceMode.System, audioId, null, _lastFilePath,
            _activeVideo.Fps, _activeVideo.BitrateKbps, ShowCursor, ShowLogo, LogoPath, LogoText, LogoOpacity, LogoScale, SelectedLogoPosition,
            audio, _activeVideo);
        _logger.Log($"START: {capSource.Width}x{capSource.Height} at {capSource.X},{capSource.Y} Audio ID: {audioId}. Cursor: {ShowCursor}. Logo: {ShowLogo}");
        _logger.Log($"Profile: {SelectedProfile?.FileName ?? "(нет)"} — {_activeVideo.Describe()}");

        // Аудио с телефона: сначала дожидаемся готовности телефона и только потом запускаем видео.
        if (_settings.Current.PhoneAudio.Enabled)
        {
            var baseName = Path.GetFileNameWithoutExtension(_lastFilePath);
            PhoneAudioStatus = $"Audio from phone: waiting for {PhoneAudioHost}...";
            var phoneReady = await _phoneAudio.StartAsync(_settings.Current.PhoneAudio, baseName, _settings.Current.OutputFolder);
            PhoneAudioStatus = phoneReady
                ? $"Audio from phone: recording «{baseName}»"
                : "Audio from phone: недоступен — видео пишется без телефона";
        }

        await _session.StartAsync(options, CancellationToken.None);
    }


    [RelayCommand(CanExecute = nameof(CanStopRecording))]
    private async Task StopRecordingAsync()
    {
        if (State != RecordingState.Recording)
        {
            _logger.Log($"STOP ignored: запись не идёт (state={State})");
            return;
        }
        await _session.StopAsync(CancellationToken.None);

        // Аудио с телефона: получаем .mp3 с тем же базовым именем рядом с .mp4
        if (_phoneAudio.IsActive)
        {
            PhoneAudioStatus = "Audio from phone: receiving…";
            var mp3 = await _phoneAudio.StopAndReceiveAsync(_settings.Current.PhoneAudio, CancellationToken.None);
            PhoneAudioStatus = mp3 != null
                ? $"Audio from phone: saved {Path.GetFileName(mp3)}"
                : "Audio from phone: файл не получен (смотрите лог)";
        }

        if (SelectedCaptureMode == CaptureMode.Region) _regionWindow?.Show();
        RefreshFiles();
    }

    // ---------- Внешние профили кодирования: configs\*.json ----------

    /// <summary>Выбор профиля в списке сразу загружает его в редактор и применяет к записи.</summary>
    partial void OnSelectedProfileChanged(ProfileEntry? value)
    {
        if (value == null) return;
        LoadProfileIntoEditors(value.Profile);
        ApplyProfile();
    }

    [RelayCommand]
    private void ReloadProfiles()
    {
        var keep = SelectedProfile?.FileName ?? _settings.Current.LastProfileFile;
        Profiles.Clear();
        foreach (var p in _profiles.Load()) Profiles.Add(p);
        var pick = Profiles.FirstOrDefault(p => p.FileName == keep) ?? Profiles.FirstOrDefault();
        if (pick == null)
        {
            SelectedProfile = null;
            ProfileArgsPreview = "";
            ProfileWarnings = "";
            ProfileStatus = $"В {_profiles.Folder} нет ни одного *.json — нажмите «Открыть папку configs».";
            return;
        }
        if (ReferenceEquals(pick, SelectedProfile)) ApplyProfile();
        else SelectedProfile = pick;
    }

    private void LoadProfileIntoEditors(EncodingProfile p)
    {
        ProfileFps = p.Video.Fps;
        ProfileEncoder = p.Video.Encoder;
        ProfilePreset = p.Video.Preset;
        ProfileRateControl = p.Video.RateControl;
        ProfileCrf = p.Video.Crf;
        ProfileBitrateKbps = p.Video.BitrateKbps;
        ProfilePixelFormat = p.Video.PixelFormat;
        if (p.Audio != null)
        {
            ProfileGainDb = p.Audio.InputGainDb;
            ProfileAacKbps = p.Audio.AacBitrateKbps;
        }
        UpdateProfilePreview();
    }

    private VideoEncodingSettings BuildVideoFromEditors()
    {
        var src = SelectedProfile?.Profile.Video;
        return new VideoEncodingSettings
        {
            Fps = ProfileFps,
            Encoder = ProfileEncoder,
            Preset = ProfilePreset,
            RateControl = ProfileRateControl,
            Crf = ProfileCrf,
            BitrateKbps = ProfileBitrateKbps,
            PixelFormat = ProfilePixelFormat,
            KeyframeSeconds = src?.KeyframeSeconds ?? 2,
            Threads = src?.Threads ?? 0,
            Tune = src?.Tune,
            MaxRateKbps = src?.MaxRateKbps ?? 0,
            BufferSizeKbps = src?.BufferSizeKbps ?? 0,
            ExtraArgs = src?.ExtraArgs
        };
    }

    private AudioCaptureSettings BuildAudioFromEditors()
    {
        var a = SelectedProfile?.Profile.Audio?.Clone() ?? _settings.Current.Audio.Clone();
        a.InputGainDb = ProfileGainDb;
        a.AacBitrateKbps = ProfileAacKbps;
        a.OutputBitDepth = AudioBitDepth.Float32; // без промежуточного квантования перед AAC
        a.Dither = false;
        a.Normalize();
        return a;
    }

    private int PreviewWidth => SelectedSource is { Width: > 0 } s ? s.Width : 1920;
    private int PreviewHeight => SelectedSource is { Height: > 0 } s ? s.Height : 1080;

    private void UpdateProfilePreview()
    {
        var v = BuildVideoFromEditors();
        v.Normalize();
        ProfileArgsPreview = "ffmpeg " + v.DescribeArgs(PreviewWidth, PreviewHeight, "record.mp4");
        var warnings = v.Validate();
        ProfileWarnings = warnings.Count == 0 ? "" : "⚠ " + string.Join("   ⚠ ", warnings);
    }

    partial void OnProfileFpsChanged(int value) => UpdateProfilePreview();
    partial void OnProfileEncoderChanged(string value) => UpdateProfilePreview();
    partial void OnProfilePresetChanged(string value) => UpdateProfilePreview();
    partial void OnProfileRateControlChanged(VideoRateControl value) => UpdateProfilePreview();
    partial void OnProfileCrfChanged(int value) => UpdateProfilePreview();
    partial void OnProfileBitrateKbpsChanged(int value) => UpdateProfilePreview();
    partial void OnProfilePixelFormatChanged(string value) => UpdateProfilePreview();

    /// <summary>Применяет выбранный профиль (и правки в редакторе) к следующей записи.</summary>
    [RelayCommand]
    private void ApplyProfile()
    {
        if (SelectedProfile == null) { ProfileStatus = "Сначала выберите профиль."; return; }
        var video = BuildVideoFromEditors();
        video.Normalize();
        _activeVideo = video;
        _activeAudio = ProfileApplyAudio ? BuildAudioFromEditors() : _settings.Current.Audio.Clone();
        SelectedProfile.Profile.Video = video;
        if (ProfileApplyAudio && _activeAudio != null) SelectedProfile.Profile.Audio = _activeAudio.Clone();
        _settings.Current.LastProfileFile = SelectedProfile.FileName;
        _settings.Save();
        LoadProfileIntoEditors(SelectedProfile.Profile);
        ProfileStatus = ProfileApplyAudio
            ? $"Применён «{SelectedProfile.Name}»: {video.Describe()}; аудио: gain {ProfileGainDb:+0.#;-0.#;0} dB, AAC {ProfileAacKbps} kbps, float32"
            : $"Применён «{SelectedProfile.Name}»: {video.Describe()}; аудио из settings.json";
        _logger.Log($"PROFILE APPLIED: {SelectedProfile.FileName} -> {video.Describe()}");
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (SelectedProfile == null) { ProfileStatus = "Сначала выберите профиль."; return; }
        ApplyProfile();
        _profiles.Save(SelectedProfile);
        ProfileStatus = $"Сохранено в {SelectedProfile.FileName}: {_activeVideo.Describe()}";
    }

    /// <summary>Сохраняет текущий профиль отдельным файлом, чтобы не портить исходный вариант.</summary>
    [RelayCommand]
    private void SaveProfileAs()
    {
        var copy = (SelectedProfile?.Profile ?? new EncodingProfile()).Clone();
        copy.Video = BuildVideoFromEditors();
        copy.Audio = ProfileApplyAudio ? BuildAudioFromEditors() : copy.Audio;
        copy.Name = $"{copy.Name} copy";
        var suggested = $"my-{DateTime.Now:HHmmss}.json";
        for (int i = 1; i < 100 && _profiles.Exists(suggested); i++) suggested = $"my-{DateTime.Now:HHmmss}-{i}.json";
        var created = _profiles.SaveAs(suggested, copy);
        if (created == null) { ProfileStatus = $"{suggested} уже существует."; return; }
        Profiles.Add(created);
        SelectedProfile = created;
        ProfileStatus = $"Создан новый файл профиля: {created.FileName}";
    }

    [RelayCommand]
    private void OpenConfigsFolder()
    {
        try
        {
            Directory.CreateDirectory(_profiles.Folder);
            Process.Start(new ProcessStartInfo(_profiles.Folder) { UseShellExecute = true });
            ProfileStatus = _profiles.Folder;
        }
        catch (Exception ex) { ProfileStatus = $"Не удалось открыть папку: {ex.Message}"; }
    }

    [RelayCommand]
    private void CopyProfileArgs()
    {
        try
        {
            var v = BuildVideoFromEditors();
            Clipboard.SetText(v.DescribeArgs(PreviewWidth, PreviewHeight, "record.mp4"));
            ProfileStatus = "Аргументы ffmpeg скопированы в буфер — можно вставить в cmd.";
        }
        catch (Exception ex) { ProfileStatus = ex.Message; }
    }

    [RelayCommand] public void RefreshFiles() {
        SavedFiles.Clear();
        if (!Directory.Exists(_settings.Current.OutputFolder)) return;
        var root = _settings.Current.OutputFolder;
        var found = new System.Collections.Generic.List<string>();
        try { found.AddRange(Directory.GetFiles(root, "*.mp4")); } catch { }
        // Тестовые записи звука лежат в подпапке AudioTests.
        var tests = Path.Combine(root, "AudioTests");
        if (Directory.Exists(tests))
        {
            try { found.AddRange(Directory.GetFiles(tests).Where(f => new[] { ".wav", ".flac", ".mp3", ".m4a" }
                .Contains(Path.GetExtension(f).ToLowerInvariant()))); } catch { }
        }
        var files = found
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.CreationTime)
            .Select(fi => new RecordingFile(fi.FullName, Path.GetRelativePath(root, fi.FullName), fi.CreationTime,
                $"{(fi.Length / 1024.0 / 1024.0):F2} MB"));
        foreach (var f in files) SavedFiles.Add(f);
    }

    [RelayCommand] private void PlayFile(RecordingFile file) { if (File.Exists(file.Path)) Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true }); }
    [RelayCommand] private void DeleteFile(RecordingFile file) { if (File.Exists(file.Path)) { try { File.Delete(file.Path); SavedFiles.Remove(file); } catch { } } }
    [RelayCommand] private void OpenRecordingsWindow() { RefreshFiles(); new RecordingsWindow(this).Show(); }
    
    [RelayCommand]
    private void OpenHelp()
    {
        new HelpWindow().Show();
    }

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

    [RelayCommand] private void OpenSettings() => ChooseOutputFolder();

    // ----- Папка для записей по умолчанию -----
    [RelayCommand]
    private void ChooseOutputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Default folder for recordings",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dialog.ShowDialog() == true) TrySetOutputFolder(dialog.FolderName);
    }

    [RelayCommand]
    private void ResetOutputFolder() =>
        TrySetOutputFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder"));

    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputFolder);
            Process.Start(new ProcessStartInfo(OutputFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show($"Cannot open folder: {ex.Message}"); }
    }

    /// <summary>Проверяет, что в папку можно писать, и только после этого запоминает её.</summary>
    private bool TrySetOutputFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write_test_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot use this folder:\n{path}\n\n{ex.Message}", "Recordings folder");
            return false;
        }
        _settings.Current.OutputFolder = path;
        _settings.Save();
        OutputFolder = path;
        RefreshFiles();
        _logger.Log($"Recordings folder: {path}");
        return true;
    }

    [RelayCommand]
    private void OpenAudioSettings()
    {
        if (_audioWindow != null) { _audioWindow.Activate(); return; }
        var vm = _audioVmFactory();
        _audioWindow = new AudioSettingsWindow(vm) { Owner = Application.Current.MainWindow };
        _audioWindow.Closed += (s, e) =>
        {
            _audioWindow = null;
            // Устройство могли сменить в окне настроек: читаем его ДО перезагрузки списка,
            // потому что перезагрузка сама перезаписывает LastAudioDevice.
            var last = _settings.Current.LastAudioDevice;
            LoadAudioDevices();
            var dev = AudioDevices.FirstOrDefault(d => d.Name == last);
            if (dev != null) SelectedAudioDevice = dev;
        };
        _audioWindow.Show();
    }

    [RelayCommand] private async Task RefreshSourcesAsync() {
        var sources = await _sourceProvider.GetSourcesAsync(SelectedCaptureMode, CancellationToken.None);
        AvailableSources.Clear();
        foreach (var s in sources) AvailableSources.Add(s);
        SelectedSource = AvailableSources.FirstOrDefault();
    }

    [RelayCommand] private void RefreshAudioSources() => LoadAudioDevices();

    [RelayCommand]
    private void OpenVideoProcessing()
    {
        if (_videoWindow != null && Application.Current.Windows.OfType<VideoProcessingWindow>().Any())
        {
            _videoWindow.Activate();
            return;
        }
        var vm = _videoVmFactory();
        _videoWindow = new VideoProcessingWindow(vm) { Owner = Application.Current.MainWindow };
        _videoWindow.Closed += (s, e) => _videoWindow = null;
        _videoWindow.Show();
    }

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
