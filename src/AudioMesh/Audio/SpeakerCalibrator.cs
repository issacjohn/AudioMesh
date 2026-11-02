using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMesh.Audio;

public sealed record CalibrationTarget(string Id, string Name, bool IsBluetooth);

public sealed record CalibrationResult(
    string Id, string Name, double ArrivalMs, int DelayMs, float EndpointPeak);

/// <summary>
/// Measures the acoustic delay of each speaker with the default microphone and turns
/// it into per-device alignment delays.
///
/// Each speaker is driven on its own through a temporary <see cref="DeviceChannel"/>,
/// so the measurement includes the real pipeline buffer and the Bluetooth link latency —
/// the parts the app cannot see from software alone. A short tone burst is injected into
/// the ring, the microphone records it, and the first threshold crossing gives the
/// arrival time. The speaker that arrives last becomes the reference; every other speaker
/// is delayed by the difference.
/// </summary>
public sealed class SpeakerCalibrator
{
    private const int Repeats = 5;
    private const int SettleMs = 900;
    private const int PreRollMs = 350;
    private const int RecordMs = 1600;

    public IReadOnlyList<CalibrationResult> Measure(
        IReadOnlyList<CalibrationTarget> targets,
        string? microphoneId,
        Action<string>? progress = null)
    {
        if (targets.Count < 1)
            throw new InvalidOperationException("No speakers selected.");

        using var enumerator = new MMDeviceEnumerator();

        MMDevice? mic = null;
        if (!string.IsNullOrWhiteSpace(microphoneId))
        {
            try
            {
                mic = enumerator.GetDevice(microphoneId);
            }
            catch
            {
                mic = null;
            }
        }

        if (mic == null)
        {
            try
            {
                mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "No microphone found. Connect a microphone or allow microphone access in Windows settings.", ex);
            }
        }

