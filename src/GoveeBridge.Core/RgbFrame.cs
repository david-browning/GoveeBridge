namespace GoveeBridge.Core;

/// <summary>
/// Represents an ordered sequence of logical RGB color positions.
///
/// The positions describe a single instant in a lighting
/// animation. They do not necessarily correspond one-to-one
/// with physical LEDs. The lamp may interpolate between how many colors are
/// set.
///
/// The frame does not contain global brightness, power,
/// or white color temperature.
/// </summary>
/// <param name="Colors">
/// The RGB values in device-independent logical order.
/// </param>
public sealed record RgbFrame(IReadOnlyList<RgbColor> Colors);