namespace GoveeBridge.Core;

/// <summary>
/// Extends a Govee device client with real-time,
/// independently colored lighting frame support.
/// </summary>
/// <remarks>
/// Implementations may use Govee's undocumented Razer
/// protocol or another supported real-time protocol.
///
/// The Host remains responsible for deciding when real-time
/// control is permitted and for coordinating the transition
/// between real-time and ordinary device operation.
///
/// Implementing this interface does not guarantee that every
/// physical device supported by the transport can accept
/// real-time frames. Device capabilities must also be checked.
/// </remarks>
public interface IGoveeRealtimeClient : IGoveeClient
{
   /// <summary>
   /// Enables or disables the device's real-time lighting mode.
   /// </summary>
   /// <param name="enabled">
   /// True to request real-time mode; false to exit it.
   /// </param>
   /// <param name="ct">Cancels the operation.</param>
   /// <remarks>
   /// Enabling real-time mode prepares the device to receive
   /// subsequent RGB frames. It must not implicitly acquire
   /// control ownership within GoveeBridge.Host.
   ///
   /// Disabling real-time mode should relinquish the device's
   /// real-time channel without intentionally turning off the
   /// lamp or replacing its configured lighting state.
   ///
   /// This method does not send an RGB frame. The Host must
   /// sequence activation, frame transmission, and release.
   /// </remarks>
   Task SetRealtimeEnabledAsync(bool enabled, CancellationToken ct);

   /// <summary>
   /// Transmits one complete real-time RGB frame.
   /// </summary>
   /// <param name="frame">
   /// Ordered logical RGB positions to transmit.
   /// </param>
   /// <param name="ct">Cancels the operation.</param>
   /// <remarks>
   /// The device must already be in real-time mode.
   ///
   /// Each invocation represents one complete frame, not
   /// a partial update to previously transmitted colors.
   ///
   /// The implementation must validate frame size and map
   /// logical positions into its supported device protocol.
   ///
   /// The client must not automatically enable real-time
   /// mode, alter control ownership, or modify global
   /// brightness.
   ///
   /// A completed send does not guarantee that the physical
   /// device rendered the frame.
   /// </remarks>
   Task SendRealtimeFrameAsync(RgbFrame frame, CancellationToken ct);
}