        using (mic)
        {
            var arrivals = new Dictionary<string, double>(StringComparer.Ordinal);
            var peaks = new Dictionary<string, float>(StringComparer.Ordinal);

            foreach (CalibrationTarget target in targets)
            {
                progress?.Invoke($"Measuring {Short(target.Name)}…");

                var samples = new List<double>();
                float endpointPeak = 0;

                using (MMDevice device = enumerator.GetDevice(target.Id))
                {
                    for (int repeat = 0; repeat < Repeats; repeat++)
                    {
                        (double arrival, float peak) = MeasureOnce(mic, device, target.IsBluetooth);
                        if (arrival > 0) samples.Add(arrival);
                        if (peak > endpointPeak) endpointPeak = peak;
                        Thread.Sleep(120);
                    }
                }

                peaks[target.Id] = endpointPeak;

                if (samples.Count == 0)
                {
                    progress?.Invoke($"{Short(target.Name)} was not heard");
                    continue;
                }

                samples.Sort();
                arrivals[target.Id] = samples[samples.Count / 2];
            }

            if (arrivals.Count == 0)
                throw new InvalidOperationException(
                    "No speaker was heard. Check that they are powered on and the microphone is not muted.");

            double latest = arrivals.Values.Max();

            return targets.Select(target =>
            {
                float peak = peaks.TryGetValue(target.Id, out float p) ? p : 0f;

                if (!arrivals.TryGetValue(target.Id, out double arrival))
                    return new CalibrationResult(target.Id, target.Name, double.NaN, -1, peak);

                int delay = (int)Math.Round(latest - arrival);
                delay = Math.Clamp(delay, 0, 500);
                return new CalibrationResult(target.Id, target.Name, arrival, delay, peak);
            }).ToList();
        }
    }

    private static (double Arrival, float Peak) MeasureOnce(MMDevice mic, MMDevice device, bool isBluetooth)
    {
        using var channel = new DeviceChannel(device, isBluetooth)
        {
            Volume = 1f,
            DelayMs = 0
        };

        // Drive the click at the endpoint's own rate: no resampling in the measurement.
        channel.SetInputRate(channel.OutputRate);
        channel.Start();

        if (!channel.IsRunning)
            throw new InvalidOperationException(
                $"Could not open {Short(channel.Name)}: {channel.LastError ?? "unknown error"}");

        // Let the Bluetooth link and the elastic buffer settle, then wake a speaker that
        // may have dropped into standby. The wake tone finishes before recording starts.
        Thread.Sleep(SettleMs);
        InjectTone(channel, 250, 1000, 0.35);
        Thread.Sleep(450);

        var recorded = new List<float>();
        var gate = new object();
        int micRate = 48000;

        try
        {
            using var capture = new WasapiCapture(mic);
            WaveFormat format = capture.WaveFormat;
            micRate = format.SampleRate;

            if (!FormatWriter.TryDetect(format, out SampleKind kind, out int channels))
                throw new InvalidOperationException($"Unsupported microphone format: {format}");

            float[] scratch = new float[format.SampleRate];

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                if (scratch.Length < e.BytesRecorded * 2)
                    scratch = new float[e.BytesRecorded * 2];

                int frames = FormatReader.ToStereoFloat(e.Buffer, e.BytesRecorded, scratch, kind, channels);
                lock (gate)
                {
                    for (int i = 0; i < frames; i++)
                        recorded.Add(0.5f * (scratch[i * 2] + scratch[i * 2 + 1]));
                }
            };

            capture.StartRecording();
            Thread.Sleep(PreRollMs);
            InjectTone(channel, 120, 2500, 0.5);

            // Read the endpoint's own meter while the probe plays. If this is loud but
            // the mic heard nothing, the problem is acoustic / TWS / microphone, not routing.
            float endpointPeak = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < RecordMs)
            {
                try
                {
                    float peak = device.AudioMeterInformation.MasterPeakValue;
                    if (peak > endpointPeak) endpointPeak = peak;
                }
                catch
                {
                    // meter unavailable
                }

                Thread.Sleep(30);
            }

            capture.StopRecording();

            float[] snapshot;
            lock (gate) snapshot = recorded.ToArray();

            return (DetectOnset(snapshot, micRate), endpointPeak);
        }
        finally
        {
            channel.Stop();
        }
    }

    private static void InjectTone(DeviceChannel channel, int milliseconds, double frequency, double amplitude)
    {
        int rate = channel.InputRate;
        int frames = Math.Max(64, rate * milliseconds / 1000);
        int fade = Math.Max(1, rate / 125); // 8 ms edges
        var buffer = new float[frames * 2];

        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            double edge = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (double)fade);
            float value = (float)(amplitude * edge * Math.Sin(2 * Math.PI * frequency * t));
            buffer[i * 2] = value;
            buffer[i * 2 + 1] = value;
        }

        channel.Ring.Write(buffer, frames);
    }

    /// <summary>Returns the first threshold crossing in milliseconds, or 0 when nothing was heard.</summary>
    private static double DetectOnset(IReadOnlyList<float> data, int rate)
    {
        if (data.Count == 0) return 0;

        // Band-pass around the probe tone. The microphone's broadband noise otherwise
        // masks a quiet speaker; at 2.5 kHz the tone stands out much further.
        float[] filtered = BandPass(data, rate, 2500, 2.0);

        int hop = Math.Max(1, rate / 1000); // 1 ms windows
        int windows = filtered.Length / hop;
        if (windows < 50) return 0;

        var envelope = new double[windows];
        for (int w = 0; w < windows; w++)
        {
            double sum = 0;
            int start = w * hop;
            int end = Math.Min(start + hop, filtered.Length);
            for (int i = start; i < end; i++) sum += Math.Abs(filtered[i]);
            envelope[w] = sum / hop;
        }

        int noiseStart = 50;
        int noiseEnd = Math.Min(windows, 220);
        double noise = 0;
        for (int w = noiseStart; w < noiseEnd; w++) noise += envelope[w];
        noise = noiseEnd > noiseStart ? noise / (noiseEnd - noiseStart) : 0.0001;

        double threshold = Math.Max(noise * 6.0, 0.003);

        // The probe is injected 350 ms into the recording. Never accept anything
        // earlier: a delayed tail from a previous probe/wake tone used to be mistaken
        // for the real onset and produced absurd delays.
        for (int w = 380; w < windows - 4; w++)
        {
            if (envelope[w] < threshold) continue;

            int above = 0;
            for (int k = 0; k < 4; k++)
                if (envelope[w + k] >= threshold * 0.5) above++;

            if (above >= 3) return w;
        }

        return 0;
    }

    private static string Short(string name)
    {
        int open = name.IndexOf('(');
        int close = name.LastIndexOf(')');
        if (open >= 0 && close > open) return name[(open + 1)..close].Trim();
        return name;
    }

    private static float[] BandPass(IReadOnlyList<float> input, int rate, double frequency, double q)
    {
        var output = new float[input.Count];
        if (input.Count == 0) return output;

        double w0 = 2 * Math.PI * frequency / rate;
        double alpha = Math.Sin(w0) / (2 * q);

        double a0 = 1 + alpha;
        double b0 = alpha / a0;
        double b2 = -alpha / a0;
        double a1 = (-2 * Math.Cos(w0)) / a0;
        double a2 = (1 - alpha) / a0;

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;

        for (int i = 0; i < input.Count; i++)
        {
            double x0 = input[i];
            double y0 = b0 * x0 + b2 * x2 - a1 * y1 - a2 * y2;
            output[i] = (float)y0;
            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;
        }

        return output;
    }
}
