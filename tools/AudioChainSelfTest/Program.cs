// Самотест цепочки обработки звука (без устройств и без WPF): ресемплинг, EQ, фильтр, каналы, форматы, анализатор.
// Запуск: dotnet run --project tools/AudioChainSelfTest -c Release   (код возврата 0 = все проверки прошли)
using NAudio.Wave;
using ScreenRecorder.Core.Models;
using ScreenRecorder.Infrastructure.Audio;

int fails = 0;
void Check(string name, bool ok, string detail = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}"); if (!ok) fails++; }

static byte[] FloatBytes(float[] s) { var b = new byte[s.Length * 4]; Buffer.BlockCopy(s, 0, b, 0, b.Length); return b; }

static float[] Sine(int rate, int ch, double sec, double freq, double amp, Func<int,int,double>? chAmp = null)
{
    int n = (int)(rate * sec); var d = new float[n * ch];
    for (int i = 0; i < n; i++) for (int c = 0; c < ch; c++)
        d[i * ch + c] = (float)(amp * (chAmp?.Invoke(c, i) ?? 1.0) * Math.Sin(2 * Math.PI * freq * i / rate));
    return d;
}

// Прогон через цепочку; возвращает выходные float и формат.
static (float[] data, WaveFormat fmt, byte[] raw) Run(float[] input, int rate, int ch, AudioCaptureSettings s, int[] chunkFrames)
{
    var fmt = WaveFormat.CreateIeeeFloatWaveFormat(rate, ch);
    var chain = new AudioProcessingChain(fmt, s);
    var bytes = FloatBytes(input);
    var ms = new MemoryStream();
    int pos = 0, k = 0, frameBytes = fmt.BlockAlign;
    while (pos < bytes.Length)
    {
        int len = Math.Min(chunkFrames[k++ % chunkFrames.Length] * frameBytes, bytes.Length - pos);
        var chunk = new byte[len]; Buffer.BlockCopy(bytes, pos, chunk, 0, len);
        chain.Process(chunk, len, (b, n) => ms.Write(b, 0, n));
        pos += len;
    }
    var raw = ms.ToArray();
    var of = chain.OutputFormat;
    var outF = new float[raw.Length / (of.BitsPerSample / 8)];
    AudioFormatHelper.Decode(raw, outF.Length, of, outF);
    return (outF, of, raw);
}

static double Rms(float[] d, int from = 0) { double s = 0; int n = 0; for (int i = from; i < d.Length; i++) { s += d[i] * d[i]; n++; } return Math.Sqrt(s / Math.Max(1, n)); }
static double MaxJump(float[] d, int ch) { double m = 0; for (int i = ch; i < d.Length; i++) m = Math.Max(m, Math.Abs(d[i] - d[i - ch])); return m; }

var plain = new AudioCaptureSettings { Dither = false, OutputBitDepth = AudioBitDepth.Float32 };

