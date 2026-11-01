namespace GoveeBridge.Host;

/// <summary>Default Host scheduling and remote-observation policies.</summary>
public static class BridgeHostDefaults
{
   public const int FramePumpIntervalMs = 50;
   public const int FrameStaleAfterMs = 5000;
   public const int ColorMismatchGraceMs = 1500;
   public const int ColorMismatchRequiredPolls = 2;
   public const int RealtimeMinimumFrameIntervalMs = 100;
   public const int RealtimeKeepaliveIntervalMs = 2000;
   public const int RealtimeArmDelayMs = 1200;
   public const int RemotePowerOffGraceMs = 1200;

   public const int CommandQueueCapacity = 64;
   public const int MaximumIpcDatagramBytes = 8192;
   public const int DiagnosticLogIntervalMs = 1000;
   public const int PowerCommandSettleDelayMs = 30;
}

/// <summary>Application-level bounds for Host options, not device capability limits.</summary>
public static class BridgeHostConstraints
{
   public const int MinimumFramePumpIntervalMs = 20;
   public const int MaximumFramePumpIntervalMs = 1000;
   public const int MinimumFrameStaleAfterMs = 500;
   public const int MaximumFrameStaleAfterMs = 60000;
   public const int MinimumColorMismatchGraceMs = 0;
   public const int MaximumColorMismatchGraceMs = 30000;
   public const int MinimumColorMismatchRequiredPolls = 2;
   public const int MaximumColorMismatchRequiredPolls = 10;
   public const int MinimumRealtimeFrameIntervalMs = 50;
   public const int MaximumRealtimeFrameIntervalMs = 5000;
   public const int MinimumRealtimeKeepaliveMs = 250;
   public const int MaximumRealtimeKeepaliveMs = 30000;
   public const int MinimumRealtimeArmDelayMs = 0;
   public const int MaximumRealtimeArmDelayMs = 5000;
   public const int MinimumRemotePowerOffGraceMs = 0;
   public const int MaximumRemotePowerOffGraceMs = 10000;
}

/// <summary>
/// Host scheduling, monitoring, and arbitration behavior. These settings do not
/// describe the capabilities or physical LED count of a Govee device.
/// </summary>
public sealed class BridgeHostOptions
{
   /// <summary>Poll physical status even while Manual owns the lamp.</summary>
   public bool EnableLampMonitoring { get; set; } = true;

   /// <summary>Log changed observed states; do not log every successful poll.</summary>
   public bool LogLampStatusChanges { get; set; } = true;

   public int FramePumpIntervalMs { get; set; } = BridgeHostDefaults.FramePumpIntervalMs;
   public int FrameStaleAfterMs { get; set; } = BridgeHostDefaults.FrameStaleAfterMs;

   /// <summary>
   /// Experimental; only applicable in static mode. Govee does not report who
   /// changed the color and may report an obsolete color during native scenes.
   /// </summary>
   public bool AutoReleaseOnColorMismatch { get; set; } = false;

   public int ColorMismatchGraceMs { get; set; } = BridgeHostDefaults.ColorMismatchGraceMs;
   public int ColorMismatchRequiredPolls { get; set; } = BridgeHostDefaults.ColorMismatchRequiredPolls;

   /// <summary>Fastest allowed outgoing Razer frame interval, even if inputs arrive faster.</summary>
   public int RealtimeMinimumFrameIntervalMs { get; set; } = BridgeHostDefaults.RealtimeMinimumFrameIntervalMs;

   /// <summary>
   /// Resend an unchanged Razer frame periodically as an experimental keepalive.
   /// This is intentionally different from deduplicating official static commands.
   /// </summary>
   public int RealtimeKeepaliveIntervalMs { get; set; } = BridgeHostDefaults.RealtimeKeepaliveIntervalMs;

   /// <summary>Delay after arming before sending the first multizone frame.</summary>
   public int RealtimeArmDelayMs { get; set; } = BridgeHostDefaults.RealtimeArmDelayMs;

   /// <summary>Suppress initial stale power-off responses after a local power-on request.</summary>
   public int RemotePowerOffGraceMs { get; set; } = BridgeHostDefaults.RemotePowerOffGraceMs;

   /// <summary>
   /// Default false: standard devStatus can stall or fail while Razer mode is armed.
   /// When false, cached status becomes stale during streaming; remote-off detection
   /// is necessarily best effort until streaming ends.
   /// </summary>
   public bool PollWhileRealtimeArmed { get; set; } = false;

   /// <summary>Validate scheduling bounds before starting the Host.</summary>
   public void Validate()
   {
      ValidateRange(FramePumpIntervalMs, nameof(FramePumpIntervalMs),
         BridgeHostConstraints.MinimumFramePumpIntervalMs, BridgeHostConstraints.MaximumFramePumpIntervalMs);
      ValidateRange(FrameStaleAfterMs, nameof(FrameStaleAfterMs),
         BridgeHostConstraints.MinimumFrameStaleAfterMs, BridgeHostConstraints.MaximumFrameStaleAfterMs);
      ValidateRange(ColorMismatchGraceMs, nameof(ColorMismatchGraceMs),
         BridgeHostConstraints.MinimumColorMismatchGraceMs, BridgeHostConstraints.MaximumColorMismatchGraceMs);
      ValidateRange(ColorMismatchRequiredPolls, nameof(ColorMismatchRequiredPolls),
         BridgeHostConstraints.MinimumColorMismatchRequiredPolls, BridgeHostConstraints.MaximumColorMismatchRequiredPolls);
      ValidateRange(RealtimeMinimumFrameIntervalMs, nameof(RealtimeMinimumFrameIntervalMs),
         BridgeHostConstraints.MinimumRealtimeFrameIntervalMs, BridgeHostConstraints.MaximumRealtimeFrameIntervalMs);
      ValidateRange(RealtimeKeepaliveIntervalMs, nameof(RealtimeKeepaliveIntervalMs),
         BridgeHostConstraints.MinimumRealtimeKeepaliveMs, BridgeHostConstraints.MaximumRealtimeKeepaliveMs);
      ValidateRange(RealtimeArmDelayMs, nameof(RealtimeArmDelayMs),
         BridgeHostConstraints.MinimumRealtimeArmDelayMs, BridgeHostConstraints.MaximumRealtimeArmDelayMs);
      ValidateRange(RemotePowerOffGraceMs, nameof(RemotePowerOffGraceMs),
         BridgeHostConstraints.MinimumRemotePowerOffGraceMs, BridgeHostConstraints.MaximumRemotePowerOffGraceMs);

      if (RealtimeKeepaliveIntervalMs < RealtimeMinimumFrameIntervalMs)
      {
         throw new ArgumentException(
            "RealtimeKeepaliveIntervalMs cannot be shorter than RealtimeMinimumFrameIntervalMs.",
            nameof(RealtimeKeepaliveIntervalMs));
      }
   }

   private static void ValidateRange(int value, string name, int minimum, int maximum)
   {
      if (value < minimum || value > maximum)
      {
         throw new ArgumentOutOfRangeException(name, value,
            $"Expected a value from {minimum} through {maximum}.");
      }
   }
}
