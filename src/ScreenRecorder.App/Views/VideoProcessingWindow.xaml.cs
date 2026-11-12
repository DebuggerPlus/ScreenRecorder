using System.ComponentModel;
using System.Windows;
using ScreenRecorder.App.ViewModels;

namespace ScreenRecorder.App.Views;

public partial class VideoProcessingWindow : Window
{
    private readonly VideoProcessingViewModel _vm;

    public VideoProcessingWindow(VideoProcessingViewModel vm)
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
