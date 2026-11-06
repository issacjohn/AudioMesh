using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMesh.Audio;

/// <summary>
/// One physical output endpoint. Owns its elastic buffer, drift-correcting resampler,
/// alignment delay and a WASAPI render client that pulls from <see cref="ChannelProvider"/>.
/// </summary>
public sealed class DeviceChannel : IDisposable
{
    private readonly object _gate = new();
    private readonly int _targetBufferMs;
    private readonly Equalizer _eq;
    private readonly SurroundProcessor _surround = new();
    private readonly Spatializer _spatial;

    private WasapiOut? _out;
    private float _gain;
    private float _peak;
    private double _ratio = 1.0;
    private double _fillEma = -1;
    private int _inputRate = 48000;
    private int _delayMs;
    private float _volume = 1f;
    private bool _muted;
    private bool _disposed;
    private long _underruns;
    private int _warmupBlocks;
    private float[] _zeros = Array.Empty<float>();
    private bool _spatialEnabled;
    private double _spatialAmount = 65;

    public DeviceChannel(MMDevice device, bool isBluetooth)
    {
        Device = device;
        Id = device.ID;
        Name = device.FriendlyName;
        IsBluetooth = isBluetooth;

        WaveFormat mix = device.AudioClient.MixFormat;

        if (FormatWriter.TryDetect(mix, out SampleKind kind, out int channels))
        {
            OutFormat = mix;
            Kind = kind;
            OutChannels = channels;
        }
        else
        {
            // Unknown layout: let WASAPI convert from plain IEEE float.
            OutFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, 2);
            Kind = SampleKind.Float32;
            OutChannels = 2;
        }

        OutputRate = OutFormat.SampleRate;
        _targetBufferMs = isBluetooth ? AudioLatency.BluetoothBufferMs : AudioLatency.WiredBufferMs;

