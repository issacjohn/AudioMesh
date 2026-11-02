using System;
using System.Threading;

namespace AudioMesh.Audio;

/// <summary>A point on the EQ response graph (plain data, no UI types).</summary>
public readonly record struct ResponsePoint(double X, double Y);

/// <summary>
/// Per-channel equalizer: 10 graphic bands, bass/treble shelves and a loudness
/// contour that follows the listening volume. Coefficients are smoothed across
/// blocks so dragging a slider does not click.
/// </summary>
public sealed class Equalizer
{
    private readonly int _rate;
    private readonly Biquad[] _bands = new Biquad[EqSettings.BandCount];
    private readonly Biquad _bass = new();
    private readonly Biquad _treble = new();
    private readonly Biquad _loudLow = new();
    private readonly Biquad _loudHigh = new();

    private readonly double[] _targetBands = new double[EqSettings.BandCount];
    private readonly double[] _smoothBands = new double[EqSettings.BandCount];

    private EqSettings? _pending;

    private double _targetBass;
    private double _targetTreble;
    private double _targetLoudAmount;
    private bool _targetLoud;
    private double _smoothBass;
    private double _smoothTreble;
    private double _smoothLoudAmount;
    private bool _smoothLoud;

    private double _volume = 1;
    private double _preamp = 1;
    private bool _active;
    private bool _dirty = true;

    public Equalizer(int sampleRate)
    {
        _rate = sampleRate > 0 ? sampleRate : 48000;
        for (int i = 0; i < _bands.Length; i++) _bands[i] = new Biquad();
        SetSettings(new EqSettings());
    }

    /// <summary>Safe to call from the UI thread while audio is rendering.</summary>
    public void SetSettings(EqSettings settings)
    {
        EqSettings copy = settings.Clone();
        copy.Normalize();
        Volatile.Write(ref _pending, copy);
    }

    public void Process(float[] buffer, int frames, float volume)
    {
        if (frames <= 0) return;

        EqSettings? pending = Interlocked.Exchange(ref _pending, null);
        if (pending != null)
        {
            ApplyTargets(pending);
            _dirty = true;
        }

        double clampedVolume = Math.Clamp(volume, 0, 1.5);
        if (Math.Abs(clampedVolume - _volume) > 0.002)
        {
            _volume += (clampedVolume - _volume) * 0.25;
            _dirty = true;
        }

        for (int i = 0; i < _targetBands.Length; i++)
        {
            double delta = _targetBands[i] - _smoothBands[i];
            if (Math.Abs(delta) > 0.01)
            {
                _smoothBands[i] += delta * 0.25;
                _dirty = true;
            }
            else if (_smoothBands[i] != _targetBands[i])
            {
                _smoothBands[i] = _targetBands[i];
                _dirty = true;
            }
        }

        _dirty |= Smooth(ref _smoothBass, _targetBass);
        _dirty |= Smooth(ref _smoothTreble, _targetTreble);
        _dirty |= Smooth(ref _smoothLoudAmount, _targetLoudAmount);

        if (_smoothLoud != _targetLoud)
        {
            _smoothLoud = _targetLoud;
            _dirty = true;
        }

        if (_dirty) Rebuild();

        // Flat EQ: skip the whole filter chain. This is the common case and keeps the
        // render thread at a few percent even with several speakers.
        if (!_active) return;

        for (int i = 0; i < _bands.Length; i++)
            if (!_bands[i].IsBypass) _bands[i].Process(buffer, frames);

        if (!_bass.IsBypass) _bass.Process(buffer, frames);
        if (!_treble.IsBypass) _treble.Process(buffer, frames);
        if (_smoothLoud)
        {
            if (!_loudLow.IsBypass) _loudLow.Process(buffer, frames);
            if (!_loudHigh.IsBypass) _loudHigh.Process(buffer, frames);
        }

        if (Math.Abs(_preamp - 1.0) > 0.0001)
        {
            float gain = (float)_preamp;
            int total = frames * 2;
            for (int i = 0; i < total; i++) buffer[i] *= gain;
        }
    }

    private void ApplyTargets(EqSettings settings)
    {
        for (int i = 0; i < _targetBands.Length; i++)
            _targetBands[i] = settings.Bands[i];

        _targetBass = settings.Bass;
        _targetTreble = settings.Treble;
        _targetLoud = settings.Loudness;
        _targetLoudAmount = settings.LoudnessAmount;
    }

    private static bool Smooth(ref double current, double target)
    {
        double delta = target - current;
        if (Math.Abs(delta) <= 0.01) return false;
        current += delta * 0.25;
        return true;
    }

