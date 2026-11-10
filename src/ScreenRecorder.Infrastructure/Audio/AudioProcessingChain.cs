using System;
using System.Collections.Generic;
using NAudio.Dsp;
using NAudio.Wave;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Infrastructure.Audio;

/// <summary>
/// Цепочка обработки: байты устройства → float → выбор каналов → усиление → фильтр НЧ → эквалайзер →
/// ресемплинг → квантование (16/24 бит PCM или 32 бит float) → приёмник. Не зависит от WASAPI,
/// поэтому используется и при записи, и при повторной обработке готового raw-файла.
/// Класс не потокобезопасен: вызывать из одного потока.
/// </summary>
public sealed class AudioProcessingChain
{
    private readonly WaveFormat _src;
    private readonly AudioCaptureSettings _s;
    private readonly int _srcCh, _outCh, _srcRate, _outRate, _blockAlign;
    private readonly float _gain;
    private readonly BiQuadFilter[][] _filters;
    private readonly StreamingResampler? _resampler;
    private readonly float[] _resBuf;

    private float[] _decoded = Array.Empty<float>();
    private float[] _mapped = Array.Empty<float>();
    private byte[] _outBytes = Array.Empty<byte>();

    private float _peak;
    private double _sumSq;
    private long _levelCount;
    private int _clipped;

    /// <summary>Формат, в котором эта цепочка отдаёт данные (стандартный WAV-заголовок).</summary>
    public WaveFormat OutputFormat { get; }

    public AudioProcessingChain(WaveFormat source, AudioCaptureSettings settings)
    {
        _src = source;
        _s = settings;
        _srcCh = source.Channels;
        _srcRate = source.SampleRate;
        _blockAlign = source.BlockAlign;

        _outCh = settings.ChannelMode switch
        {
            AudioChannelMode.Mono or AudioChannelMode.LeftOnly or AudioChannelMode.RightOnly => 1,
            AudioChannelMode.Stereo => 2,
            _ => _srcCh == 1 ? 1 : 2
        };
        _outRate = settings.OutputSampleRate > 0 ? settings.OutputSampleRate : _srcRate;

        OutputFormat = settings.OutputBitDepth switch
        {
            AudioBitDepth.Pcm16 => new WaveFormat(_outRate, 16, _outCh),
            AudioBitDepth.Pcm24 => new WaveFormat(_outRate, 24, _outCh),
            _ => WaveFormat.CreateIeeeFloatWaveFormat(_outRate, _outCh)
        };

        _gain = (float)Math.Pow(10.0, settings.InputGainDb / 20.0);

        // Фильтры считаются на частоте источника: полосы выше 0.45*Fs не применяем (иначе нестабильны).
        _filters = new BiQuadFilter[_outCh][];
        for (int c = 0; c < _outCh; c++)
        {
            var list = new List<BiQuadFilter>();
            if (settings.HighPassHz > 0 && settings.HighPassHz < _srcRate * 0.45f)
                list.Add(BiQuadFilter.HighPassFilter(_srcRate, settings.HighPassHz, 0.7071f));
            if (settings.Equalizer.Enabled)
            {
                for (int b = 0; b < EqualizerPresets.BandCount; b++)
                {
                    float gainDb = settings.Equalizer.BandGainsDb[b];
                    float f = EqualizerPresets.BandFrequencies[b];
                    if (Math.Abs(gainDb) >= 0.1f && f < _srcRate * 0.45f)
                        list.Add(BiQuadFilter.PeakingEQ(_srcRate, f, 1.0f, gainDb));
                }
            }
            _filters[c] = list.ToArray();
        }

        if (_outRate != _srcRate)
            _resampler = new StreamingResampler(_srcRate, _outRate, _outCh);

        _resBuf = new float[_outCh * 2048];
        ValidateSource();
    }

    private void ValidateSource()
    {
        // Бросаем исключение заранее, а не посреди записи.
        var probe = new byte[_blockAlign];
        var dst = new float[_srcCh];
        AudioFormatHelper.Decode(probe, _srcCh, _src, dst);
    }

