using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using AudioMesh.Audio;
using AudioMesh.Services;
using AudioMesh.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioMesh.SmokeTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("== AudioMesh engine smoke test ==");

        if (args.Length > 0 && args[0].Equals("meters", StringComparison.OrdinalIgnoreCase))
            return Meters(args.Length > 1 ? args[1] : null);

        if (args.Length > 0 && args[0].Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            using var en = new MMDeviceEnumerator();
            using var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Console.WriteLine($"default = {def.FriendlyName}");
            Console.WriteLine($"id      = {def.ID}");
            return 0;
        }

        if (args.Length > 0 && args[0].Equals("channels", StringComparison.OrdinalIgnoreCase))
            return Channels(args.Length > 1 ? args[1] : null, args.Length > 2 &&
                            args[2].Equals("live", StringComparison.OrdinalIgnoreCase));

        if (args.Length > 0 && args[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
            return Watch(args.Length > 1 ? args[1] : null);

        if (args.Length > 0 && args[0].Equals("soak", StringComparison.OrdinalIgnoreCase))
            return Soak(args.Length > 1 ? args[1] : null,
                        args.Length > 2 && int.TryParse(args[2], out int soakSeconds) ? soakSeconds : 60);

        if (args.Length > 0 && args[0].Equals("eq", StringComparison.OrdinalIgnoreCase))
            return EqTest();

        if (args.Length > 0 && args[0].Equals("order", StringComparison.OrdinalIgnoreCase))
            return OrderTest();

        string? filter = args.Length > 0 ? args[0] : null;
        int maxDevices = args.Length > 1 && int.TryParse(args[1], out int m) ? m : 3;

        using var enumerator = new MMDeviceEnumerator();
        List<EndpointInfo> endpoints;

        try
        {
            endpoints = EndpointCatalog.Enumerate(enumerator);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: cannot enumerate endpoints: " + ex.Message);
            return 2;
        }

        Console.WriteLine($"Found {endpoints.Count} active render endpoint(s):");
        foreach (EndpointInfo e in endpoints)
            Console.WriteLine($"  - {e.Name}  [{e.Kind}]  {e.Rate / 1000.0:0.#} kHz  {e.Channels} ch" +
                              (e.IsDefault ? "  (default)" : string.Empty) +
                              $"\n      id={e.Id}");

        if (endpoints.Count == 0)
        {
            Console.WriteLine("No endpoints - nothing to route. UI will still start.");
            return 0;
        }

        using var engine = new AudioEngine();
        var enabled = new List<DeviceChannel>();

        var targets = filter == null
            ? endpoints
            : endpoints.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        Console.WriteLine(filter == null
            ? $"Routing to first {maxDevices} endpoint(s)."
            : $"Filter '{filter}' matched {targets.Count} endpoint(s).");

        foreach (EndpointInfo info in targets.Take(maxDevices))
        {
            try
            {
                enabled.Add(engine.EnableDevice(info.Device, info.IsBluetooth));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ! could not open {info.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Opened {enabled.Count} channel(s).");

        bool solo = args.Length > 2 && args[2].Equals("solo", StringComparison.OrdinalIgnoreCase);

        if (solo)
        {
            engine.SetSource(SourceKind.TestTone);
            engine.Start();

            foreach (EndpointInfo info in targets.Take(maxDevices))
            {
                Console.WriteLine($"### SOLO: {info.Name}  (listen 6 s)");
                DeviceChannel ch = engine.EnableDevice(info.Device, info.IsBluetooth);
                Thread.Sleep(6000);
                Console.WriteLine($"    fill={ch.FillMs:0.0} ms  drift={ch.DriftPpm:+0;-0;0} ppm  " +
                                  $"under={ch.Underruns}  run={ch.IsRunning}  err={ch.LastError ?? "-"}");
                engine.DisableDevice(info.Id);
                Thread.Sleep(1200);
            }

            engine.Stop();
            foreach (EndpointInfo e in endpoints) e.Dispose();
            Console.WriteLine("SOLO sweep done.");
            return 0;
        }

        engine.SetSource(SourceKind.TestTone);

        try
        {
            engine.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: engine start: " + ex.Message);
            return 3;
        }

        Console.WriteLine("-- phase 1: test tone, 3 s --");
        Sample(enabled, 3000, 500);

        Console.WriteLine("-- phase 2: switch to system audio, 5 s --");
        AudioEngine.Diagnostic = msg => Console.WriteLine("   [diag] " + msg);
        engine.SetSource(SourceKind.SystemAudio);
        Console.WriteLine($"   capture rate = {engine.CurrentInputRate} Hz");
        Sample(enabled, 5000, 500);

        bool anyAudio = enabled.Any(c => c.FillMs > 0);
        bool anyUnderrun = enabled.Any(c => c.Underruns > 2);
        long dropped = enabled.Sum(c => c.DroppedFrames);

        engine.Stop();

        Console.WriteLine(anyAudio ? "PASS: engine produced audio on at least one endpoint."
                                   : "WARN: no channel reported audio (headless session?).");
        if (anyUnderrun) Console.WriteLine($"WARN: {enabled.Sum(c => c.Underruns)} underruns.");
        if (dropped > 0) Console.WriteLine($"WARN: {dropped} frames dropped by ring overflow.");

        foreach (EndpointInfo e in endpoints) e.Dispose();

        return anyAudio ? 0 : 1;
    }

    private static (float l, float r) Peak(MMDevice d)
    {
        try
        {
            var values = d.AudioMeterInformation.PeakValues;
            return (values.Count > 0 ? values[0] : 0f,
                    values.Count > 1 ? values[1] : (values.Count > 0 ? values[0] : 0f));
        }
        catch
        {
            return (0f, 0f);
        }
    }

    private static int Channels(string? filter, bool live)
    {
        if (!VerifyChannelMix()) return 4;

        if (!live)
        {
            Console.WriteLine("PASS: channel mixing math (add 'live' to test real endpoints).");
            return 0;
        }

        using var enumerator = new MMDeviceEnumerator();
        List<EndpointInfo> endpoints = EndpointCatalog.Enumerate(enumerator);
        List<EndpointInfo> targets = endpoints
            .Where(e => filter == null || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        if (targets.Count < 2)
        {
            Console.WriteLine($"FAIL: need two endpoints matching '{filter ?? "*"}', found {targets.Count}.");
            foreach (EndpointInfo e in endpoints) e.Dispose();
            return 5;
        }

        EndpointInfo? capture = endpoints.FirstOrDefault(e =>
                                   !targets.Any(t => t.Id == e.Id) &&
                                   e.Name.Contains("Digital", StringComparison.OrdinalIgnoreCase))
                               ?? endpoints.FirstOrDefault(e => !targets.Any(t => t.Id == e.Id));

        if (capture == null)
        {
            Console.WriteLine("FAIL: no unused endpoint available to capture from.");
            foreach (EndpointInfo e in endpoints) e.Dispose();
            return 5;
        }

        Console.WriteLine($"pair    : L={targets[0].Name}  R={targets[1].Name}");
        Console.WriteLine($"capture : {capture.Name}");

        string leftWav = WriteTone("left", leftSide: true);
        string rightWav = WriteTone("right", leftSide: false);

        string? original = CurrentDefaultId(enumerator);
        int result;

        try
        {
            if (!AudioDefaults.SetDefault(capture.Id))
            {
                Console.WriteLine("FAIL: could not move the Windows default output.");
                foreach (EndpointInfo e in endpoints) e.Dispose();
                return 6;
            }

            Thread.Sleep(1200);

            using var engine = new AudioEngine();
            engine.SetSource(SourceKind.SystemAudio);

            DeviceChannel leftChannel = engine.EnableDevice(targets[0].Device, targets[0].IsBluetooth);
            DeviceChannel rightChannel = engine.EnableDevice(targets[1].Device, targets[1].IsBluetooth);
            leftChannel.Channel = ChannelMode.Left;
            rightChannel.Channel = ChannelMode.Right;
            engine.Start();

            Console.WriteLine("warming up…");
            Thread.Sleep(4000);

            var leftClip = MeasurePair(targets[0].Device, targets[1].Device, leftWav, 2200);
            var rightClip = MeasurePair(targets[0].Device, targets[1].Device, rightWav, 2200);

            Console.WriteLine($"left-only clip : L-speaker=({leftClip.aL:0.000},{leftClip.aR:0.000}) " +
                              $"R-speaker=({leftClip.bL:0.000},{leftClip.bR:0.000})");
            Console.WriteLine($"right-only clip: L-speaker=({rightClip.aL:0.000},{rightClip.aR:0.000}) " +
                              $"R-speaker=({rightClip.bL:0.000},{rightClip.bR:0.000})");

            bool leftOk = Loud(leftClip.aL) && Loud(leftClip.aR) && Quiet(leftClip.bL) && Quiet(leftClip.bR);
            bool rightOk = Quiet(rightClip.aL) && Quiet(rightClip.aR) && Loud(rightClip.bL) && Loud(rightClip.bR);

            if (leftOk && rightOk)
            {
                Console.WriteLine("PASS: each endpoint plays only its assigned side.");
                result = 0;
            }
            else
            {
                Console.WriteLine("FAIL: routed side did not match the channel mode.");
                result = 7;
            }

            engine.Stop();
        }
        finally
        {
            if (original != null) AudioDefaults.SetDefault(original);
            foreach (EndpointInfo e in endpoints) e.Dispose();
            try { File.Delete(leftWav); File.Delete(rightWav); } catch { }
        }

        return result;
    }

    private static bool Loud(float peak) => peak > 0.02f;
    private static bool Quiet(float peak) => peak < 0.01f;

    private static (float aL, float aR, float bL, float bR) MeasurePair(
        MMDevice a, MMDevice b, string wav, int milliseconds)
    {
        float aL = 0, aR = 0, bL = 0, bR = 0;

        using var player = new System.Media.SoundPlayer(wav);
        player.Load();
        player.Play();
        Thread.Sleep(350);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            var pa = Peak(a);
            var pb = Peak(b);
            if (pa.l > aL) aL = pa.l;
            if (pa.r > aR) aR = pa.r;
            if (pb.l > bL) bL = pb.l;
            if (pb.r > bR) bR = pb.r;
            Thread.Sleep(40);
        }

        player.Stop();
        return (aL, aR, bL, bR);
    }

    private static string? CurrentDefaultId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    private static bool VerifyChannelMix()
    {
        bool ok = true;
        ok &= CheckMix(ChannelMode.Stereo, 0.8f, 0.2f, 0.8f, 0.2f, "stereo unchanged");
        ok &= CheckMix(ChannelMode.Mono, 0.8f, 0.2f, 0.5f, 0.5f, "mono downmix");
        ok &= CheckMix(ChannelMode.Left, 0.8f, 0.2f, 0.8f, 0.8f, "left on both");
        ok &= CheckMix(ChannelMode.Right, 0.8f, 0.2f, 0.2f, 0.2f, "right on both");
        ok &= CheckMix(ChannelMode.Center, 0.8f, 0.2f, 0.5f, 0.5f, "centre is the mid");

        // The rear channels must be mirror images and the bass must be removed.
        float[] rearL = RenderMode(ChannelMode.RearLeft, 400, 0.8f, 0f);
        float[] rearR = RenderMode(ChannelMode.RearRight, 400, 0.8f, 0f);
        float[] rearBass = RenderMode(ChannelMode.RearLeft, 50, 0.8f, 0f);

        double rearLevel = RmsChannel(rearL, 0);
        double rearBassLevel = RmsChannel(rearBass, 0);
        bool mirror = rearLevel > 0.2 && RmsChannel(rearR, 0) > 0.2;

        for (int i = 0; i < rearL.Length && mirror; i++)
            if (Math.Abs(rearL[i] + rearR[i]) > 0.02f) mirror = false;

        bool rearBassOk = rearBassLevel < rearLevel * 0.15;
        Console.WriteLine($"  {(mirror ? "ok  " : "FAIL")} rear left/right mirror: {rearLevel:0.000} / {RmsChannel(rearR, 0):0.000}");
        Console.WriteLine($"  {(rearBassOk ? "ok  " : "FAIL")} rear bass removed: {rearBassLevel:0.000} at 50 Hz");
        ok &= mirror && rearBassOk;

        // The subwoofer must pass deep bass and reject the mids.
        float[] subLow = RenderMode(ChannelMode.Subwoofer, 50, 0.8f, 0f);
        float[] subHigh = RenderMode(ChannelMode.Subwoofer, 400, 0.8f, 0f);
        double low = RmsChannel(subLow, 0);
        double high = RmsChannel(subHigh, 0);
        bool subOk = low > 0.2 && high < low * 0.15;
        Console.WriteLine($"  {(subOk ? "ok  " : "FAIL")} subwoofer crossover: 50 Hz {low:0.000}, 400 Hz {high:0.000}");
        ok &= subOk;

        // The spatial widener must not alter a centred (mono) signal's balance.
        float[] wide = RenderSpatial(1.0, hardLeft: false);
        double left = RmsChannel(wide, 0);
        double right = RmsChannel(wide, 1);
        bool centred = Math.Abs(left - right) < left * 0.05;
        Console.WriteLine($"  {(centred ? "ok  " : "FAIL")} spatial keeps mono centred: L {left:0.000} R {right:0.000}");
        ok &= centred;

        // A hard-panned signal should gain opposite-channel energy (width).
        float[] dry = RenderSpatial(0.0, hardLeft: true);
        float[] wet = RenderSpatial(1.0, hardLeft: true);
        double dryOther = RmsChannel(dry, 1);
        double wetOther = RmsChannel(wet, 1);
        bool wideOk = dryOther < 0.001 && wetOther > 0.10;
        Console.WriteLine($"  {(wideOk ? "ok  " : "FAIL")} spatial widens hard left: other channel {dryOther:0.000} -> {wetOther:0.000}");
        ok &= wideOk;

        return ok;
    }

    private static float[] RenderMode(ChannelMode mode, double frequency,
                                      float left, float right, double seconds = 1.0)
    {
        const int rate = 48000;
        const int block = 480;

        var buffer = new float[block * 2];
        var last = new float[block * 2];
        var processor = new SurroundProcessor();
        int blocks = Math.Max(1, (int)(rate * seconds / block));

        for (int b = 0; b < blocks; b++)
        {
            for (int f = 0; f < block; f++)
            {
                double t = (double)(b * block + f) / rate;
                float s = (float)Math.Sin(2 * Math.PI * frequency * t);
                buffer[f * 2] = s * left;
                buffer[f * 2 + 1] = s * right;
            }

            processor.Process(mode, rate, buffer, block);
            if (b == blocks - 1) Array.Copy(buffer, last, buffer.Length);
        }

        return last;
    }

    private static float[] RenderSpatial(double amount, bool hardLeft, double seconds = 1.0)
    {
        const int rate = 48000;
        const int block = 480;

        var buffer = new float[block * 2];
        var last = new float[block * 2];
        var spatial = new Spatializer(rate);
        int blocks = Math.Max(1, (int)(rate * seconds / block));

        for (int b = 0; b < blocks; b++)
        {
            for (int f = 0; f < block; f++)
            {
                double t = (double)(b * block + f) / rate;
                float s = (float)(0.5 * Math.Sin(2 * Math.PI * 1000.0 * t));
                buffer[f * 2] = s;
                buffer[f * 2 + 1] = hardLeft ? 0f : s;
            }

            spatial.Process(buffer, block, (float)amount);
            if (b == blocks - 1) Array.Copy(buffer, last, buffer.Length);
        }

        return last;
    }

    private static double RmsChannel(float[] buffer, int channel)
    {
        double sum = 0;
        int count = 0;
        for (int f = 0; f < buffer.Length / 2; f++)
        {
            double v = buffer[f * 2 + channel];
            sum += v * v;
            count++;
        }
        return Math.Sqrt(sum / Math.Max(1, count));
    }

    private static bool CheckMix(ChannelMode mode, float l, float r,
                                 float wantL, float wantR, string label)
    {
        var buffer = new float[8];
        for (int f = 0; f < 4; f++)
        {
            buffer[f * 2] = l;
            buffer[f * 2 + 1] = r;
        }

        if (mode.IsSurroundPosition())
        {
            var processor = new SurroundProcessor();
            processor.Process(mode, 48000, buffer, 4);
        }
        else
        {
            ChannelMix.Apply(mode, buffer, 4);
        }

        for (int f = 0; f < 4; f++)
        {
            if (Math.Abs(buffer[f * 2] - wantL) > 1e-4f ||
                Math.Abs(buffer[f * 2 + 1] - wantR) > 1e-4f)
            {
                Console.WriteLine($"FAIL: {label} frame {f} = ({buffer[f * 2]}, {buffer[f * 2 + 1]})");
                return false;
            }
        }

        Console.WriteLine($"  ok   {label}");
        return true;
    }

    private static string WriteTone(string name, bool leftSide)
    {
        string path = Path.Combine(Path.GetTempPath(), $"audiomesh-{name}.wav");

        const int rate = 48000;
        const int seconds = 5;
        const int channels = 2;
        const float amplitude = 0.5f;
        const double frequency = 440.0;

        int frames = rate * seconds;
        int dataBytes = frames * channels * 2;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        WriteTag(writer, "RIFF");
        writer.Write(36 + dataBytes);
        WriteTag(writer, "WAVE");
        WriteTag(writer, "fmt ");
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        WriteTag(writer, "data");
        writer.Write(dataBytes);

        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            short sample = (short)(Math.Sin(2 * Math.PI * frequency * t) * amplitude * short.MaxValue);
            writer.Write(leftSide ? sample : (short)0);
            writer.Write(leftSide ? (short)0 : sample);
        }

        return path;
    }

    private static void WriteTag(BinaryWriter writer, string tag)
    {
        foreach (char c in tag) writer.Write((byte)c);
    }

    private static int Soak(string? filter, int seconds)
    {
        using var enumerator = new MMDeviceEnumerator();
        List<EndpointInfo> endpoints = EndpointCatalog.Enumerate(enumerator);

        List<EndpointInfo> targets = endpoints
            .Where(e => filter == null || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        if (targets.Count < 2)
        {
            Console.WriteLine($"FAIL: need two endpoints matching '{filter ?? "*"}', found {targets.Count}.");
            foreach (EndpointInfo e in endpoints) e.Dispose();
            return 5;
        }

        EndpointInfo? capture = endpoints.FirstOrDefault(e =>
                                   !targets.Any(t => t.Id == e.Id) &&
                                   e.Name.Contains("Digital", StringComparison.OrdinalIgnoreCase))
                               ?? endpoints.FirstOrDefault(e => !targets.Any(t => t.Id == e.Id));

        if (capture == null)
        {
            Console.WriteLine("FAIL: no unused endpoint to capture from.");
            foreach (EndpointInfo e in endpoints) e.Dispose();
            return 5;
        }

        string? original = CurrentDefaultId(enumerator);
        string wav = WriteTone("soak", leftSide: false);
        int result = 0;

        try
        {
            if (!AudioDefaults.SetDefault(capture.Id))
            {
                Console.WriteLine("FAIL: could not move the default output.");
                return 6;
            }

            Thread.Sleep(1000);

            using var engine = new AudioEngine();
            engine.SetSource(SourceKind.SystemAudio);

            List<DeviceChannel> channels = targets
                .Select(t => engine.EnableDevice(t.Device, t.IsBluetooth))
                .ToList();

            engine.Start();

            using var player = new System.Media.SoundPlayer(wav);
            player.PlayLooping();

            long[] underruns = new long[channels.Count];
            long[] dropped = new long[channels.Count];

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"soaking {seconds}s on {targets[0].Name} + {targets[1].Name}...");

            while (sw.ElapsedMilliseconds < seconds * 1000)
            {
                Thread.Sleep(500);

                for (int i = 0; i < channels.Count; i++)
                {
                    long u = channels[i].Underruns;
                    long d = channels[i].DroppedFrames;

                    if (u != underruns[i] || d != dropped[i])
                    {
                        Console.WriteLine(
                            $"  {sw.ElapsedMilliseconds / 1000,3}s  {Trim(targets[i].Name),-30} " +
                            $"under +{u - underruns[i]}  drop +{d - dropped[i]}  " +
                            $"fill={channels[i].FillMs,5:0.0}  drift={channels[i].DriftPpm,6:0}");
                        result = 1;
                    }

                    underruns[i] = u;
                    dropped[i] = d;
                }

                if (sw.ElapsedMilliseconds % 5000 < 500)
                {
                    for (int i = 0; i < channels.Count; i++)
                        Console.WriteLine(
                            $"  {sw.ElapsedMilliseconds / 1000,3}s  {Trim(targets[i].Name),-30} " +
                            $"fill={channels[i].FillMs,5:0.0} ms  drift={channels[i].DriftPpm,6:0} ppm  " +
                            $"total under={channels[i].Underruns}  drop={channels[i].DroppedFrames}");
                }
            }

            player.Stop();

            Console.WriteLine("silence phase (pump should hold the buffers)...");
            Thread.Sleep(1200);
            long underSilence = channels.Sum(c => c.Underruns);
            long dropSilence = channels.Sum(c => c.DroppedFrames);
            Thread.Sleep(5000);
            long underSilence2 = channels.Sum(c => c.Underruns);
            long dropSilence2 = channels.Sum(c => c.DroppedFrames);
            Console.WriteLine($"  silence: under +{underSilence2 - underSilence}, drop +{dropSilence2 - dropSilence}, " +
                              $"fill={channels[0].FillMs:0} ms");
            if (underSilence2 != underSilence || dropSilence2 != dropSilence) result = 1;

            Console.WriteLine("resume phase (buffers should be warm)...");
            player.PlayLooping();
            Thread.Sleep(1200);
            long underResume = channels.Sum(c => c.Underruns);
            long dropResume = channels.Sum(c => c.DroppedFrames);
            Thread.Sleep(5000);
            long underResume2 = channels.Sum(c => c.Underruns);
            long dropResume2 = channels.Sum(c => c.DroppedFrames);
            Console.WriteLine($"  resume: under +{underResume2 - underResume}, drop +{dropResume2 - dropResume}, " +
                              $"fill={channels[0].FillMs:0} ms");
            if (underResume2 != underResume || dropResume2 != dropResume) result = 1;

            player.Stop();
            engine.Stop();

            Console.WriteLine(result == 0
                ? "PASS: no underruns or dropped frames during the soak."
                : "WARN: pipeline glitches detected (see increments above).");
        }
        finally
        {
            if (original != null) AudioDefaults.SetDefault(original);
            foreach (EndpointInfo e in endpoints) e.Dispose();
            try { File.Delete(wav); } catch { }
        }

        return result;
    }

    private static int Watch(string? filter)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var list = new List<(string Name, MMDevice Device)>();

        for (int i = 0; i < devices.Count; i++)
        {
            MMDevice d = devices[i];
            if (filter != null && !d.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add((d.FriendlyName, d));
        }

        if (list.Count == 0)
        {
            Console.WriteLine("No matching endpoints.");
            return 1;
        }

        string leftWav = WriteTone("watch-left", leftSide: true);
        string rightWav = WriteTone("watch-right", leftSide: false);

        try
        {
            Console.WriteLine("-- left-only clip --");
            SampleClip(list, leftWav);

            Thread.Sleep(700);

            Console.WriteLine("-- right-only clip --");
            SampleClip(list, rightWav);
        }
        finally
        {
            try { File.Delete(leftWav); File.Delete(rightWav); } catch { }
            foreach (var (_, device) in list) device.Dispose();
        }

        return 0;
    }

    private static void SampleClip(List<(string Name, MMDevice Device)> list, string wav)
    {
        var peaks = new float[list.Count, 2];

        using var player = new System.Media.SoundPlayer(wav);
        player.Load();
        player.Play();
        Thread.Sleep(350);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var p = Peak(list[i].Device);
                if (p.l > peaks[i, 0]) peaks[i, 0] = p.l;
                if (p.r > peaks[i, 1]) peaks[i, 1] = p.r;
            }

            Thread.Sleep(40);
        }

        player.Stop();

        for (int i = 0; i < list.Count; i++)
            Console.WriteLine($"  {list[i].Name,-42} L={peaks[i, 0]:0.000}  R={peaks[i, 1]:0.000}");
    }

    private static void Sample(List<DeviceChannel> channels, int totalMs, int stepMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < totalMs)
        {
            Thread.Sleep(stepMs);
            foreach (DeviceChannel c in channels)
                Console.WriteLine($"  {sw.ElapsedMilliseconds,5} ms  {Trim(c.Name),-34} in={c.InputRate} " +
                                  $"fill={c.FillMs,6:0.0} ms  drift={c.DriftPpm,7:+0;-0;0} ppm  " +
                                  $"under={c.Underruns}  drop={c.DroppedFrames}  run={c.IsRunning}  err={c.LastError ?? "-"}");
            Console.Out.Flush();
        }
    }

    private static string Trim(string s) => s.Length <= 34 ? s : s[..31] + "...";

    /// <summary>Reads the OS peak meter of every active render endpoint (no streams opened).</summary>
    private static int OrderTest()
    {
        bool ok = true;

        var items = new ObservableCollection<string> { "A", "B", "C" };
        items.Add("X");
        items.Add("Y");
        DeviceOrdering.PromoteToTop(items, new[] { "X", "Y" });
        ok &= Expect(items, "X,Y,A,B,C", "new batch on top");

        items.Add("Z");
        DeviceOrdering.PromoteToTop(items, new[] { "Z" });
        ok &= Expect(items, "Z,X,Y,A,B,C", "single hot-plugged endpoint");

        DeviceOrdering.PromoteToTop(items, Array.Empty<string>());
        ok &= Expect(items, "Z,X,Y,A,B,C", "nothing new");

        DeviceOrdering.PromoteToTop(items, new[] { "C", "A" });
        ok &= Expect(items, "C,A,Z,X,Y,B", "batch keeps discovery order");

        Console.WriteLine(ok ? "PASS: device ordering" : "FAIL: device ordering");
        return ok ? 0 : 1;
    }

    private static bool Expect(ObservableCollection<string> items, string expected, string label)
    {
        string actual = string.Join(",", items);
        bool ok = actual == expected;
        Console.WriteLine($"  {label}: {actual} -> {(ok ? "ok" : $"FAIL (expected {expected})")}");
        return ok;
    }

    private static int EqTest()
    {
        const int rate = 48000;
        const int frames = 480;
        var sine = new float[frames * 2];

        for (int i = 0; i < frames; i++)
        {
            float v = (float)(0.1 * Math.Sin(2 * Math.PI * 1000.0 * i / rate));
            sine[i * 2] = v;
            sine[i * 2 + 1] = v;
        }

        var eq = new Equalizer(rate);
        var buffer = new float[frames * 2];

        // A flat curve must be a true bypass.
        eq.SetSettings(new EqSettings());
        float worst = 0;

        for (int block = 0; block < 12; block++)
        {
            Array.Copy(sine, buffer, sine.Length);
            eq.Process(buffer, frames, 1f);
            if (block < 6) continue;

            for (int i = 0; i < buffer.Length; i++)
                worst = Math.Max(worst, Math.Abs(buffer[i] - sine[i]));
        }

        bool flatOk = worst < 0.0005f;
        Console.WriteLine($"  flat bypass: max error {worst:0.000000} -> {(flatOk ? "ok" : "FAIL")}");

        // +9 dB at 1 kHz, minus the automatic preamp (-5.4 dB).
        var boost = new EqSettings();
        boost.Bands[5] = 9;
        eq.SetSettings(boost);

        double ratio = 0;
        for (int block = 0; block < 60; block++)
        {
            Array.Copy(sine, buffer, sine.Length);
            eq.Process(buffer, frames, 1f);
            if (block < 50) continue;

            ratio = Rms(buffer) / Rms(sine);
        }

        double expected = Math.Pow(10, (9 + Equalizer.PreampDb(boost, 1.0)) / 20.0);
        bool boostOk = Math.Abs(ratio - expected) < expected * 0.12;
        Console.WriteLine($"  1 kHz +9 dB: gain {ratio:0.000} (expected ~{expected:0.000}) -> {(boostOk ? "ok" : "FAIL")}");

        Console.WriteLine(flatOk && boostOk ? "PASS: equalizer" : "FAIL: equalizer");
        return flatOk && boostOk ? 0 : 1;
    }

    private static double Rms(float[] buffer)
    {
        double sum = 0;
        for (int i = 0; i < buffer.Length; i++) sum += buffer[i] * buffer[i];
        return Math.Sqrt(sum / buffer.Length);
    }

    private static int Meters(string? filter)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var list = new List<(string Name, MMDevice Device)>();

        for (int i = 0; i < devices.Count; i++)
        {
            MMDevice d = devices[i];
            if (filter != null && !d.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add((d.FriendlyName, d));
        }

        Console.WriteLine($"Watching {list.Count} endpoint(s) for 6 s. Play something now.\n");

        for (int tick = 0; tick < 12; tick++)
        {
            var parts = new List<string>();
            foreach (var (name, dev) in list)
            {
                float peak;
                try { peak = dev.AudioMeterInformation.MasterPeakValue; }
                catch { peak = -1; }
                parts.Add($"{Trim(name),-26} {peak,5:0.000}");
            }
            Console.WriteLine($"  {tick * 500,5} ms  " + string.Join(" | ", parts));
            Thread.Sleep(500);
        }

        foreach (var (_, dev) in list) dev.Dispose();
        return 0;
    }
}
