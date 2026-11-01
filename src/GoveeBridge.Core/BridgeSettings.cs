using System.Net;
using System.Net.Sockets;

namespace GoveeBridge.Core;

/// <summary>
/// Configures the bridge's local input and the single physical lamp it owns.
/// These are deployment settings, not model-specific device capabilities.
/// </summary>
public sealed class BridgeSettings
{
   public TransportMode TransportMode { get; set; } = TransportMode.Diagnostic;
   public string? LampIp { get; set; }
   public int FrameListenPort { get; set; } = BridgeSettingsDefaults.FrameListenPort;
   public int LogicalLedCount { get; set; } = BridgeSettingsDefaults.LogicalLedCount;
   public int MinimumSendIntervalMs { get; set; } = BridgeSettingsDefaults.MinimumSendIntervalMs;
   public int RemotePollIntervalMs { get; set; } = BridgeSettingsDefaults.RemotePollIntervalMs;
   public int LampTimeoutMs { get; set; } = BridgeSettingsDefaults.LampTimeoutMs;
   public bool AutoReleaseWhenRemoteTurnsOff { get; set; } = true;

   /// <summary>
   /// Rejects invalid settings before any UDP sockets are opened.
   /// </summary>
   public void Validate()
   {
      ValidateRange(FrameListenPort, nameof(FrameListenPort),
         BridgeSettingsConstraints.MinimumFrameListenPort,
         BridgeSettingsConstraints.MaximumFrameListenPort);
      ValidateRange(LogicalLedCount, nameof(LogicalLedCount),
         BridgeSettingsConstraints.MinimumLogicalLedCount,
         BridgeSettingsConstraints.MaximumLogicalLedCount);
      ValidateRange(MinimumSendIntervalMs, nameof(MinimumSendIntervalMs),
         BridgeSettingsConstraints.MinimumSendIntervalMs,
         BridgeSettingsConstraints.MaximumSendIntervalMs);
      ValidateRange(RemotePollIntervalMs, nameof(RemotePollIntervalMs),
         BridgeSettingsConstraints.MinimumRemotePollIntervalMs,
         BridgeSettingsConstraints.MaximumRemotePollIntervalMs);
      ValidateRange(LampTimeoutMs, nameof(LampTimeoutMs),
         BridgeSettingsConstraints.MinimumLampTimeoutMs,
         BridgeSettingsConstraints.MaximumLampTimeoutMs);

      if (!Enum.IsDefined(TransportMode))
      {
         throw new ArgumentOutOfRangeException(nameof(TransportMode));
      }

      if (TransportMode != TransportMode.Diagnostic &&
         (!IPAddress.TryParse(LampIp, out IPAddress? address) ||
             address.AddressFamily != AddressFamily.InterNetwork))
      {
         throw new ArgumentException(
            "Physical Govee transports require Bridge:LampIp to be an IPv4 address.",
            nameof(LampIp));
      }

      // This is an encoder field-width constraint, not a physical LED count.
      if (TransportMode == TransportMode.GoveeRazer && LogicalLedCount > byte.MaxValue)
      {
         throw new ArgumentOutOfRangeException(nameof(LogicalLedCount),
            "Razer's frame color-count field holds at most 255 positions.");
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
