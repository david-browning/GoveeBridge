using System.Text.Json.Serialization;

namespace GoveeBridge.Core.Ipc;

/// <summary>
/// An IPC management command with no data payload, for example 'resume'.
/// Serializes to exactly {"type":"command","action":"resume"}.
/// </summary>
/// <remarks>
/// Use <see cref="BridgeIpcRequests"/> to create supported requests rather than
/// constructing arbitrary command/action combinations. The Host must still
/// validate all messages received from external processes.
/// </remarks>
public sealed class BridgeIpcCommandRequest
{
   internal BridgeIpcCommandRequest(string action)
   {
      Action = action;
   }

   /// <summary>Identifies this as a management command.</summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Type)]
   public string Type => BridgeIpcMessageTypes.Command;

   /// <summary>The operation to perform.</summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Action)]
   public string Action { get; }
}

/// <summary>
/// A management command carrying an operation-specific data payload.
/// For example, set-brightness includes a brightnessPercent field in data.
/// </summary>
/// <typeparam name="TPayload">Strongly typed request body.</typeparam>
public sealed class BridgeIpcCommandRequest<TPayload> where TPayload : class
{
   internal BridgeIpcCommandRequest(string action, TPayload data)
   {
      ArgumentNullException.ThrowIfNull(data);
      Action = action;
      Data = data;
   }

   /// <summary>Identifies this as a management command.</summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Type)]
   public string Type => BridgeIpcMessageTypes.Command;

   /// <summary>The operation to perform.</summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Action)]
   public string Action { get; }

   /// <summary>Parameters for the operation.</summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Data)]
   public TPayload Data { get; }
}

/// <summary>Parameters for a power-state request.</summary>
public sealed class BridgeIpcPowerPayload
{
   /// <summary>True requests power on; false requests power off.</summary>
   [JsonPropertyName(BridgeIpcPropertyNames.IsOn)]
   public required bool IsOn { get; init; }
}

/// <summary>Parameters for a global-brightness request.</summary>
public sealed class BridgeIpcBrightnessPayload
{
   /// <summary>
   /// Requested brightness from 0 through 100. This is a desired-state
   /// value, not a Govee LAN 'brightness' packet. The Host must decide how
   /// to implement zero on devices whose brightness command starts at 1.
   /// </summary>
   [JsonPropertyName(BridgeIpcPropertyNames.BrightnessPercent)]
   public required int BrightnessPercent { get; init; }
}

/// <summary>Parameters for a white color-temperature request.</summary>
public sealed class BridgeIpcColorTemperaturePayload
{
   /// <summary>
   /// Requested color temperature in Kelvin. The receiving Host must validate
   /// the range against the selected physical device's capabilities.
   /// </summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Kelvin)]
   public required int Kelvin { get; init; }
}

/// <summary>
/// One RGB color in an IPC message. This is deliberately independent of the
/// JSON DTOs used by Govee's physical LAN protocol.
/// </summary>
public sealed class BridgeIpcRgbColor
{
   /// <summary>Constructs a wire RGB color from component bytes.</summary>
   [JsonConstructor]
   public BridgeIpcRgbColor(byte r, byte g, byte b)
   {
      R = r;
      G = g;
      B = b;
   }

   [JsonPropertyName(BridgeIpcPropertyNames.Red)]
   public byte R { get; }

   [JsonPropertyName(BridgeIpcPropertyNames.Green)]
   public byte G { get; }

   [JsonPropertyName(BridgeIpcPropertyNames.Blue)]
   public byte B { get; }
}

/// <summary>Parameters for setting a single, uniform RGB color.</summary>
public sealed class BridgeIpcColorPayload
{
   /// <summary>A solid RGB color, not a multizone frame.</summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Color)]
   public required BridgeIpcRgbColor Color { get; init; }
}

/// <summary>
/// One time-sensitive streaming RGB frame in the existing SignalRGB-compatible
/// Host wire format: {"type":"rgb-frame","count":2,"colors":[255,0,0,0,0,255]}.
/// </summary>
/// <remarks>
/// 'colors' is an array of INTEGER components in RGB order, not byte[].
/// System.Text.Json serializes byte[] as a Base64 string, which would break
/// compatibility with the current Host's flat JSON number array.
///
/// A frame does not set power, global brightness, or color temperature. The
/// Host can accept frames while Manual owns the lamp, but must not apply them
/// without granting PC control. No command acknowledgment is implied.
/// </remarks>
public sealed class BridgeIpcFrameRequest
{
   internal BridgeIpcFrameRequest(IReadOnlyList<int> components, int count)
   {
      Colors = Array.AsReadOnly(components.ToArray());
      Count = count;
   }

   /// <summary>The legacy frame message kind.</summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Type)]
   public string Type => BridgeIpcMessageTypes.RgbFrame;

   /// <summary>The number of logical RGB positions represented.</summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Count)]
   public int Count { get; }

   /// <summary>Flat RGB component sequence: R,G,B,R,G,B,...</summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Colors)]
   public IReadOnlyList<int> Colors { get; }
}
