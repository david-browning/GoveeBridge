namespace GoveeBridge.Core;

public sealed class BridgeSettings
{
   public TransportMode TransportMode { get; set; } = TransportMode.Diagnostic;

   public string? LampIp { get; set; }

   public int FrameListenPort { get; set; } =
       BridgeSettingsDefaults.FrameListenPort;

   public int LogicalLedCount { get; set; } =
      BridgeSettingsDefaults.LogicalLedCount;

   public int MinimumSendIntervalMs { get; set; } =
       BridgeSettingsDefaults.MinimumSendIntervalMs;

   public int RemotePollIntervalMs { get; set; } =
       BridgeSettingsDefaults.RemotePollIntervalMs;

   public int LampTimeoutMs { get; set; } =
       BridgeSettingsDefaults.LampTimeoutMs;

   public bool AutoReleaseWhenRemoteTurnsOff { get; set; } = true;

   public void Validate()
   {
      ValidateRange(
          FrameListenPort,
          nameof(FrameListenPort),
          BridgeSettingsConstraints.MinimumFrameListenPort,
          BridgeSettingsConstraints.MaximumFrameListenPort);

      ValidateRange(
          LogicalLedCount,
          nameof(LogicalLedCount),
          BridgeSettingsConstraints.MinimumLogicalLedCount,
          BridgeSettingsConstraints.MaximumLogicalLedCount);

      ValidateRange(
          MinimumSendIntervalMs,
          nameof(MinimumSendIntervalMs),
          BridgeSettingsConstraints.MinimumSendIntervalMs,
          BridgeSettingsConstraints.MaximumSendIntervalMs);

      ValidateRange(
          RemotePollIntervalMs,
          nameof(RemotePollIntervalMs),
          BridgeSettingsConstraints.MinimumRemotePollIntervalMs,
          BridgeSettingsConstraints.MaximumRemotePollIntervalMs);

      ValidateRange(
          LampTimeoutMs,
          nameof(LampTimeoutMs),
          BridgeSettingsConstraints.MinimumLampTimeoutMs,
          BridgeSettingsConstraints.MaximumLampTimeoutMs);

      if (TransportMode == TransportMode.GoveeStatic &&
          !System.Net.IPAddress.TryParse(LampIp, out _))
      {
         throw new ArgumentException(
             "GoveeStatic requires Bridge:LampIp in a private configuration file.",
             nameof(LampIp));
      }
   }

   private static void ValidateRange(
       int value,
       string parameterName,
       int minimum,
       int maximum)
   {
      if (value < minimum || value > maximum)
      {
         throw new ArgumentOutOfRangeException(
             parameterName,
             value,
             $"Value must be between {minimum} and {maximum}.");
      }
   }
}
