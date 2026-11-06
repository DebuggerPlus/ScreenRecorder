using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Core.Enums;
using ScreenRecorder.Infrastructure.Encoding;

namespace ScreenRecorder.Infrastructure.Capture;

public class WindowsGraphicsCapture : IVideoCapture
{
    private readonly FFmpegEncoder _encoder;
    private readonly ILoggerService _logger;
    private CancellationTokenSource? _cts;

    public WindowsGraphicsCapture(FFmpegEncoder encoder, ILoggerService logger)
    {
        _encoder = encoder;
        _logger = logger;
    }

    public Task StartAsync(RecordingOptions options, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => CaptureLoop(options, _cts.Token));
        return Task.CompletedTask;
    }

    private async Task CaptureLoop(RecordingOptions options, CancellationToken ct)
    {
        try
        {
            var source = options.Source;
            int width = (source.Width / 2) * 2;
            int height = (source.Height / 2) * 2;
            int startX = source.X;
            int startY = source.Y;

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            var rect = new Rectangle(0, 0, width, height);

            // Точный расчет времени для синхронизации FPS
            var sw = Stopwatch.StartNew();
            double frameTimeMs = 1000.0 / options.FramesPerSecond;
            
            while (!ct.IsCancellationRequested)
            {
                long frameStartMs = sw.ElapsedMilliseconds;
                
                using (var screenBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(screenBmp))
                {
                    g.CopyFromScreen(startX, startY, 0, 0, rect.Size);

                    if (options.ShowCursor)
                    {
                        var cursorRect = new Rectangle(
                            System.Windows.Forms.Cursor.Position.X - startX, 
                            System.Windows.Forms.Cursor.Position.Y - startY, 
                            System.Windows.Forms.Cursors.Default.Size.Width, 
                            System.Windows.Forms.Cursors.Default.Size.Height);
                        System.Windows.Forms.Cursors.Default.Draw(g, cursorRect);
                    }

                    var data = screenBmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int bytes = Math.Abs(data.Stride) * screenBmp.Height;
                        byte[] rgbValues = new byte[bytes];
                        Marshal.Copy(data.Scan0, rgbValues, 0, bytes);
                        await _encoder.WriteFrameAsync(rgbValues);
                    }
                    finally { screenBmp.UnlockBits(data); }
                }

                long elapsed = sw.ElapsedMilliseconds - frameStartMs;
                int sleepTime = (int)(frameTimeMs - elapsed);
                if (sleepTime > 0)
                {
                    await Task.Delay(sleepTime, ct);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }
        catch (Exception ex) { _logger.Log($"Capture Error: {ex.Message}"); }
    }

    public Task StopAsync(CancellationToken ct) { _cts?.Cancel(); return Task.CompletedTask; }
    public Task PauseAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
}