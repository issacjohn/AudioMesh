using System;

namespace AudioMesh.Audio;

/// <summary>
/// Virtual surround / width for a single speaker. The side signal is high-passed so
/// bass stays centred, then widened and mixed with a short delayed reflection. The
/// mono sum keeps the direct sound in time, so this does not break speaker sync.
/// </summary>
public sealed class Spatializer
{
    private const double SideHighPassHz = 130;
    private const double ReflectionMs = 11;
    private const float ReflectionLevel = 0.20f;

    private readonly int _rate;
    private readonly Biquad _sideHighPass = new();
    private readonly float[] _reflection;
    private int _write;
    private float _current;

    public Spatializer(int rate)
    {
        _rate = rate > 0 ? rate : 48000;
        _sideHighPass.SetHighPass(_rate, SideHighPassHz, 0.707);
        int samples = Math.Max(1, (int)Math.Round(_rate * ReflectionMs / 1000.0));
        _reflection = new float[samples];
    }

    /// <summary>True while the effect is still fading out after being switched off.</summary>
    public bool IsActive => _current > 0.0005f;

    public void Process(float[] buffer, int frames, float target)
    {
        if (frames <= 0) return;

        target = Math.Clamp(target, 0f, 1f);

        // Glide over roughly 25 ms, so toggling or dragging the slider does not click.
        float blend = Math.Min(1f, frames / (_rate * 0.025f));
        _current += (target - _current) * blend;

        if (_current < 0.0005f)
        {
            if (target <= 0f)
            {
                Array.Clear(_reflection);
                _sideHighPass.Reset();
                _write = 0;
                _current = 0f;
            }
            return;
        }

        float sideGain = 1f + _current * 0.85f;
        float outGain = 1f / (1f + _current * 0.30f);
        float reflection = ReflectionLevel * _current;
        int length = _reflection.Length;
        int write = _write;

        for (int f = 0; f < frames; f++)
        {
            int i = f * 2;
            float mid = 0.5f * (buffer[i] + buffer[i + 1]);
            float side = _sideHighPass.ProcessSample(0.5f * (buffer[i] - buffer[i + 1]));

            float delayed = _reflection[write];
            _reflection[write] = side;

            write++;
            if (write == length) write = 0;

            float spatial = side * sideGain + delayed * reflection;
            buffer[i] = Math.Clamp((mid + spatial) * outGain, -1f, 1f);
            buffer[i + 1] = Math.Clamp((mid - spatial) * outGain, -1f, 1f);
        }

        _write = write;
    }
}
