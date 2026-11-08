using System.Net;

namespace GoveeBridge.Core;

/// <summary>
/// Configures one Host IPC endpoint and its manually selected lamps. Physical
/// discovery and automatic device enrollment are intentionally deferred.
/// </summary>
public sealed class BridgeSettings
{
   /// <summary>
   /// Runs IPC/state-machine diagnostics without opening a Govee LAN socket.
   /// </summary>
   public bool Diagnostic { get; set; } = true;

   /// <summary>
   /// Configured physical lamps. Diagnostic mode allows this collection to be empty.
   /// </summary>
   public List<BridgeLampSettings> Lamps { get; set; } = [];

   /// <summary>
   /// Loopback UDP port used by clients, independent of Govee's LAN ports.
   /// </summary>
   public int FrameListenPort { get; set; } = BridgeSettingsDefaults.FrameListenPort;

   /// <summary>
   /// Maximum RGB positions accepted in a client frame. Each lamp can impose
   /// a stricter RealtimeZoneCount through its capability profile.
   /// </summary>
   public int LogicalLedCount { get; set; } = BridgeSettingsDefaults.LogicalLedCount;

   /// <summary>
   /// Minimum spacing between static RGB command submissions.
   /// </summary>
   public int MinimumSendIntervalMs { get; set; } = BridgeSettingsDefaults.MinimumSendIntervalMs;

   /// <summary>
   /// Frequency of best-effort physical observations.
   /// </summary>
   public int RemotePollIntervalMs { get; set; } = BridgeSettingsDefaults.RemotePollIntervalMs;

   /// <summary>
   /// Per-lamp status-query deadline, in milliseconds.
   /// </summary>
   public int LampTimeoutMs { get; set; } = BridgeSettingsDefaults.LampTimeoutMs;

   /// <summary>
   /// Conservatively returns to Manual following an observed unexpected OFF.
   /// </summary>
   public bool AutoReleaseWhenRemoteTurnsOff { get; set; } = true;

   /// <summary>
   /// Validates settings before creating the shared LAN response listener.
   /// </summary>
   public void Validate()
   {
      Check(FrameListenPort, nameof(FrameListenPort),
         BridgeSettingsConstraints.MinimumFrameListenPort, BridgeSettingsConstraints.MaximumFrameListenPort);
      Check(LogicalLedCount, nameof(LogicalLedCount),
         BridgeSettingsConstraints.MinimumLogicalLedCount, BridgeSettingsConstraints.MaximumLogicalLedCount);
      Check(MinimumSendIntervalMs, nameof(MinimumSendIntervalMs),
         BridgeSettingsConstraints.MinimumSendIntervalMs, BridgeSettingsConstraints.MaximumSendIntervalMs);
      Check(RemotePollIntervalMs, nameof(RemotePollIntervalMs),
         BridgeSettingsConstraints.MinimumRemotePollIntervalMs, BridgeSettingsConstraints.MaximumRemotePollIntervalMs);
      Check(LampTimeoutMs, nameof(LampTimeoutMs),
         BridgeSettingsConstraints.MinimumLampTimeoutMs, BridgeSettingsConstraints.MaximumLampTimeoutMs);

      if (Lamps is null)
      {
         throw new ArgumentException("Lamps collection cannot be null.");
      }
      if (!Diagnostic && Lamps.Count == 0)
      {
         throw new ArgumentException("Configure at least one lamp or enable Diagnostic.");
      }

      var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var ips = new HashSet<IPAddress>();
      foreach (BridgeLampSettings lamp in Lamps)
      {
         lamp.Validate(Diagnostic);
         if (lamp.Capabilities.RealtimeZoneCount is int zoneCount &&
            zoneCount > LogicalLedCount)
         {
            throw new ArgumentException(
               $"Lamp '{lamp.Id}' needs {zoneCount} zones; raise Bridge:LogicalLedCount accordingly.");
         }
         if (!ids.Add(lamp.Id))
         {
            throw new ArgumentException($"Duplicate lamp Id '{lamp.Id}'.");
         }
         if (!Diagnostic && !ips.Add(IPAddress.Parse(lamp.Ip)))
         {
            throw new ArgumentException($"Multiple lamps share address '{lamp.Ip}'; status routing would be ambiguous.");
         }
      }
   }

   /// <summary>
   /// Validates an integer option while retaining its conventional exception name.
   /// </summary>
   private static void Check(int value, string name, int min, int max)
   {
      if (value < min || value > max)
      {
         throw new ArgumentOutOfRangeException(name, value, $"Expected {min}..{max}.");
      }
   }
}
