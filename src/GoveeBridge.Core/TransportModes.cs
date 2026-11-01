namespace GoveeBridge.Core;

/// <summary>
/// How GoveeBridge translates incoming RGB frames into lamp operations.
/// </summary>
public enum TransportMode
{
   /// <summary>
   /// Accept frames and log diagnostics; never open the lamp connection.
   /// </summary>
   Diagnostic,

   /// <summary>
   /// Average each frame into one color using the official LAN API.
   /// </summary>
   GoveeStatic,

   /// <summary>
   /// Send full RGB frames using the experimental Razer LAN protocol.
   /// </summary>
   GoveeRazer
}
