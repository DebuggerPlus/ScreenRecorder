using System;
using System.IO;
using System.Collections.ObjectModel;
using ScreenRecorder.Core.Interfaces;

namespace ScreenRecorder.Infrastructure.Services;

public class LoggerService : ILoggerService
{
    private readonly string _logFilePath;
    public ObservableCollection<string> LogEntries { get; } = new();
    private static object _lock = new object();
    private Action<string>? _uiDispatcherAction;

    public LoggerService()
    {
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir, $"log_{DateTime.Now:yyyyMMdd}.txt");
    }

    // Метод для регистрации способа обновления UI из App проекта
    public void RegisterUIDispatcher(Action<string> action)
    {
        _uiDispatcherAction = action;
    }

    public void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        
        // Запись в файл всегда в фоне
        lock(_lock)
        {
            try { File.AppendAllText(_logFilePath, entry + Environment.NewLine); } catch { }
        }

        // Обновление UI через делегат, который передаст нам App
        _uiDispatcherAction?.Invoke(entry);
    }
}