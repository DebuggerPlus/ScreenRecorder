using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ScreenRecorder.App.Views;

public partial class RegionSelectionWindow : Window
{
    public RegionSelectionWindow()
    {
        InitializeComponent();
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Thumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newWidth = Width + e.HorizontalChange;
        double newHeight = Height + e.VerticalChange;
        if (newWidth > 50) Width = newWidth;
        if (newHeight > 50) Height = newHeight;
    }
}