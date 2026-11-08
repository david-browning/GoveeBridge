using System.Net.Sockets;
using System.Text.Json;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;

namespace GoveeBridge.Host;

public sealed partial class BridgeRuntime
{
   /// <summary>
   /// Dispatches management commands. Each handler assumes _lampGate is already held;
   /// frames use ReceiveFrame instead and never enter this management queue.
   /// </summary>
   public async Task<BridgeIpcResponse> ExecuteCommandAsync(
      JsonDocument buffer,
      CancellationToken ct)
   {
      JsonElement root = buffer.RootElement;

      if (!root.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String || type.GetString() != "command" ||
         !root.TryGetProperty("action", out JsonElement action) || action.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(action.GetString()))
      {
         return new BridgeIpcErrorResponse($"Invalid command packet");
      }

      var command = action.GetString();
      ArgumentException.ThrowIfNullOrWhiteSpace(command);

      JsonElement? data = root.TryGetProperty(
         BridgeIpcPropertyNames.Data, out JsonElement value) ? value : null;

      await _lampGate.WaitAsync(ct);
      try
      {
         switch (command.Trim().ToLowerInvariant())
         {
            case BridgeIpcActions.Resume:
            {
               return ResumeUnsafe();
            }
            case BridgeIpcActions.Release:
            {
               return await ReleaseCommandUnsafeAsync(ct);
            }
            case BridgeIpcActions.GetHostStatus:
            {
               return SnapshotUnsafe();
            }
            case BridgeIpcActions.GetLampStatus:
            {
               return await GetLampStatusUnsafeAsync(ct);
            }
            case BridgeIpcActions.SetPower:
            {
               var request = DeserializePayload<BridgeIpcPowerPayload>(data);
               return await SetLampPowerUnsafeAsync(request.IsOn, ct);
            }
            case BridgeIpcActions.SetColorTemperature:
            {
               var request = DeserializePayload<BridgeIpcColorTemperaturePayload>(data);
               return await SetLampTemperatureUnsafeAsync(request.Kelvin, ct);
            }
            case BridgeIpcActions.SetBrightness:
            {
               var request = DeserializePayload<BridgeIpcBrightnessPayload>(data);
               return await SetLampBrightnessUnsafeAsync(request.BrightnessPercent, ct);
            }
            default:
            {
               return new BridgeIpcErrorResponse($"Unknown command: {command}");
            }
         }
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
         throw;
      }
      catch (Exception ex) when (ex is JsonException or ArgumentException)
      {
         _log.LogWarning(ex, "Invalid IPC command payload.");
         return new BridgeIpcErrorResponse(ex.Message);
      }
      catch (Exception ex)
      {
         _log.LogError(ex, "Command execution failed.");
         return new BridgeIpcErrorResponse(
            "An internal error occurred while processing the command.");
      }
      finally
      {
         _lampGate.Release();
      }
   }

   private static T DeserializePayload<T>(JsonElement? data) where T : class
   {
      if (data is not JsonElement payload ||
          payload.ValueKind != JsonValueKind.Object)
      {
         throw new ArgumentException(
            $"Missing or invalid payload for {typeof(T).Name}.");
      }

      return payload.Deserialize<T>()
         ?? throw new JsonException(
            $"Could not deserialize {typeof(T).Name}.");
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
      bool isOn,
      CancellationToken ct)
   {
      var check = CheckLampReady();
      if(check != null)
      {
         return check;
      }

      await _lamp!.SetPowerAsync(isOn, ct);

      // This describes Host bookkeeping, not confirmation that
      // the physical lamp applied the requested change.
      return SnapshotUnsafe();
   }

   private async Task<BridgeIpcResponse> SetLampTemperatureUnsafeAsync(
      int kelvin,
      CancellationToken ct)
   {
      var check = CheckLampReady();
      if (check != null)
      {
         return check;
      }

      await _lamp!.SetColorTemperatureAsync(kelvin, ct);
      return SnapshotUnsafe();
   }

   private async Task<BridgeIpcResponse> SetLampBrightnessUnsafeAsync(
      int percent,
      CancellationToken ct)
   {
      var check = CheckLampReady();
      if (check != null)
      {
         return check;
      }

      await _lamp!.SetBrightnessAsync(percent, ct);
      return SnapshotUnsafe();
   }

   /// <summary>
   /// Returns null if the lamp is ready for a command. 
   /// Otherwise returns the error that should be propagated up the host.
   /// </summary>
   /// <returns></returns>
   private BridgeIpcErrorResponse? CheckLampReady()
   {
      if (_lamp is null)
      {
         return new BridgeIpcErrorResponse(
            "Diagnostic mode has no physical lamp connection");
      }

      // Do not change the lamp unless PC control was granted.
      if (!_session.IsPcOwner)
      {
         return new BridgeIpcErrorResponse(
            "PC control is required before changing lamp power.");
      }

      return null;
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
