using System.Windows;
namespace ScreenRecorder.App.Views;
public partial class RecordingOverlayWindow : Window
{
    public RecordingOverlayWindow(Rect area)
    {
        InitializeComponent();
        this.Left = area.X;
        this.Top = area.Y;
        this.Width = area.Width;
        this.Height = area.Height;
    }
}