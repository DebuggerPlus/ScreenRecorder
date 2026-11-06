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

            // Загружаем логотип заранее, если он есть
            Image? logoImage = null;
            if (options.ShowLogo && !string.IsNullOrEmpty(options.LogoPath) && System.IO.File.Exists(options.LogoPath))
            {
                logoImage = Image.FromFile(options.LogoPath);
            }

            while (!ct.IsCancellationRequested)
            {
                long frameStartMs = sw.ElapsedMilliseconds;
                
                // Захват экрана
                graphics.CopyFromScreen(startX, startY, 0, 0, rect.Size);

                    // Отрисовка логотипа
                    if (options.ShowLogo)
                    {
                        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        
                        int lw, lh;
                        if (logoImage != null) { lw = (int)(logoImage.Width * options.LogoScale); lh = (int)(logoImage.Height * options.LogoScale); }
                        else { 
                            using var tempFont = new Font("Arial", 24 * options.LogoScale, FontStyle.Bold);
                            var size = graphics.MeasureString(options.LogoText, tempFont);
                            lw = (int)size.Width; lh = (int)size.Height;
                        }

                        int padding = 20;
                        int x = options.LogoPosition switch {
                            LogoPosition.TopLeft => padding,
                            LogoPosition.BottomLeft => padding,
                            LogoPosition.TopRight => width - lw - padding,
                            LogoPosition.BottomRight => width - lw - padding,
                            LogoPosition.Center => (width - lw) / 2,
                            _ => width - lw - padding
                        };
                        int y = options.LogoPosition switch {
                            LogoPosition.TopLeft => padding,
                            LogoPosition.TopRight => padding,
                            LogoPosition.BottomLeft => height - lh - padding,
                            LogoPosition.BottomRight => height - lh - padding,
                            LogoPosition.Center => (height - lh) / 2,
                            _ => padding
                        };

                        if (logoImage != null)
                        {
                            var cm = new ColorMatrix { Matrix33 = options.LogoOpacity };
                            var ia = new ImageAttributes();
                            ia.SetColorMatrix(cm);
                            graphics.DrawImage(logoImage, new Rectangle(x, y, lw, lh), 0, 0, logoImage.Width, logoImage.Height, GraphicsUnit.Pixel, ia);
                        }
                        else if (!string.IsNullOrEmpty(options.LogoText))
                        {
                            using var font = new Font("Arial", 24 * options.LogoScale, FontStyle.Bold);
                            var color = Color.FromArgb((int)(options.LogoOpacity * 255), Color.White);
                            using var brush = new SolidBrush(color);
                            using var shadowBrush = new SolidBrush(Color.FromArgb((int)(options.LogoOpacity * 128), Color.Black));
                            graphics.DrawString(options.LogoText, font, shadowBrush, x + 2, y + 2);
                            graphics.DrawString(options.LogoText, font, brush, x, y);
                        }
                    }

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
            logoImage?.Dispose();
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
