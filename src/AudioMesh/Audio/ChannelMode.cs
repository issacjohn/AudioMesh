namespace AudioMesh.Audio;

/// <summary>
/// How a single endpoint renders the shared stereo stream.
///
/// <see cref="Left"/> and <see cref="Right"/> exist so two separate speakers can be
/// used as one stereo pair: point one at Left and the other at Right. The surround
/// positions turn a group of speakers into a matrix-decoded surround layout.
/// </summary>
public enum ChannelMode
{
    /// <summary>Normal stereo (left stays left, right stays right).</summary>
    Stereo,

    /// <summary>Both channels get the L+R downmix, so a single speaker hears everything.</summary>
    Mono,

    /// <summary>Plays the left channel on both outputs.</summary>
    Left,

    /// <summary>Plays the right channel on both outputs.</summary>
    Right,

    /// <summary>Plays the phantom centre (the L+R sum), for a dedicated centre speaker.</summary>
    Center,

    /// <summary>Plays the left side difference (L-R), high-passed, for a rear-left speaker.</summary>
    RearLeft,

    /// <summary>Plays the right side difference (R-L), high-passed, for a rear-right speaker.</summary>
    RearRight,

    /// <summary>Plays the mono sum below the crossover, for a subwoofer.</summary>
    Subwoofer
}

public static class ChannelModeExtensions
{
    /// <summary>True for the matrix positions that are derived from the stereo mix.</summary>
    public static bool IsSurroundPosition(this ChannelMode mode) =>
        mode is ChannelMode.Center or ChannelMode.RearLeft or
                ChannelMode.RearRight or ChannelMode.Subwoofer;
}
