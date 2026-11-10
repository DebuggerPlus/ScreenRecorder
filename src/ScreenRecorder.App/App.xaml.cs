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
using ScreenRecorder.Infrastructure.Services;
using System.Windows;
using System;

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
        services.AddSingleton<IVideoCapture, WindowsGraphicsCapture>();
        services.AddSingleton<IAudioCapture, WasapiAudioCapture>();
        services.AddSingleton<FFmpegEncoder>();
        services.AddSingleton<IRecordingSession, RecordingSession>();
        services.AddSingleton<ICaptureSourceProvider, WindowsGraphicsCaptureSourceProvider>();
        services.AddSingleton<IAudioDeviceService, AudioDeviceService>();
        services.AddSingleton<IAudioTestService, AudioTestService>();
        services.AddTransient<AudioSettingsViewModel>();
        services.AddSingleton<Func<AudioSettingsViewModel>>(sp => () => sp.GetRequiredService<AudioSettingsViewModel>());
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();
        services.AddTransient<LogWindow>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        var logger = (LoggerService)_serviceProvider.GetRequiredService<ILoggerService>();
        
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