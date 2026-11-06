using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMesh.Audio;

public sealed class EndpointInfo : IDisposable
{
    public required MMDevice Device { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public bool IsDefault { get; init; }
    public bool IsBluetooth { get; init; }
    public int Rate { get; init; }
    public int Channels { get; init; }

    public string Id => Device.ID;

    public void Dispose()
    {
        try { Device.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>Plain snapshot of an endpoint, safe to move between threads.</summary>
public sealed record EndpointSnapshot(
    string Id, string Name, string Kind, bool IsBluetooth,
    bool IsDefault, int Rate, int Channels);

public static class EndpointCatalog
{
    /// <summary>
    /// PKEY_Device_EnumeratorName ({A45C254E-DF1C-4EFD-8020-67D146A850E0}, 24).
    /// NAudio's PropertyKeys does not expose this one, but it is the reliable way to
    /// tell a Bluetooth endpoint ("BTHENUM"/"BTHHFENUM") from a wired one — the
    /// friendly name often does not mention Bluetooth at all.
    /// </summary>
    private static readonly PropertyKey DeviceEnumeratorName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 24);

    private static readonly string[] BluetoothHints =
    {
        "bluetooth", "airpods", "buds", "beats", "hands-free", "headset",
        "wh-1000", "wf-1000", "bose", "jbl", "soundcore", "freebuds",
        "pixel buds", "earbuds", "tws", "momentum"
    };

    // Reading AudioClient.MixFormat is the slow part of enumeration (especially on
    // Bluetooth). The values are display-only, so cache them per endpoint: refreshes
    // then stay responsive instead of stalling the UI thread.
    private static readonly object FormatGate = new();
    private static readonly Dictionary<string, (int Rate, int Channels)> FormatCache =
        new(StringComparer.Ordinal);

    // Friendly name / kind / Bluetooth detection also come from the property store,
    // which is slow on Bluetooth endpoints. Cache them per ID too.
    private static readonly object MetaGate = new();
    private static readonly Dictionary<string, (string Name, string Kind, bool IsBluetooth)> MetaCache =
        new(StringComparer.Ordinal);

    public static List<EndpointInfo> Enumerate(MMDeviceEnumerator enumerator)
    {
        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def.ID;
        }
        catch
        {
            // no default device yet - still enumerate the rest
        }

        var result = new List<EndpointInfo>();
        var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        for (int i = 0; i < collection.Count; i++)
        {
            MMDevice? device = null;
            try
            {
                device = collection[i];
                (string name, string kind, bool bluetooth) = GetMetadata(device);
                (int rate, int channels) = GetFormat(device);

                result.Add(new EndpointInfo
                {
                    Device = device,
                    Name = name,
                    Kind = kind,
                    IsBluetooth = bluetooth,
                    IsDefault = device.ID == defaultId,
                    Rate = rate,
                    Channels = channels
                });
            }
            catch
            {
                device?.Dispose();
            }
        }

        return result;
    }

    /// <summary>
    /// Enumerates everything into plain records and disposes the COM devices.
    /// Safe to call on a background thread; nothing COM crosses back to the caller.
    /// </summary>
    public static List<EndpointSnapshot> Snapshot(MMDeviceEnumerator enumerator)
    {
        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def.ID;
        }
        catch
        {
            // no default device yet
        }

        var result = new List<EndpointSnapshot>();
        var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        for (int i = 0; i < collection.Count; i++)
        {
            MMDevice? device = null;
            try
            {
                device = collection[i];
                string id = device.ID;
                (string name, string kind, bool bluetooth) = GetMetadata(device);
                (int rate, int channels) = GetFormat(device);

                result.Add(new EndpointSnapshot(
                    id, name, kind, bluetooth, id == defaultId, rate, channels));
            }
            catch
            {
                // endpoint vanished or refused metadata
            }
            finally
            {
                device?.Dispose();
            }
        }

        return result;
    }

    private static (string Name, string Kind, bool IsBluetooth) GetMetadata(MMDevice device)
    {
        string id = device.ID;

        lock (MetaGate)
            if (MetaCache.TryGetValue(id, out (string Name, string Kind, bool IsBluetooth) cached))
                return cached;

        string name = device.FriendlyName;
        bool bluetooth = IsBluetooth(device, name);
        var meta = (Name: name, Kind: Classify(name, bluetooth), IsBluetooth: bluetooth);

        lock (MetaGate) MetaCache[id] = meta;
        return meta;
    }

    private static (int Rate, int Channels) GetFormat(MMDevice device)
    {
        string id = device.ID;

        lock (FormatGate)
            if (FormatCache.TryGetValue(id, out (int Rate, int Channels) cached))
                return cached;

        WaveFormat mix = device.AudioClient.MixFormat;
        var format = (Rate: mix.SampleRate, Channels: mix.Channels);

        lock (FormatGate) FormatCache[id] = format;
        return format;
    }

    private static bool IsBluetooth(MMDevice device, string name)
    {
        try
        {
            object? value = device.Properties[DeviceEnumeratorName].Value;
            if (value is string enumeratorName &&
                enumeratorName.Contains("BTH", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // property store unavailable - fall back to the name
        }

        string lower = name.ToLowerInvariant();
        foreach (string hint in BluetoothHints)
            if (lower.Contains(hint)) return true;
        return false;
    }

    private static string Classify(string name, bool isBluetooth)
    {
        string lower = name.ToLowerInvariant();

        if (isBluetooth) return "Bluetooth";
        if (lower.Contains("hdmi") || lower.Contains("nvidia") || lower.Contains("displayport") ||
            lower.Contains("display audio")) return "HDMI";
        if (lower.Contains("usb")) return "USB";
        if (lower.Contains("realtek") || lower.Contains("speakers") || lower.Contains("headphones"))
            return "Analog";
        return "Output";
    }
}
