using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Core.Enums;
using NAudio.CoreAudioApi;

namespace ScreenRecorder.Infrastructure.Capture;

public class WindowsGraphicsCaptureSourceProvider : ICaptureSourceProvider
{
    public Task<IReadOnlyList<CaptureSource>> GetSourcesAsync(CaptureMode mode, CancellationToken cancellationToken)
    {
        var sources = new List<CaptureSource>();
        if (mode == CaptureMode.Display)
            sources.Add(new CaptureSource("Primary", "Primary Monitor", mode, 1920, 1080));
        return Task.FromResult<IReadOnlyList<CaptureSource>>(sources);
    }

    public List<AudioDevice> GetAudioDevices()
    {
        var devices = new List<AudioDevice>();
        try {
            var enumerator = new MMDeviceEnumerator();
            // Получаем устройства воспроизведения (для системного звука через Stereo Mix)
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                devices.Add(new AudioDevice(dev.ID, dev.FriendlyName, false));
            // Получаем микрофоны
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                devices.Add(new AudioDevice(dev.ID, dev.FriendlyName, true));
        } catch { }
        return devices;
    }
}