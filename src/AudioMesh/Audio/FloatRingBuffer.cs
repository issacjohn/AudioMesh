using System;

namespace AudioMesh.Audio;

/// <summary>
/// Interleaved float sample ring buffer, single producer / single consumer.
/// Overflow drops the oldest frames so the pipeline never blocks the audio thread.
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _buf;
    private readonly int _frames;
    private readonly int _ch;
    private readonly object _sync = new();

    private int _read;
    private int _write;
    private int _count;
    private long _dropped;

    public FloatRingBuffer(int channels, int capacityFrames)
    {
        _ch = Math.Max(1, channels);
        _frames = Math.Max(64, capacityFrames);
        _buf = new float[_frames * _ch];
    }

    public int Channels => _ch;
    public int CapacityFrames => _frames;

    public int CountFrames
    {
        get { lock (_sync) return _count; }
    }

    public long DroppedFrames => Interlocked.Read(ref _dropped);

    public void Write(ReadOnlySpan<float> source, int frames)
    {
        if (frames <= 0) return;

        lock (_sync)
        {
            if (frames > _frames)
            {
                source = source.Slice((frames - _frames) * _ch);
                frames = _frames;
            }

            int overflow = _count + frames - _frames;
            if (overflow > 0)
            {
                _read = (_read + overflow) % _frames;
                _count -= overflow;
                Interlocked.Add(ref _dropped, overflow);
            }

            int first = Math.Min(frames, _frames - _write);
            source[..(first * _ch)].CopyTo(_buf.AsSpan(_write * _ch));
            if (frames > first)
                source[(first * _ch)..].CopyTo(_buf);

            _write = (_write + frames) % _frames;
            _count += frames;
        }
    }

    /// <summary>Reads up to <paramref name="maxFrames"/> frames into <paramref name="dest"/>.</summary>
    public int Read(float[] dest, int destFrameOffset, int maxFrames)
    {
        if (maxFrames <= 0) return 0;

        lock (_sync)
        {
            int n = Math.Min(maxFrames, _count);
            if (n <= 0) return 0;

            int first = Math.Min(n, _frames - _read);
            Array.Copy(_buf, _read * _ch, dest, destFrameOffset * _ch, first * _ch);
            if (n > first)
                Array.Copy(_buf, 0, dest, (destFrameOffset + first) * _ch, (n - first) * _ch);

            _read = (_read + n) % _frames;
            _count -= n;
            return n;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _read = 0;
            _write = 0;
            _count = 0;
            Array.Clear(_buf);
        }
    }
}
