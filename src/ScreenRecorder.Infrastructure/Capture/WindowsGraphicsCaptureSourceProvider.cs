using ScreenRecorder.Core.Enums;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using Windows.Graphics.Capture;
using System.Runtime.InteropServices.WindowsRuntime;

namespace ScreenRecorder.Infrastructure.Capture;

public class WindowsGraphicsCaptureSourceProvider : ICaptureSourceProvider
{
    public Task<IReadOnlyList<CaptureSource>> GetSourcesAsync(CaptureMode mode, CancellationToken cancellationToken)
    {
        var sources = new List<CaptureSource>();

        if (mode == CaptureMode.Display)
        {
            // Note: In a real app we'd use Win32 to enumerate monitors. 
            // For MVP via WGC, we can use GraphicsCapturePicker or known IDs.
            // Here we provide a dummy for the picker to be called later or static IDs.
            sources.Add(new CaptureSource("Primary", "Primary Monitor", mode, 1920, 1080));
        }
        else if (mode == CaptureMode.Window)
        {
            // Placeholder: Typically requires Win32 FindWindow enumeration
            sources.Add(new CaptureSource("Window1", "Active Window", mode, 1280, 720));
        }

        return Task.FromResult<IReadOnlyList<CaptureSource>>(sources);
    }
}