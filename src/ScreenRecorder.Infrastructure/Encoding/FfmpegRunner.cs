using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenRecorder.Infrastructure.Encoding;

/// <summary>Запуск ffmpeg как дочернего процесса. Аргументы передаются списком (без склейки в строку).</summary>
public static class FfmpegRunner
{
    public static async Task<(int ExitCode, string StdErr)> RunAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync(ct);
        await outp;
        return (p.ExitCode, await err);
    }
}