    private void Rebuild()
    {
        bool active = false;

        for (int i = 0; i < _bands.Length; i++)
        {
            double gain = _smoothBands[i];
            _bands[i].SetPeaking(_rate, EqSettings.Frequencies[i], 1.2, gain);
            if (Math.Abs(gain) > 0.001) active = true;
        }

        _bass.SetLowShelf(_rate, 120, _smoothBass);
        _treble.SetHighShelf(_rate, 8000, _smoothTreble);
        if (Math.Abs(_smoothBass) > 0.001 || Math.Abs(_smoothTreble) > 0.001) active = true;

        double loud = _smoothLoud
            ? _smoothLoudAmount / 100.0 * (1.0 - Math.Clamp(_volume, 0, 1))
            : 0;

        _loudLow.SetLowShelf(_rate, 90, loud * 10);
        _loudHigh.SetHighShelf(_rate, 10000, loud * 6);
        if (loud > 0.001) active = true;

        double maxBoost = 0;
        for (int i = 0; i < _smoothBands.Length; i++)
            if (_smoothBands[i] > maxBoost) maxBoost = _smoothBands[i];
        if (_smoothBass > maxBoost) maxBoost = _smoothBass;
        if (_smoothTreble > maxBoost) maxBoost = _smoothTreble;
        if (loud * 10 > maxBoost) maxBoost = loud * 10;

        _preamp = Math.Pow(10, -Math.Max(0, maxBoost) * 0.6 / 20.0);
        if (Math.Abs(_preamp - 1.0) > 0.0001) active = true;

        _active = active;
        _dirty = false;
    }

    /// <summary>Auto preamp in dB for the current settings (negative or zero).</summary>
    public static double PreampDb(EqSettings settings, double volume)
    {
        settings.Normalize();

        double maxBoost = 0;
        for (int i = 0; i < EqSettings.BandCount; i++)
            if (settings.Bands[i] > maxBoost) maxBoost = settings.Bands[i];

        if (settings.Bass > maxBoost) maxBoost = settings.Bass;
        if (settings.Treble > maxBoost) maxBoost = settings.Treble;

        if (settings.Loudness)
        {
            double loud = settings.LoudnessAmount / 100.0 * (1.0 - Math.Clamp(volume, 0, 1));
            if (loud * 10 > maxBoost) maxBoost = loud * 10;
        }

        return -Math.Max(0, maxBoost) * 0.6;
    }

    /// <summary>
    /// Frequency response points for the UI curve (20 Hz–20 kHz). Returns plain
    /// coordinates so the audio layer does not depend on WPF.
    /// </summary>
    public static ResponsePoint[] BuildCurve(EqSettings settings, int sampleRate,
                                             double width, double height, double volume,
                                             double rangeDb = 18)
    {
        settings.Normalize();
        int rate = sampleRate > 0 ? sampleRate : 48000;

        var filters = new Biquad[EqSettings.BandCount + 4];
        for (int i = 0; i < EqSettings.BandCount; i++)
        {
            filters[i] = new Biquad();
            filters[i].SetPeaking(rate, EqSettings.Frequencies[i], 1.2, settings.Bands[i]);
        }

        var bass = new Biquad();
        bass.SetLowShelf(rate, 120, settings.Bass);
        filters[EqSettings.BandCount] = bass;

        var treble = new Biquad();
        treble.SetHighShelf(rate, 8000, settings.Treble);
        filters[EqSettings.BandCount + 1] = treble;

        double loud = settings.Loudness
            ? settings.LoudnessAmount / 100.0 * (1.0 - Math.Clamp(volume, 0, 1))
            : 0;

        var loudLow = new Biquad();
        loudLow.SetLowShelf(rate, 90, loud * 10);
        filters[EqSettings.BandCount + 2] = loudLow;

        var loudHigh = new Biquad();
        loudHigh.SetHighShelf(rate, 10000, loud * 6);
        filters[EqSettings.BandCount + 3] = loudHigh;

        double preampDb = PreampDb(settings, volume);
        var points = new ResponsePoint[96];

        for (int i = 0; i < points.Length; i++)
        {
            double fraction = i / (double)(points.Length - 1);
            double frequency = 20.0 * Math.Pow(1000.0, fraction);
            double omega = 2 * Math.PI * frequency / rate;

            double db = preampDb;
            for (int f = 0; f < filters.Length; f++)
                db += filters[f].MagnitudeDb(omega);

            double x = fraction * width;
            double y = height / 2.0 - (db / rangeDb) * (height / 2.0);
            y = Math.Clamp(y, 0, height);
            points[i] = new ResponsePoint(x, y);
        }

        return points;
    }
}

internal sealed class Biquad
{
    private double _b0 = 1, _b1, _b2, _a1, _a2;
    private double _z1L, _z2L, _z1R, _z2R;

    public bool IsBypass => _b0 == 1 && _b1 == 0 && _b2 == 0 && _a1 == 0 && _a2 == 0;

    public void SetPeaking(double fs, double frequency, double q, double gainDb)
    {
        if (Math.Abs(gainDb) < 0.001)
        {
            Identity();
            return;
        }

        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * Math.Clamp(frequency, 10, fs / 2.05) / fs;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * q);

        double b0 = 1 + alpha * a;
        double b1 = -2 * cos;
        double b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a;
        double a1 = -2 * cos;
        double a2 = 1 - alpha / a;

