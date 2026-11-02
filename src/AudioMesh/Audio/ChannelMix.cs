namespace AudioMesh.Audio;

/// <summary>
/// Channel-mode mixing for a single endpoint. Kept separate from <see cref="DeviceChannel"/>
/// so it can be exercised without opening a WASAPI stream.
/// </summary>
public static class ChannelMix
{
    /// <summary>
    /// Rewrites an interleaved stereo buffer in place according to <paramref name="mode"/>.
    /// </summary>
    public static void Apply(ChannelMode mode, float[] buffer, int frames)
    {
        if (mode == ChannelMode.Stereo) return;

        for (int f = 0; f < frames; f++)
        {
            int i = f * 2;
            float l = buffer[i];
            float r = buffer[i + 1];

            switch (mode)
            {
                case ChannelMode.Mono:
                {
                    float m = 0.5f * (l + r);
                    buffer[i] = m;
                    buffer[i + 1] = m;
                    break;
                }
                case ChannelMode.Left:
                    // Both drivers get the left channel.
                    buffer[i + 1] = l;
                    break;
                case ChannelMode.Right:
                    // Both drivers get the right channel.
                    buffer[i] = r;
                    break;
            }
        }
    }
}
