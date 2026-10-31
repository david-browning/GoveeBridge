namespace GoveeBridge.Protocol;

internal static class GoveeCommands
{
    // Official LAN commands

    /// <summary>
    /// Power on/off
    /// </summary>
    public const string PowerCommand = "turn";

    /// <summary>
    /// Set RGB color or color temperature
    /// </summary>
    public const string ColorCommand = "colorwc";

    /// <summary>
    /// Query current device state
    /// </summary>
    public const string StatusCommand = "devStatus";

    /// <summary>
    ///	Set brightness, 1–100
    /// </summary>
    public const string BrightnessCommand = "brightness";

    /// <summary>
    /// Discover devices using multicast
    /// </summary>
    public const string ScanCommand = "scan";

    // Undocumented LAN commands - experimental

    /// <summary>
    /// Alternative status/capability query
    /// </summary>
    public const string ExtendedStatusCommand = "status";

    /// <summary>
    /// Send encoded real-time lighting commands
    /// </summary>
    public const string RazerCommand = "razer";
}