        Set(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public void SetLowShelf(double fs, double frequency, double gainDb)
    {
        if (Math.Abs(gainDb) < 0.001)
        {
            Identity();
            return;
        }

        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * Math.Clamp(frequency, 10, fs / 2.05) / fs;
        double cos = Math.Cos(w0);
        double sin = Math.Sin(w0);
        double alpha = sin / 2 * Math.Sqrt(2);
        double k = 2 * Math.Sqrt(a) * alpha;

        double b0 = a * ((a + 1) - (a - 1) * cos + k);
        double b1 = 2 * a * ((a - 1) - (a + 1) * cos);
        double b2 = a * ((a + 1) - (a - 1) * cos - k);
        double a0 = (a + 1) + (a - 1) * cos + k;
        double a1 = -2 * ((a - 1) + (a + 1) * cos);
        double a2 = (a + 1) + (a - 1) * cos - k;

        Set(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public void SetHighShelf(double fs, double frequency, double gainDb)
    {
        if (Math.Abs(gainDb) < 0.001)
        {
            Identity();
            return;
        }

        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * Math.Clamp(frequency, 10, fs / 2.05) / fs;
        double cos = Math.Cos(w0);
        double sin = Math.Sin(w0);
        double alpha = sin / 2 * Math.Sqrt(2);
        double k = 2 * Math.Sqrt(a) * alpha;

        double b0 = a * ((a + 1) + (a - 1) * cos + k);
        double b1 = -2 * a * ((a - 1) + (a + 1) * cos);
        double b2 = a * ((a + 1) + (a - 1) * cos - k);
        double a0 = (a + 1) - (a - 1) * cos + k;
        double a1 = 2 * ((a - 1) - (a + 1) * cos);
        double a2 = (a + 1) - (a - 1) * cos - k;

        Set(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public void SetLowPass(double fs, double frequency, double q)
    {
        double w0 = 2 * Math.PI * Math.Clamp(frequency, 10, fs / 2.05) / fs;
        double cos = Math.Cos(w0);
        double sin = Math.Sin(w0);
        double alpha = sin / (2 * Math.Max(0.1, q));

        double b0 = (1 - cos) / 2;
        double b1 = 1 - cos;
        double b2 = (1 - cos) / 2;
        double a0 = 1 + alpha;
        double a1 = -2 * cos;
        double a2 = 1 - alpha;

        Set(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public void SetHighPass(double fs, double frequency, double q)
    {
        double w0 = 2 * Math.PI * Math.Clamp(frequency, 10, fs / 2.05) / fs;
        double cos = Math.Cos(w0);
        double sin = Math.Sin(w0);
        double alpha = sin / (2 * Math.Max(0.1, q));

        double b0 = (1 + cos) / 2;
        double b1 = -(1 + cos);
        double b2 = (1 + cos) / 2;
        double a0 = 1 + alpha;
        double a1 = -2 * cos;
        double a2 = 1 - alpha;

        Set(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>Clears the filter history without touching the coefficients.</summary>
    public void Reset()
    {
        _z1L = _z2L = _z1R = _z2R = 0;
    }

    /// <summary>Single-channel variant, used by the spatial widener on the side signal.</summary>
    public float ProcessSample(float x)
    {
        double y = _b0 * x + _z1L;
        _z1L = _b1 * x - _a1 * y + _z2L;
        _z2L = _b2 * x - _a2 * y;
        return (float)y;
    }

    private void Set(double b0, double b1, double b2, double a1, double a2)
    {
        _b0 = b0;
        _b1 = b1;
        _b2 = b2;
        _a1 = a1;
        _a2 = a2;
    }

    private void Identity()
    {
        _b0 = 1;
        _b1 = _b2 = _a1 = _a2 = 0;
        _z1L = _z2L = _z1R = _z2R = 0;
    }

    public void Process(float[] buffer, int frames)
    {
        double b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2;
        double z1L = _z1L, z2L = _z2L, z1R = _z1R, z2R = _z2R;

        for (int i = 0; i < frames; i++)
        {
            int index = i * 2;

            double x = buffer[index];
            double y = b0 * x + z1L;
            z1L = b1 * x - a1 * y + z2L;
            z2L = b2 * x - a2 * y;
            buffer[index] = (float)y;

            x = buffer[index + 1];
            y = b0 * x + z1R;
            z1R = b1 * x - a1 * y + z2R;
            z2R = b2 * x - a2 * y;
            buffer[index + 1] = (float)y;
        }

        _z1L = z1L;
        _z2L = z2L;
        _z1R = z1R;
        _z2R = z2R;
    }

    public double MagnitudeDb(double omega)
    {
        double cos = Math.Cos(omega);
        double sin = Math.Sin(omega);
        double cos2 = Math.Cos(2 * omega);
        double sin2 = Math.Sin(2 * omega);

        double numRe = _b0 + _b1 * cos + _b2 * cos2;
        double numIm = -_b1 * sin - _b2 * sin2;
        double denRe = 1 + _a1 * cos + _a2 * cos2;
        double denIm = -_a1 * sin - _a2 * sin2;

        double num = numRe * numRe + numIm * numIm;
        double den = denRe * denRe + denIm * denIm;
        if (den < 1e-12) return 0;

        return 10 * Math.Log10(Math.Max(1e-12, num / den));
    }
}
