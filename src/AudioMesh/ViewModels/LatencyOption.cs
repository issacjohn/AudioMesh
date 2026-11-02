using System;
using System.Collections.Generic;
using System.Linq;
using AudioMesh.Audio;

namespace AudioMesh.ViewModels;

/// <summary>A labelled buffer mode for the setup deck.</summary>
public sealed class LatencyOption
{
    public LatencyOption(LatencyMode mode, string label, string hint)
    {
        Mode = mode;
        Label = label;
        Hint = hint;
    }

    public LatencyMode Mode { get; }
    public string Label { get; }
    public string Hint { get; }

    /// <summary>Approximate app-side delay for a Bluetooth endpoint.</summary>
    public string Estimate => $"~{AudioLatency.EstimateMs(Mode)} ms";

    public static IReadOnlyList<LatencyOption> All { get; } = new[]
    {
        new LatencyOption(LatencyMode.Latency, "Latency", "Lowest delay, less headroom"),
        new LatencyOption(LatencyMode.Balanced, "Balanced", "Middle ground for most rooms"),
        new LatencyOption(LatencyMode.Stable, "Stable", "Most headroom, highest delay")
    };

    public static LatencyOption For(LatencyMode mode) =>
        All.FirstOrDefault(o => o.Mode == mode) ?? All[2];
}
