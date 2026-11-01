namespace GoveeBridge.Core;

/// <summary>
/// Describes one way a light should produce color.
///
/// Exactly one appearance is represented by each instance:
/// a solid RGB color, a multizone RGB frame, or white light
/// specified by color temperature.
///
/// This type is independent of Govee's LAN protocol and does
/// not include power or global brightness.
/// </summary>
/// <remarks>
/// The constructor is private so callers cannot introduce
/// additional appearance types. Use the public factory methods
/// to construct one of the supported appearances.
/// </remarks>
public abstract class LightColorState
{
   private LightColorState()
   {
   }

   /// <summary>
   /// Creates an appearance with one RGB color.
   /// </summary>
   /// <param name="color">The requested RGB color.</param>
   /// <returns>A solid-color appearance.</returns>
   public static SolidRgb Solid(RgbColor color)
   {
      return new SolidRgb(color);
   }

   /// <summary>
   /// Creates an appearance containing multiple ordered RGB
   /// color positions.
   /// </summary>
   /// <param name="frame">
   /// The logical RGB frame. Physical mapping is performed
   /// by the device adapter.
   /// </param>
   /// <returns>A multizone appearance.</returns>
   public static MultiZoneRgb Zones(RgbFrame frame)
   {
      return new MultiZoneRgb(frame);
   }

   /// <summary>
   /// Creates a white-light appearance specified in Kelvin.
   /// </summary>
   /// <param name="kelvin">A positive color temperature.</param>
   /// <returns>A white-temperature appearance.</returns>
   /// <remarks>
   /// Device-specific minimum and maximum temperatures
   /// are validated by the device adapter.
   /// </remarks>
   public static WhiteTemperature White(int kelvin)
   {
      return new WhiteTemperature(kelvin);
   }

   /// <summary>
   /// Represents a uniform RGB color across a light.
   /// </summary>
   public sealed class SolidRgb : LightColorState
   {
      internal SolidRgb(RgbColor color)
      {
         Color = color;
      }

      /// <summary>Gets the requested RGB color.</summary>
      public RgbColor Color { get; }
   }

   /// <summary>
   /// Represents an ordered collection of RGB color positions.
   /// </summary>
   /// <remarks>
   /// The number of logical positions need not equal the
   /// number of physical LEDs. The device adapter determines
   /// how positions are mapped and interpolated.
   /// </remarks>
   public sealed class MultiZoneRgb : LightColorState
   {
      internal MultiZoneRgb(RgbFrame frame)
      {
         ArgumentNullException.ThrowIfNull(frame);

         if (frame.Colors is null || frame.Colors.Count == 0)
         {
            throw new ArgumentException(
                "An RGB frame must contain at least one color.",
                nameof(frame));
         }

         Frame = frame;
      }

      /// <summary>Gets the logical RGB frame.</summary>
      public RgbFrame Frame { get; }
   }

   /// <summary>
   /// Represents white light with a requested color temperature.
   /// </summary>
   public sealed class WhiteTemperature : LightColorState
   {
      internal WhiteTemperature(int kelvin)
      {
         ArgumentOutOfRangeException.ThrowIfNegativeOrZero(kelvin);

         Kelvin = kelvin;
      }

      /// <summary>Gets the requested temperature in Kelvin.</summary>
      public int Kelvin { get; }
   }
}