// 1. Ресемплинг 48k stereo -> 16k mono, без щелчков, длина верная
{
    var s = plain.Clone(); s.OutputSampleRate = 16000; s.ChannelMode = AudioChannelMode.Mono;
    var inp = Sine(48000, 2, 5, 440, 0.5);
    var (o, f, _) = Run(inp, 48000, 2, s, new[] { 480 });
    double expectFrames = 5 * 16000;
    Check("48k->16k length", Math.Abs(o.Length - expectFrames) < 400, $"frames={o.Length} expected~{expectFrames}");
    double maxJ = MaxJump(o, 1), theory = 2 * Math.PI * 440 / 16000 * 0.5;
    Check("48k->16k no discontinuities", maxJ <= theory * 1.15, $"maxJump={maxJ:F4} theory={theory:F4}");
    Check("48k->16k amplitude", Math.Abs(Rms(o, 2000) - 0.5 / Math.Sqrt(2)) < 0.02, $"rms={Rms(o, 2000):F4}");
}
// 2. Результат не зависит от размера пакетов (в том числе нерегулярных)
{
    var s = plain.Clone(); s.OutputSampleRate = 16000; s.ChannelMode = AudioChannelMode.Mono;
    var inp = Sine(48000, 2, 3, 700, 0.4);
    var (a, _, ra) = Run(inp, 48000, 2, s, new[] { 480 });
    var (b, _, rb) = Run(inp, 48000, 2, s, new[] { 37, 1000, 480, 101, 4800, 7 });
    int n = Math.Min(a.Length, b.Length); double md = 0; for (int i = 0; i < n; i++) md = Math.Max(md, Math.Abs(a[i] - b[i]));
    Check("chunk-size independent", md < 1e-4 && Math.Abs(a.Length - b.Length) < 300, $"maxDiff={md:E2} len {a.Length} vs {b.Length}");
}
// 3. Апсемплинг 16k -> 48k и 44.1k -> 48k
foreach (var (inRate, freq) in new[] { (16000, 300.0), (44100, 1000.0), (8000, 200.0) })
{
    var s = plain.Clone(); s.OutputSampleRate = 48000;
    var inp = Sine(inRate, 1, 3, freq, 0.5);
    var (o, _, _) = Run(inp, inRate, 1, s, new[] { 160 });
    double maxJ = MaxJump(o, 1), theory = 2 * Math.PI * freq / 48000 * 0.5;
    Check($"{inRate}->48k continuity", maxJ <= theory * 1.2 && Math.Abs(o.Length - 3 * 48000) < 1000, $"maxJump={maxJ:F4} theory={theory:F4} len={o.Length}");
}
{
    var s = plain.Clone(); s.OutputSampleRate = 48000;
    var inp = Sine(16000, 1, 3, 300, 0.5);
    var (a, _, _) = Run(inp, 16000, 1, s, new[] { 160 });
    var (b, _, _) = Run(inp, 16000, 1, s, new[] { 5, 1600, 33, 160 });
    int n = Math.Min(a.Length, b.Length); double md = 0; for (int i = 0; i < n; i++) md = Math.Max(md, Math.Abs(a[i] - b[i]));
    Check("upsample chunk-size independent", md < 1e-4, $"maxDiff={md:E2}");
}
// 4. Усиление +6 dB
{
    var s = plain.Clone(); s.InputGainDb = 6;
    var (o, _, _) = Run(Sine(48000, 1, 1, 1000, 0.2), 48000, 1, s, new[] { 480 });
    Check("gain +6 dB", Math.Abs(Rms(o) / (0.2 / Math.Sqrt(2)) - 1.995) < 0.02, $"ratio={Rms(o) / (0.2 / Math.Sqrt(2)):F3}");
}
// 5. Эквалайзер: полоса 1 кГц +6 dB
{
    var s = plain.Clone(); s.Equalizer.Enabled = true; s.Equalizer.BandGainsDb[5] = 6;
    var (o, _, _) = Run(Sine(48000, 1, 1, 1000, 0.2), 48000, 1, s, new[] { 480 });
    double ratio = Rms(o, 4800) / (0.2 / Math.Sqrt(2));
    var s2 = plain.Clone(); s2.Equalizer.Enabled = true; s2.Equalizer.BandGainsDb[5] = 6;
    var (o2, _, _) = Run(Sine(48000, 1, 1, 8000, 0.2), 48000, 1, s2, new[] { 480 });
    double ratioFar = Rms(o2, 4800) / (0.2 / Math.Sqrt(2));
    Check("EQ 1k +6 dB boosts 1 kHz", Math.Abs(ratio - 1.995) < 0.1, $"ratio={ratio:F3}");
    Check("EQ 1k band leaves 8 kHz alone", Math.Abs(ratioFar - 1.0) < 0.1, $"ratio={ratioFar:F3}");
}
// 6. Фильтр НЧ 200 Гц гасит 50 Гц и не трогает 1 кГц
{
    var s = plain.Clone(); s.HighPassHz = 200;
    var (lo, _, _) = Run(Sine(48000, 1, 1, 50, 0.5), 48000, 1, s, new[] { 480 });
    var (hi, _, _) = Run(Sine(48000, 1, 1, 1000, 0.5), 48000, 1, s, new[] { 480 });
    Check("HPF attenuates 50 Hz", Rms(lo, 4800) < 0.5 / Math.Sqrt(2) * 0.1, $"rms={Rms(lo, 4800):F4}");
    Check("HPF passes 1 kHz", Math.Abs(Rms(hi, 4800) / (0.5 / Math.Sqrt(2)) - 1) < 0.05, $"ratio={Rms(hi, 4800) / (0.5 / Math.Sqrt(2)):F3}");
}
// 7. Режимы каналов (L = синус, R = тишина)
{
    var inp = Sine(48000, 2, 1, 1000, 0.5, (c, i) => c == 0 ? 1.0 : 0.0);
    foreach (var (mode, expectCh, expectRms) in new[] { (AudioChannelMode.LeftOnly, 1, 0.3536), (AudioChannelMode.RightOnly, 1, 0.0), (AudioChannelMode.Mono, 1, 0.1768), (AudioChannelMode.Stereo, 2, 0.3536), (AudioChannelMode.Auto, 2, 0.3536) })
    {
        var s = plain.Clone(); s.ChannelMode = mode;
        var (o, f, _) = Run(inp, 48000, 2, s, new[] { 480 });
        double r = Rms(o);
        if (mode == AudioChannelMode.Stereo || mode == AudioChannelMode.Auto) { var l = o.Where((x, i) => i % 2 == 0).ToArray(); r = Rms(l); }
        Check($"channel mode {mode}", f.Channels == expectCh && Math.Abs(r - expectRms) < 0.01, $"ch={f.Channels} rms={r:F4}");
    }
    var s1 = plain.Clone(); s1.ChannelMode = AudioChannelMode.Stereo;
    var (dup, df, _) = Run(Sine(48000, 1, 1, 1000, 0.5), 48000, 1, s1, new[] { 480 });
    Check("mono source -> stereo duplicates", df.Channels == 2 && Math.Abs(dup[100] - dup[101]) < 1e-6);
}
// 8. Форматы вывода + анализатор
{
    foreach (var depth in new[] { AudioBitDepth.Pcm16, AudioBitDepth.Pcm24, AudioBitDepth.Float32 })
    {
        var s = new AudioCaptureSettings { OutputBitDepth = depth, OutputSampleRate = 16000, ChannelMode = AudioChannelMode.Mono };
        var (o, f, raw) = Run(Sine(48000, 2, 4, 500, 0.4), 48000, 2, s, new[] { 480 });
        string path = Path.Combine(Path.GetTempPath(), $"sr_selftest_{depth}.wav");
        using (var w = new WaveFileWriter(path, f)) w.Write(raw, 0, raw.Length);
        var a = AudioAnalyzer.AnalyzeWav(path);
        Check($"output {depth} analyze clean", a.Clicks == 0 && a.Dropouts == 0 && a.SampleRate == 16000 && Math.Abs(a.DurationSec - 4) < 0.05,
            $"clicks={a.Clicks} dropouts={a.Dropouts} rate={a.SampleRate} dur={a.DurationSec:F2} rms={a.RmsDb:F1}dB");
    }
}
// 9. Анализатор находит дефекты
{
    var f = new WaveFormat(48000, 16, 1);
    short[] Make(Action<short[]> mut) { var d = new short[48000 * 4]; for (int i = 0; i < d.Length; i++) d[i] = (short)(0.3 * 32767 * Math.Sin(2 * Math.PI * 300 * i / 48000) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 3 * i / 48000)) + (Random.Shared.NextDouble() - .5) * 20); mut(d); return d; }
    AudioAnalysis An(short[] d, string name) { var p = Path.Combine(Path.GetTempPath(), $"sr_selftest_{name}.wav"); using (var w = new WaveFileWriter(p, f)) { var b = new byte[d.Length * 2]; Buffer.BlockCopy(d, 0, b, 0, b.Length); w.Write(b, 0, b.Length); } return AudioAnalyzer.AnalyzeWav(p); }
    var clean = An(Make(_ => { }), "clean");
    var gap = An(Make(d => { for (int i = 96000; i < 96000 + 480; i++) d[i] = 0; }), "gap");
    var click = An(Make(d => { for (int k = 1; k <= 8; k++) { int p = k * 20000; d[p] = (short)(d[p] > 0 ? -20000 : 20000); } }), "click");
    var clip = An(Make(d => { for (int i = 50000; i < 50400; i++) d[i] = 32767; }), "clip");
    Check("analyzer: clean has no defects", clean.Clicks == 0 && clean.Dropouts == 0, $"clicks={clean.Clicks} dropouts={clean.Dropouts} {clean.Verdict}");
    Check("analyzer: finds 10 ms zero gap", gap.Dropouts == 1, $"dropouts={gap.Dropouts}");
    Check("analyzer: finds clicks", click.Clicks >= 6, $"clicks={click.Clicks} perMin={click.ClicksPerMinute:F0}");
    Check("analyzer: finds clipping", clip.ClippedSamples >= 300, $"clipped={clip.ClippedSamples}");
}
// 10. Settings: Normalize / Clone / битый JSON
{
    var bad = new AudioCaptureSettings { BufferMs = -5, DriverSampleRate = 47000, DriverChannels = 9, InputGainDb = float.NaN, HighPassHz = 5, AacBitrateKbps = 1, OutputSampleRate = 5 };
    bad.Equalizer = new EqualizerSettings { BandGainsDb = new float[] { 99, -99, float.NaN } };
    bad.Normalize();
    Check("settings normalize", bad.BufferMs == 10 && bad.DriverSampleRate == 48000 && bad.DriverChannels == 2 && bad.InputGainDb == 0 && bad.HighPassHz == 20 && bad.AacBitrateKbps == 64 && bad.OutputSampleRate == 8000 && bad.Equalizer.BandGainsDb.Length == 10 && bad.Equalizer.BandGainsDb[0] == 12 && bad.Equalizer.BandGainsDb[1] == -12 && bad.Equalizer.BandGainsDb[2] == 0);
    var json = System.Text.Json.JsonSerializer.Serialize(new AudioCaptureSettings { Api = AudioCaptureApi.WasapiPolling });
    var back = System.Text.Json.JsonSerializer.Deserialize<AudioCaptureSettings>(json)!;
    Check("settings json roundtrip", back.Api == AudioCaptureApi.WasapiPolling && back.Equalizer.BandGainsDb.Length == 10);
    var c = back.Clone(); c.Equalizer.BandGainsDb[0] = 5; Check("clone is deep", back.Equalizer.BandGainsDb[0] == 0);
}
Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAILED");
return fails == 0 ? 0 : 1;
