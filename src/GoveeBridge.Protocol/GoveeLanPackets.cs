using System.Text.Json;
using GoveeBridge.Core;

namespace GoveeBridge.Protocol;

public static class GoveeLanPackets
{
   private static string Serialize<TPayload>(string command, TPayload payload)
   {
      var packet = new GoveePacket<TPayload>
      {
         Message = new GoveeMessage<TPayload>
         {
            Command = command,
            Data = payload
         }
      };

      return JsonSerializer.Serialize(packet);
   }

   public static string StatusRequest()
   {
      return Serialize(GoveeCommands.StatusCommand, new GoveeEmptyPayload());
   }

   public static string ScanRequest()
   {
      return Serialize(
          GoveeCommands.ScanCommand,
          new GoveeScanPayload
          {
             AccountTopic = _discoveryAccountTopic
          });
   }

   public static string Power(bool on)
   {
      return Serialize(
          GoveeCommands.PowerCommand,
          new GoveeValuePayload
          {
             Value = on ? 1 : 0
          });
   }

   public static string Brightness(int percentage)
   {
      ArgumentOutOfRangeException.ThrowIfLessThan(percentage, _minBrightness);
      ArgumentOutOfRangeException.ThrowIfGreaterThan(percentage, _maxBrightness);

      return Serialize(
          GoveeCommands.BrightnessCommand,
          new GoveeValuePayload
          {
             Value = percentage
          });
   }

   public static string Color(RgbColor color)
   {
      return Serialize(
          GoveeCommands.ColorCommand,
          new GoveeColorPayload
          {
             Color = new GoveeRgbPayload
             {
                R = color.R,
                G = color.G,
                B = color.B
             },
             ColorTemperatureKelvin = 0
          });
   }

   public static string ColorTemperature(int kelvin)
   {
      ArgumentOutOfRangeException.ThrowIfLessThan(kelvin, _minColorTemperature);
      ArgumentOutOfRangeException.ThrowIfGreaterThan(kelvin, _maxColorTemperature);

      return Serialize(
          GoveeCommands.ColorCommand,
          new GoveeColorPayload
          {
             Color = new GoveeRgbPayload
             {
                R = 0,
                G = 0,
                B = 0
             },
             ColorTemperatureKelvin = kelvin
          });
   }

   /// <summary>
   /// Creates a JSON command to arm or disarm Razer mode.
   /// </summary>
   public static string RazerEnabled(bool enabled)
   {
      byte[] binary = GoveeRazerEncoder.EncodeEnabled(enabled);
      return SerializeRazer(binary);
   }

   /// <summary>
   /// Creates a JSON command containing an RGB frame.
   /// The device must already be in real-time mode.
   /// </summary>
   public static string RazerFrame(RgbFrame frame, bool gradient = false)
   {
      byte[] binary = GoveeRazerEncoder.EncodeFrame(frame, gradient);
      return SerializeRazer(binary);
   }

   /// <summary>
   /// Wraps a complete binary Razer command in the standard
   /// Govee LAN JSON envelope.
   /// </summary>
   private static string SerializeRazer(ReadOnlySpan<byte> binary)
   {
      return Serialize(
          GoveeCommands.RazerCommand,
          new GoveeRazerPayload
          {
             PacketBase64 = Convert.ToBase64String(binary)
          });
   }

   public static bool TryReadStatus(
      ReadOnlySpan<byte> data,
      out GoveeDeviceStatus? status)
   {
      status = null;

      try
      {
         var packet = JsonSerializer.Deserialize<GoveePacket<GoveeStatusPayload>>(data);

         GoveeMessage<GoveeStatusPayload>? message = packet?.Message;

         if (message is null ||
             message.Command != GoveeCommands.StatusCommand ||
             message.Data is null)
         {
            return false;
         }

         GoveeStatusPayload payload = message.Data;

         if (payload.Color is null ||
             payload.OnOff is not (0 or 1))
         {
            return false;
         }

         var color = new RgbColor(
             payload.Color.R,
             payload.Color.G,
             payload.Color.B);

         status = new GoveeDeviceStatus(
             IsOn: payload.OnOff == 1,
             Brightness: payload.Brightness,
             Color: color,
             ColorTemperatureKelvin: payload.ColorTemperatureKelvin);

         return true;
      }
      catch (JsonException)
      {
         // Malformed JSON, missing required properties,
         // invalid numeric values, or incompatible types.
         return false;
      }
   }

   private const string _discoveryAccountTopic = "reserve";

   private const int _minBrightness = 1;
   private const int _maxBrightness = 100;

   private const int _minColorTemperature = 2000;
   private const int _maxColorTemperature = 9000;
}
