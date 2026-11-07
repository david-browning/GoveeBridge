using System.Net.Sockets;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Protocol;

namespace GoveeBridge.Host;

public sealed partial class BridgeRuntime
{
   /// <summary>
   /// Performs the common physical status request and observation pipeline.
   /// Both periodic polling and the explicit lamp-status command use this path;
   /// the caller must already hold _lampGate.
   /// </summary>
   private async Task<GoveeDeviceStatus?> RefreshLampObservationUnsafeAsync(CancellationToken ct)
   {
      GoveeDeviceStatus? status = await _lamp!.GetStatusAsync(ct);
      await ObserveStatusUnsafeAsync(status, ct);
      return status;
   }

   /// <summary>Updates connectivity, observation history, and remote-takeover evidence.</summary>
   private async Task ObserveStatusUnsafeAsync(
      GoveeDeviceStatus? status, CancellationToken ct)
   {
      // Timeout/no usable response is not an observed OFF state. It merely
      // means we don't currently have a fresh status; never infer takeover
      // from a missing response.
      if (status is null)
      {
         UpdateConnectivityUnsafe(false);
         return;
      }

      UpdateConnectivityUnsafe(true);
      // Preserve the old observation for change detection, and refresh the
      // timestamp EVEN IF all status fields are unchanged.
      GoveeDeviceStatus? previous = _observed.Observe(status);

      if (previous != status)
      {
         if (_options.LogLampStatusChanges)
         {
            _log.LogInformation(
               "Observed lamp state: On={On}, Brightness={Brightness}%, RGB={Color}, Kelvin={Kelvin}",
               status.IsOn, status.Brightness, status.Color, status.ColorTemperatureKelvin);
         }
         PublishUnsafe(BridgeEventKind.LampStatusChanged, "Observed via devStatus");
      }

      if (_session.Owner != ControlOwner.Pc)
      {
         return;
      }

      // Only treat a later OFF as a possible external takeover after we've
      // observed this PC-owned session in an ON state. This avoids treating
      // an initial stale OFF reply as a remote power-off event.
      if (status.IsOn)
      {
         _session.ObservedOnDuringOwnership = true;
      }

      // Firmware reports state, not WHO caused it. An OFF after an observed
      // ON is our strongest available signal of manual intervention. The
      // grace interval helps reject stale replies after our power-on send.
      if (_settings.AutoReleaseWhenRemoteTurnsOff && !status.IsOn &&
         _session.ObservedOnDuringOwnership &&
         DateTimeOffset.UtcNow - _attempts.LastPowerOnRequestedAt >=
            TimeSpan.FromMilliseconds(_options.RemotePowerOffGraceMs))
      {
         await ReleaseUnsafeAsync("Observed external power-off", ct);
         return;
      }

      if (_settings.TransportMode == TransportMode.GoveeStatic)
      {
         await ReconcileStaticColorUnsafeAsync(status, previous, ct);
      }
   }

   /// <summary>Reports whether the latest devStatus is fresh enough for best-effort checks.</summary>
   private bool HasRecentStatusUnsafe()
   {
      // Freshness is a heuristic window, not a proof that nobody used the
      // remote after the query. Stale status must not suppress new commands.
      return _observed.IsRecent(_settings);
   }

   /// <summary>Raises online/offline transitions only when connectivity changes.</summary>
   private void UpdateConnectivityUnsafe(bool online)
   {
      if (_observed.Online == online)
      {
         return;
      }
      _observed.Online = online;
      if (online)
      {
         _log.LogInformation("Lamp online");
      }
      else
      {
         _log.LogWarning("Lamp unreachable or status timed out");
      }
      PublishUnsafe(online ? BridgeEventKind.LampOnline : BridgeEventKind.LampOffline,
         online ? "online" : "offline");
   }

   /// <summary>Publishes without performing synchronous I/O; caller holds _lampGate.</summary>
   private void PublishUnsafe(BridgeEventKind kind, string details) =>
      _events.Publish(new BridgeEvent(kind, DateTimeOffset.UtcNow, _observed.Status, details));

   /// <summary>Creates a typed, immutable host snapshot with the existing JSON field names.</summary>
   private BridgeHostStatusSnapshot SnapshotUnsafe() => new(
      Mode: _settings.TransportMode.ToString(),
      Ownership: _session.IsPcOwner ? "PC" : "Manual",
      RazerArmed: _session.RealtimeArmed,
      LampOnline: _observed.Online,
      LampStatusObservedAt: _observed.ObservedAt,
      LampStatusMayBeStale: _session.RealtimeArmed && !_options.PollWhileRealtimeArmed,
      FramesReceived: _frames.ReceivedCount,
      FramesSent: _attempts.FramesSent,
      PowerCommandsSent: _attempts.PowerCommandsSent,
      LastStaticColor: _attempts.StaticColorRequest?.Color.ToString(),
      StaticVerified: _attempts.StaticColorRequest?.Verified,
      StaticAttempts: _attempts.StaticColorRequest?.Attempts,
      StaticRetryPending: _attempts.StaticColorRequest?.RetryPending,
      StaticSuspended: _attempts.StaticColorRequest?.Suspended,
      Lamp: _observed.Status);
}
