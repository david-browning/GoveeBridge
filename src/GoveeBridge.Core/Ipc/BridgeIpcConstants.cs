namespace GoveeBridge.Core.Ipc;

/// <summary>
/// JSON property names understood by GoveeBridge's application-facing IPC contract.
/// These names belong to the bridge protocol, not to Govee's LAN protocol.
/// </summary>
public static class BridgeIpcPropertyNames
{
   public const string Type = "type";
   public const string Action = "action";
   public const string Data = "data";

   // Named fields for individual desired-state changes.
   public const string IsOn = "isOn";
   public const string BrightnessPercent = "brightnessPercent";
   public const string Kelvin = "kelvin";
   public const string Color = "color";
   public const string Red = "r";
   public const string Green = "g";
   public const string Blue = "b";

   // Legacy streaming-frame representation. Colors are flat RGB triplets.
   public const string Count = "count";
   public const string Colors = "colors";
}

/// <summary>
/// Top-level IPC message kinds. These are intentionally transport-independent:
/// the same serialized message could travel over UDP loopback or a future pipe.
/// </summary>
public static class BridgeIpcMessageTypes
{
   /// <summary>A management request with an action and optional data payload.</summary>
   public const string Command = "command";

   /// <summary>
   /// A complete RGB frame. Kept separate from the management-command queue
   /// because frames are time-sensitive and should use latest-frame-wins handling.
   /// </summary>
   public const string RgbFrame = "rgb-frame";
}

/// <summary>
/// Actions accepted by the GoveeBridge Host's management IPC.
/// These names are independent of the underlying Govee protocol.
/// </summary>
public static class BridgeIpcActions
{
   // Queries
   public const string GetHostStatus = "status";
   public const string GetLampStatus = "lamp-status";

   // Control ownership
   public const string Resume = "resume";
   public const string Release = "release";

   // Desired state changes
   public const string SetPower = "set-power";
   public const string SetBrightness = "set-brightness";
   public const string SetSolidColor = "set-color";
   public const string SetColorTemperature = "set-color-temperature";
}
