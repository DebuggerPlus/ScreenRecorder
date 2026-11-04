using System;
using System.IO;
using System.Collections.ObjectModel;

namespace ScreenRecorder.Core.Interfaces;

public interface ILoggerService
{
    ObservableCollection<string> LogEntries { get; }
    void Log(string message);
}