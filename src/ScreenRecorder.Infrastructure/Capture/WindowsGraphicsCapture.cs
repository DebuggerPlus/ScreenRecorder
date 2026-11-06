using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Infrastructure.Encoding;
using System.Windows.Forms;

namespace ScreenRecorder.Infrastructure.Capture;

public class WindowsGraphicsCapture : IVideoCapture
{
    private readonly FFmpegEncoder _encoder;
    private readonly ILoggerService _logger;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;

    public WindowsGraphicsCapture(FFmpegEncoder encoder, ILoggerService logger)
    {
        _encoder = encoder;
        _logger = logger;
    }

    public Task StartAsync(RecordingOptions options, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _captureTask = Task.Run(() => CaptureLoop(options, _cts.Token));
        return Task.CompletedTask;
    }

    private async Task CaptureLoop(RecordingOptions options, CancellationToken ct)
    {
        long frames = 0;
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
                frames++;
                long targetTimeMs = (long)(frames * frameTimeMs);

                graphics.CopyFromScreen(startX, startY, 0, 0, rect.Size);

                if (options.ShowCursor)
                {
                    var cursorRect = new Rectangle(
                        Cursor.Position.X - startX,
                        Cursor.Position.Y - startY,
                        Cursors.Default.Size.Width,
                        Cursors.Default.Size.Height);
                    Cursors.Default.Draw(graphics, cursorRect);
                }

                var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int bytes = Math.Abs(data.Stride) * bitmap.Height;
                    byte[] rgbValues = new byte[bytes];
                    Marshal.Copy(data.Scan0, rgbValues, 0, bytes);
                    await _encoder.WriteFrameAsync(rgbValues);
                }
                finally { bitmap.UnlockBits(data); }

                long waitTime = targetTimeMs - sw.ElapsedMilliseconds;
                if (waitTime > 0)
                {
                    await Task.Delay((int)waitTime, ct);
                }
                else
                {
                    // Если захват кадра занял больше времени, чем интервал (например, 40мс вместо 33мс),
                    // мы НЕ пропускаем кадр, а просто идем к следующему.
                    // FFmpeg с флагом -vsync cfr сам вставит дубликаты, если входной поток будет слишком медленным.
                    await Task.Yield();
                }
            }
        }
        catch (Exception ex) { _logger.Log($"Capture Error: {ex.Message}"); }
        finally
        {
            _logger.Log($"Video Stopped.");
        }
    }

    public async Task StopAsync(CancellationToken ct) 
    { 
        _cts?.Cancel(); 
        if (_captureTask != null) await Task.WhenAny(_captureTask, Task.Delay(3000));
    }

    public Task PauseAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
}
