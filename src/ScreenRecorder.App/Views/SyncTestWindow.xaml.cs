using System;
using System.Windows;
using System.Windows.Threading;
using System.Media;
using System.Windows.Media;

namespace ScreenRecorder.App.Views;

public partial class SyncTestWindow : Window
{
    private DispatcherTimer _timer;
    private DateTime _startTime;
    private TimeSpan _accumulatedTime = TimeSpan.Zero;
    private bool _isRunning = false;
    private bool _isSoundEnabled = true;

    public SyncTestWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Interval = TimeSpan.FromMilliseconds(10);
        _timer.Tick += Timer_Tick;
        
        UpdateUI();
    }

    private void Timer_Tick(object? s, EventArgs e)
    {
        var diff = (DateTime.Now - _startTime) + _accumulatedTime;
        TimerText.Text = ((int)diff.TotalSeconds).ToString();
        
        // Звук на каждой десятой секунде
        if (_isSoundEnabled)
        {
            int currentSecond = (int)diff.TotalSeconds;
            int prevSecond = (int)(diff.TotalSeconds - 0.015);
            if (currentSecond != prevSecond && currentSecond % 10 == 0 && currentSecond > 0)
            {
                SystemSounds.Beep.Play();
            }
        }
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning)
        {
            _startTime = DateTime.Now;
            _timer.Start();
            _isRunning = true;
        }
        else
        {
            _accumulatedTime += DateTime.Now - _startTime;
            _timer.Stop();
            _isRunning = false;
        }
        UpdateUI();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _isRunning = false;
        _accumulatedTime = TimeSpan.Zero;
        TimerText.Text = "0";
        UpdateUI();
    }

    private void SoundToggle_Click(object sender, RoutedEventArgs e)
    {
        _isSoundEnabled = !_isSoundEnabled;
        SoundToggleBtn.Content = _isSoundEnabled ? "🔊 Sound ON" : "🔇 Sound OFF";
    }

    private void UpdateUI()
    {
        StartStopButton.Content = _isRunning ? "STOP" : "START";
        StartStopButton.Background = _isRunning ? Brushes.DarkRed : Brushes.DarkGreen;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
