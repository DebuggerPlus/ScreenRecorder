// Самотест обработки готового видео (без UI и без устройств): сборка цепочки фильтров, прогон через ffmpeg,
// проверка по уровням — гул вентилятора уходит, голос остаётся.
// Запуск: dotnet run --project tools/VideoProcessingSelfTest -c Release   (код возврата 0 = все проверки прошли)
using System.Diagnostics;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Infrastructure.Audio;
using ScreenRecorder.Infrastructure.Processing;
using ScreenRecorder.Infrastructure.Services;

int fails = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
    if (!ok) fails++;
}

var work = Path.Combine(Path.GetTempPath(), "VideoProcessingSelfTest");
if (Directory.Exists(work)) Directory.Delete(work, true);
Directory.CreateDirectory(work);

var logger = new LoggerService();
var svc = new VideoProcessingService(logger);

// ---------- ffmpeg helpers ----------
(int code, string err) Ffmpeg(params string[] ffargs)
{
    var psi = new ProcessStartInfo("ffmpeg")
    {
        RedirectStandardError = true, RedirectStandardOutput = true,
        UseShellExecute = false, CreateNoWindow = true
    };
    foreach (var a in ffargs) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi) ?? throw new Exception("cannot start ffmpeg");
    string err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, err);
}

string P(string name) => Path.Combine(work, name);

