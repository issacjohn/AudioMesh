namespace AudioMesh.ViewModels;

/// <summary>A capture endpoint offered in the calibration microphone picker.</summary>
public sealed class MicrophoneOption
{
    public MicrophoneOption(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    public override string ToString() => Name;
}
