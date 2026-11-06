using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Timer = System.Threading.Timer;

namespace AudioMesh.Audio;

public enum SourceKind
{
    SystemAudio,
    TestTone
}

/// <summary>
/// Owns the fan-out. One capture source is duplicated into every enabled
/// <see cref="DeviceChannel"/>; each channel then runs at its own device clock.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int ToneRate = 48000;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<string, DeviceChannel> _channels = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly object _toneLock = new();

    private float[] _pushBuffer = new float[8192];
    private readonly float[] _toneBuffer = new float[ToneRate / 100 * 2];

    private WasapiLoopbackCapture? _capture;
    private MMDevice? _captureDevice;
    private SampleKind _captureKind;
    private int _captureChannels;
    private Timer? _toneTimer;
    private Timer? _idleTimer;
    private long _toneFrames;
    private long _lastPushTimestamp;
    private bool _captureRunning;
    private bool _monoOutput;
    private float[] _silence = new float[9600];

    public bool IsRunning { get; private set; }
    public SourceKind Source { get; private set; } = SourceKind.SystemAudio;

    /// <summary>Downmixes the source to mono before fan-out, so every endpoint hears the same signal.</summary>
    public bool MonoOutput
    {
        get => _monoOutput;
        set
        {
            _monoOutput = value;

            // Let every open channel know: rear/side roles would otherwise be silent
            // on a mono stream, and the spatial widener has nothing to widen.
            lock (_gate)
            {
                foreach (DeviceChannel channel in _channels.Values)
                    channel.MonoInput = value;
            }
        }
    }

    public string? LastError { get; private set; }

    /// <summary>Optional diagnostic sink (console harness / troubleshooting).</summary>
    public static Action<string>? Diagnostic { get; set; }

    public IReadOnlyList<DeviceChannel> Channels
    {
        get { lock (_gate) return _channels.Values.ToList(); }
    }

    public int CurrentInputRate =>
        Source == SourceKind.TestTone
            ? ToneRate
            : _capture?.WaveFormat.SampleRate ?? 48000;

    public DeviceChannel EnableDevice(MMDevice device, bool isBluetooth)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(device.ID, out DeviceChannel? existing))
                return existing;

            var channel = new DeviceChannel(device, isBluetooth);
            channel.MonoInput = _monoOutput;
            _channels[device.ID] = channel;

            if (IsRunning)
            {
                channel.SetInputRate(CurrentInputRate);
                channel.Start();
            }

            return channel;
        }
    }

    public void DisableDevice(string id)
    {
        DeviceChannel? channel;
        lock (_gate)
        {
            if (!_channels.Remove(id, out channel)) return;
        }

        channel.Stop();
        channel.Dispose();
    }

    public void SetSource(SourceKind kind)
    {
        if (Source == kind) return;
        Source = kind;
        if (!IsRunning) return;

        StopSource();
        StartSource();
        ApplyInputRate();
    }

    public void Start()
    {
        if (IsRunning) return;

        StartSource();

        IsRunning = true;
        ApplyInputRate();

        foreach (DeviceChannel channel in Channels)
            channel.Start();
    }

    public void Stop()
    {
        if (!IsRunning) return;

        IsRunning = false;
        StopSource();

        foreach (DeviceChannel channel in Channels)
            channel.Stop();
    }

    /// <summary>Re-creates the loopback capture if the default device changed or capture died.</summary>
    public void EnsureSource()
    {
        if (!IsRunning || Source != SourceKind.SystemAudio) return;

        bool defaultChanged = false;
        try
        {
            using MMDevice current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultChanged = _captureDevice == null || current.ID != _captureDevice.ID;
        }
        catch
        {
            // no default device right now; keep the existing capture alive
        }

        if (_captureRunning && !defaultChanged) return;

        try
        {
            StopSource();
            StartSource();
            ApplyInputRate();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Endpoint currently being captured (null when running the test tone).</summary>
    public string? CaptureDeviceId => _captureDevice?.ID;

    private void ApplyInputRate()
    {
        int rate = CurrentInputRate;
        foreach (DeviceChannel channel in Channels)
            channel.SetInputRate(rate);
    }

    private void StartSource()
    {
        LastError = null;
        if (Source == SourceKind.SystemAudio) StartLoopback();
        else StartTone();
    }

    private void StartLoopback()
    {
        MMDevice device;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("No default playback device is available.", ex);
        }

        var capture = CreateLoopback(device);
        WaveFormat format = capture.WaveFormat;
        if (!FormatWriter.TryDetect(format, out SampleKind kind, out int channels))
            throw new InvalidOperationException(
                $"Unsupported capture format on '{device.FriendlyName}': {format}");

        capture.DataAvailable += OnCaptureData;
        capture.RecordingStopped += OnCaptureStopped;

        _capture = capture;
        _captureDevice = device;
        _captureKind = kind;
        _captureChannels = channels;

        capture.StartRecording();
        _captureRunning = true;

        _lastPushTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _idleTimer = new Timer(OnIdleTick, null, 250, 50);
    }

    private void StartTone()
    {
        _toneFrames = 0;
        _toneTimer = new Timer(OnToneTick, null, 0, 10);
    }

    /// <summary>
    /// WASAPI loopback does not expose its capture buffer size publicly. Set NAudio's
    /// private buffer field before recording starts, using the current buffer mode; if
    /// that ever stops working, the app simply keeps the default 100 ms block.
    /// </summary>
    private static WasapiLoopbackCapture CreateLoopback(MMDevice device)
    {
        var capture = new WasapiLoopbackCapture(device);

        try
        {
            FieldInfo? field = typeof(WasapiCapture).GetField(
                "audioBufferMillisecondsLength",
                BindingFlags.Instance | BindingFlags.NonPublic);

            field?.SetValue(capture, AudioLatency.CaptureBufferMs);
        }
        catch
        {
            // fall back to NAudio's default buffer
        }

        return capture;
    }

    private void StopSource()
    {
        var timer = _toneTimer;
        _toneTimer = null;
        timer?.Dispose();

        var idle = _idleTimer;
        _idleTimer = null;
        idle?.Dispose();

        var capture = _capture;
        _capture = null;
        _captureRunning = false;

        if (capture != null)
        {
            capture.DataAvailable -= OnCaptureData;
            capture.RecordingStopped -= OnCaptureStopped;
            try { capture.StopRecording(); } catch { /* ignore */ }
            try { capture.Dispose(); } catch { /* ignore */ }
        }

        try { _captureDevice?.Dispose(); } catch { /* ignore */ }
        _captureDevice = null;
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        int neededFloats = e.BytesRecorded; // worst case: 16-bit mono expands 1 byte -> 1 float
        if (_pushBuffer.Length < neededFloats) _pushBuffer = new float[neededFloats];

        int frames = FormatReader.ToStereoFloat(
            e.Buffer, e.BytesRecorded, _pushBuffer, _captureKind, _captureChannels);

        Push(_pushBuffer, frames);
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        _captureRunning = false;
        if (e.Exception != null) LastError = e.Exception.Message;
    }

    /// <summary>
    /// Loopback capture stops delivering packets while the endpoint is idle (nothing is
    /// playing), which drains every elastic buffer. After a genuinely long gap — well
    /// beyond normal packet jitter — top the rings up with silence so the next track
    /// starts from a full buffer. The 200 ms threshold is what keeps the pump away from
    /// live audio; short gaps are left entirely to the capture and the drift loop.
    /// </summary>
    private void OnIdleTick(object? state)
    {
        if (!IsRunning || Source != SourceKind.SystemAudio || !_captureRunning) return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastPushTimestamp);
        double idleMs = (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (idleMs < 200) return;

        int rate = CurrentInputRate;
        if (rate <= 0) return;

        int maxFrames = (int)(idleMs * rate / 1000.0);
        maxFrames = Math.Clamp(maxFrames, 0, rate / 10);
        if (maxFrames <= 0) return;

        if (_silence.Length < maxFrames * 2) _silence = new float[maxFrames * 2];
        ReadOnlySpan<float> silence = _silence.AsSpan(0, maxFrames * 2);

        lock (_gate)
        {
            foreach (DeviceChannel channel in _channels.Values)
            {
                channel.CaptureIdle = true;

                int target = channel.TargetBufferMs * rate / 1000;
                int deficit = target - channel.Ring.CountFrames;
                if (deficit <= 0) continue;

                int frames = Math.Min(deficit, maxFrames);
                channel.Ring.Write(silence[..(frames * 2)], frames);
            }
        }
    }

    private void OnToneTick(object? state)
    {
        lock (_toneLock)
        {
            if (_toneTimer == null) return;

            int frames = ToneRate / 100;

            for (int i = 0; i < frames; i++)
            {
                double t = (double)_toneFrames / ToneRate;

                // steady 1 kHz reference tone plus a click every two seconds,
                // so you can hear when two devices are out of alignment.
                // The right tone is phase-shifted 90° so the side/rear and subwoofer
                // test roles get real signal instead of a silent difference channel.
                double left = 0.10 * Math.Sin(2 * Math.PI * 1000.0 * t);
                double right = 0.10 * Math.Sin(2 * Math.PI * 1000.0 * t + Math.PI / 2);
                double cycle = t % 2.0;
                if (cycle < 0.02)
                {
                    double click = 0.45 * Math.Sin(2 * Math.PI * 2200.0 * t) * (1.0 - cycle / 0.02);
                    left += click;
                    right += click;
                }

                _toneBuffer[i * 2] = (float)left;
                _toneBuffer[i * 2 + 1] = (float)right;
                _toneFrames++;
            }

            Push(_toneBuffer, frames);
        }
    }

    private void Push(float[] buffer, int frames)
    {
        if (frames <= 0) return;

        Volatile.Write(ref _lastPushTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

        var span = buffer.AsSpan(0, frames * 2);

        if (MonoOutput)
        {
            for (int i = 0; i < span.Length; i += 2)
            {
                float m = 0.5f * (span[i] + span[i + 1]);
                span[i] = m;
                span[i + 1] = m;
            }
        }

        lock (_gate)
        {
            foreach (DeviceChannel channel in _channels.Values)
            {
                channel.CaptureIdle = false;
                channel.Ring.Write(span, frames);
            }
        }
    }

    public void Dispose()
    {
        Stop();

        foreach (DeviceChannel channel in Channels)
            channel.Dispose();

        lock (_gate) _channels.Clear();

        try { _enumerator.Dispose(); } catch { /* ignore */ }
    }
}
