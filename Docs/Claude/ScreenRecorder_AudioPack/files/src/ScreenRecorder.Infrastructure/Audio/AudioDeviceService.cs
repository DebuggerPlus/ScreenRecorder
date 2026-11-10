using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Infrastructure.Audio;

public sealed class AudioDeviceService : IAudioDeviceService
{
    public IReadOnlyList<AudioDevice> GetDevices()
    {
        var list = new List<AudioDevice>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName, true));
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName, false));
        }
        catch { /* нет устройств или сервис звука недоступен */ }
        return list;
    }

    public string DescribeMixFormat(string deviceId)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDevice(deviceId);
            return AudioFormatHelper.Describe(dev.AudioClient.MixFormat) + " (shared-mode mix format)";
        }
        catch (Exception ex)
        {
            return "unavailable: " + ex.Message;
        }
    }

    public Task<string> ProbeFormatsAsync(string deviceId) => Task.Run(() =>
    {
        var sb = new StringBuilder();
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDevice(deviceId);
            var ac = dev.AudioClient;
            sb.AppendLine($"Device: {dev.FriendlyName} ({(dev.DataFlow == DataFlow.Capture ? "input" : "output, loopback")})");
            sb.AppendLine($"Mix format: {AudioFormatHelper.Describe(ac.MixFormat)}");

            foreach (var mode in new[] { AudioClientShareMode.Shared, AudioClientShareMode.Exclusive })
            {
                sb.AppendLine();
                sb.AppendLine($"{mode} mode, formats accepted by the driver:");
                bool any = false;
                foreach (int rate in AudioCaptureSettings.AllowedDriverRates)
                {
                    var ok = new List<string>();
                    foreach (int ch in new[] { 1, 2 })
                    {
                        if (Supported(ac, mode, new WaveFormat(rate, 16, ch))) ok.Add($"16-bit/{ch}ch");
                        if (Supported(ac, mode, new WaveFormat(rate, 24, ch))) ok.Add($"24-bit/{ch}ch");
                        if (Supported(ac, mode, WaveFormat.CreateIeeeFloatWaveFormat(rate, ch))) ok.Add($"float/{ch}ch");
                    }
                    if (ok.Count > 0) { any = true; sb.AppendLine($"  {rate} Hz: {string.Join(", ", ok)}"); }
                }
                if (!any) sb.AppendLine("  none of the tested formats");
            }
            sb.AppendLine();
            sb.AppendLine("Note: in shared mode Windows can convert formats itself, so the list may be short while a custom format still works.");
        }
        catch (Exception ex)
        {
            sb.AppendLine("Probe failed: " + ex.Message);
        }
        return sb.ToString();
    });

    private static bool Supported(AudioClient ac, AudioClientShareMode mode, WaveFormat fmt)
    {
        try { return ac.IsFormatSupported(mode, fmt); }
        catch { return false; }
    }
}
