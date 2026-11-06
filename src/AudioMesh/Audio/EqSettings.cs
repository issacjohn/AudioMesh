using System;

namespace AudioMesh.Audio;

/// <summary>
/// Per-speaker equalizer state: 10 graphic bands, bass/treble shelves and a
/// volume-dependent loudness contour. Serialized with the device settings.
/// </summary>
public sealed class EqSettings
{
    public const int BandCount = 10;

    public static readonly double[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public static readonly string[] Presets =
    {
        "Flat", "Bass Boost", "Vocal", "Rock", "Pop", "Classical", "Night", "Custom"
    };

    public double[] Bands { get; set; } = new double[BandCount];

    /// <summary>Low shelf at 120 Hz, dB.</summary>
    public double Bass { get; set; }

    /// <summary>High shelf at 8 kHz, dB.</summary>
    public double Treble { get; set; }

    /// <summary>Boost lows/highs as the listening volume goes down.</summary>
    public bool Loudness { get; set; }

    /// <summary>0–100, how strongly loudness compensates.</summary>
    public double LoudnessAmount { get; set; } = 60;

    public string Preset { get; set; } = "Flat";

    public EqSettings Clone() => new()
    {
        Bands = (double[])Bands.Clone(),
        Bass = Bass,
        Treble = Treble,
        Loudness = Loudness,
        LoudnessAmount = LoudnessAmount,
        Preset = Preset
    };

    public void Normalize()
    {
        if (Bands == null || Bands.Length != BandCount)
        {
            var fixedBands = new double[BandCount];
            if (Bands != null)
            {
                for (int i = 0; i < Math.Min(Bands.Length, BandCount); i++)
                    fixedBands[i] = Bands[i];
            }
            Bands = fixedBands;
        }

        for (int i = 0; i < BandCount; i++)
            Bands[i] = Math.Clamp(Bands[i], -12, 12);

        Bass = Math.Clamp(Bass, -12, 12);
        Treble = Math.Clamp(Treble, -12, 12);
        LoudnessAmount = Math.Clamp(LoudnessAmount, 0, 100);

        if (Array.IndexOf(Presets, Preset) < 0) Preset = "Custom";
    }

    public bool IsFlat
    {
        get
        {
            if (Bass != 0 || Treble != 0 || Loudness) return false;
            for (int i = 0; i < BandCount; i++)
                if (Bands[i] != 0) return false;
            return true;
        }
    }

    public static EqSettings Create(string preset)
    {
        var settings = new EqSettings { Preset = preset };

        switch (preset)
        {
            case "Bass Boost":
                settings.Bands = new double[] { 7, 6, 5, 3, 1, 0, 0, 0, 0, 0 };
                break;
            case "Vocal":
                settings.Bands = new double[] { -4, -3, -2, 0, 1, 3, 3, 2, 0, -2 };
                break;
            case "Rock":
                settings.Bands = new double[] { 4, 3, 1, -1, -2, 0, 2, 3, 3, 2 };
                break;
            case "Pop":
                settings.Bands = new double[] { -1, 1, 3, 3, 1, 0, 1, 2, 3, 2 };
                break;
            case "Classical":
                settings.Bands = new double[] { 3, 2, 1, 0, 0, 0, -1, 0, 2, 3 };
                break;
            case "Night":
                settings.Bands = new double[] { 5, 4, 2, 0, -2, -2, -1, 0, -3, -5 };
                break;
        }

        return settings;
    }
}
