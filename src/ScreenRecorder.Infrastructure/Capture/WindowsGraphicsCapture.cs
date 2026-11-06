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
        long framesWritten = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            var source = options.Source;
            int width = (source.Width / 2) * 2;
            int height = (source.Height / 2) * 2;
            int startX = source.X;
            int startY = source.Y;

            // Используем один буфер для экономии памяти
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            var rect = new Rectangle(0, 0, width, height);
            byte[]? frameBuffer = null;

            double fps = options.FramesPerSecond;
            double frameTimeMs = 1000.0 / fps;

            while (!ct.IsCancellationRequested)
            {
                long frameStartMs = sw.ElapsedMilliseconds;
                
                // Захват экрана
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

                // Копируем данные в буфер
                var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int bytes = Math.Abs(data.Stride) * bitmap.Height;
                    if (frameBuffer == null || frameBuffer.Length != bytes) frameBuffer = new byte[bytes];
                    Marshal.Copy(data.Scan0, frameBuffer, 0, bytes);
                }
                finally { bitmap.UnlockBits(data); }

                // Дублирование кадров, если захват отстает от реального времени
                long dueFrames = (long)(sw.Elapsed.TotalSeconds * fps) + 1;
                while (framesWritten < dueFrames && !ct.IsCancellationRequested)
                {
                    await _encoder.WriteFrameAsync(frameBuffer);
                    framesWritten++;
                }

                // Ждем до следующего интервала захвата
                long elapsed = sw.ElapsedMilliseconds - frameStartMs;
                int sleepTime = (int)(frameTimeMs - elapsed);
                if (sleepTime > 0) await Task.Delay(sleepTime, ct);
            }
        }
        catch (Exception ex) { _logger.Log($"Capture Error: {ex.Message}"); }
        finally
        {
            sw.Stop();
            _logger.Log($"Video Stopped: {framesWritten} frames in {sw.Elapsed.TotalSeconds:F1}s (Avg {framesWritten / sw.Elapsed.TotalSeconds:F1} fps)");
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
