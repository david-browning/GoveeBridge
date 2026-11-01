using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GoveeBridge.Core;

public static class GoveeHelpers
{
   public static bool TryParseFrame(
      ReadOnlySpan<byte> payload,
      int maxLeds,
      out RgbFrame? frame)
   {
      frame = null;
      try
      {
         using var doc = JsonDocument.Parse(payload.ToArray());
         var root = doc.RootElement;
         if (!root.TryGetProperty("type", out JsonElement type) ||
            type.GetString() != "rgb-frame")
         {
            return false;
         }

         int count = root.GetProperty("count").GetInt32();
         var bytes = root.GetProperty("colors");
         if (count is < 1 or > 256 || count > maxLeds ||
            bytes.ValueKind != JsonValueKind.Array ||
            bytes.GetArrayLength() != count * 3)
         {
            return false;
         }

         var colors = new List<RgbColor>(count);
         var enumerator = bytes.EnumerateArray();
         for (int i = 0; i < count; i++)
         {
            if (!enumerator.MoveNext())
            {
               return false;
            }

            byte r = checked((byte)enumerator.Current.GetInt32());
            if (!enumerator.MoveNext())
            {
               return false;
            }

            byte g = checked((byte)enumerator.Current.GetInt32());
            if (!enumerator.MoveNext())
            {
               return false;
            }

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

   /// <summary>
   /// Calculates an average color from a frame of different RGB values.
   /// </summary>
   /// <param name="frame"></param>
   /// <returns></returns>
   public static RgbColor AverageColor(RgbFrame frame)
   {
      if (frame.Colors.Count == 0)
      {
         return default;
      }

      return new RgbColor(
          (byte)Math.Round(frame.Colors.Average(c => (double)c.R)),
          (byte)Math.Round(frame.Colors.Average(c => (double)c.G)),
          (byte)Math.Round(frame.Colors.Average(c => (double)c.B)));
   }
}
