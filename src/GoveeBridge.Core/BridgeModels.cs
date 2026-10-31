using System.Text.Json;

namespace GoveeBridge.Core;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

public sealed record RgbFrame(IReadOnlyList<RgbColor> Colors);

public enum TransportMode
{
    Diagnostic,
    GoveeStatic
}

public sealed class BridgeSettings
{
    public TransportMode TransportMode { get; set; } = TransportMode.Diagnostic;
    public string? LampIp { get; set; }
    public int FrameListenPort { get; set; } = 41005;
    public int LogicalLedCount { get; set; } = 8;
    public int MinimumSendIntervalMs { get; set; } = 1000;
    public int RemotePollIntervalMs { get; set; } = 2000;
    public int LampTimeoutMs { get; set; } = 2500;
    public bool AutoReleaseWhenRemoteTurnsOff { get; set; } = true;

    public void Validate()
    {
        if (FrameListenPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(FrameListenPort));
        if (LogicalLedCount is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(LogicalLedCount));
        if (MinimumSendIntervalMs is < 100 or > 60000) throw new ArgumentOutOfRangeException(nameof(MinimumSendIntervalMs));
        if (RemotePollIntervalMs is < 500 or > 60000) throw new ArgumentOutOfRangeException(nameof(RemotePollIntervalMs));
        if (LampTimeoutMs is < 500 or > 15000) throw new ArgumentOutOfRangeException(nameof(LampTimeoutMs));
        if (TransportMode == TransportMode.GoveeStatic && !System.Net.IPAddress.TryParse(LampIp, out _))
            throw new ArgumentException("GoveeStatic requires Bridge:LampIp in a private configuration file.");
    }
}

public static class BridgeWireProtocol
{
    // Schema used only on localhost. Not Govee's proprietary LAN wire protocol.
    public static bool TryParseFrame(ReadOnlySpan<byte> payload, int maxLeds, out RgbFrame? frame)
    {
        frame = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload.ToArray());
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out JsonElement type) || type.GetString() != "rgb-frame") return false;
            int count = root.GetProperty("count").GetInt32();
            JsonElement bytes = root.GetProperty("colors");
            if (count is < 1 or > 256 || count > maxLeds || bytes.ValueKind != JsonValueKind.Array || bytes.GetArrayLength() != count * 3) return false;
            var colors = new List<RgbColor>(count);
            var enumerator = bytes.EnumerateArray();
            for (int i = 0; i < count; i++)
            {
                if (!enumerator.MoveNext()) return false;
                byte r = checked((byte)enumerator.Current.GetInt32());
                if (!enumerator.MoveNext()) return false;
                byte g = checked((byte)enumerator.Current.GetInt32());
                if (!enumerator.MoveNext()) return false;
                byte b = checked((byte)enumerator.Current.GetInt32());
                colors.Add(new RgbColor(r, g, b));
            }
            frame = new RgbFrame(colors);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or FormatException)
        {
            return false;
        }
    }

    public static RgbColor AverageColor(RgbFrame frame)
    {
        if (frame.Colors.Count == 0) return default;
        return new RgbColor(
            (byte)Math.Round(frame.Colors.Average(c => (double)c.R)),
            (byte)Math.Round(frame.Colors.Average(c => (double)c.G)),
            (byte)Math.Round(frame.Colors.Average(c => (double)c.B)));
    }
}
