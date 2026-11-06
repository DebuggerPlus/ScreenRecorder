using System.Windows;
using ScreenRecorder.App.ViewModels;
namespace ScreenRecorder.App.Views;
public partial class RecordingsWindow : Window
{
    public RecordingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}