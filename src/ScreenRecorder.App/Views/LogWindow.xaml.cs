using System.Windows;
using ScreenRecorder.Core.Interfaces;
namespace ScreenRecorder.App.Views;
public partial class LogWindow : Window
{
    public LogWindow(ILoggerService logger)
    {
        InitializeComponent();
        DataContext = logger;
    }
}