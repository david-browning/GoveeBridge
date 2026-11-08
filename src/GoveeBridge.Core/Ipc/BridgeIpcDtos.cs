using System.Text.Json.Serialization;

namespace GoveeBridge.Core.Ipc;

/// <summary>
/// Parameterless management command.
/// </summary>
public sealed class BridgeIpcCommandRequest
{
   /// <summary>
   /// Constructs a command using the stable wire action and optional lamp target.
   /// </summary>
   internal BridgeIpcCommandRequest(string action, string lampId)
   {
      Action = action;
      LampId = lampId;
   }

   /// <summary>
   /// Management-command discriminator.
   /// </summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Type)]
   public string Type => BridgeIpcMessageTypes.Command;

   /// <summary>
   /// The requested operation.
   /// </summary>
   [JsonPropertyName(BridgeIpcPropertyNames.Action)]
   public string Action { get; }

   /// <summary>
   /// Target lamp's configured stable Id
   /// </summary>
   [JsonPropertyName(BridgeIpcPropertyNames.LampId)]
   public string LampId { get; }
}

/// <summary>
/// Management command carrying an existing operation-specific Core payload.
/// </summary>
public sealed class BridgeIpcCommandRequest<TPayload> where TPayload : class
{
   /// <summary>
   /// Constructs the request, retaining the concrete payload for JSON serialization.
   /// </summary>
   internal BridgeIpcCommandRequest(string action, TPayload data, string lampId)
   {
      ArgumentNullException.ThrowIfNull(data);
      Action = action;
      Data = data;
      LampId = lampId;
   }

   [JsonPropertyName(BridgeIpcPropertyNames.Type)]
   public string Type => BridgeIpcMessageTypes.Command;

   [JsonPropertyName(BridgeIpcPropertyNames.Action)]
   public string Action { get; }

   [JsonPropertyName(BridgeIpcPropertyNames.LampId)]
   public string LampId { get; }

   [JsonPropertyName(BridgeIpcPropertyNames.Data)]
   public TPayload Data { get; }
}

/// <summary>
/// Requested device power state.
/// </summary>
public sealed class BridgeIpcPowerPayload
{
   [JsonPropertyName(BridgeIpcPropertyNames.IsOn)]
   public required bool IsOn { get; init; }
}

/// <summary>
/// Global brightness, where zero is interpreted as an explicit power-off request.
/// </summary>
public sealed class BridgeIpcBrightnessPayload
{
   [JsonPropertyName(BridgeIpcPropertyNames.BrightnessPercent)]
   public required int BrightnessPercent { get; init; }
}

/// <summary>
/// White temperature requested from a device that supports that capability.
/// </summary>
public sealed class BridgeIpcColorTemperaturePayload
{
   [JsonPropertyName(BridgeIpcPropertyNames.Kelvin)]
   public required int Kelvin { get; init; }
}

/// <summary>
/// Three components of one IPC RGB color, independent of LAN packet encoding.
/// </summary>
public sealed class BridgeIpcRgbColor
{
   /// <summary>
   /// Creates an RGB value from exact component bytes; never from a base64 array.
   /// </summary>
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

/// <summary>
/// One uniform RGB color for a discrete set-color command.
/// </summary>
public sealed class BridgeIpcColorPayload
{
   [JsonPropertyName(BridgeIpcPropertyNames.Color)]
   public required BridgeIpcRgbColor Color { get; init; }
}

/// <summary>
/// One complete time-sensitive RGB frame, with an optional target-lamp selector.
/// </summary>
public sealed class BridgeIpcFrameRequest
{
   /// <summary>
   /// Preserves the existing flat integer RGB sequence, never base64 byte[] JSON.
   /// </summary>
   internal BridgeIpcFrameRequest(IReadOnlyList<int> components, int count, string lampId)
   {
      Colors = Array.AsReadOnly(components.ToArray());
      Count = count;
      LampId = lampId;
   }

   [JsonPropertyName(BridgeIpcPropertyNames.Type)]
   public string Type => BridgeIpcMessageTypes.RgbFrame;

   [JsonPropertyName(BridgeIpcPropertyNames.LampId)]
   public string LampId { get; }

   [JsonPropertyName(BridgeIpcPropertyNames.Count)]
   public int Count { get; }

   [JsonPropertyName(BridgeIpcPropertyNames.Colors)]
   public IReadOnlyList<int> Colors { get; }
}
