using System.Net.Sockets;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Protocol;

namespace GoveeBridge.Host;

public sealed partial class BridgeRuntime
{
   /// <summary>
   /// Applies a static RGB target, with bounded verification retries when enabled.
   /// </summary>
   private async Task ApplyStaticFrameUnsafeAsync(
      RgbFrame frame, DateTimeOffset now, CancellationToken ct)
   {
      RgbColor wanted = GoveeHelpers.AverageColor(frame);

      // A new requested color replaces the previous reconciliation attempt.
      // This matters when SignalRGB is animating: never retry an obsolete color.
      StaticColorRequest request = _attempts.GetStaticTarget(wanted, out bool targetChanged);
      if (targetChanged)
      {
         _session.ResetColorMismatch();
      }

      // A verified color needs no repeats. A suspended request has either
      // exhausted its retries or shown signs of an external change; writing
      // it repeatedly would risk fighting the remote or an app-selected scene.
      if (!request.NeedsSend)
      {
         return;
      }

      // Respect the physical LAN rate limit for BOTH first attempts and retries.
      if (!_attempts.CanSendStatic(now, _settings.MinimumSendIntervalMs))
      {
         return;
      }

      // If the lamp already reports the desired solid RGB mode, there is
      // nothing to transmit. A fresh observation is preferable to sending.
      if (request.Attempts == 0 && HasRecentStatusUnsafe() &&
         IsObservedStaticColor(_observed.Status, wanted))
      {
         request.ConfirmObserved();
         _log.LogDebug("Static color {Color} already observed; no command sent", wanted);
         return;
      }

      // Do not send a color retry while the latest observation still says OFF.
      // Power-on recovery is a separate policy; retrying RGB will not repair it.
      if (request.Attempts > 0 && HasRecentStatusUnsafe() &&
         _observed.Status?.IsOn == false)
      {
         return;
      }

      await EnsurePoweredOnUnsafeAsync(ct);

      // Preserve the state that was actually OBSERVED before transmission.
      // Later, monitoring can distinguish "the lamp stayed where it was"
      // from "the lamp changed to another state", which may indicate a user.
      GoveeDeviceStatus? before = HasRecentStatusUnsafe() ? _observed.Status : null;
      await _lamp!.SetColorAsync(wanted, ct);

      _attempts.RecordStaticSend(request, before);

      _log.LogDebug("Requested static RGB {Color}; attempt {Attempt}",
         wanted, request.Attempts);
   }

   /// <summary>Checks the device's observed state before allowing a static RGB retry.</summary>
   private async Task ReconcileStaticColorUnsafeAsync(
      GoveeDeviceStatus status, GoveeDeviceStatus? previous, CancellationToken ct)
   {
      StaticColorRequest? request = _attempts.StaticColorRequest;
      if (request is null || request.Suspended)
      {
         return;
      }

      if (IsObservedStaticColor(status, request.Color))
      {
         // Confirmation is based on an observation, not a successful SendAsync.
         request.ConfirmObserved();
         // ConfirmObserved also clears the pending retry.
         _session.ResetColorMismatch();
         return;
      }

      if (!status.IsOn)
      {
         // Power-off handling happens above; this isn't a color mismatch.
         return;
      }

      if (request.Verified)
      {
         // Avoid treating status propagation immediately after a write as
         // evidence that the remote or app changed the light.
         if (DateTimeOffset.UtcNow - request.LastAttemptAt <
            TimeSpan.FromMilliseconds(_options.ColorMismatchGraceMs))
         {
            return;
         }

         // We previously saw our color, but the device now reports something
         // else. This is NOT evidence that our original packet was lost.
         // Never resend this same target simply because it now differs.
         if (_options.AutoReleaseOnColorMismatch)
         {
            if (previous is not null &&
               (previous.Color != status.Color || previous.ColorTemperatureKelvin != status.ColorTemperatureKelvin))
            {
               _session.MismatchSuspected = true;
            }

            _session.MismatchPollCount = _session.MismatchSuspected ? _session.MismatchPollCount + 1 : 0;
            if (_session.MismatchPollCount >= _options.ColorMismatchRequiredPolls)
            {
               await ReleaseUnsafeAsync("Possible external static-color override", ct);
            }
         }
         else
         {
            request.Suspend();
            _log.LogWarning(
               "Observed {Color}/{Kelvin}K after verified RGB {Wanted}; " +
               "not resending this target (possible app/remote override)",
               status.Color, status.ColorTemperatureKelvin, request.Color);
         }
         return;
      }

      if (!request.CanVerify(_observed.ObservedAt, _options.StaticVerificationGraceMs))
      {
         // A cached response from before the write is not verification.
         // Give the lamp a little time to process the outgoing datagram.
         return;
      }

      bool colorChangedSinceAttempt = request.ObservedBeforeAttempt is not null &&
         !SameObservedColorMode(request.ObservedBeforeAttempt, status);

      if (colorChangedSinceAttempt)
      {
         // A change to a THIRD color or another color mode may be someone
         // choosing a scene/white temperature. Don't overwrite it with a retry.
         request.Suspend();
         _log.LogWarning(
            "Static RGB {Wanted} not verified: device changed to {Actual}/{Kelvin}K; " +
            "automatic retries suspended", request.Color, status.Color,
            status.ColorTemperatureKelvin);
         if (_options.AutoReleaseOnColorMismatch)
         {
            await ReleaseUnsafeAsync("Possible external color change while verifying", ct);
         }
         return;
      }

      if (request.Attempts <= _options.StaticColorRetryLimit)
      {
         // Same observed color/mode as before the command: the write may have
         // been lost or ignored. Ask the pump for ONE more attempt by default.
         if (!request.RetryPending)
         {
            request.ScheduleRetry();
            _log.LogWarning(
               "Static RGB {Wanted} not observed after attempt {Attempt}; " +
               "scheduled bounded retry", request.Color, request.Attempts);
         }
      }
      else
      {
         // No confirmation after the retry. Stop; we cannot distinguish
         // ignored commands from modes that devStatus fails to describe.
         request.Suspend();
         _log.LogWarning(
            "Static RGB {Wanted} still not observed after {Attempts} attempts; " +
            "no more automatic writes", request.Color, request.Attempts);
      }
   }

