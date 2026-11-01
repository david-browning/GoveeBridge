namespace GoveeBridge.Core;

/// <summary>
/// Defines basic operations for communicating with a Govee
/// lighting device.
/// </summary>
/// <remarks>
/// Implementations may use LAN, Bluetooth, or another supported
/// communication protocol.
///
/// This interface is responsible for sending device commands
/// and retrieving device-reported status. It does not manage
/// control ownership, lighting schedules, frame coalescing,
/// or automatic remote-control arbitration.
///
/// A successfully completed write operation means the command
/// was submitted through the underlying transport. It does
/// not guarantee that the physical device applied the change.
///
/// Implementations must coordinate concurrent operations
/// where required by their underlying transport.
///
/// The caller owns the client and must dispose of it when
/// device communication is no longer required.
/// </remarks>
public interface IGoveeClient : IDisposable
{
   /// <summary>
   /// Queries the current status reported by the device.
   /// </summary>
   /// <param name="ct">
   /// Cancels the pending query.
   /// </param>
   /// <returns>
   /// The reported status, or null when no usable status
   /// response is obtained within the configured timeout.
   /// </returns>
   /// <remarks>
   /// This method should request fresh status rather than
   /// returning a previously cached observation.
   ///
   /// A null result does not necessarily mean the device
   /// is offline. Some operating modes may prevent normal
   /// status responses.
   ///
   /// The result does not identify which controller last
   /// modified the device.
   /// </remarks>
   Task<GoveeDeviceStatus?> GetStatusAsync(CancellationToken ct);

   /// <summary>
   /// Requests that the device turn on or off.
   /// </summary>
   /// <param name="on">
   /// True to request power on; false to request power off.
   /// </param>
   /// <param name="ct">Cancels the operation.</param>
   /// <remarks>
   /// This operation should only change the power state.
   /// It must not implicitly claim Host control ownership
   /// or replace the requested color or brightness.
   /// </remarks>
   Task SetPowerAsync(bool on, CancellationToken ct);

   /// <summary>
   /// Sets the device's global brightness.
   /// </summary>
   /// <param name="percentage">
   /// Requested brightness percentage, from 1 through 100.
   /// </param>
   /// <param name="ct">Cancels the operation.</param>
   /// <remarks>
   /// The client must validate the supported brightness range.
   ///
   /// This operation should not change the selected color
   /// mode, color temperature, or power state unless the
   /// device protocol makes that unavoidable.
   ///
   /// A brightness value of zero is not part of this contract.
   /// The Host can translate a zero-brightness desired state
   /// into a power-off request when appropriate.
   /// </remarks>
   Task SetBrightnessAsync(int percentage, CancellationToken ct);

   /// <summary>
   /// Requests a uniform RGB color for the device.
   /// </summary>
   /// <param name="color">The requested RGB color.</param>
   /// <param name="ct">Cancels the operation.</param>
   /// <remarks>
   /// This operation selects solid RGB color mode.
   ///
   /// It does not represent an independently addressable
   /// multizone frame and should not change global brightness.
   ///
   /// The device may replace its previous white-temperature
   /// mode when this command is applied.
   /// </remarks>
   Task SetColorAsync(RgbColor color, CancellationToken ct);

   /// <summary>
   /// Requests white light at a specified color temperature.
   /// </summary>
   /// <param name="kelvin">
   /// The requested white color temperature in Kelvin.
   /// </param>
   /// <param name="ct">Cancels the operation.</param>
   /// <remarks>
   /// This operation selects white-temperature mode, which
   /// is mutually exclusive with RGB color mode.
   ///
   /// Implementations must validate device-specific
   /// temperature limits.
   ///
   /// A device without adjustable white-temperature support
   /// may throw NotSupportedException. The Host should
   /// validate capabilities before invoking this operation.
   /// </remarks>
   Task SetColorTemperatureAsync(int kelvin, CancellationToken ct);
}
