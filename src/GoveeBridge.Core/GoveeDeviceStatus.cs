namespace GoveeBridge.Core;

/// <summary>
/// The status object returned from a lamp when the devStatus command is 
/// queried.
/// </summary>
/// <param name="IsOn"></param>
/// <param name="Brightness"></param>
/// <param name="Color"></param>
/// <param name="ColorTemperatureKelvin"></param>
public sealed record GoveeDeviceStatus(
    bool IsOn,
    int Brightness,
    RgbColor Color,
    int ColorTemperatureKelvin);