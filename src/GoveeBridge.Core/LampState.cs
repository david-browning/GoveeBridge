namespace GoveeBridge.Core;

/// <summary>
/// Describes the desired output state of a lighting device,
/// including power, brightness, and color.
/// </summary>
/// <remarks>
/// This type represents a request, not an observation of the
/// physical device. Applying a LampState does not guarantee
/// that the device has reached the requested state.
/// </remarks>
public sealed class LampState
{
   /// <summary>
   /// Creates a desired lighting state.
   /// </summary>
   /// <param name="isOn">
   /// Whether the light should be powered on.
   /// </param>
   /// <param name="brightnessPercent">
   /// The requested brightness, from 0 through 100.
   /// </param>
   /// <param name="appearance">
   /// The requested color appearance.
   /// </param>
   public LampState(
       bool isOn,
       int brightnessPercent,
       LightColorState appearance)
   {
      ArgumentOutOfRangeException.ThrowIfNegative(brightnessPercent);
      ArgumentOutOfRangeException.ThrowIfGreaterThan(brightnessPercent, 100);
      ArgumentNullException.ThrowIfNull(appearance);

      IsOn = isOn;
      BrightnessPercent = brightnessPercent;
      Appearance = appearance;
   }

   /// <summary>Gets the requested power state.</summary>
   public bool IsOn { get; }

   /// <summary>
   /// Gets the global brightness percentage.
   /// </summary>
   public int BrightnessPercent { get; }

   /// <summary>
   /// Gets the requested appearance of the light.
   /// </summary>
   public LightColorState Appearance { get; }

   /// <summary>
   /// Creates a new state with a different brightness.
   /// </summary>
   public LampState WithBrightness(int brightnessPercent)
   {
      return new LampState(IsOn, brightnessPercent, Appearance);
   }

   /// <summary>
   /// Creates a new state with a different color appearance.
   /// </summary>
   public LampState WithAppearance(LightColorState appearance)
   {
      return new LampState(IsOn, BrightnessPercent, appearance);
   }

   /// <summary>
   /// Creates a new state with a different power setting.
   /// </summary>
   public LampState WithPower(bool isOn)
   {
      return new LampState(isOn, BrightnessPercent, Appearance);
   }
}
