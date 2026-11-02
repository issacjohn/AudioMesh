using System;

namespace AudioMesh.Audio;

/// <summary>
/// Band-limited sinc resampler with a continuously variable ratio.
///
/// This is the heart of the drift correction: every output device pulls samples at
/// its own DAC clock, so the ratio is nudged by a few hundred ppm to keep the
/// elastic buffer at a fixed fill level. Without it, two devices that both claim
/// 48 kHz drift apart and one of them eventually stutters.
/// </summary>
public sealed class SincResampler
{
    private const int Taps = 32;
    private const int Phases = 256;
    private const int Half = Taps / 2;
    private const int HistoryCapacity = 16384;

    private readonly int _ch;
    private readonly float[] _coef = new float[Phases * Taps];
    private readonly float[] _hist = new float[HistoryCapacity * 2];
    private int _histFrames;
    private double _pos;
    private double _baseStep = 1.0;
    private double _cutoff = -1;

    public SincResampler(int channels, int inputRate, int outputRate)
    {
        _ch = channels;
        SetRates(inputRate, outputRate);
        _pos = Half;
    }

    /// <summary>Extra rate multiplier on top of inputRate/outputRate (1.0 = no drift correction).</summary>
    public double Ratio { get; set; } = 1.0;

    public int InputRate { get; private set; }
    public int OutputRate { get; private set; }

    public void SetRates(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) return;

        InputRate = inputRate;
        OutputRate = outputRate;
        _baseStep = (double)inputRate / outputRate;

        double cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.94;
        if (Math.Abs(cutoff - _cutoff) > 1e-4)
        {
            _cutoff = cutoff;
            BuildTable(cutoff);
        }
    }

    private void BuildTable(double cutoff)
    {
        for (int p = 0; p < Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;

            for (int k = 0; k < Taps; k++)
            {
                double d = k - Half + 1;
                double t = d - frac;

                double s = Math.Abs(t) < 1e-9
                    ? 1.0
                    : Math.Sin(Math.PI * cutoff * t) / (Math.PI * cutoff * t);

                double w = 0.42
                           + 0.5 * Math.Cos(Math.PI * t / Half)
                           + 0.08 * Math.Cos(2 * Math.PI * t / Half);

                double v = s * w;
                _coef[p * Taps + k] = (float)v;
                sum += v;
            }

            if (Math.Abs(sum) > 1e-9)
                for (int k = 0; k < Taps; k++)
                    _coef[p * Taps + k] = (float)(_coef[p * Taps + k] / sum);
        }
    }

    /// <summary>
    /// Pulls only what the next <paramref name="outputFrames"/> need. Everything else
    /// stays in the ring - that ring IS the elastic buffer the drift controller
    /// regulates, so draining it here would break the control loop.
    /// </summary>
    public void Feed(FloatRingBuffer ring, int outputFrames)
    {
        int drop = (int)_pos - Half;
        if (drop > 0)
        {
            int keep = _histFrames - drop;
            if (keep < 0) keep = 0;
            if (keep > 0) Array.Copy(_hist, drop * _ch, _hist, 0, keep * _ch);
            _histFrames = keep;
            _pos -= drop;
        }

        double step = _baseStep * Ratio;
        int needed = (int)Math.Ceiling(outputFrames * step) + Taps + 8;
        int missing = needed - _histFrames;
        if (missing <= 0) return;

        int space = HistoryCapacity - _histFrames;
        if (space <= 0) return;

        _histFrames += ring.Read(_hist, _histFrames, Math.Min(missing, space));
    }

    /// <summary>
    /// Produces up to <paramref name="frames"/> output frames into <paramref name="dst"/>.
    /// Returns how many were actually produced; the caller fills the rest with silence.
    /// </summary>
    public int Generate(float[] dst, int frames)
    {
        if (_histFrames < Taps) return 0;

        int produced = 0;
        double step = _baseStep * Ratio;

        while (produced < frames)
        {
            int i = (int)_pos;
            if (i + Half >= _histFrames) break;
            if (i - Half + 1 < 0)
            {
                _pos = Half;
                continue;
            }

            double frac = _pos - i;
            int phase = (int)(frac * Phases);
            if (phase >= Phases) phase = Phases - 1;

            int co = phase * Taps;
            int baseIdx = i - Half + 1;

            float l = 0f;
            float r = 0f;

            for (int k = 0; k < Taps; k++)
            {
                float w = _coef[co + k];
                int idx = (baseIdx + k) * _ch;
                l += w * _hist[idx];
                r += w * _hist[idx + 1];
            }

            int o = produced * _ch;
            dst[o] = l;
            dst[o + 1] = r;
            produced++;
            _pos += step;
        }

        return produced;
    }
}
