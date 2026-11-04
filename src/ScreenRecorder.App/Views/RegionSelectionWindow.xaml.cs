using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenRecorder.App.Views;

public partial class RegionSelectionWindow : Window
{
    private Point _startPoint;
    private bool _isSelecting = false;
    public Rect SelectedRegion { get; private set; }

    public RegionSelectionWindow()
    {
        InitializeComponent();
        // Принудительно ставим прозрачный фон для Canvas в коде
        SelectionCanvas.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); 
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        this.Topmost = true; // Гарантируем, что окно сверху всех
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { this.DialogResult = false; this.Close(); }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isSelecting = true;
            _startPoint = e.GetPosition(SelectionCanvas);
            SelectionBorder.Visibility = Visibility.Visible;
            SelectionCanvas.CaptureMouse();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isSelecting)
        {
            var cur = e.GetPosition(SelectionCanvas);
            var x = Math.Min(_startPoint.X, cur.X);
            var y = Math.Min(_startPoint.Y, cur.Y);
            var w = Math.Abs(_startPoint.X - cur.X);
            var h = Math.Abs(_startPoint.Y - cur.Y);

            Canvas.SetLeft(SelectionBorder, x);
            Canvas.SetTop(SelectionBorder, y);
            SelectionBorder.Width = Math.Max(1, w);
            SelectionBorder.Height = Math.Max(1, h);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSelecting)
        {
            _isSelecting = false;
            SelectionCanvas.ReleaseMouseCapture();
            
            if (SelectionBorder.Width > 10 && SelectionBorder.Height > 10)
            {
                SelectedRegion = new Rect(Canvas.GetLeft(SelectionBorder), Canvas.GetTop(SelectionBorder), SelectionBorder.Width, SelectionBorder.Height);
                this.DialogResult = true;
            }
            else { this.DialogResult = false; }
            this.Close();
        }
    }
}