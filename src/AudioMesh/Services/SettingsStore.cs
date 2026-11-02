using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AudioMesh.Audio;

namespace AudioMesh.Services;

public sealed class DeviceSettings
{
    public bool Enabled { get; set; }
    public double Volume { get; set; } = 100;
    public int DelayMs { get; set; }
    public bool Muted { get; set; }
    public string Channel { get; set; } = "Stereo";
    public bool Spatial { get; set; }
    public double SpatialAmount { get; set; } = 65;
    public EqSettings? Eq { get; set; }
}

public sealed class AppSettings
{
    public double MasterVolume { get; set; } = 100;
    public string Source { get; set; } = "SystemAudio";
    public bool PerfectSync { get; set; }
    public bool MonoOutput { get; set; }
    public bool AutoStart { get; set; }
    public bool LaunchAtLogin { get; set; }
    public bool LowLatency { get; set; }
    public string LatencyMode { get; set; } = string.Empty;
    public string CalibrationMicId { get; set; } = string.Empty;
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public List<string> DeviceOrder { get; set; } = new();
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new();
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AudioMesh");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string path = FilePath;
            string json = JsonSerializer.Serialize(settings, Options);

            // Write to a sibling and swap it in: a crash mid-write must not leave
            // the real settings file truncated.
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
        catch
        {
            // settings are a convenience, never a hard failure
        }
    }
}
