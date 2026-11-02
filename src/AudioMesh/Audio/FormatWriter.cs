using System;
using NAudio.Wave;

namespace AudioMesh.Audio;

public enum SampleKind
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32
}

/// <summary>
/// Converts the engine's internal stereo float stream into whatever the endpoint's
/// mix format actually is (float, 16/24/32-bit PCM, 2-8 channels).
/// </summary>
public static class FormatWriter
{
    private static readonly Guid PcmGuid = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatGuid = new("00000003-0000-0010-8000-00aa00389b71");

    public static bool TryDetect(WaveFormat format, out SampleKind kind, out int channels)
    {
        kind = SampleKind.Float32;
        channels = format.Channels;

        if (channels < 1) return false;
        if (format.BitsPerSample <= 0) return false;

        // Reject padded / non-tight layouts we cannot write cheaply.
        if (format.BlockAlign != channels * format.BitsPerSample / 8) return false;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            kind = SampleKind.Float32;
            return format.BitsPerSample == 32;
        }

        if (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == FloatGuid)
            {
                kind = SampleKind.Float32;
                return format.BitsPerSample == 32;
            }

            if (ext.SubFormat != PcmGuid) return false;
        }
        else if (format.Encoding != WaveFormatEncoding.Pcm)
        {
            return false;
        }

        switch (format.BitsPerSample)
        {
            case 16: kind = SampleKind.Pcm16; return true;
            case 24: kind = SampleKind.Pcm24; return true;
            case 32: kind = SampleKind.Pcm32; return true;
            default: return false;
        }
    }

    public static void Write(float[] src, int frames, byte[] dst, int offset, int channels, SampleKind kind)
    {
        int p = offset;

        switch (kind)
        {
            case SampleKind.Float32:
                for (int f = 0; f < frames; f++)
                {
                    int s = f * 2;
                    for (int c = 0; c < channels; c++)
                    {
                        float v = c < 2 ? src[s + c] : 0f;
                        BitConverter.TryWriteBytes(dst.AsSpan(p), v);
                        p += 4;
                    }
                }
                break;

            case SampleKind.Pcm16:
                for (int f = 0; f < frames; f++)
                {
                    int s = f * 2;
                    for (int c = 0; c < channels; c++)
                    {
                        float v = c < 2 ? src[s + c] : 0f;
                        short x = (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue);
                        dst[p] = (byte)x;
                        dst[p + 1] = (byte)(x >> 8);
                        p += 2;
                    }
                }
                break;

            case SampleKind.Pcm24:
                for (int f = 0; f < frames; f++)
                {
                    int s = f * 2;
                    for (int c = 0; c < channels; c++)
                    {
                        float v = c < 2 ? src[s + c] : 0f;
                        int x = Math.Clamp((int)MathF.Round(v * 8388607f), -8388608, 8388607);
                        dst[p] = (byte)x;
                        dst[p + 1] = (byte)(x >> 8);
                        dst[p + 2] = (byte)(x >> 16);
                        p += 3;
                    }
                }
                break;

            case SampleKind.Pcm32:
                for (int f = 0; f < frames; f++)
                {
                    int s = f * 2;
                    for (int c = 0; c < channels; c++)
                    {
                        float v = c < 2 ? src[s + c] : 0f;
                        int x = (int)Math.Clamp(v * 2147483647.0, -2147483648.0, 2147483647.0);
                        dst[p] = (byte)x;
                        dst[p + 1] = (byte)(x >> 8);
                        dst[p + 2] = (byte)(x >> 16);
                        dst[p + 3] = (byte)(x >> 24);
                        p += 4;
                    }
                }
                break;
        }
    }
}
