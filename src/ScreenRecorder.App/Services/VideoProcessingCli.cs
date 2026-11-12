using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.App.Services;

/// <summary>
/// Headless-прогон обработки видео из командной строки:
///   ScreenRecorder.App.exe --process-video in.mp4 out.mp4 [key=value ...]
/// Нужен, чтобы проверять цепочку фильтров без запуска UI. Отчёт пишется в process_result.txt рядом с exe.
/// Ключи: recipe=, hp=, lp=, spec=0|1, nf=, amt=, tn=0|1, tr=0|1, rf=, nt=white|vinyl|shellac,
///        rnn=0|1, model=, rmix=, gate=0|1, gthr=, gratio=, grange=, gain=, norm=off|loudness|limiter|speech,
///        eq=off|&lt;preset&gt;, copy=0|1, crf=, preset=, kbps=, container=mp4|mkv, ss=, t=
/// </summary>
public static class VideoProcessingCli
{
    public const string Flag = "--process-video";

    public static bool IsRequested(string[] args)
        => args.Any(a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args, IVideoProcessor processor, ILoggerService logger)
    {
        var report = new StringBuilder();
        var list = args.ToList();
        int index = list.FindIndex(a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));
        var positional = list.Skip(index + 1).ToList();

        string input = positional.Count > 0 ? positional[0] : "";
        string output = positional.Count > 1 ? positional[1] : "";

        var settings = new VideoProcessingSettings { InputPath = input, OutputPath = output };

        try
        {
            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
                return Fail(report, logger, 2, "usage: " + Flag + " <input> <output> [recipe=<name>] [hp=150] [spec=1 nf=-40 amt=16 rf=-70] " +
                                               "[rnn=1] [gate=1 gthr=-45] [gain=0] [norm=off|loudness|limiter|speech] [eq=off|<preset>] " +
                                               "[copy=0|1] [crf=20] [preset=medium] [kbps=192] [container=mp4|mkv] [ss=10] [t=10]");

            foreach (var arg in positional.Skip(2))
            {
                var eq = arg.IndexOf('=');
                if (eq <= 0) return Fail(report, logger, 2, "unknown argument: " + arg);
                var key = arg.Substring(0, eq).Trim().ToLowerInvariant();
                var value = arg.Substring(eq + 1).Trim();

                if (key == "recipe")
                {
                    if (!NoiseReductionSettings.Recipes.Contains(value))
                        throw new ArgumentException("recipe must be one of: " + string.Join(" | ", NoiseReductionSettings.Recipes));
                    settings.NoiseReduction = NoiseReductionSettings.Recipe(value);
                    continue;
                }

                var n = settings.NoiseReduction;

                switch (key)
                {
                    case "hp": n.HighPassHz = ToInt(value); break;
                    case "lp": n.LowPassHz = ToInt(value); break;
                    case "spec": n.SpectralEnabled = ToBool(value); break;
                    case "nf": n.NoiseFloorDb = (float)ToDouble(value); break;
                    case "amt": n.ReductionDb = (float)ToDouble(value); break;
                    case "tn": n.TrackNoise = ToBool(value); break;
                    case "tr": n.TrackResidual = ToBool(value); break;
                    case "rf": n.ResidualFloorDb = (float)ToDouble(value); break;
                    case "rnn": n.RnnEnabled = ToBool(value); break;
                    case "model": n.RnnModel = value; break;
                    case "rmix": n.RnnMix = (float)ToDouble(value); break;
                    case "gate": n.GateEnabled = ToBool(value); break;
                    case "gthr": n.GateThresholdDb = (float)ToDouble(value); break;
                    case "gratio": n.GateRatio = (float)ToDouble(value); break;
                    case "grange": n.GateRangeDb = (float)ToDouble(value); break;
                    case "gain": settings.GainDb = (float)ToDouble(value); break;
                    case "crf": settings.Crf = ToInt(value); break;
                    case "kbps": settings.AacBitrateKbps = ToInt(value); break;
                    case "preset": settings.Preset = value; break;
                    case "ss": settings.StartSeconds = ToInt(value); break;
                    case "t": settings.LimitSeconds = ToInt(value); break;
                    case "copy": settings.CopyVideoStream = ToBool(value); break;
                    case "container":
                        settings.Container = value.Equals("mkv", StringComparison.OrdinalIgnoreCase) ? OutputContainer.Mkv : OutputContainer.Mp4;
                        break;
                    case "nt":
                        n.Type = value.ToLowerInvariant() switch
                        {
                            "white" => NoiseType.White,
                            "vinyl" => NoiseType.Vinyl,
                            "shellac" => NoiseType.Shellac,
                            _ => throw new ArgumentException("nt must be white|vinyl|shellac, got: " + value)
                        };
                        break;
                    case "norm":
                        settings.Level = value.ToLowerInvariant() switch
                        {
                            "off" => LevelTarget.Off,
                            "loudness" => LevelTarget.Loudness,
                            "limiter" => LevelTarget.Limiter,
                            "speech" => LevelTarget.Speech,
                            _ => throw new ArgumentException("norm must be off|loudness|limiter|speech, got: " + value)
                        };
                        break;
                    case "eq":
                        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                        {
                            settings.Equalizer.Enabled = false;
                        }
                        else if (EqualizerPresets.All.TryGetValue(value, out var gains))
                        {
                            settings.Equalizer.Enabled = true;
                            settings.Equalizer.Preset = value;
                            for (int i = 0; i < EqualizerPresets.BandCount && i < gains.Length; i++)
                                settings.Equalizer.BandGainsDb[i] = gains[i];
                        }
                        else
                        {
                            throw new ArgumentException("eq preset not found: " + value + " (available: " + string.Join(", ", EqualizerPresets.Names) + ")");
                        }
                        break;
                    default:
                        return Fail(report, logger, 2, "unknown key: " + key);
                }
            }
        }
        catch (Exception ex)
        {
            return Fail(report, logger, 2, "bad argument: " + ex.Message);
        }

