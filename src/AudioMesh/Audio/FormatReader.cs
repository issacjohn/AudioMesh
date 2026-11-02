using System;
using NAudio.Wave;

namespace AudioMesh.Audio;

/// <summary>
/// Converts captured endpoint bytes into the engine's internal stereo float stream.
/// Channel downmix only - sample-rate conversion is done per output device by
/// <see cref="SincResampler"/>, which already has to handle arbitrary ratios.
/// </summary>
public static class FormatReader
{
    public static int ToStereoFloat(byte[] src, int byteCount, float[] dst, SampleKind kind, int channels)
    {
        int bytesPerSample = BytesPerSample(kind);
        int bytesPerFrame = bytesPerSample * channels;
        int frames = byteCount / bytesPerFrame;

        // Switch once per packet instead of once per sample: this runs on the capture
        // thread for every block of system audio.
        switch (kind)
        {
            case SampleKind.Float32:
                for (int f = 0; f < frames; f++)
                {
                    int i = f * bytesPerFrame;
                    float left = BitConverter.ToSingle(src, i);
                    dst[f * 2] = left;
                    dst[f * 2 + 1] = channels > 1 ? BitConverter.ToSingle(src, i + bytesPerSample) : left;
                }
                break;

            case SampleKind.Pcm16:
                for (int f = 0; f < frames; f++)
                {
                    int i = f * bytesPerFrame;
                    float left = BitConverter.ToInt16(src, i) / 32768f;
                    dst[f * 2] = left;
                    dst[f * 2 + 1] = channels > 1 ? BitConverter.ToInt16(src, i + bytesPerSample) / 32768f : left;
                }
                break;

            case SampleKind.Pcm24:
                for (int f = 0; f < frames; f++)
                {
                    int i = f * bytesPerFrame;
                    float left = (src[i] | (src[i + 1] << 8) | ((sbyte)src[i + 2] << 16)) / 8388608f;
                    dst[f * 2] = left;

                    if (channels > 1)
                    {
                        int j = i + bytesPerSample;
                        dst[f * 2 + 1] = (src[j] | (src[j + 1] << 8) | ((sbyte)src[j + 2] << 16)) / 8388608f;
                    }
                    else
                    {
                        dst[f * 2 + 1] = left;
                    }
                }
                break;

            case SampleKind.Pcm32:
                for (int f = 0; f < frames; f++)
                {
                    int i = f * bytesPerFrame;
                    float left = BitConverter.ToInt32(src, i) / 2147483648f;
                    dst[f * 2] = left;
                    dst[f * 2 + 1] = channels > 1 ? BitConverter.ToInt32(src, i + bytesPerSample) / 2147483648f : left;
                }
                break;
        }

        return frames;
    }

    private static int BytesPerSample(SampleKind kind) => kind switch
    {
        SampleKind.Pcm16 => 2,
        SampleKind.Pcm24 => 3,
        _ => 4
    };
}
