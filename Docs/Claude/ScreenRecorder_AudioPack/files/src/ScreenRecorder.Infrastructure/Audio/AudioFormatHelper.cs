using System;
using NAudio.Wave;

namespace ScreenRecorder.Infrastructure.Audio;

/// <summary>Вспомогательные функции для работы с форматами и декодирования PCM/float в float[].</summary>
public static class AudioFormatHelper
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static bool IsFloat(WaveFormat f) =>
        f.Encoding == WaveFormatEncoding.IeeeFloat ||
        (f is WaveFormatExtensible ext && ext.SubFormat == SubtypeIeeeFloat);

    /// <summary>Заголовок WAV без WAVE_FORMAT_EXTENSIBLE (его понимают все плееры и NAudio-ридеры).</summary>
    public static WaveFormat ToStandard(WaveFormat f) =>
        IsFloat(f)
            ? WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.Channels)
            : new WaveFormat(f.SampleRate, f.BitsPerSample, f.Channels);

    public static string Describe(WaveFormat f) =>
        $"{f.SampleRate} Hz, {f.BitsPerSample}-bit {(IsFloat(f) ? "float" : "PCM")}, {f.Channels} ch";

    /// <summary>
    /// Преобразует байты в float [-1..1]. Выходной массив должен вмещать count сэмплов
    /// (count = число сэмплов всех каналов).
    /// </summary>
    public static void Decode(byte[] src, int sampleCount, WaveFormat f, float[] dst)
    {
        bool isFloat = IsFloat(f);
        int bits = f.BitsPerSample;
        if (isFloat)
        {
            if (bits == 32) { for (int i = 0; i < sampleCount; i++) dst[i] = BitConverter.ToSingle(src, i * 4); }
            else if (bits == 64) { for (int i = 0; i < sampleCount; i++) dst[i] = (float)BitConverter.ToDouble(src, i * 8); }
            else throw new NotSupportedException($"Unsupported float bit depth: {bits}");
            return;
        }
        switch (bits)
        {
            case 8:
                for (int i = 0; i < sampleCount; i++) dst[i] = (src[i] - 128) / 128f;
                break;
            case 16:
                for (int i = 0; i < sampleCount; i++) dst[i] = BitConverter.ToInt16(src, i * 2) / 32768f;
                break;
            case 24:
                for (int i = 0; i < sampleCount; i++)
                {
                    int o = i * 3;
                    int v = src[o] | (src[o + 1] << 8) | ((sbyte)src[o + 2] << 16);
                    dst[i] = v / 8388608f;
                }
                break;
            case 32:
                for (int i = 0; i < sampleCount; i++) dst[i] = BitConverter.ToInt32(src, i * 4) / 2147483648f;
                break;
            default:
                throw new NotSupportedException($"Unsupported PCM bit depth: {bits}");
        }
    }
}
