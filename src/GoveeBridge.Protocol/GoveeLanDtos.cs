using System.Text.Json.Serialization;

namespace GoveeBridge.Protocol;

// Common JSON message structure:
//
// {
//     "msg": {
//         "cmd": "...",
//         "data": { ... }
//     }
// }

internal sealed class GoveePacket<TPayload>
{
   [JsonPropertyName(GoveePayloadProperties.Message)]
   public required GoveeMessage<TPayload> Message { get; init; }
}

internal sealed class GoveeMessage<TPayload>
{
   [JsonPropertyName(GoveePayloadProperties.Command)]
   public required string Command { get; init; }

   [JsonPropertyName(GoveePayloadProperties.Data)]
   public required TPayload Data { get; init; }
}

// Used by commands that have no data.
internal sealed class GoveeEmptyPayload
{
}

// Used for power and brightness commands.
internal sealed class GoveeValuePayload
{
   [JsonPropertyName(GoveePayloadProperties.Value)]
   public required int Value { get; init; }
}

// Used for device discovery.
internal sealed class GoveeScanPayload
{
   [JsonPropertyName(GoveePayloadProperties.AccountTopic)]
   public required string AccountTopic { get; init; }
}

// RGB components.
// Bytes enforce the supported 0-255 range.
internal sealed class GoveeRgbPayload
{
   [JsonPropertyName(GoveePayloadProperties.Red)]
   public required byte R { get; init; }

   [JsonPropertyName(GoveePayloadProperties.Green)]
   public required byte G { get; init; }

   [JsonPropertyName(GoveePayloadProperties.Blue)]
   public required byte B { get; init; }
}

// Used for RGB and color-temperature commands.
internal sealed class GoveeColorPayload
{
   [JsonPropertyName(GoveePayloadProperties.Color)]
   public required GoveeRgbPayload Color { get; init; }

   [JsonPropertyName(
       GoveePayloadProperties.ColorTemperatureKelvin)]
   public required int ColorTemperatureKelvin { get; init; }
}

// Response from the devStatus command.
internal sealed class GoveeStatusPayload
{
   [JsonPropertyName(GoveePayloadProperties.OnOff)]
   public required int OnOff { get; init; }

   [JsonPropertyName(GoveePayloadProperties.Brightness)]
   public required int Brightness { get; init; }

   [JsonPropertyName(GoveePayloadProperties.Color)]
   public required GoveeRgbPayload Color { get; init; }

   // Optional to tolerate firmware that omits this field.
   [JsonPropertyName(GoveePayloadProperties.ColorTemperatureKelvin)]
   public int ColorTemperatureKelvin { get; init; }
}

/// <summary>
/// JSON payload for Govee's undocumented Razer real-time
/// lighting protocol.
/// </summary>
/// <remarks>
/// The PacketBase64 property contains an encoded binary
/// command, including its header, opcode, payload, and
/// checksum.
///
/// The payload is used for both real-time mode control
/// and multizone RGB frames.
/// </remarks>
internal sealed class GoveeRazerPayload
{
   /// <summary>
   /// Gets the Base64-encoded binary Razer command.
   /// </summary>
   [JsonPropertyName(GoveePayloadProperties.Packet)]
   public required string PacketBase64 { get; init; }
}
