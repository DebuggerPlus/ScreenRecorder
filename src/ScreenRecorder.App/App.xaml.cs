using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScreenRecorder.App.ViewModels;
using ScreenRecorder.App.Services;
using ScreenRecorder.App.Views;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Infrastructure.Audio;
using ScreenRecorder.Infrastructure.Capture;
using ScreenRecorder.Infrastructure.Recording;
using ScreenRecorder.Infrastructure.Encoding;
using ScreenRecorder.Infrastructure.PhoneAudio;
using ScreenRecorder.Infrastructure.Processing;
using ScreenRecorder.Infrastructure.Services;
using System.Windows;
using System;
using System.Linq;

namespace ScreenRecorder.App;

public partial class App : Application
{
    private readonly ServiceProvider _serviceProvider;

    public App()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<ILoggerService, LoggerService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ProfileService>();
        services.AddSingleton<PhoneAudioService>();
        services.AddSingleton<IVideoCapture, WindowsGraphicsCapture>();
        services.AddSingleton<IAudioCapture, WasapiAudioCapture>();
        services.AddSingleton<FFmpegEncoder>();
        services.AddSingleton<IRecordingSession, RecordingSession>();
        services.AddSingleton<ICaptureSourceProvider, WindowsGraphicsCaptureSourceProvider>();
        services.AddSingleton<IAudioDeviceService, AudioDeviceService>();
        services.AddSingleton<IAudioTestService, AudioTestService>();
        services.AddSingleton<IVideoProcessor, VideoProcessingService>();
        services.AddTransient<AudioSettingsViewModel>();
        services.AddSingleton<Func<AudioSettingsViewModel>>(sp => () => sp.GetRequiredService<AudioSettingsViewModel>());
        services.AddTransient<VideoProcessingViewModel>();
        services.AddSingleton<Func<VideoProcessingViewModel>>(sp => () => sp.GetRequiredService<VideoProcessingViewModel>());
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();
        services.AddTransient<LogWindow>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logger = (LoggerService)_serviceProvider.GetRequiredService<ILoggerService>();

        // Headless-обработка видео: ScreenRecorder.App.exe --process-video in.mp4 out.mp4 [key=value ...]
        // Отчёт — process_result.txt рядом с exe. Окна не открываются.
        if (VideoProcessingCli.IsRequested(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = 1;
            try
            {
                var processor = _serviceProvider.GetRequiredService<IVideoProcessor>();
                code = System.Threading.Tasks.Task.Run(() => VideoProcessingCli.Run(e.Args, processor, logger)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "process_result.txt"), "ERROR: " + ex); } catch { }
                code = 1;
            }
            Shutdown(code);
            return;
        }

        // Headless-проверка настроек: ScreenRecorder.App.exe --dump-settings
        // Пишет settings_dump.txt рядом с exe и выходит. Проверяет, в частности, миграцию
        // настроек окна обработки видео (пустая цепочка → рабочий рецепт по умолчанию).
        if (e.Args.Any(a => a.Equals("--dump-settings", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var dumpPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings_dump.txt");
            try
            {
                var settings = _serviceProvider.GetRequiredService<SettingsService>();
                var v = settings.Current.VideoProcessing;
                var n = v.NoiseReduction;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"schema: {settings.Current.VideoProcessingSchema}");
                sb.AppendLine($"noise enabled: {n.IsEnabled}");
                sb.AppendLine($"highpass: {n.HighPassHz}  lowpass: {n.LowPassHz}");
                sb.AppendLine($"spectral: {n.SpectralEnabled} nf={n.NoiseFloorDb} nr={n.ReductionDb} rf={n.ResidualFloorDb}");
                sb.AppendLine($"rnn: {n.RnnEnabled} model={n.RnnModel} mix={n.RnnMix}");
                sb.AppendLine($"gate: {n.GateEnabled} thr={n.GateThresholdDb}");
                sb.AppendLine($"input: {v.InputPath}");
                sb.AppendLine($"output: {v.OutputPath}");
                sb.AppendLine($"filters: {v.BuildFilterChain()}");
                System.IO.File.WriteAllText(dumpPath, sb.ToString());
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(dumpPath, "ERROR: " + ex); } catch { }
            }
            Shutdown(0);
            return;
        }

        // Headless-проверка внешних профилей: ScreenRecorder.App.exe --dump-config
        // Пишет config_dump.txt рядом с exe и выходит, окна не открывает.
        if (e.Args.Any(a => a.Equals("--dump-config", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var profiles = _serviceProvider.GetRequiredService<ProfileService>();
                var entries = profiles.Load();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"configs folder: {profiles.Folder}");
                sb.AppendLine($"profiles loaded: {entries.Count}");
                sb.AppendLine();
                foreach (var entry in entries)
                {
                    sb.AppendLine($"[{entry.FileName}] {entry.Profile.Name}");
                    sb.AppendLine($"  description: {entry.Profile.Description}");
                    sb.AppendLine($"  video: {entry.Profile.Video.Describe()}");
                    sb.AppendLine($"  audio: {(entry.Profile.Audio == null ? "(из settings.json)" : entry.Profile.Audio.Describe())}");
                    sb.AppendLine($"  args@1920x1080: {entry.Profile.Video.DescribeArgs(1920, 1080, "out.mp4")}");
                    foreach (var w in entry.Profile.Video.Validate()) sb.AppendLine($"  warning: {w}");
                    sb.AppendLine();
                }
                var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config_dump.txt");
                System.IO.File.WriteAllText(path, sb.ToString());
                logger.Log($"--dump-config: отчёт записан в {path}");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config_dump.txt"), "ERROR: " + ex);
                Shutdown(1);
            }
            return;
        }

        // Регистрируем мост между фоновым потоком и UI
        logger.RegisterUIDispatcher(entry => 
        {
            Dispatcher.BeginInvoke(new Action(() => 
            {
                logger.LogEntries.Insert(0, entry);
                if (logger.LogEntries.Count > 100) logger.LogEntries.RemoveAt(100);
            }));
        });

        logger.Log("Application started.");

        var logWindow = _serviceProvider.GetRequiredService<LogWindow>();
        logWindow.Show();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _serviceProvider.GetRequiredService<MainViewModel>();
        mainWindow.Show();
    }
}