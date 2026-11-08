using System.Collections.Frozen;
using System.Net.Sockets;
using System.Text.Json;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;

namespace GoveeBridge.Host;

public sealed partial class BridgeRuntime
{
   /// <summary>
   /// Mode permissions are immutable policy data, not a configurable file.
   /// A permitted action may still fail a capability check or a physical send.
   /// </summary>
   [Flags]
   private enum ModeMask
   {
      None = 0,
      Manual = 1,
      Pc = 2,
      PcStreaming = 4,
      Owned = Pc | PcStreaming,
      Any = Manual | Owned
   }

   /// <summary>
   /// Central action-to-control-mode permission table. Mode transitions are
   /// handled by methods, not hidden as mutations of the table.
   /// </summary>
   private static readonly FrozenDictionary<string, ModeMask> _commandPermissions =
      new Dictionary<string, ModeMask>(StringComparer.OrdinalIgnoreCase)
      {
         [BridgeIpcActions.GetHostStatus] = ModeMask.Any,
         [BridgeIpcActions.GetLampStatus] = ModeMask.Any,
         [BridgeIpcActions.Resume] = ModeMask.Any,
         [BridgeIpcActions.Release] = ModeMask.Any,
         [BridgeIpcActions.Arm] = ModeMask.Owned,
         [BridgeIpcActions.Disarm] = ModeMask.Any,
         [BridgeIpcActions.SetPower] = ModeMask.Owned,
         [BridgeIpcActions.SetBrightness] = ModeMask.Owned,
         [BridgeIpcActions.SetSolidColor] = ModeMask.Pc,
         [BridgeIpcActions.SetColorTemperature] = ModeMask.Pc
      }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

   /// <summary>
   /// Selects a lamp by configured stable identity, checks the central mode
   /// policy, then executes one Core DTO payload under that lamp's state gate.
   /// Device-specific parameter validation belongs to the physical client.
   /// </summary>
   public async Task<BridgeIpcResponse> ExecuteCommandAsync(
      string command, JsonElement? data, string lampId, CancellationToken ct)
   {
      if (string.IsNullOrWhiteSpace(command))
      {
         return new BridgeIpcErrorResponse("Invalid command packet");
      }

      if (!TrySelectLamp(lampId, out LampSession? lamp))
      {
         return new BridgeIpcErrorResponse($"No lamp exists named {lampId}");
      }

      string action = command.Trim().ToLowerInvariant();
      await lamp!.Gate.WaitAsync(ct);
      try
      {
         if (!_commandPermissions.ContainsKey(action))
         {
            return new BridgeIpcErrorResponse($"Unknown command: {command}");
         }
         if (!IsCommandAllowedUnsafe(lamp, action))
         {
            return new BridgeIpcErrorResponse(
               $"Command '{action}' is not permitted in {lamp.Session.Mode} mode.",
               SnapshotUnsafe(lamp));
         }

         return action switch
         {
            BridgeIpcActions.GetHostStatus => SnapshotUnsafe(lamp),
            BridgeIpcActions.GetLampStatus => await GetLampStatusUnsafeAsync(lamp, ct),
            BridgeIpcActions.Resume => ResumeUnsafe(lamp),
            BridgeIpcActions.Release => await ReleaseCommandUnsafeAsync(lamp, ct),
            BridgeIpcActions.Arm => await ArmUnsafeAsync(lamp, ct),
            BridgeIpcActions.Disarm => await DisarmCommandUnsafeAsync(lamp, ct),
            BridgeIpcActions.SetPower => await SetLampPowerUnsafeAsync(lamp,
               DeserializePayload<BridgeIpcPowerPayload>(data).IsOn, ct),
            BridgeIpcActions.SetBrightness => await SetLampBrightnessUnsafeAsync(lamp,
               DeserializePayload<BridgeIpcBrightnessPayload>(data).BrightnessPercent, ct),
            BridgeIpcActions.SetSolidColor => await SetLampColorUnsafeAsync(
               lamp,
               DeserializePayload<BridgeIpcColorPayload>(data).Color ?? 
                  throw new JsonException("Color must be a non-null RGB object."),
               ct),
            BridgeIpcActions.SetColorTemperature => await SetLampTemperatureUnsafeAsync(lamp,
               DeserializePayload<BridgeIpcColorTemperaturePayload>(data).Kelvin, ct),
            _ => new BridgeIpcErrorResponse($"Unknown command: {command}")
         };
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
         throw;
      }
      catch (LampParameterException ex)
      {
         _log.LogWarning(ex, "Invalid lamp parameter for command {Action} on lamp {LampId}",
            action, lamp.Settings.Id);
         return new BridgeIpcErrorResponse(ex.Message);
      }
      catch (Exception ex) when (ex is JsonException or ArgumentException)
      {
         _log.LogWarning(ex, "Invalid IPC payload for lamp {LampId}", lamp.Settings.Id);
         return new BridgeIpcErrorResponse(ex.Message);
      }
      catch (Exception ex)
      {
         _log.LogError(ex, "Command {Action} failed for lamp {LampId}", action, lamp.Settings.Id);
         return new BridgeIpcErrorResponse("A device or internal command error occurred.",
            SnapshotUnsafe(lamp));
      }
      finally
      {
         lamp.Gate.Release();
      }
   }

