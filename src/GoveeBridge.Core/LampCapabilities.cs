namespace GoveeBridge.Core;

/// <summary>
/// Operator-configured device capabilities. Discovery and cloud capability
/// retrieval are not implemented; a successful UDP send is not proof of support.
/// </summary>
public sealed class LampCapabilities
{
   /// <summary>
   /// Indicates that ordinary LAN power commands are supported.
   /// </summary>
   public bool SupportsPower { get; set; } = true;

   /// <summary>
   /// Indicates that ordinary LAN brightness commands are supported.
   /// </summary>
   public bool SupportsBrightness { get; set; } = true;

   /// <summary>
   /// Indicates that solid RGB commands are supported.
   /// </summary>
   public bool SupportsRgb { get; set; } = true;

   /// <summary>
   /// Indicates that adjustable white-temperature commands are supported.
   /// </summary>
   public bool SupportsColorTemperature { get; set; } = true;

   /// <summary>
   /// An explicit opt-in to experimental real-time frames. Unknown models
   /// default to false; never infer support from an unanswered UDP send.
   /// </summary>
   public bool SupportsRealtime { get; set; }

   /// <summary>
   /// Exact logical zone count for real-time frames, when known. A null value
   /// permits any encoder-supported count but provides weaker validation.
   /// </summary>
   public int? RealtimeZoneCount { get; set; }

   /// <summary>
   /// Rejects impossible capability settings before opening sockets.
   /// </summary>
   public void Validate()
   {
      if (RealtimeZoneCount is int count && (count < 1 || count > byte.MaxValue))
      {
         throw new ArgumentOutOfRangeException(nameof(RealtimeZoneCount),
            "Real-time zone count must be between 1 and 255.");
      }
   }
}