   /// <summary>Recognizes an observed solid RGB value; devStatus cannot identify scenes.</summary>
   private static bool IsObservedStaticColor(GoveeDeviceStatus? status, RgbColor wanted) =>
      status is { IsOn: true, ColorTemperatureKelvin: 0 } observed &&
      observed.Color == wanted;

   /// <summary>Compares RGB plus mode, not brightness or power.</summary>
   private static bool SameObservedColorMode(GoveeDeviceStatus left, GoveeDeviceStatus right) =>
      left.Color == right.Color &&
      left.ColorTemperatureKelvin == right.ColorTemperatureKelvin;

   /// <summary>Transmits multizone samples, arming Razer when first needed.</summary>
   private async Task ApplyRealtimeFrameUnsafeAsync(
      RgbFrame frame, DateTimeOffset now, CancellationToken ct)
   {
      // The worker wakes more often than our maximum allowed physical
      // Razer rate. Skip ticks until the minimum spacing has elapsed.
      if (!_attempts.ShouldSendRealtime(frame, now, _options))
      {
         return;
      }

      // Power, arm, and send are distinct protocol operations. Owning the
      // lamp from the PC's viewpoint does not imply Razer is already armed.
      await EnsurePoweredOnUnsafeAsync(ct);
      if (!_session.RealtimeArmed)
      {
         await _lamp!.SetRealtimeEnabledAsync(true, ct);
         // Best-effort transport assumption: we know we sent the ARM packet,
         // not whether the lamp entered real-time mode. This field is Host
         // bookkeeping, not a verified hardware bit.
         _session.RealtimeArmed = true;
         _log.LogInformation("Razer mode armed");
         if (_options.RealtimeArmDelayMs > 0)
         {
            await Task.Delay(_options.RealtimeArmDelayMs, ct);
         }
      }

      // Each frame contains ALL logical RGB positions; it is not a delta.
      // The input frame is a private snapshot from ReceiveFrame, safe to read.
      await _lamp!.SendRealtimeFrameAsync(frame, ct);
      _attempts.RecordRealtimeSend(frame);
   }

   /// <summary>Requests power once per PC session when no recent ON observation exists.</summary>
   private async Task EnsurePoweredOnUnsafeAsync(CancellationToken ct)
   {
      // If the lamp was recently observed on, avoid an unnecessary power
      // command. Otherwise request power on only once per PC ownership session.
      // Limitation: a lost power-on UDP packet is also suppressed thereafter.
      if ((HasRecentStatusUnsafe() && _observed.Status?.IsOn == true) ||
         _attempts.PowerOnRequested)
      {
         return;
      }

      await _lamp!.SetPowerAsync(true, ct);
      _attempts.RecordPowerOnSend();
      await Task.Delay(BridgeHostDefaults.PowerCommandSettleDelayMs, ct);
   }
}
