namespace AudioMesh.Audio;

/// <summary>How aggressively the app trades buffer headroom for lower delay.</summary>
public enum LatencyMode
{
    /// <summary>Smallest buffers. Closest to direct playback, least tolerant of jitter.</summary>
    Latency,

    /// <summary>Middle ground for most rooms and Bluetooth links.</summary>
    Balanced,

    /// <summary>Most headroom. Highest delay, but the most resilient to dropouts.</summary>
    Stable
}

/// <summary>
/// Central place for the buffering numbers. All three stages move together so the
/// user can pick a trade-off instead of editing constants.
/// </summary>
public static class AudioLatency
{
    public static LatencyMode Mode { get; set; } = LatencyMode.Stable;

    /// <summary>Loopback capture block size. NAudio's default is 100 ms.</summary>
    public static int CaptureBufferMs => CaptureBufferMsFor(Mode);

    /// <summary>Elastic buffer target for a Bluetooth endpoint.</summary>
    public static int BluetoothBufferMs => BluetoothBufferMsFor(Mode);

    /// <summary>Elastic buffer target for a wired endpoint.</summary>
    public static int WiredBufferMs => WiredBufferMsFor(Mode);

    /// <summary>WASAPI render buffer requested from the endpoint.</summary>
    public static int RenderBufferMs => RenderBufferMsFor(Mode);

    /// <summary>Rough app-side delay for a Bluetooth endpoint, in milliseconds.</summary>
    public static int EstimateMs(LatencyMode mode) =>
        CaptureBufferMsFor(mode) + BluetoothBufferMsFor(mode) + RenderBufferMsFor(mode);

    private static int CaptureBufferMsFor(LatencyMode mode) => mode switch
    {
        LatencyMode.Latency => 25,
        LatencyMode.Balanced => 40,
        _ => 100
    };

    private static int BluetoothBufferMsFor(LatencyMode mode) => mode switch
    {
        LatencyMode.Latency => 80,
        LatencyMode.Balanced => 100,
        _ => 150
    };

    private static int WiredBufferMsFor(LatencyMode mode) => mode switch
    {
        LatencyMode.Latency => 45,
        LatencyMode.Balanced => 60,
        _ => 80
    };

    private static int RenderBufferMsFor(LatencyMode mode) => mode switch
    {
        LatencyMode.Latency => 30,
        LatencyMode.Balanced => 45,
        _ => 60
    };
}
