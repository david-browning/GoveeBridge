using System.Text.Json;
using GoveeBridge.Core;

namespace GoveeBridge.Protocol;

public sealed record GoveeStatus(
    bool IsOn,
    int Brightness,
    RgbColor Color,
    int ColorTemperatureKelvin);

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

    public static bool TryReadStatus(ReadOnlySpan<byte> data, out GoveeStatus? status)
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

            status = new GoveeStatus(
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