        settings.OutputPath = VideoProcessingSettings.FixExtension(settings.OutputPath, settings.Container);
        settings.Normalize();

        var info = processor.ProbeAsync(settings.InputPath).GetAwaiter().GetResult();
        settings.InputHasVideo = info.IsValid ? info.HasVideo : true;
        settings.InputHasAudio = info.IsValid ? info.HasAudio : true;

        report.AppendLine("input: " + info.ToText());
        report.AppendLine("output: " + settings.OutputPath);
        report.AppendLine("settings: " + settings.Describe());
        report.AppendLine("filters: " + settings.BuildFilterChain());
        report.AppendLine("command: " + settings.BuildCommandLine());
        report.AppendLine("hasVideo=" + settings.InputHasVideo + " hasAudio=" + settings.InputHasAudio
                          + " copy=" + settings.CopyVideoStream + " ss=" + settings.StartSeconds + " t=" + settings.LimitSeconds);

        logger.Log("--process-video: " + settings.BuildCommandLine());

        VideoProcessingResult result;
        try
        {
            result = processor.RunAsync(settings, null).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            report.AppendLine("EXCEPTION: " + ex);
            return Fail(report, logger, 1, "exception: " + ex.Message);
        }

        report.AppendLine("success: " + result.Success);
        report.AppendLine("elapsed_s: " + result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture));
        report.AppendLine("output_duration_s: " + result.OutputDurationSec.ToString("0.00", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(result.Error)) report.AppendLine("error: " + result.Error);
        if (!string.IsNullOrEmpty(result.LogTail)) { report.AppendLine("--- ffmpeg ---"); report.AppendLine(result.LogTail); }

        WriteReport(report);
        logger.Log($"--process-video: success={result.Success} ({result.Elapsed.TotalSeconds:0.0}s)");
        return result.Success ? 0 : 1;
    }

    private static int Fail(StringBuilder report, ILoggerService logger, int code, string message)
    {
        report.AppendLine("FAILED: " + message);
        WriteReport(report);
        logger.Log("--process-video: " + message);
        return code;
    }

    private static void WriteReport(StringBuilder report)
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "process_result.txt");
            File.WriteAllText(path, report.ToString());
        }
        catch { /* отчёт — не критично */ }
    }

    private static int ToInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);
    private static double ToDouble(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static bool ToBool(string value)
        => value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
