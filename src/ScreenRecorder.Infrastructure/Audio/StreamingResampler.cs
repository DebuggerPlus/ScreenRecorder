using System;
using NAudio.Dsp;

namespace ScreenRecorder.Infrastructure.Audio;

/// <summary>
/// Потоковый ресемплер на базе WDL (NAudio). В отличие от WdlResamplingSampleProvider никогда не
/// вызывает ресемплер с неполным входом: при нехватке данных WDL дополняет вход нулями, и в потоке
/// появляются разрывы (щелчки). Здесь вход копится во внутренней очереди, а блок обрабатывается
/// только когда накоплено ровно столько, сколько запросил ResamplePrepare.
/// </summary>
internal sealed class StreamingResampler
{
    private const int BlockFrames = 128;

    private readonly WdlResampler _wdl = new();
    private readonly int _ch;
    private readonly float[] _block;
    private float[] _fifo = new float[1 << 15];
    private int _head;
    private int _count; // в сэмплах (кадры * каналы)

    public StreamingResampler(int inRate, int outRate, int channels)
    {
        _ch = channels;
        _block = new float[BlockFrames * channels];
        _wdl.SetMode(true, 2, false);
        _wdl.SetFilterParms();
        _wdl.SetFeedMode(false); // управление по выходу
        _wdl.SetRates(inRate, outRate);
    }

    public void Push(float[] data, int sampleCount)
    {
        if (_head + _count + sampleCount > _fifo.Length)
        {
            if (_head > 0)
            {
                Array.Copy(_fifo, _head, _fifo, 0, _count);
                _head = 0;
            }
            if (_count + sampleCount > _fifo.Length)
                Array.Resize(ref _fifo, Math.Max(_fifo.Length * 2, _count + sampleCount));
        }
        Array.Copy(data, 0, _fifo, _head + _count, sampleCount);
        _count += sampleCount;
    }

    /// <summary>Выдаёт столько выходных кадров, сколько позволяет накопленный вход. Возвращает число сэмплов.</summary>
    public int Pull(float[] output)
    {
        int outFrames = 0;
        int maxFrames = output.Length / _ch;
        while (outFrames + BlockFrames <= maxFrames)
        {
            int need = _wdl.ResamplePrepare(BlockFrames, _ch, out float[] inBuf, out int inOff);
            if (_count < need * _ch) break; // недостаточно входа: ждём следующий пакет
            if (need > 0)
            {
                Array.Copy(_fifo, _head, inBuf, inOff, need * _ch);
                _head += need * _ch;
                _count -= need * _ch;
                if (_count == 0) _head = 0;
            }
            // Важно: ResampleOut всегда получает нулевое смещение выходного буфера. При апсемплинге WDL
            // применяет выходной IIR-фильтр с начала буфера, игнорируя смещение, и блоки со смещением
            // оставались бы нефильтрованными (скачки на границах блоков).
            int got = _wdl.ResampleOut(_block, 0, need, BlockFrames, _ch);
            if (got <= 0 && need == 0) break;
            Array.Copy(_block, 0, output, outFrames * _ch, got * _ch);
            outFrames += got;
        }
        return outFrames * _ch;
    }
}