// Тестовый файл: картинка + «голос» 300 Гц + гул вентилятора 50 Гц + розовый шум.
// Ровно 6 с, чтобы DFT по тонам попадала в целые периоды и мерила амплитуду точно.
var noisy = P("noisy.mp4");
{
    var (code, err) = Ffmpeg(
        "-hide_banner", "-y",
        "-f", "lavfi", "-i", "testsrc=size=320x240:rate=15",
        "-f", "lavfi", "-i", "sine=frequency=300:duration=6",
        "-f", "lavfi", "-i", "anoisesrc=color=pink:amplitude=0.10:duration=6",
        "-f", "lavfi", "-i", "sine=frequency=50:duration=6",
        "-filter_complex",
        "[1:a]volume=0.25[voice];[3:a]volume=0.25[hum];[2:a]volume=0.60[noise];[voice][hum][noise]amix=inputs=3:duration=longest:normalize=0[a]",
        "-map", "0:v", "-map", "[a]",
        "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "192k", "-t", "6", noisy);
    Check("generate test clip", code == 0 && File.Exists(noisy), code == 0 ? noisy : err.Trim().Split('\n').Last());
}

// Чисто шумовой файл — для проверки спектрального и нейросетевого шумоподавления.
var noiseOnly = P("noise_only.wav");
{
    var (code, err) = Ffmpeg("-hide_banner", "-y",
        "-f", "lavfi", "-i", "anoisesrc=color=pink:amplitude=0.10:duration=6",
        "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", noiseOnly);
    Check("generate noise-only wav", code == 0 && File.Exists(noiseOnly), code == 0 ? "" : err.Trim().Split('\n').Last());
}

// ---------- анализ уровней ----------
float[] ReadWavMono(string path)
{
    using var fs = File.OpenRead(path);
    using var br = new BinaryReader(fs);
    if (new string(br.ReadChars(4)) != "RIFF") throw new Exception("not a riff: " + path);
    br.ReadInt32();
    if (new string(br.ReadChars(4)) != "WAVE") throw new Exception("not a wave: " + path);
    int bits = 16, channels = 1;
    long dataStart = -1; long dataLen = 0;
    while (fs.Position < fs.Length - 8)
    {
        var id = new string(br.ReadChars(4));
        int size = br.ReadInt32();
        if (id == "fmt ") { br.ReadInt16(); channels = br.ReadInt16(); br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16(); if (size > 16) br.ReadBytes(size - 16); }
        else if (id == "data") { dataStart = fs.Position; dataLen = size; break; }
        else br.ReadBytes(size + (size % 2));
    }
    if (dataStart < 0 || bits != 16) throw new Exception("unsupported wav: " + path);
    fs.Position = dataStart;
    var raw = br.ReadBytes((int)Math.Min(dataLen, fs.Length - dataStart));
    var mono = new float[raw.Length / 2 / channels];
    for (int i = 0; i < mono.Length; i++)
        mono[i] = BitConverter.ToInt16(raw, i * 2 * channels) / 32768f;
    return mono;
}

// Амплитуда одной частоты (DFT по целому числу периодов — утечки нет).
double ToneAmp(float[] x, int rate, double freq)
{
    double re = 0, im = 0;
    for (int i = 0; i < x.Length; i++)
    {
        double a = 2 * Math.PI * freq * i / rate;
        re += x[i] * Math.Cos(a);
        im -= x[i] * Math.Sin(a);
    }
    return 2 * Math.Sqrt(re * re + im * im) / x.Length;
}

double Rms(float[] x)
{
    double s = 0;
    for (int i = 0; i < x.Length; i++) s += (double)x[i] * x[i];
    return Math.Sqrt(s / Math.Max(1, x.Length));
}

double Db(double lin) => lin <= 1e-12 ? -240 : 20 * Math.Log10(lin);

string ExtractWav(string media, string name)
{
    var wav = P(name);
    var (code, err) = Ffmpeg("-hide_banner", "-y", "-i", media, "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", wav);
    if (code != 0) throw new Exception("extract failed: " + err.Trim().Split('\n').Last());
    return wav;
}

// ---------- 1. Сборка аргументов и фильтров (без ffmpeg) ----------
{
    string Chain(NoiseReductionSettings n) { var l = new List<string>(); n.AppendFilters(l); return string.Join(",", l); }

    var s = new VideoProcessingSettings { InputPath = "in.mp4", OutputPath = "out.mp4" };
    // Дефолты уже содержат рабочий рецепт, поэтому «ничего не включено» проверяем на пустых настройках.
    var sEmpty = new VideoProcessingSettings { InputPath = "in.mp4", OutputPath = "out.mp4", NoiseReduction = NoiseReductionSettings.CreateEmpty() };
    Check("filter: nothing enabled = empty", sEmpty.BuildFilterChain() == "", sEmpty.BuildFilterChain());

    var fresh = new VideoProcessingSettings { InputPath = "in.mp4", OutputPath = "out.mp4" };
    Check("defaults: fresh settings block = working recipe (hp100 + rnn)",
        Chain(fresh.NoiseReduction) == "highpass=f=100:poles=2,arnndn=m=models/rnnoise/bd.rnnn:mix=1", Chain(fresh.NoiseReduction));
    Check("defaults: fresh settings chain = default recipe chain",
        Chain(fresh.NoiseReduction) == Chain(NoiseReductionSettings.CreateDefault()), "");
    Check("defaults: 'All off' recipe = empty chain",
        Chain(NoiseReductionSettings.Recipe("All off")) == "", Chain(NoiseReductionSettings.Recipe("All off")));
    Check("defaults: 'Fan + voice (RNN)' recipe = fresh settings",
        Chain(NoiseReductionSettings.Recipe("Fan + voice (RNN)")) == Chain(fresh.NoiseReduction), "");

    s.NoiseReduction.HighPassHz = 150;
    Check("filter: high-pass 150", s.BuildFilterChain().Contains("highpass=f=150"), s.BuildFilterChain());

    s.NoiseReduction.SpectralEnabled = true;
    s.NoiseReduction.NoiseFloorDb = -42; s.NoiseReduction.ReductionDb = 14; s.NoiseReduction.ResidualFloorDb = -70;
    var f = s.BuildFilterChain();
    Check("filter: spectral afftdn parameters",
        f.Contains("afftdn=nr=14:nf=-42:tn=0:tr=0:rf=-70:nt=w"), f);

    s.NoiseReduction.SpectralEnabled = false;
    s.NoiseReduction.GateEnabled = true;
    s.NoiseReduction.GateThresholdDb = -40;
    Check("filter: gate threshold in linear units",
        s.BuildFilterChain().Contains("agate=threshold=0.01:ratio="), s.BuildFilterChain());

    s.NoiseReduction.GateEnabled = false;
    s.NoiseReduction.RnnEnabled = true;
    Check("filter: rnn model argument",
        s.BuildFilterChain().Contains("arnndn=m=models/rnnoise/bd.rnnn:mix=1"), s.BuildFilterChain());
    Check("filter: absolute model path escaped",
        NoiseReductionSettings.EscapeFilterPath(@"C:\Users\busin\bd.rnnn") == @"C\:/Users/busin/bd.rnnn",
        NoiseReductionSettings.EscapeFilterPath(@"C:\Users\busin\bd.rnnn"));

    s.NoiseReduction.RnnEnabled = false;
    s.Level = LevelTarget.Loudness;
    Check("filter: loudnorm appended", s.BuildFilterChain().Contains("loudnorm=I=-16"), s.BuildFilterChain());

    var cmd = string.Join(" ", s.BuildArguments());
    Check("args: video copied", cmd.Contains("-c:v copy"), cmd);
    Check("args: optional mapping", cmd.Contains("-map 0:v:0? -map 0:a:0?"), cmd);
    Check("args: progress on stdout", cmd.Contains("-progress pipe:1"), "");
    Check("args: no filter when the input has no audio",
        !new VideoProcessingSettings { InputPath = "a.mp4", OutputPath = "b.mp4", InputHasAudio = false }
            .BuildArguments().Contains("-af"), "");

    var prev = new VideoProcessingSettings { InputPath = "in.mp4", OutputPath = "p.mp4", StartSeconds = 2, LimitSeconds = 3 };
    var pargs = prev.BuildArguments();
    int ss = pargs.IndexOf("-ss"), i = pargs.IndexOf("-i"), t = pargs.IndexOf("-t");
    Check("args: preview -ss before -i, -t after", ss == 3 && ss < i && t > i && pargs[ss + 1] == "2" && pargs[t + 1] == "3",
        string.Join(" ", pargs));

    Check("output extension follows container",
        VideoProcessingSettings.FixExtension(@"C:\x\out.mkv", OutputContainer.Mp4) == @"C:\x\out.mp4", "");

    var eq = new VideoProcessingSettings { InputPath = "in.mp4", OutputPath = "out.mp4" };
    eq.Equalizer.Enabled = true;
    eq.Equalizer.Preset = "Reduce rumble";
    if (EqualizerPresets.All.TryGetValue("Reduce rumble", out var g))
        for (int k = 0; k < EqualizerPresets.BandCount; k++) eq.Equalizer.BandGainsDb[k] = g[k];
    Check("filter: equalizer preset 'Reduce rumble' adds bands",
        eq.BuildFilterChain().Contains("equalizer=f="), eq.BuildFilterChain());

    var rec = NoiseReductionSettings.Recipe("Fan + voice (RNN)");
    Check("recipe: fan + voice enables rnn and low cut", rec.RnnEnabled && rec.HighPassHz == 100 && !rec.SpectralEnabled, rec.Describe());
    Check("recipe: all off disables everything", !NoiseReductionSettings.Recipe("All off").IsEnabled, "");
    var def = NoiseReductionSettings.CreateDefault();
    Check("defaults: a fresh settings block already clean noise (no silent copy)",
        def.IsEnabled && def.RnnEnabled && def.HighPassHz == 100, def.Describe());
}

// ---------- 1b. Шумоподавление при съёмке: план склейки (без ffmpeg) ----------
{
    string Ch(NoiseReductionSettings n) { var l = new List<string>(); n.AppendFilters(l); return string.Join(",", l); }

    var off = new AudioCaptureSettings();
    Check("record denoise: off by default = no filter", RecordingDenoise.BuildChain(off) == "", off.Describe());
    Check("record denoise: default recipe = the working one",
        off.DenoiseRecipe == NoiseReductionSettings.DefaultRecipe, off.DenoiseRecipe);

    var on = new AudioCaptureSettings { DenoiseOnRecord = true };
    Check("record denoise: on = default recipe chain",
        RecordingDenoise.BuildChain(on) == "highpass=f=100:poles=2,arnndn=m=models/rnnoise/bd.rnnn:mix=1",
        RecordingDenoise.BuildChain(on));
    Check("record denoise: on = the same chain as the Process Video window",
        RecordingDenoise.BuildChain(on) == Ch(NoiseReductionSettings.CreateDefault()), RecordingDenoise.BuildChain(on));

    on.DenoiseRecipe = "Fan / hum";
    Check("record denoise: recipe 'Fan / hum' = hp150 + spectral",
        RecordingDenoise.BuildChain(on).StartsWith("highpass=f=150:poles=2,afftdn="), RecordingDenoise.BuildChain(on));

    on.DenoiseRecipe = "All off";
    Check("record denoise: recipe 'All off' = no filter", RecordingDenoise.BuildChain(on) == "", RecordingDenoise.BuildChain(on));

    on.DenoiseRecipe = "No such recipe";
    Check("record denoise: unknown recipe = no filter, no crash", RecordingDenoise.BuildChain(on) == "", RecordingDenoise.BuildChain(on));

    off.Normalize();
    Check("record denoise: broken recipe name is repaired on load",
        off.DenoiseRecipe == NoiseReductionSettings.DefaultRecipe, off.DenoiseRecipe);

    // Профиль кодирования не управляет шумоподавлением: в нём живёт аудио-пресет (усиление, битрейт),
    // и раньше его применение молча выключало шумоподавление при съёмке. Значения берём из Audio Settings.
    var fromProfile = new AudioCaptureSettings { DenoiseOnRecord = false, DenoiseRecipe = "All off", KeepRawAudio = false, InputGainDb = 15 };
    var fromUser = new AudioCaptureSettings { DenoiseOnRecord = true, DenoiseRecipe = "Fan / hum", KeepRawAudio = true, InputGainDb = 0 };
    fromProfile.CopyDenoiseFrom(fromUser);
    Check("record denoise: profile cannot switch noise reduction off",
        fromProfile.DenoiseOnRecord && RecordingDenoise.BuildChain(fromProfile).StartsWith("highpass=f=150:poles=2,afftdn="),
        fromProfile.Describe());
    Check("record denoise: profile keeps its own gain/bitrate after the copy",
        Math.Abs(fromProfile.InputGainDb - 15f) < 0.001f && fromProfile.AacBitrateKbps == fromUser.AacBitrateKbps, fromProfile.Describe());
    Check("record denoise: keep-raw comes from Audio Settings too", fromProfile.KeepRawAudio, fromProfile.Describe());

    var margs = RecordingDenoise.BuildMergeArgs("v.mp4", @"C:\rec\a.wav", @"C:\rec\out.mp4", 192, "highpass=f=100:poles=2");
    var mcmd = string.Join(" ", margs);
    Check("record merge args: video copied, audio aac 192k",
        mcmd.Contains("-c:v copy") && mcmd.Contains("-b:a 192k"), mcmd);
    Check("record merge args: recorded wav mapped as audio and filtered",
        mcmd.Contains("-map 0:v:0 -map 1:a:0") && mcmd.Contains("-af highpass=f=100:poles=2"), mcmd);
    Check("record merge args: no -af when nothing is enabled",
        !string.Join(" ", RecordingDenoise.BuildMergeArgs("v.mp4", "a.wav", "out.mp4", 192, "")).Contains("-af"), "");
    Check("record merge args: -shortest and output last",
        margs.Contains("-shortest") && margs[^1] == @"C:\rec\out.mp4", mcmd);
}

// ---------- 2. ffprobe ----------
MediaInfo info = svc.ProbeAsync(noisy).GetAwaiter().GetResult();
Check("probe: video+audio", info.IsValid && info.HasVideo && info.HasAudio, info.ToText());
Check("probe: resolution and duration", info.Width == 320 && info.Height == 240 && Math.Abs(info.DurationSec - 6) < 0.4, info.ToText());
Check("probe: missing file is not valid", !svc.ProbeAsync(P("nope.mp4")).GetAwaiter().GetResult().IsValid, "");

// ---------- 3. Прогон: только обрезка снизу (гул вентилятора) ----------
{
    var outFile = P("hp150.mp4");
    var s = new VideoProcessingSettings { InputPath = noisy, OutputPath = outFile, NoiseReduction = NoiseReductionSettings.CreateEmpty() };
    s.NoiseReduction.HighPassHz = 150;   // изолируем блок: дефолтный RNN здесь не нужен
    var r = svc.RunAsync(s, null).GetAwaiter().GetResult();
    Check("run hp150: success", r.Success, r.ToText() + " " + r.Error);
    Check("run hp150: file written", File.Exists(outFile) && new FileInfo(outFile).Length > 1000, "");

    var oi = svc.ProbeAsync(outFile).GetAwaiter().GetResult();
    Check("run hp150: video stream copied (h264, same size)",
        oi.HasVideo && oi.VideoCodec == info.VideoCodec && oi.Width == info.Width && oi.Height == info.Height, oi.ToText());
    Check("run hp150: duration kept", Math.Abs(oi.DurationSec - 6) < 0.5, $"{oi.DurationSec:0.00} s");

    var before = ReadWavMono(ExtractWav(noisy, "hp_before.wav"));
    var after = ReadWavMono(ExtractWav(outFile, "hp_after.wav"));
    double humB = ToneAmp(before, 48000, 50), humA = ToneAmp(after, 48000, 50);
    double voiceB = ToneAmp(before, 48000, 300), voiceA = ToneAmp(after, 48000, 300);
    Console.WriteLine($"      hum 50 Hz: {Db(humB):F1} -> {Db(humA):F1} dBFS ({Db(humA) - Db(humB):+0.0;-0.0} dB)");
    Console.WriteLine($"      voice 300 Hz: {Db(voiceB):F1} -> {Db(voiceA):F1} dBFS ({Db(voiceA) - Db(voiceB):+0.0;-0.0} dB)");
    Check("hp150: fan hum down by >= 12 dB", Db(humA) - Db(humB) <= -12, $"{Db(humA) - Db(humB):F1} dB");
    Check("hp150: voice kept within +-2 dB", Math.Abs(Db(voiceA) - Db(voiceB)) <= 2, $"{(Db(voiceA) - Db(voiceB)):F1} dB");
}

// ---------- 4. Прогон: hp150 + спектральное шумоподавление ----------
{
    var outFile = P("hp150_spectral.mp4");
    var s = new VideoProcessingSettings
    {
        InputPath = noisy,
        OutputPath = outFile,
        NoiseReduction = NoiseReductionSettings.CreateEmpty()

    };
    s.NoiseReduction.HighPassHz = 150; s.NoiseReduction.SpectralEnabled = true;
    s.NoiseReduction.NoiseFloorDb = -28; s.NoiseReduction.ReductionDb = 30; s.NoiseReduction.ResidualFloorDb = -80;
    var r = svc.RunAsync(s, null).GetAwaiter().GetResult();
    Check("run hp150+spectral: success", r.Success, r.ToText() + " " + r.Error);

    var beforeWav = ExtractWav(noisy, "sp_before.wav");
    var afterWav = ExtractWav(outFile, "sp_after.wav");
    var before = ReadWavMono(beforeWav);
    var after = ReadWavMono(afterWav);
    double humB = ToneAmp(before, 48000, 50), humA = ToneAmp(after, 48000, 50);
    double voiceB = ToneAmp(before, 48000, 300), voiceA = ToneAmp(after, 48000, 300);
    double dbB = AudioAnalyzer.AnalyzeWav(beforeWav).NoiseFloorDb;
    double dbA = AudioAnalyzer.AnalyzeWav(afterWav).NoiseFloorDb;
    Console.WriteLine($"      hum 50 Hz: {Db(humB):F1} -> {Db(humA):F1} dBFS ({Db(humA) - Db(humB):+0.0;-0.0} dB)");
    Console.WriteLine($"      voice 300 Hz: {Db(voiceB):F1} -> {Db(voiceA):F1} dBFS ({Db(voiceA) - Db(voiceB):+0.0;-0.0} dB)");
    Console.WriteLine($"      noise floor: {dbB:F1} -> {dbA:F1} dBFS ({dbA - dbB:+0.0;-0.0} dB)");
    Check("hp150+spectral: fan hum down by >= 12 dB", Db(humA) - Db(humB) <= -12, $"{(Db(humA) - Db(humB)):F1} dB");
    Check("hp150+spectral: voice kept within +-3 dB", Math.Abs(Db(voiceA) - Db(voiceB)) <= 3, $"{(Db(voiceA) - Db(voiceB)):F1} dB");
    Check("hp150+spectral: noise floor down by >= 3 dB", dbA - dbB <= -3, $"{dbA - dbB:F1} dB");
}

// ---------- 5. Спектральное шумоподавление на чистом шуме ----------
{
    var outFile = P("noise_spectral.mp4");
    var s = new VideoProcessingSettings
    {
        InputPath = noiseOnly,
        OutputPath = outFile,
        NoiseReduction = NoiseReductionSettings.CreateEmpty()

    };
    s.NoiseReduction.SpectralEnabled = true;
    s.NoiseReduction.NoiseFloorDb = -30; s.NoiseReduction.ReductionDb = 30; s.NoiseReduction.ResidualFloorDb = -80;
    var r = svc.RunAsync(s, null).GetAwaiter().GetResult();
    Check("run spectral on noise-only: success", r.Success, r.ToText() + " " + r.Error);
    var b = ReadWavMono(noiseOnly);
    var a = ReadWavMono(ExtractWav(outFile, "noise_after.wav"));
    Console.WriteLine($"      noise RMS: {Db(Rms(b)):F1} -> {Db(Rms(a)):F1} dBFS ({Db(Rms(a)) - Db(Rms(b)):+0.0;-0.0} dB)");
    Check("spectral on noise-only: >= 3 dB reduction", Db(Rms(a)) - Db(Rms(b)) <= -3, $"{Db(Rms(a)) - Db(Rms(b)):F1} dB");
}

// ---------- 6. Нейросетевое шумоподавление речи (arnndn + модель rnnoise) ----------
{
    var model = Path.Combine(AppContext.BaseDirectory, "models", "rnnoise", "bd.rnnn");
    if (!File.Exists(model))
    {
        Check("rnn: model file present next to the test", false, model);
    }
    else
    {
        var outFile = P("noise_rnn.mp4");
        var s = new VideoProcessingSettings
        {
            InputPath = noiseOnly,
            OutputPath = outFile,
            NoiseReduction = NoiseReductionSettings.CreateEmpty()

        };
        s.NoiseReduction.RnnEnabled = true;
        var r = svc.RunAsync(s, null).GetAwaiter().GetResult();
        Check("run rnn on noise-only: success", r.Success, r.ToText() + " " + r.Error);

        var b = ReadWavMono(noiseOnly);
        var a = ReadWavMono(ExtractWav(outFile, "rnn_after.wav"));
        double drop = Db(Rms(a)) - Db(Rms(b));
        Console.WriteLine($"      rnn on noise-only: {Db(Rms(b)):F1} -> {Db(Rms(a)):F1} dBFS ({drop:+0.0;-0.0} dB)");
        Check("rnn on noise-only: >= 6 dB reduction", drop <= -6, $"{drop:F1} dB");

        var outFile2 = P("mix_rnn.mp4");
        var s2 = new VideoProcessingSettings
        {
            InputPath = noisy,
            OutputPath = outFile2,
            NoiseReduction = NoiseReductionSettings.CreateDefault()
        };
        var r2 = svc.RunAsync(s2, null).GetAwaiter().GetResult();
        Check("run hp100+rnn on mixed: success", r2.Success, r2.ToText() + " " + r2.Error);
        var oi2 = svc.ProbeAsync(outFile2).GetAwaiter().GetResult();
        Check("run hp100+rnn: duration kept", Math.Abs(oi2.DurationSec - 6) < 0.5, $"{oi2.DurationSec:0.00} s");
    }
}

// ---------- 7. Остальные режимы просто должны отработать ----------
{
    var outFile = P("gate.mp4");
    var s = new VideoProcessingSettings
    {
        InputPath = noisy,
        OutputPath = outFile,
        NoiseReduction = NoiseReductionSettings.CreateEmpty()

    };
    s.NoiseReduction.GateEnabled = true;
    s.NoiseReduction.GateThresholdDb = -35;
    var r = svc.RunAsync(s, null).GetAwaiter().GetResult();
    Check("run gate: success", r.Success, r.ToText() + " " + r.Error);

    var outFile3 = P("norm.mp4");
    var s3 = new VideoProcessingSettings { InputPath = noisy, OutputPath = outFile3, NoiseReduction = NoiseReductionSettings.CreateEmpty() };
    s3.NoiseReduction.HighPassHz = 150;
    s3.GainDb = 3; s3.Level = LevelTarget.Loudness;
    s3.Equalizer.Enabled = true;
    if (EqualizerPresets.All.TryGetValue("Reduce rumble", out var g))
        for (int k = 0; k < EqualizerPresets.BandCount; k++) s3.Equalizer.BandGainsDb[k] = g[k];
    var r3 = svc.RunAsync(s3, null).GetAwaiter().GetResult();
    Check("run hp+eq+gain+loudnorm: success", r3.Success, r3.ToText() + " " + r3.Error);
    var a3 = ReadWavMono(ExtractWav(outFile3, "norm_after.wav"));
    double peak = a3.Max(Math.Abs);
    Console.WriteLine($"      loudnorm result peak: {Db(peak):F2} dBFS, RMS {Db(Rms(a3)):F1} dBFS");
    Check("loudnorm: no clipping (peak <= -1 dBFS)", Db(peak) <= -1.0, $"{Db(peak):F2} dBFS");
}

// ---------- 8. Предпросмотр (отрезок) ----------
{
    var outFile = P("preview.mp4");
    var s = new VideoProcessingSettings
    {
        InputPath = noisy, OutputPath = outFile, StartSeconds = 2, LimitSeconds = 2,
        NoiseReduction = NoiseReductionSettings.CreateEmpty()
    };
    s.NoiseReduction.HighPassHz = 150;
    var r = svc.RunAsync(s, null).GetAwaiter().GetResult();
    Check("run preview: success", r.Success, r.ToText() + " " + r.Error);
    var oi = svc.ProbeAsync(outFile).GetAwaiter().GetResult();
    Check("run preview: about 2 s long", Math.Abs(oi.DurationSec - 2) < 0.4, $"{oi.DurationSec:0.00} s");
}

// ---------- 9. Ошибки ----------
{
    var s = new VideoProcessingSettings { InputPath = P("missing.mp4"), OutputPath = P("x.mp4") };
    var r = svc.RunAsync(s, null).GetAwaiter().GetResult();
    Check("missing input: fails with a message", !r.Success && r.Error.Contains("не найден"), r.Error);

    var s2 = new VideoProcessingSettings { InputPath = noisy, OutputPath = noisy, NoiseReduction = NoiseReductionSettings.CreateDefault() };
    var r2 = svc.RunAsync(s2, null).GetAwaiter().GetResult();
    Check("same in/out: refused", !r2.Success, r2.Error);
    Check("same in/out: source untouched", new FileInfo(noisy).Length > 1000, "");
}

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
Console.WriteLine("files: " + work);
return fails == 0 ? 0 : 1;