    /// <summary>Обрабатывает порцию данных устройства и передаёт готовые байты приёмнику.</summary>
    public void Process(byte[] src, int length, Action<byte[], int> sink)
    {
        int frames = length / _blockAlign;
        if (frames <= 0) return;

        int srcSamples = frames * _srcCh;
        int outSamples = frames * _outCh;
        Ensure(ref _decoded, srcSamples);
        Ensure(ref _mapped, outSamples);

        AudioFormatHelper.Decode(src, srcSamples, _src, _decoded);
        MapChannels(frames);

        for (int c = 0; c < _outCh; c++)
        {
            var chain = _filters[c];
            for (int i = c; i < outSamples; i += _outCh)
            {
                float x = _mapped[i] * _gain;
                for (int k = 0; k < chain.Length; k++) x = chain[k].Transform(x);
                _mapped[i] = x;
            }
        }

        if (_resampler == null)
        {
            Emit(_mapped, outSamples, sink);
        }
        else
        {
            _resampler.Push(_mapped, outSamples);
            for (int guard = 0; guard < 4096; guard++)
            {
                int n = _resampler.Pull(_resBuf);
                if (n <= 0) break;
                Emit(_resBuf, n, sink);
            }
        }
    }

    /// <summary>Возвращает пик/RMS с прошлого вызова и сбрасывает счётчики.</summary>
    public AudioLevel TakeLevel()
    {
        float peakDb = _peak > 1e-6f ? 20f * MathF.Log10(_peak) : -120f;
        float rms = _levelCount > 0 ? (float)Math.Sqrt(_sumSq / _levelCount) : 0f;
        float rmsDb = rms > 1e-6f ? 20f * MathF.Log10(rms) : -120f;
        var level = new AudioLevel(peakDb, rmsDb, _clipped);
        _peak = 0; _sumSq = 0; _levelCount = 0; _clipped = 0;
        return level;
    }

    private void MapChannels(int frames)
    {
        int sc = _srcCh, oc = _outCh;
        var d = _decoded;
        var m = _mapped;
        var mode = _s.ChannelMode;
        for (int f = 0; f < frames; f++)
        {
            int si = f * sc, oi = f * oc;
            switch (mode)
            {
                case AudioChannelMode.Mono:
                {
                    float sum = 0;
                    for (int c = 0; c < sc; c++) sum += d[si + c];
                    m[oi] = sum / sc;
                    break;
                }
                case AudioChannelMode.LeftOnly:
                    m[oi] = d[si];
                    break;
                case AudioChannelMode.RightOnly:
                    m[oi] = d[si + (sc > 1 ? 1 : 0)];
                    break;
                default: // Auto, Stereo
                    if (oc == 1) m[oi] = d[si];
                    else { m[oi] = d[si]; m[oi + 1] = sc > 1 ? d[si + 1] : d[si]; }
                    break;
            }
        }
    }

    private void Emit(float[] data, int count, Action<byte[], int> sink)
    {
        for (int i = 0; i < count; i++)
        {
            float x = data[i];
            float a = Math.Abs(x);
            if (a >= 1f) { _clipped++; x = x > 0 ? 1f : -1f; a = 1f; data[i] = x; }
            if (a > _peak) _peak = a;
            _sumSq += (double)x * x;
        }
        _levelCount += count;

        switch (_s.OutputBitDepth)
        {
            case AudioBitDepth.Pcm16:
            {
                int bytes = count * 2;
                Ensure(ref _outBytes, bytes);
                int o = 0;
                var rnd = Random.Shared;
                for (int i = 0; i < count; i++)
                {
                    float v = data[i] * 32767f;
                    if (_s.Dither) v += (float)(rnd.NextDouble() - rnd.NextDouble()); // TPDF, ±1 LSB
                    int q = (int)MathF.Round(v);
                    if (q > 32767) q = 32767; else if (q < -32768) q = -32768;
                    _outBytes[o++] = (byte)q;
                    _outBytes[o++] = (byte)(q >> 8);
                }
                sink(_outBytes, bytes);
                break;
            }
            case AudioBitDepth.Pcm24:
            {
                int bytes = count * 3;
                Ensure(ref _outBytes, bytes);
                int o = 0;
                for (int i = 0; i < count; i++)
                {
                    int q = (int)MathF.Round(data[i] * 8388607f);
                    if (q > 8388607) q = 8388607; else if (q < -8388608) q = -8388608;
                    _outBytes[o++] = (byte)q;
                    _outBytes[o++] = (byte)(q >> 8);
                    _outBytes[o++] = (byte)(q >> 16);
                }
                sink(_outBytes, bytes);
                break;
            }
            default:
            {
                int bytes = count * 4;
                Ensure(ref _outBytes, bytes);
                Buffer.BlockCopy(data, 0, _outBytes, 0, bytes);
                sink(_outBytes, bytes);
                break;
            }
        }
    }

    private static void Ensure<T>(ref T[] arr, int size)
    {
        if (arr.Length < size) arr = new T[Math.Max(size, arr.Length * 2)];
    }
}