        Ring = new FloatRingBuffer(2, OutputRate / 2);
        Resampler = new SincResampler(2, _inputRate, OutputRate);
        Delay = new DelayLine(2000, OutputRate);
        Delay.TargetMilliseconds = 0;
        _delayMs = Delay.TargetMilliseconds;
        _eq = new Equalizer(OutputRate);
        _spatial = new Spatializer(OutputRate);
    }

    public MMDevice Device { get; }
    public string Id { get; }
    public string Name { get; }
    public bool IsBluetooth { get; }

    public WaveFormat OutFormat { get; }
    public int OutputRate { get; }
    public int OutChannels { get; }
    public SampleKind Kind { get; }

    public FloatRingBuffer Ring { get; }
    public SincResampler Resampler { get; }
    public DelayLine Delay { get; }

    public int TargetBufferMs => _targetBufferMs;
    public int InputRate => _inputRate;
    public double FillMs { get; private set; }
    public double DriftPpm { get; private set; }
    public long Underruns => Interlocked.Read(ref _underruns);
    public long DroppedFrames => Ring.DroppedFrames;

    public string? LastError { get; private set; }

    public bool IsRunning
    {
        get
        {
            var o = _out;
            if (o == null) return false;
            try { return o.PlaybackState == PlaybackState.Playing; }
            catch { return false; }
        }
    }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1.5f);
    }

    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    public int DelayMs
    {
        get => _delayMs;
        set
        {
            _delayMs = Math.Clamp(value, 0, 1000);
            Delay.TargetMilliseconds = _delayMs;
        }
    }

    /// <summary>Replaces this endpoint's equalizer settings.</summary>
    public void SetEq(EqSettings settings) => _eq.SetSettings(settings);

    /// <summary>How this endpoint renders the shared stereo stream.</summary>
    public ChannelMode Channel { get; set; } = ChannelMode.Stereo;

    /// <summary>Virtual surround width for this speaker (on top of a plain stereo role).</summary>
    public bool SpatialEnabled
    {
        get => _spatialEnabled;
        set => _spatialEnabled = value;
    }

    /// <summary>Surround width, 0–100 %.</summary>
    public double SpatialAmount
    {
        get => _spatialAmount;
        set => _spatialAmount = Math.Clamp(value, 0, 100);
    }

    /// <summary>Set while the global mono switch is on, so rear roles fall back to mono.</summary>
    public bool MonoInput { get; set; }

    /// <summary>
    /// True while the engine is filling the ring with silence because the capture source
    /// has been idle. The drift loop should not chase that sawtooth.
    /// </summary>
    public bool CaptureIdle { get; set; }

    public void SetInputRate(int rate)
    {
        if (rate <= 0 || rate == _inputRate) return;
        _inputRate = rate;
        Resampler.SetRates(rate, OutputRate);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _out != null) return;

            try
            {
                if (Ring.CountFrames < _targetBufferMs * _inputRate / 1000) Prefill();
                _warmupBlocks = 60;

                _gain = 0f;
                _ratio = 1.0;
                _fillEma = -1;

                var output = new WasapiOut(Device, AudioClientShareMode.Shared, true, AudioLatency.RenderBufferMs);
                output.PlaybackStopped += OnPlaybackStopped;
                output.Init(new ChannelProvider(this));
                output.Play();

                _out = output;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            var output = _out;
            if (output == null) return;
            _out = null;

            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Stop(); } catch { /* device vanished */ }
            try { output.Dispose(); } catch { /* ignore */ }

            Ring.Clear();
        }
    }

    private void Prefill()
    {
        int target = _targetBufferMs * _inputRate / 1000;
        int missing = target - Ring.CountFrames;
        if (missing <= 0) return;
        Ring.Write(new float[missing * 2], missing);
    }

    private void WarmUp()
    {
        int target = _targetBufferMs * _inputRate / 1000;
        int missing = target - Ring.CountFrames;

        if (missing > 0)
        {
            if (_zeros.Length < missing * 2) _zeros = new float[missing * 2];
            Ring.Write(_zeros.AsSpan(0, missing * 2), missing);
        }
        else
        {
            _warmupBlocks--;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) LastError = e.Exception.Message;
    }

    /// <summary>Called on the WASAPI render thread for every block.</summary>
    public void Render(float[] dst, int frames)
    {
        // While the device is still filling its first buffers, keep the elastic buffer
        // topped up with silence. The output is silence at this point anyway, so this
        // is free - and it avoids a 20 second slow climb to the target level.
        if (_warmupBlocks > 0) WarmUp();

        double fillMs = Ring.CountFrames * 1000.0 / _inputRate;
        FillMs = fillMs;

        // The producer delivers in bursts while we consume in blocks, so the raw level
        // sawtooths. Regulate on a smoothed average or the loop hunts.
        _fillEma = _fillEma < 0 ? fillMs : _fillEma + (fillMs - _fillEma) * 0.03;

        // Proportional drift control: too full -> consume input faster, too empty -> slower.
        // Widen the range while the buffer is far from target so lock is quick, then trim gently.
        // During a genuine input gap the engine holds the ring at target with silence;
        // chasing that sawtooth would bias the resampler, so ease back to unity instead.
        if (CaptureIdle)
        {
            _ratio += (1.0 - _ratio) * 0.01;
        }
        else
        {
            double error = (_fillEma - _targetBufferMs) / _targetBufferMs;
            double limit = Math.Abs(error) > 0.25 ? 0.010 : 0.004;
            double desired = 1.0 + Math.Clamp(error * 0.5, -limit, limit);
            _ratio += (desired - _ratio) * 0.02;
        }

        Resampler.Ratio = _ratio;
        DriftPpm = (_ratio - 1.0) * 1e6;

        Resampler.Feed(Ring, frames);
        int produced = Resampler.Generate(dst, frames);

        if (produced < frames)
        {
            Array.Clear(dst, produced * 2, (frames - produced) * 2);

            // Do not report startup underruns: the output is still silence while the
            // elastic buffer is filling, so they are not audible playback dropouts.
            if (produced == 0 && _warmupBlocks <= 0) Interlocked.Increment(ref _underruns);
        }

        Delay.Process(dst, frames);

        // With global mono on, the rear/side channels would be silent, so those roles
        // fall back to the full mono mix. The subwoofer still keeps its low-pass.
        ChannelMode mode = Channel;
        if (MonoInput && mode.IsSurroundPosition() && mode != ChannelMode.Subwoofer)
            mode = ChannelMode.Mono;

        if (mode.IsSurroundPosition())
            _surround.Process(mode, OutputRate, dst, frames);
        else
            ChannelMix.Apply(mode, dst, frames);

        if (!MonoInput && mode == ChannelMode.Stereo && (_spatialEnabled || _spatial.IsActive))
            _spatial.Process(dst, frames, _spatialEnabled ? (float)(_spatialAmount / 100.0) : 0f);

        _eq.Process(dst, frames, _volume);

        float targetGain = _muted ? 0f : _volume;
        float step = (targetGain - _gain) * 0.002f;
        float gain = _gain;
        float peak = 0f;

        int total = frames * 2;
        for (int i = 0; i < total; i++)
        {
            gain += step;
            float v = dst[i] * gain;
            dst[i] = v;
            float a = v < 0 ? -v : v;
            if (a > peak) peak = a;
        }

        _gain = gain;
        _peak = peak;
    }

    public float TakePeak()
    {
        float p = _peak;
        _peak = 0f;
        return p;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

internal sealed class ChannelProvider : IWaveProvider
{
    private readonly DeviceChannel _channel;
    private float[] _scratch = Array.Empty<float>();

    public ChannelProvider(DeviceChannel channel)
    {
        _channel = channel;
        WaveFormat = channel.OutFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        int frames = count / WaveFormat.BlockAlign;
        if (frames <= 0) return 0;

        if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];

        _channel.Render(_scratch, frames);
        FormatWriter.Write(_scratch, frames, buffer, offset, _channel.OutChannels, _channel.Kind);

        return frames * WaveFormat.BlockAlign;
    }
}
