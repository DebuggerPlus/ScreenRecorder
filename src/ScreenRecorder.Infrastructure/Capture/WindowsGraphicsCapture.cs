using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

    public Task StartAsync(CaptureSource source, CancellationToken ct)
    {
        _logger.Log($"Starting real capture for {source.Name} ({source.Width}x{source.Height})");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        // Запускаем цикл захвата
        _ = Task.Run(() => CaptureLoop(source, _cts.Token));
        
        return Task.CompletedTask;
    }

    private async Task CaptureLoop(CaptureSource source, CancellationToken ct)
    {
        try
        {
            // Определяем область захвата (для простоты MVP - основной экран или область)
            // В будущем здесь можно добавить смещение X,Y для CaptureMode.Region
            int width = (source.Width / 2) * 2;
            int height = (source.Height / 2) * 2;
            
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            
            var rect = new Rectangle(0, 0, width, height);
            
            _logger.Log("Capture loop active.");

            while (!ct.IsCancellationRequested)
            {
                // Захват экрана через GDI
                graphics.CopyFromScreen(0, 0, 0, 0, rect.Size);
                
                // Извлекаем байты (BGRA)
                var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int bytes = Math.Abs(data.Stride) * bitmap.Height;
                    byte[] rgbValues = new byte[bytes];
                    Marshal.Copy(data.Scan0, rgbValues, 0, bytes);
                    
                    await _encoder.WriteFrameAsync(rgbValues);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                // Ограничение FPS (30 кадров)
                await Task.Delay(33, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Log($"Capture Loop Error: {ex.Message}");
        }
    }

    public Task PauseAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _logger.Log("Capture stopped.");
        return Task.CompletedTask;
    }
}