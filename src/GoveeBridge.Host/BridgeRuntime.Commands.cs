using System.Net.Sockets;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Protocol;

namespace GoveeBridge.Host;

public sealed partial class BridgeRuntime
{
   /// <summary>
   /// Dispatches management commands. Each handler assumes _lampGate is already held;
   /// frames use ReceiveFrame instead and never enter this management queue.
   /// </summary>
   public async Task<BridgeIpcResponse> ExecuteCommandAsync(
      string command, 
      CancellationToken ct)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(command);
      await _lampGate.WaitAsync(ct);
      try
      {
         return command.Trim().ToLowerInvariant() switch
         {
            BridgeIpcActions.Resume => ResumeUnsafe(),
            BridgeIpcActions.Release => await ReleaseCommandUnsafeAsync(ct),
            BridgeIpcActions.GetHostStatus => SnapshotUnsafe(),
            BridgeIpcActions.GetLampStatus => await GetLampStatusUnsafeAsync(ct),
            BridgeIpcActions.SetPower => await SetLampPowerUnsafeAsync(ct),
            _ => new BridgeIpcErrorResponse($"Unknown command: {command}")
         };
      }
      finally
      {
         _lampGate.Release();
      }
   }

   /// <summary>
   /// Grants PC control without replaying a frame received in Manual mode.
   /// </summary>
   private BridgeHostStatusSnapshot ResumeUnsafe()
   {
      if (!_session.IsPcOwner)
      {
         _frames.Clear();
         _session.BeginPcOwnership(HasRecentStatusUnsafe() && _observed.Status?.IsOn == true);
         _attempts.ResetForNewSession();
         _log.LogInformation("PC control granted by explicit resume command");
         PublishUnsafe(BridgeEventKind.ControlOwnershipChanged, "PC");
      }

      return SnapshotUnsafe();
   }

   /// <summary>
   /// Explicitly relinquishes control, then returns a typed runtime snapshot.
   /// </summary>
   private async Task<BridgeHostStatusSnapshot> ReleaseCommandUnsafeAsync(
      CancellationToken ct)
   {
      await ReleaseUnsafeAsync("Explicit release", ct);
      return SnapshotUnsafe();
   }

   /// <summary>
   /// Performs a fresh device query, distinct from GetHostStatus's cached snapshot.
   /// Returns a typed error if polling would be unsupported or disruptive.
   /// </summary>
   private async Task<BridgeIpcResponse> GetLampStatusUnsafeAsync(
      CancellationToken ct)
   {
      if (_lamp is null)
      {
         return new BridgeIpcErrorResponse(
            "Diagnostic mode has no physical lamp connection");
      }

      if (_session.RealtimeArmed && !_options.PollWhileRealtimeArmed)
      {
         return new BridgeIpcErrorResponse(
            "devStatus is intentionally suspended while Razer mode is armed", SnapshotUnsafe());
      }

      GoveeDeviceStatus? status = await RefreshLampObservationUnsafeAsync(ct);
      return new BridgeIpcLampStatusResponse(status, SnapshotUnsafe());
   }

   /// <summary>
   /// Returns to Manual mode before attempting physical disarm, never powering off
   /// or restoring a previous color (which could override the user's remote).
   /// </summary>
   private async Task ReleaseUnsafeAsync(string reason, CancellationToken ct)
   {
      bool changed = _session.IsPcOwner || _session.RealtimeArmed;
      bool disarm = _session.RealtimeArmed;

      _session.Release();
      _attempts.ResetOnRelease();
      _frames.Clear();

      if (changed)
      {
         _log.LogInformation("Manual ownership: {Reason}", reason);
         PublishUnsafe(BridgeEventKind.ControlOwnershipChanged, reason);
      }

      // Existing behavior retained: if this fails, the Razer armed flag is no
      // longer held. Disarm/retry policy is a separate unresolved concern.
      if (disarm && _lamp is not null)
      {
         try
         {
            await _lamp.SetRealtimeEnabledAsync(false, ct);
            _log.LogInformation("Razer mode disarm requested");
         }
         catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
         {
            _log.LogWarning(ex, "Razer disarm failed: physical mode may still be armed");
         }
      }
   }

   private async Task<BridgeIpcResponse> SetLampPowerUnsafeAsync(
      CancellationToken ct)
   {
      throw new NotImplementedException();
   }

   /// <summary>
   /// Uses a separate timeout because the worker's cancellation token is already
   /// cancelled by the time the Host attempts its final disarm.
   /// </summary>
   public async Task ShutdownAsync()
   {
      // The worker's cancellation token is already canceled at shutdown.
      // Use a fresh, bounded token to permit one last disarm attempt rather
      // than cancelling it immediately or delaying service shutdown forever.
      using CancellationTokenSource timeout = new(
         TimeSpan.FromMilliseconds(_settings.LampTimeoutMs));

      try
      {
         await _lampGate.WaitAsync(timeout.Token);
         try
         {
            await ReleaseUnsafeAsync("Host stopping", timeout.Token);
         }
         finally
         {
            _lampGate.Release();
         }
      }
      catch (OperationCanceledException)
      {
         _log.LogWarning("Timed out while releasing the lamp during shutdown");
      }
   }
}
