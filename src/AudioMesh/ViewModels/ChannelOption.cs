using System.Collections.Generic;
using AudioMesh.Audio;

namespace AudioMesh.ViewModels;

/// <summary>A labelled choice for the per-device channel picker.</summary>
public sealed class ChannelOption
{
    public ChannelOption(ChannelMode mode, string label, string hint)
    {
        Mode = mode;
        Label = label;
        Hint = hint;
    }

    public ChannelMode Mode { get; }
    public string Label { get; }
    public string Hint { get; }

    public static IReadOnlyList<ChannelOption> All { get; } = new[]
    {
        new ChannelOption(ChannelMode.Stereo, "Stereo", "Left plays left, right plays right"),
        new ChannelOption(ChannelMode.Mono, "Mono", "Both drivers play the L+R downmix"),
        new ChannelOption(ChannelMode.Left, "Left only", "Front left: both drivers play the left channel"),
        new ChannelOption(ChannelMode.Right, "Right only", "Front right: both drivers play the right channel"),
        new ChannelOption(ChannelMode.Center, "Center", "Surround centre: the phantom middle of the mix"),
        new ChannelOption(ChannelMode.RearLeft, "Rear left", "Surround rear left: the ambient side difference, bass removed"),
        new ChannelOption(ChannelMode.RearRight, "Rear right", "Surround rear right: the mirrored ambient side difference"),
        new ChannelOption(ChannelMode.Subwoofer, "Subwoofer", "Bass only: mono below the 90 Hz crossover")
    };
}
