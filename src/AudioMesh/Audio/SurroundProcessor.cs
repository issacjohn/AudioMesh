using System;

namespace AudioMesh.Audio;

/// <summary>
/// Passive surround matrix for one endpoint. A stereo source already contains the
/// information for a convincing surround layout: the sum is the front/centre image,
/// the difference is the ambient side/rear image, and the bass lives in the sum.
///
/// The rear channels are high-passed so bass does not cancel between the front and
/// rear speakers, and the subwoofer output is a cascaded 4th-order low-pass.
/// </summary>
public sealed class SurroundProcessor
{
    private const double RearHighPassHz = 180;
    private const double SubCrossoverHz = 90;

    private readonly Biquad _rearHighPass = new();
    private readonly Biquad _subLowPassA = new();
    private readonly Biquad _subLowPassB = new();

    private int _rate;
    private ChannelMode _mode = ChannelMode.Stereo;

    public void Process(ChannelMode mode, int rate, float[] buffer, int frames)
    {
        if (frames <= 0 || !mode.IsSurroundPosition()) return;

        if (mode != _mode || rate != _rate)
        {
            _mode = mode;
            _rate = rate;

            // Do not carry filter history across roles: a stale tail would click.
            _rearHighPass.Reset();
            _subLowPassA.Reset();
            _subLowPassB.Reset();

            if (mode is ChannelMode.RearLeft or ChannelMode.RearRight)
                _rearHighPass.SetHighPass(rate, RearHighPassHz, 0.707);
            else if (mode == ChannelMode.Subwoofer)
            {
                _subLowPassA.SetLowPass(rate, SubCrossoverHz, 0.707);
                _subLowPassB.SetLowPass(rate, SubCrossoverHz, 0.707);
            }
        }

        switch (mode)
        {
            case ChannelMode.Center:
                ToMid(buffer, frames);
                break;

            case ChannelMode.RearLeft:
                ToSide(buffer, frames, 0.5f);
                _rearHighPass.Process(buffer, frames);
                break;

            case ChannelMode.RearRight:
                ToSide(buffer, frames, -0.5f);
                _rearHighPass.Process(buffer, frames);
                break;

            case ChannelMode.Subwoofer:
                ToMid(buffer, frames);
                _subLowPassA.Process(buffer, frames);
                _subLowPassB.Process(buffer, frames);
                break;
        }
    }

    private static void ToMid(float[] buffer, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            int i = f * 2;
            float m = 0.5f * (buffer[i] + buffer[i + 1]);
            buffer[i] = m;
            buffer[i + 1] = m;
        }
    }

    private static void ToSide(float[] buffer, int frames, float sign)
    {
        for (int f = 0; f < frames; f++)
        {
            int i = f * 2;
            float side = sign * (buffer[i] - buffer[i + 1]);
            buffer[i] = side;
            buffer[i + 1] = side;
        }
    }
}
