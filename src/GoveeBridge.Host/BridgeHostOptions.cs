namespace GoveeBridge.Host;

/// <summary>Host-only behaviors; physical LAN settings remain in BridgeSettings.</summary>
public sealed class BridgeHostOptions
{
    public bool EnableLampMonitoring { get; set; } = true;
    public bool LogLampStatusChanges { get; set; } = true;
    public int FramePumpIntervalMs { get; set; } = 50;
    public int FrameStaleAfterMs { get; set; } = 5000;

    // Experimental: some devices report stale colors or native-scene colors.
    // Power-off takeover detection remains governed by BridgeSettings.
    public bool AutoReleaseOnColorMismatch { get; set; } = false;
    public int ColorMismatchGraceMs { get; set; } = 1500;
    public int ColorMismatchRequiredPolls { get; set; } = 2;

    public void Validate()
    {
        if (FramePumpIntervalMs is < 20 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(FramePumpIntervalMs));
        if (FrameStaleAfterMs is < 500 or > 60000)
            throw new ArgumentOutOfRangeException(nameof(FrameStaleAfterMs));
        if (ColorMismatchGraceMs is < 0 or > 30000)
            throw new ArgumentOutOfRangeException(nameof(ColorMismatchGraceMs));
        if (ColorMismatchRequiredPolls is < 2 or > 10)
            throw new ArgumentOutOfRangeException(nameof(ColorMismatchRequiredPolls));
    }
}