   /// <summary>
   /// Evaluates the permission data against this lamp's current Host control mode.
   /// </summary>
   private static bool IsCommandAllowedUnsafe(LampSession lamp, string action)
   {
      if (!_commandPermissions.TryGetValue(action, out ModeMask supported))
      {
         return false;
      }

      ModeMask mode = lamp.Session.Mode switch
      {
         ControlMode.Manual => ModeMask.Manual,
         ControlMode.Pc => ModeMask.Pc,
         ControlMode.PcStreaming => ModeMask.PcStreaming,
         _ => ModeMask.None
      };

      return (supported & mode) != ModeMask.None;
   }

   /// <summary>
   /// Uses the existing Core IPC payload type; parameterless commands omit data.
   /// This validates the wire envelope, not device-specific parameter ranges.
   /// </summary>
   private static T DeserializePayload<T>(JsonElement? data) where T : class
   {
      if (data is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
      {
         throw new ArgumentException($"Missing or invalid payload for {typeof(T).Name}.");
      }

      return payload.Deserialize<T>() ??
         throw new JsonException($"Could not deserialize {typeof(T).Name}.");
   }

   /// <summary>
   /// Claims Direct PC control without replaying stale frames or bypassing an
   /// unresolved Razer DISARM submission from an earlier session.
   /// </summary>
   private BridgeIpcResponse ResumeUnsafe(LampSession lamp)
   {
      if (lamp.Session.Mode != ControlMode.Manual)
      {
         return SnapshotUnsafe(lamp);
      }

      if (lamp.Session.RealtimeArmed)
      {
         return new BridgeIpcErrorResponse(
            "Previous DISARM has not been submitted successfully. Retry disarm or release.",
            SnapshotUnsafe(lamp));
      }

      lamp.Frames.Clear();
      lamp.Attempts.ResetForNewSession();
      lamp.Session.BeginPcOwnership(HasRecentStatusUnsafe(lamp) && lamp.Observed.Status?.IsOn == true);
      PublishUnsafe(lamp, BridgeEventKind.ControlOwnershipChanged, "PC");
      return SnapshotUnsafe(lamp);
   }

   /// <summary>
   /// Enters PC Streaming only with explicit capability support and after a
   /// Razer ARM packet has been submitted. ARM cannot be inferred from a frame.
   /// </summary>
   private async Task<BridgeIpcResponse> ArmUnsafeAsync(
      LampSession lamp,
      CancellationToken ct)
   {
      if (lamp.Session.Mode == ControlMode.PcStreaming)
      {
         return SnapshotUnsafe(lamp);
      }

      var error = CheckCapabilityUnsafe(lamp, lamp.Settings.Capabilities.SupportsRealtime, "Razer real-time streaming");
      if (error is not null)
      {
         return error;
      }

      if (lamp.Session.RealtimeArmed)
      {
         return new BridgeIpcErrorResponse(
            "A previous DISARM remains unresolved; retry disarm first.", SnapshotUnsafe(lamp));
      }

      if (lamp.Session.RequestedPowerOn == false)
      {
         return new BridgeIpcErrorResponse("Turn the lamp on before arming streaming.");
      }

      await EnsurePoweredOnUnsafeAsync(lamp, ct);
      await lamp.Client!.SetRealtimeEnabledAsync(true, ct);
      lamp.Session.BeginStreaming();
      lamp.Attempts.ClearStaticTarget();
      lamp.Attempts.ClearRealtimeHistory();
      lamp.Frames.Clear();
      lamp.Session.ResetColorMismatch();
      _log.LogInformation("Lamp {LampId} entered PC Streaming (ARM submitted)", lamp.Settings.Id);
      PublishUnsafe(lamp, BridgeEventKind.ControlOwnershipChanged, "PC Streaming");
      if (_options.RealtimeArmDelayMs > 0)
      {
         await Task.Delay(_options.RealtimeArmDelayMs, ct);
      }

      return SnapshotUnsafe(lamp);
   }

   /// <summary>
   /// Stops sending frames before attempting physical DISARM. Failed DISARM
   /// remains visible as RealtimeArmed and can be retried explicitly.
   /// </summary>
   private async Task<BridgeIpcResponse> DisarmCommandUnsafeAsync(
      LampSession lamp,
      CancellationToken ct)
   {
      bool changed = lamp.Session.Mode == ControlMode.PcStreaming;
      lamp.Session.StopStreaming();
      lamp.Attempts.ClearRealtimeHistory();
      lamp.Frames.Clear();
      if (lamp.Session.RealtimeArmed)
      {
         if (lamp.Client is null)
         {
            return new BridgeIpcErrorResponse("Diagnostic mode has no physical DISARM connection.");
         }

         await lamp.Client.SetRealtimeEnabledAsync(false, ct);
         lamp.Session.RecordDisarmSent();
      }

      if (changed)
      {
         PublishUnsafe(lamp, BridgeEventKind.ControlOwnershipChanged, "PC Direct");
      }

      return SnapshotUnsafe(lamp);
   }

   /// <summary>
   /// Explicitly relinquishes Host ownership and attempts physical DISARM.
   /// </summary>
   private async Task<BridgeHostStatusSnapshot> ReleaseCommandUnsafeAsync(
      LampSession lamp,
      CancellationToken ct)
   {
      await ReleaseUnsafeAsync(lamp, "Explicit release", ct);
      return SnapshotUnsafe(lamp);
   }

   /// <summary>
   /// Returns to Manual first and makes a best-effort physical DISARM. The
   /// RealtimeArmed flag remains set if the send fails, so it can be retried.
   /// </summary>
   private async Task ReleaseUnsafeAsync(
      LampSession lamp,
      string reason,
      CancellationToken ct)
   {
      bool changed = lamp.Session.Mode != ControlMode.Manual;
      lamp.Session.Release();
      lamp.Attempts.ResetOnRelease();
      lamp.Frames.Clear();
      if (changed)
      {
         PublishUnsafe(lamp, BridgeEventKind.ControlOwnershipChanged, reason);
      }

      if (lamp.Session.RealtimeArmed && lamp.Client is not null)
      {
         try
         {
            await lamp.Client.SetRealtimeEnabledAsync(false, ct);
            lamp.Session.RecordDisarmSent();
         }
         catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
         {
            _log.LogWarning(ex, "Lamp {LampId}: DISARM failed; retry is still required", lamp.Settings.Id);
         }
      }
   }

   /// <summary>
   /// Queries this device independently of cached Host status; physical
   /// devStatus may be intentionally disabled while Razer is armed.
   /// </summary>
   private async Task<BridgeIpcResponse> GetLampStatusUnsafeAsync(
      LampSession lamp,
      CancellationToken ct)
   {
      if (lamp.Client is null)
      {
         return new BridgeIpcErrorResponse("Diagnostic mode has no physical lamp connection");
      }

      if (lamp.Session.RealtimeArmed && !_options.PollWhileRealtimeArmed)
      {
         return new BridgeIpcErrorResponse(
            "devStatus is suspended while Razer may be armed", SnapshotUnsafe(lamp));
      }

      var status = await RefreshLampObservationUnsafeAsync(lamp, ct);
      return new BridgeIpcLampStatusResponse(status, SnapshotUnsafe(lamp));
   }

   /// <summary>
   /// Sets power in either PC mode. Explicit OFF suppresses frame output and
   /// implicit power-on, while the next ON requires a fresh stream frame.
   /// </summary>
   private async Task<BridgeIpcResponse> SetLampPowerUnsafeAsync(
      LampSession lamp,
      bool isOn,
      CancellationToken ct)
   {
      var error = CheckCapabilityUnsafe(lamp, lamp.Settings.Capabilities.SupportsPower, "power");
      if (error is not null)
      {
         return error;
      }

      await lamp.Client!.SetPowerAsync(isOn, ct);
      lamp.Session.SetPowerIntent(isOn);
      if (isOn)
      {
         lamp.Attempts.RecordExplicitPowerOn();
      }
      else
      {
         lamp.Attempts.ClearPowerOnRequest();
         lamp.Attempts.ClearStaticTarget();
         lamp.Attempts.ClearRealtimeHistory();
         lamp.Frames.Clear();
      }

      return SnapshotUnsafe(lamp);
   }

   /// <summary>
   /// Sets global brightness in either PC mode. Zero maps to explicit power
   /// OFF as Host policy; the client validates physical brightness 1..100.
   /// </summary>
   private async Task<BridgeIpcResponse> SetLampBrightnessUnsafeAsync(
      LampSession lamp,
      int percent,
      CancellationToken ct)
   {
      var error = CheckCapabilityUnsafe(lamp, lamp.Settings.Capabilities.SupportsBrightness, "brightness");
      if (error is not null)
      {
         return error;
      }

      if (percent == 0)
      {
         return await SetLampPowerUnsafeAsync(lamp, false, ct);
      }

      await lamp.Client!.SetBrightnessAsync(percent, ct);
      return SnapshotUnsafe(lamp);
   }

   /// <summary>
   /// Requests a discrete solid color using the existing bounded, observed
   /// static-color retry pipeline. Streaming mode is disallowed by policy.
   /// </summary>
   private async Task<BridgeIpcResponse> SetLampColorUnsafeAsync(
      LampSession lamp,
      BridgeIpcRgbColor rgb,
      CancellationToken ct)
   {
      var error = CheckDirectCapabilityUnsafe(lamp, lamp.Settings.Capabilities.SupportsRgb, "solid RGB");
      if (error is not null)
      {
         return error;
      }

      if (lamp.Session.RequestedPowerOn == false)
      {
         return new BridgeIpcErrorResponse("Turn the lamp on before setting its color.");
      }

      RgbColor color = new(rgb.R, rgb.G, rgb.B);
      await ApplyStaticColorUnsafeAsync(lamp, color, DateTimeOffset.UtcNow, ct);
      return SnapshotUnsafe(lamp);
   }

   /// <summary>
   /// Requests white color temperature and cancels any older static RGB retry.
   /// Physical temperature limits are validated by the device client.
   /// </summary>
   private async Task<BridgeIpcResponse> SetLampTemperatureUnsafeAsync(
      LampSession lamp,
      int kelvin,
      CancellationToken ct)
   {
      var error = CheckDirectCapabilityUnsafe(lamp, lamp.Settings.Capabilities.SupportsColorTemperature, "color temperature");
      if (error is not null)
      {
         return error;
      }

      if (lamp.Session.RequestedPowerOn == false)
      {
         return new BridgeIpcErrorResponse("Turn the lamp on before setting white temperature.");
      }

      await lamp.Client!.SetColorTemperatureAsync(kelvin, ct);
      lamp.Attempts.ClearStaticTarget();
      lamp.Session.ResetColorMismatch();
      return SnapshotUnsafe(lamp);
   }

   /// <summary>
   /// Checks physical connection and configured support without inferring that
   /// the device acknowledged a previous request.
   /// </summary>
   private static BridgeIpcErrorResponse? CheckCapabilityUnsafe(
      LampSession lamp,
      bool supported,
      string feature)
   {
      if (lamp.Client is null)
      {
         return new BridgeIpcErrorResponse("Diagnostic mode has no physical lamp connection");
      }

      return supported ? null : new BridgeIpcErrorResponse(
         $"Lamp '{lamp.Settings.Id}' does not have configured {feature} support.");
   }

   /// <summary>
   /// Blocks direct color/temperature changes after a failed DISARM even when
   /// the logical mode has already returned to PC Direct.
   /// </summary>
   private static BridgeIpcErrorResponse? CheckDirectCapabilityUnsafe(
      LampSession lamp,
      bool supported,
      string feature)
   {
      if (lamp.Session.RealtimeArmed)
      {
         return new BridgeIpcErrorResponse("DISARM has not been submitted successfully.");
      }

      return CheckCapabilityUnsafe(lamp, supported, feature);
   }
}
