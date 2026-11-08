using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;

namespace GoveeBridge.Host;

public sealed partial class BridgeRuntime
{
   /// <summary>
   /// Updates this lamp's observation cache from a fresh physical status query.
   /// Called by polling and lamp-status while the device gate is held.
   /// </summary>
   private async Task<GoveeDeviceStatus?> RefreshLampObservationUnsafeAsync(LampSession lamp, CancellationToken ct)
   {
      GoveeDeviceStatus? status = await lamp.Client!.GetStatusAsync(ct);
      await ObserveStatusUnsafeAsync(lamp, status, ct);
      return status;
   }

   /// <summary>
   /// Records physical observations and conservatively detects possible external
   /// power/color overrides without conflating a timeout with an OFF state.
   /// </summary>
   private async Task ObserveStatusUnsafeAsync(
      LampSession lamp, GoveeDeviceStatus? status, CancellationToken ct)
   {
      // Timeout/no usable response is not an observed OFF state. It merely
      // means we don't currently have a fresh status; never infer takeover
      // from a missing response.
      if (status is null)
      {
         UpdateConnectivityUnsafe(lamp, false);
         return;
      }

      UpdateConnectivityUnsafe(lamp, true);
      // Preserve the old observation for change detection, and refresh the
      // timestamp EVEN IF all status fields are unchanged.
      GoveeDeviceStatus? previous = lamp.Observed.Observe(status);

      if (previous != status)
      {
         if (_options.LogLampStatusChanges)
         {
            _log.LogInformation(
               "Observed \"{Name}\" state: On={On}, Brightness={Brightness}%, RGB={Color}, Kelvin={Kelvin}",
               lamp.Settings.Id, status.IsOn, status.Brightness, status.Color, status.ColorTemperatureKelvin);
         }
         PublishUnsafe(lamp, BridgeEventKind.LampStatusChanged, "Observed via devStatus");
      }

      if (!lamp.Session.IsPcOwner)
      {
         return;
      }

      // Only treat a later OFF as a possible external takeover after we've
      // observed this PC-owned session in an ON state. This avoids treating
      // an initial stale OFF reply as a remote power-off event.
      if (status.IsOn)
      {
         lamp.Session.ObservedOnDuringOwnership = true;
      }

      // Firmware reports state, not WHO caused it. An OFF after an observed
      // ON is our strongest available signal of manual intervention. The
      // grace interval helps reject stale replies after our power-on send.
      if (_settings.AutoReleaseWhenRemoteTurnsOff && !status.IsOn &&
         lamp.Session.RequestedPowerOn != false &&
         lamp.Session.ObservedOnDuringOwnership &&
         DateTimeOffset.UtcNow - lamp.Attempts.LastPowerOnRequestedAt >=
            TimeSpan.FromMilliseconds(_options.RemotePowerOffGraceMs))
      {
         await ReleaseUnsafeAsync(lamp, "Observed external power-off", ct);
         return;
      }

      if (lamp.Session.Mode == ControlMode.Pc &&
         lamp.Attempts.StaticColorRequest is not null)
      {
         await ReconcileStaticColorUnsafeAsync(lamp, status, previous, ct);
      }
   }

   /// <summary>
   /// Reports whether devStatus is fresh enough for best-effort observation checks.
   /// </summary>
   private bool HasRecentStatusUnsafe(LampSession lamp)
   {
      // Freshness is a heuristic window, not a proof that nobody used the
      // remote after the query. Stale status must not suppress new commands.
      return lamp.Observed.IsRecent(_settings);
   }

   /// <summary>
   /// Raises per-device online/offline events only after an observed transition.
   /// </summary>
   private void UpdateConnectivityUnsafe(LampSession lamp, bool online)
   {
      if (lamp.Observed.Online == online)
      {
         return;
      }

      lamp.Observed.Online = online;
      if (online)
      {
         _log.LogInformation($"Lamp \"{lamp.Settings.Id}\" online");
      }
      else
      {
         _log.LogWarning($"Lamp \"{lamp.Settings.Id}\" unreachable or status timed out");
      }
      
      PublishUnsafe(lamp, online ? BridgeEventKind.LampOnline : BridgeEventKind.LampOffline,
         online ? "online" : "offline");
   }

   /// <summary>
   /// Publishes an event for this lamp while its session gate is held.
   /// </summary>
   private void PublishUnsafe(LampSession lamp, BridgeEventKind kind, string details) =>
      _events.Publish(new BridgeEvent(kind, DateTimeOffset.UtcNow,
         lamp.Observed.Status, details, lamp.Settings.Id));

   /// <summary>
   /// Creates a typed per-lamp snapshot. Mode now means Host ControlMode, not
   /// the removed configuration TransportMode; lampId identifies the target.
   /// </summary>
   private BridgeHostStatusSnapshot SnapshotUnsafe(LampSession lamp) => new(
      Mode: lamp.Session.Mode.ToString(),
      Ownership: lamp.Session.IsPcOwner ? "PC" : "Manual",
      RazerArmed: lamp.Session.RealtimeArmed,
      LampOnline: lamp.Observed.Online,
      LampStatusObservedAt: lamp.Observed.ObservedAt,
      LampStatusMayBeStale: lamp.Session.RealtimeArmed && !_options.PollWhileRealtimeArmed,
      FramesReceived: lamp.Frames.ReceivedCount,
      FramesSent: lamp.Attempts.FramesSent,
      PowerCommandsSent: lamp.Attempts.PowerCommandsSent,
      LastStaticColor: lamp.Attempts.StaticColorRequest?.Color.ToString(),
      StaticVerified: lamp.Attempts.StaticColorRequest?.Verified,
      StaticAttempts: lamp.Attempts.StaticColorRequest?.Attempts,
      StaticRetryPending: lamp.Attempts.StaticColorRequest?.RetryPending,
      StaticSuspended: lamp.Attempts.StaticColorRequest?.Suspended,
      Lamp: lamp.Observed.Status,
      LampId: lamp.Settings.Id,
      RequestedPowerOn: lamp.Session.RequestedPowerOn);
}
