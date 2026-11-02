using System;

namespace AudioMesh.ViewModels;

/// <summary>One graphic-EQ band slider.</summary>
public sealed class EqBandViewModel : ObservableObject
{
    private double _gain;

    public EqBandViewModel(double frequency, string label, double gain)
    {
        Frequency = frequency;
        Label = label;
        _gain = Math.Clamp(gain, -12, 12);
    }

    public double Frequency { get; }
    public string Label { get; }

    public double Gain
    {
        get => _gain;
        set
        {
            if (!Set(ref _gain, Math.Clamp(value, -12, 12))) return;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
}
