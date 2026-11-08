using GoveeBridge.Core;

namespace GoveeBridge.Host;

public sealed partial class BridgeRuntime
{
   /// <summary>
   /// Applies a discrete static RGB request with bounded verification retries.
   /// The PC Direct pump may call this again only for an existing pending target.
   /// </summary>
   private async Task ApplyStaticColorUnsafeAsync(
      LampSession lamp, RgbColor wanted, DateTimeOffset now, CancellationToken ct)
   {

      // A new requested color replaces the previous reconciliation attempt.
      // This matters when SignalRGB is animating: never retry an obsolete color.
      StaticColorRequest request = lamp.Attempts.GetStaticTarget(wanted, out bool targetChanged);
      if (targetChanged)
      {
         lamp.Session.ResetColorMismatch();
      }

      // A verified color needs no repeats. A suspended request has either
      // exhausted its retries or shown signs of an external change; writing
      // it repeatedly would risk fighting the remote or an app-selected scene.
      if (!request.NeedsSend)
      {
         return;
      }

      // Respect the physical LAN rate limit for BOTH first attempts and retries.
      if (!lamp.Attempts.CanSendStatic(now, _settings.MinimumSendIntervalMs))
      {
         return;
      }

      // If the lamp already reports the desired solid RGB mode, there is
      // nothing to transmit. A fresh observation is preferable to sending.
      if (request.Attempts == 0 && HasRecentStatusUnsafe(lamp) &&
         IsObservedStaticColor(lamp.Observed.Status, wanted))
      {
         request.ConfirmObserved();
         _log.LogDebug("Static color {Color} already observed; no command sent", wanted);
         return;
      }

      // Do not send a color retry while the latest observation still says OFF.
      // Power-on recovery is a separate policy; retrying RGB will not repair it.
      if (request.Attempts > 0 && HasRecentStatusUnsafe(lamp) &&
         lamp.Observed.Status?.IsOn == false)
      {
         return;
      }

      await EnsurePoweredOnUnsafeAsync(lamp, ct);

      // Preserve the state that was actually OBSERVED before transmission.
      // Later, monitoring can distinguish "the lamp stayed where it was"
      // from "the lamp changed to another state", which may indicate a user.
      GoveeDeviceStatus? before = HasRecentStatusUnsafe(lamp) ? lamp.Observed.Status : null;
      await lamp.Client!.SetColorAsync(wanted, ct);

      lamp.Attempts.RecordStaticSend(request, before);

      _log.LogDebug("Requested static RGB {Color}; attempt {Attempt}",
         wanted, request.Attempts);
   }

   /// <summary>
   /// Uses later observations to verify an attempted solid color or schedule a
   /// bounded retry without fighting another controller's observed changes.
   /// </summary>
   private async Task ReconcileStaticColorUnsafeAsync(
      LampSession lamp, GoveeDeviceStatus status, GoveeDeviceStatus? previous, CancellationToken ct)
   {
      StaticColorRequest? request = lamp.Attempts.StaticColorRequest;
      if (request is null || request.Suspended)
      {
         return;
      }

      if (IsObservedStaticColor(status, request.Color))
      {
         // Confirmation is based on an observation, not a successful SendAsync.
         request.ConfirmObserved();
         // ConfirmObserved also clears the pending retry.
         lamp.Session.ResetColorMismatch();
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
               (previous.Color != status.Color ||
                  previous.ColorTemperatureKelvin != status.ColorTemperatureKelvin))
            {
               lamp.Session.MismatchSuspected = true;
            }

            lamp.Session.MismatchPollCount = lamp.Session.MismatchSuspected ? lamp.Session.MismatchPollCount + 1 : 0;
            if (lamp.Session.MismatchPollCount >= _options.ColorMismatchRequiredPolls)
            {
               await ReleaseUnsafeAsync(lamp, "Possible external static-color override", ct);
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

      if (!request.CanVerify(lamp.Observed.ObservedAt, _options.StaticVerificationGraceMs))
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
            await ReleaseUnsafeAsync(lamp, "Possible external color change while verifying", ct);
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

   /// <summary>
   /// Identifies an observed solid RGB state; devStatus cannot identify scenes.
   /// </summary>
   private static bool IsObservedStaticColor(GoveeDeviceStatus? status, RgbColor wanted) =>
      status is { IsOn: true, ColorTemperatureKelvin: 0 } observed &&
      observed.Color == wanted;

   /// <summary>
   /// Compares observed RGB and white-temperature mode, not brightness/power.
   /// </summary>
   private static bool SameObservedColorMode(GoveeDeviceStatus left, GoveeDeviceStatus right) =>
      left.Color == right.Color &&
      left.ColorTemperatureKelvin == right.ColorTemperatureKelvin;

   /// <summary>
   /// Sends one current full RGB frame in PC Streaming only; re-arms explicitly
   /// after a commanded power cycle, but never transitions from PC Direct here.
   /// </summary>
   private async Task ApplyRealtimeFrameUnsafeAsync(
      LampSession lamp, RgbFrame frame, DateTimeOffset now, CancellationToken ct)
   {
      // The worker wakes more often than our maximum allowed physical
      // Razer rate. Skip ticks until the minimum spacing has elapsed.
      if (!lamp.Attempts.ShouldSendRealtime(frame, now, _options))
      {
         return;
      }

      // Streaming is explicitly armed by the IPC arm command, not by the
      // arrival of an RGB frame. A power cycle may require a fresh ARM send.
      await EnsurePoweredOnUnsafeAsync(lamp, ct);
      if (lamp.Session.NeedsRealtimeRearm)
      {
         await lamp.Client!.SetRealtimeEnabledAsync(true, ct);
         lamp.Session.RecordRearmSent();
         if (_options.RealtimeArmDelayMs > 0)
         {
            await Task.Delay(_options.RealtimeArmDelayMs, ct);
         }
      }
      if (!lamp.Session.RealtimeArmed)
      {
         // A previous DISARM was sent or ARM failed. Never silently ARM
         // a lamp in the frame pump without an explicit control transition.
         return;
      }

      // Each frame contains ALL logical RGB positions; it is not a delta.
      // The input frame is a private snapshot from ReceiveFrame, safe to read.
      await lamp.Client!.SendRealtimeFrameAsync(frame, ct);
      lamp.Attempts.RecordRealtimeSend(frame);
   }

   /// <summary>
   /// Performs at most one automatic power-on attempt per session, unless an
   /// explicit power preference or fresh observation already determines it.
   /// </summary>
   private async Task EnsurePoweredOnUnsafeAsync(LampSession lamp, CancellationToken ct)
   {
      // If the lamp was recently observed on, avoid an unnecessary power
      // command. Otherwise request power on only once per PC ownership session.
      // Limitation: a lost power-on UDP packet is also suppressed thereafter.
      if (lamp.Session.RequestedPowerOn == false ||
         !lamp.Settings.Capabilities.SupportsPower ||
         (HasRecentStatusUnsafe(lamp) && lamp.Observed.Status?.IsOn == true) ||
         lamp.Attempts.PowerOnRequested)
      {
         return;
      }

      await lamp.Client!.SetPowerAsync(true, ct);
      lamp.Attempts.RecordPowerOnSend();
      await Task.Delay(BridgeHostDefaults.PowerCommandSettleDelayMs, ct);
   }
}
