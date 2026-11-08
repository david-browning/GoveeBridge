using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using GoveeBridge.Core.Ipc;

namespace GoveeBridge.Core;

/// <summary>
/// Pure frame parsing and color utilities shared by Host and test callers.
/// </summary>
public static class GoveeHelpers
{
   /// <summary>
   /// Parses a complete frame and required stable lampId in one JSON pass.
   /// Invalid frames do not reach the management-command queue as valid frames.
   /// </summary>
   public static bool TryParseFrame(
      ReadOnlySpan<byte> payload, 
      int maxLeds,
      out RgbFrame? frame, 
      [NotNullWhen(true)] out string? lampId)
   {
      frame = null;
      lampId = null;
      try
      {
         using JsonDocument document = JsonDocument.Parse(payload.ToArray());
         JsonElement root = document.RootElement;
         if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out JsonElement type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "rgb-frame")
         {
            return false;
         }

         if (!root.TryGetProperty(BridgeIpcPropertyNames.LampId, out JsonElement selector) ||
            selector.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(selector.GetString()))
         {
            return false;
         }

         lampId = selector.GetString();

         int count = root.GetProperty("count").GetInt32();
         JsonElement values = root.GetProperty("colors");
         if (count < 1 || count > 256 || count > maxLeds ||
            values.ValueKind != JsonValueKind.Array ||
            values.GetArrayLength() != count * 3)
         {
            return false;
         }

         var colors = new List<RgbColor>(count);
         var iterator = values.EnumerateArray();
         for (int index = 0; index < count; index++)
         {
            iterator.MoveNext();
            byte r = checked((byte)iterator.Current.GetInt32());
            iterator.MoveNext();
            byte g = checked((byte)iterator.Current.GetInt32());
            iterator.MoveNext();
            byte b = checked((byte)iterator.Current.GetInt32());
            colors.Add(new RgbColor(r, g, b));
         }

         frame = new RgbFrame(colors);

         // lampId is actually validated when trying to parse it out of the root.
#pragma warning disable CS8762 // Parameter must have a non-null value when exiting in some condition.
         return true;
#pragma warning restore CS8762 // Parameter must have a non-null value when exiting in some condition.
      }
      catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
         InvalidOperationException or OverflowException or FormatException)
      {
         return false;
      }
   }

   /// <summary>
   /// Calculates a single solid RGB color from independently colored positions.
   /// This remains available for explicit use but is no longer an automatic mode.
   /// </summary>
   public static RgbColor AverageColor(RgbFrame frame)
   {
      if (frame.Colors.Count == 0)
      {
         return default;
      }
      return new RgbColor(
         (byte)Math.Round(frame.Colors.Average(color => (double)color.R)),
         (byte)Math.Round(frame.Colors.Average(color => (double)color.G)),
         (byte)Math.Round(frame.Colors.Average(color => (double)color.B)));
   }
}
