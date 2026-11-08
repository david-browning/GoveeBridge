namespace GoveeBridge.Core.Ipc;

/// <summary>
/// JSON property names in Host application IPC, not in physical LAN packets.
/// </summary>
public static class BridgeIpcPropertyNames
{
   /// <summary>
   /// Top-level message discriminator.
   /// </summary>
   public const string Type = "type";
   /// <summary>
   /// Stable command action name.
   /// </summary>
   public const string Action = "action";
   /// <summary>
   /// Optional command-specific payload object.
   /// </summary>
   public const string Data = "data";
   /// <summary>
   /// Required configured stable lamp selector.
   /// </summary>
   public const string LampId = "lampId";
   /// <summary>
   /// Desired power state Boolean.
   /// </summary>
   public const string IsOn = "isOn";
   /// <summary>
   /// Desired global brightness percentage.
   /// </summary>
   public const string BrightnessPercent = "brightnessPercent";
   /// <summary>
   /// Desired white temperature in Kelvin.
   /// </summary>
   public const string Kelvin = "kelvin";
   /// <summary>
   /// One desired solid RGB color.
   /// </summary>
   public const string Color = "color";
   /// <summary>
   /// Red component.
   /// </summary>
   public const string Red = "r";
   /// <summary>
   /// Green component.
   /// </summary>
   public const string Green = "g";
   /// <summary>
   /// Blue component.
   /// </summary>
   public const string Blue = "b";
   /// <summary>
   /// Number of RGB positions in a frame.
   /// </summary>
   public const string Count = "count";
   /// <summary>
   /// Flat integer RGB triplets in a frame.
   /// </summary>
   public const string Colors = "colors";
}

/// <summary>
/// Message kinds accepted by the Host IPC endpoint.
/// </summary>
public static class BridgeIpcMessageTypes
{
   /// <summary>
   /// Management command with action and optional data.
   /// </summary>
   public const string Command = "command";
   /// <summary>
   /// Time-sensitive, complete RGB snapshot.
   /// </summary>
   public const string RgbFrame = "rgb-frame";
}

/// <summary>
/// Stable action names understood by the Host's command dispatcher.
/// </summary>
public static class BridgeIpcActions
{
   /// <summary>
   /// Read cached per-lamp Host status.
   /// </summary>
   public const string GetHostStatus = "status";
   /// <summary>
   /// Request a fresh physical lamp observation.
   /// </summary>
   public const string GetLampStatus = "lamp-status";
   /// <summary>
   /// Claim PC Direct control.
   /// </summary>
   public const string Resume = "resume";
   /// <summary>
   /// Return to Manual control.
   /// </summary>
   public const string Release = "release";
   /// <summary>
   /// Arm PC Streaming explicitly.
   /// </summary>
   public const string Arm = "arm";
   /// <summary>
   /// Disarm PC Streaming.
   /// </summary>
   public const string Disarm = "disarm";
   /// <summary>
   /// Change global power.
   /// </summary>
   public const string SetPower = "set-power";
   /// <summary>
   /// Change global brightness.
   /// </summary>
   public const string SetBrightness = "set-brightness";
   /// <summary>
   /// Request a solid RGB color.
   /// </summary>
   public const string SetSolidColor = "set-color";
   /// <summary>
   /// Request white temperature.
   /// </summary>
   public const string SetColorTemperature = "set-color-temperature";
}
