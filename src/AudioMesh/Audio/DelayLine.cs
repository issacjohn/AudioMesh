using System;

namespace AudioMesh.Audio;

/// <summary>
/// Per-device delay line used to align a fast output with a slow one (Bluetooth is
/// typically 150-300 ms behind a wired DAC).
///
/// The read offset is moved toward the target at one frame per frame, which is a
/// 1/48000 rate change - completely inaudible - so dragging the slider never clicks.
/// </summary>
public sealed class DelayLine
{
    private const int Channels = 2;

    private readonly float[] _buf;
    private readonly int _maxFrames;
    private int _write;
    private int _current;
    private int _target;

    private int _rate;

    public DelayLine(int maxMilliseconds, int sampleRate)
    {
        _rate = Math.Max(8000, sampleRate);
        _maxFrames = Math.Max(64, maxMilliseconds * _rate / 1000 + 8);
        _buf = new float[_maxFrames * Channels];
    }

    public int CurrentMs => _current * 1000 / Math.Max(1, _rate);
    public int TargetMs => _target * 1000 / Math.Max(1, _rate);

    public int MaxMilliseconds => _maxFrames * 1000 / Math.Max(1, _rate);

    public void SetRate(int sampleRate)
    {
        if (sampleRate > 0) _rate = sampleRate;
    }

    public int TargetMilliseconds
    {
        get => _target * 1000 / Math.Max(1, _rate);
        set
        {
            int frames = value * _rate / 1000;
            _target = Math.Clamp(frames, 0, _maxFrames - 2);
        }
    }

    public void Reset()
    {
        Array.Clear(_buf);
        _write = 0;
        _current = 0;
    }

    public void Process(float[] data, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            int src = f * Channels;
            int w = _write * Channels;

            _buf[w] = data[src];
            _buf[w + 1] = data[src + 1];

            if (_current < _target) _current++;
            else if (_current > _target) _current--;

            int r = ((_write - _current) % _maxFrames + _maxFrames) % _maxFrames;
            int ri = r * Channels;

            data[src] = _buf[ri];
            data[src + 1] = _buf[ri + 1];

            _write = (_write + 1) % _maxFrames;
        }
    }
}
