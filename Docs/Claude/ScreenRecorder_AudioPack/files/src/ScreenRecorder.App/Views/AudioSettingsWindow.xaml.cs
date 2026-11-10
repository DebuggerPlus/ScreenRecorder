using System.ComponentModel;
using System.Windows;
using ScreenRecorder.App.ViewModels;

namespace ScreenRecorder.App.Views;

public partial class AudioSettingsWindow : Window
{
    private readonly AudioSettingsViewModel _vm;

    public AudioSettingsWindow(AudioSettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Cleanup();
        base.OnClosing(e);
    }
}
