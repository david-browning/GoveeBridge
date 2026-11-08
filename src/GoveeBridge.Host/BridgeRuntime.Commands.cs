using System.Net.Sockets;
using System.Text.Json;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;

namespace GoveeBridge.Host;

public sealed partial class BridgeRuntime
{
   /// <summary>
   /// Executes one validated IPC command while serializing ownership and lamp I/O.
   /// The Worker checks the JSON envelope; Runtime understands only the action
   /// and the existing operation-specific Core payload DTOs.
   /// </summary>
   /// <param name="command">An IPC action name, such as <see cref="BridgeIpcActions.SetPower"/>.</param>
   /// <param name="data">The optional JSON payload. Parameterless actions omit it.</param>
   /// <param name="ct">Cancels the command and acquisition of the lamp gate.</param>
   public async Task<BridgeIpcResponse> ExecuteCommandAsync(
      string command,
      JsonElement? data,
      CancellationToken ct)
   {
      if (string.IsNullOrWhiteSpace(command))
      {
         return new BridgeIpcErrorResponse("Invalid command packet");
      }

      // Keep JSON envelope validation in BridgeWorker. Deserializing the small
      // command-specific payload here avoids creating a parallel command model.
      string action = command.Trim().ToLowerInvariant();

      await _lampGate.WaitAsync(ct);
      try
      {
         return action switch
         {
            BridgeIpcActions.Resume => ResumeUnsafe(),
            BridgeIpcActions.Release => await ReleaseCommandUnsafeAsync(ct),
            BridgeIpcActions.GetHostStatus => SnapshotUnsafe(),
            BridgeIpcActions.GetLampStatus => await GetLampStatusUnsafeAsync(ct),
            BridgeIpcActions.SetPower => await SetLampPowerUnsafeAsync(
               DeserializePayload<BridgeIpcPowerPayload>(data).IsOn, ct),
            BridgeIpcActions.SetColorTemperature => await SetLampTemperatureUnsafeAsync(
               DeserializePayload<BridgeIpcColorTemperaturePayload>(data).Kelvin, ct),
            BridgeIpcActions.SetBrightness => await SetLampBrightnessUnsafeAsync(
               DeserializePayload<BridgeIpcBrightnessPayload>(data).BrightnessPercent, ct),
            _ => new BridgeIpcErrorResponse($"Unknown command: {command}")
         };
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
         // Host cancellation is not a malformed command or device failure.
         throw;
      }
      catch (Exception ex) when (ex is JsonException or ArgumentException)
      {
         _log.LogWarning(ex, "Invalid IPC command payload");
         return new BridgeIpcErrorResponse(ex.Message);
      }
      catch (Exception ex)
      {
         _log.LogError(ex, "Command execution failed");
         return new BridgeIpcErrorResponse(
            "An internal error occurred while processing the command.");
      }
      finally
      {
         _lampGate.Release();
      }
   }

   /// <summary>
   /// Deserializes an existing Core IPC DTO. Only commands with parameters call
   /// this helper; resume, release, and status do not require a data object.
   /// </summary>
   private static T DeserializePayload<T>(JsonElement? data) where T : class
   {
      if (data is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
      {
         throw new ArgumentException($"Missing or invalid payload for {typeof(T).Name}.");
      }

      return payload.Deserialize<T>()
         ?? throw new JsonException($"Could not deserialize {typeof(T).Name}.");
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
   private async Task<BridgeHostStatusSnapshot> ReleaseCommandUnsafeAsync(CancellationToken ct)
   {
      await ReleaseUnsafeAsync("Explicit release", ct);
      return SnapshotUnsafe();
   }

   /// <summary>
   /// Performs a fresh physical status query rather than returning only the
   /// cached snapshot. Razer streaming may intentionally suspend this query.
   /// </summary>
   private async Task<BridgeIpcResponse> GetLampStatusUnsafeAsync(CancellationToken ct)
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
   /// Returns control to Manual before attempting Razer disarm. Never powers
   /// off or restores a previous color, so it does not fight the remote.
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

      // Existing behavior preserved: a failed disarm is logged, but the armed
      // flag was already cleared. Recovery is a separate policy decision.
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

   /// <summary>
   /// Requests power without implying physical acknowledgment.
   /// </summary>
   private async Task<BridgeIpcResponse> SetLampPowerUnsafeAsync(
      bool isOn, 
      CancellationToken ct)
   {
      BridgeIpcErrorResponse? error = CheckPcLampAccessUnsafe();
      if (error is not null)
      {
         return error;
      }

      await _lamp!.SetPowerAsync(isOn, ct);
      return SnapshotUnsafe();
   }

   /// <summary>
   /// Requests white-temperature mode without changing control ownership.
   /// </summary>
   private async Task<BridgeIpcResponse> SetLampTemperatureUnsafeAsync(int kelvin, CancellationToken ct)
   {
      BridgeIpcErrorResponse? error = CheckPcLampAccessUnsafe();
      if (error is not null)
      {
         return error;
      }

      await _lamp!.SetColorTemperatureAsync(kelvin, ct);
      return SnapshotUnsafe();
   }

   /// <summary>
   /// Requests device brightness. The physical client's range is 1..100;
   /// zero-brightness mapping remains an explicit, unresolved Host policy.
   /// </summary>
   private async Task<BridgeIpcResponse> SetLampBrightnessUnsafeAsync(int percent, CancellationToken ct)
   {
      BridgeIpcErrorResponse? error = CheckPcLampAccessUnsafe();
      if (error is not null)
      {
         return error;
      }

      await _lamp!.SetBrightnessAsync(percent, ct);
      return SnapshotUnsafe();
   }

   /// <summary>
   /// Guards all direct writes. Caller holds _lampGate; neither this method
   /// nor individual command handlers should acquire the semaphore again.
   /// Returns null only when PC owns an available physical lamp client.
   /// </summary>
   private BridgeIpcErrorResponse? CheckPcLampAccessUnsafe()
   {
      if (_lamp is null)
      {
         return new BridgeIpcErrorResponse(
            "Diagnostic mode has no physical lamp connection");
      }

      if (!_session.IsPcOwner)
      {
         return new BridgeIpcErrorResponse(
            "PC control is required before changing the lamp.");
      }

      return null;
   }

   /// <summary>
   /// Uses a separate timeout because the Worker's cancellation token is
   /// already canceled by the time shutdown attempts its final disarm.
   /// </summary>
   public async Task ShutdownAsync()
   {
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
