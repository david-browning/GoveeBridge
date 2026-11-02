using System.Net.Sockets;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Protocol;

namespace GoveeBridge.Host;

/// <summary>
/// Maintains ownership of one physical Govee lamp. All physical operations and
/// observed-state mutations are serialized by a single gate; frame reception never
/// waits for lamp I/O. Manual ownership is the startup default.
/// </summary>
/// <remarks>
/// Sending UDP is not an acknowledgment. We deduplicate our own requested commands,
/// but never infer that a transmitted command was physically applied. Remote
/// takeover detection is best effort because devStatus does not identify its actor.
/// </remarks>
public sealed class BridgeRuntime : IDisposable
{
   // READ THIS CLASS AS A CONTROLLER, NOT A SOCKET LISTENER.
   // BridgeWorker calls us; we decide which requests are permitted and when a
   // physical command is appropriate. GoveeLanClient only knows how to send it.
   //
   // There are TWO different kinds of state:
   //   Requested/attempted: last command or RGB frame we tried to submit.
   //   Observed: last devStatus response from the real lamp.
   // Neither an attempted UDP send nor an old observation proves current state.
   //
   // "Unsafe" in private method names means the caller MUST already hold _lampGate;
   // it does not mean raw pointers or unsafe C#. This convention avoids taking
   // the same SemaphoreSlim recursively, which would deadlock.
   /// <summary>Creates the sole physical lamp connection for a configured transport.</summary>
   public BridgeRuntime(
      BridgeSettings settings,
      BridgeHostOptions options,
      IBridgeEventPublisher events,
      ILogger<BridgeRuntime> log)
   {
      _settings = settings;
      _options = options;
      _events = events;
      _log = log;

      // Diagnostic mode deliberately avoids binding Govee's fixed reply port.
      // In all physical modes this instance is the sole device client owner.
      // The field is typed as IGoveeRealtimeClient because the two currently
      // implemented physical modes both use the Razer-capable LAN client.
      if (settings.TransportMode != TransportMode.Diagnostic)
      {
         _lamp = new GoveeLanClient(settings.LampIp!, settings.LampTimeoutMs);
      }

      _log.LogInformation("GoveeBridge mode={Mode}; owner=Manual", settings.TransportMode);
   }

   /// <summary>
   /// Accepts a complete logical RGB frame without blocking on lamp I/O.
   /// Copies the values so an external caller cannot mutate an in-flight frame.
   /// </summary>
   public void ReceiveFrame(RgbFrame frame)
   {
      ArgumentNullException.ThrowIfNull(frame);
      // RgbFrame exposes an IReadOnlyList, but a caller could have provided a
      // mutable List/array. Copy its elements before publishing it to another
      // asynchronous loop, so an in-flight frame cannot change beneath us.
      RgbFrame snapshot = new(frame.Colors.ToArray());
      DateTimeOffset now = DateTimeOffset.UtcNow;
      // ReceiveFrame runs on the fast IPC path, outside _lampGate. Interlocked
      // updates this counter without risking a data race with status snapshots.
      long count = Interlocked.Increment(ref _framesReceived);
      bool logDiagnostic = false;

      // This lock covers only frame-memory handoff, NOT physical networking.
      // New arrivals replace the previous frame: queued animation frames would
      // be obsolete by the time a slow lamp request completed.
      lock (_frameGate)
      {
         _latestFrame = snapshot;
         _latestFrameAt = now;

         if (_lamp is null && now - _lastDiagnosticLog >=
            TimeSpan.FromMilliseconds(BridgeHostDefaults.DiagnosticLogIntervalMs))
         {
            _lastDiagnosticLog = now;
            logDiagnostic = true;
         }
      }

      // Log outside the lock: logging can allocate or block, and must not hold
      // up the next incoming frame. Diagnostic mode still receives test frames.
      if (logDiagnostic)
      {
         _log.LogInformation("Diagnostic frame #{Count}: {LedCount} positions, average {Color}",
            count, snapshot.Colors.Count, GoveeHelpers.AverageColor(snapshot));
      }
   }

   /// <summary>
   /// Applies only the freshest pending frame while PC control is active. Uses
   /// non-blocking lock acquisition to avoid queuing frames behind status polls.
   /// </summary>
   public async Task PumpFrameAsync(CancellationToken ct)
   {
      // Zero timeout is deliberate: if a status poll or management command
      // owns the lamp gate, skip this tick instead of queuing old frames behind
      // a slow network operation. The worker will call this method again.
      if (_lamp is null || !await _lampGate.WaitAsync(0, ct))
      {
         return;
      }

      try
      {
         // Manual is the default and takes precedence over every incoming frame.
         // Receiving frames alone never grants ownership to the PC.
         // Keep observing and logging the remote-controlled lamp, but do not
      // interpret status changes as a reason to send commands in Manual mode.
      if (_owner != ControlOwner.Pc)
         {
            return;
         }

         // Take a quick, consistent snapshot (frame + receipt time) under the
         // small memory lock. We NEVER hold _frameGate across an await.
         RgbFrame? pending;
         DateTimeOffset receivedAt;
         lock (_frameGate)
         {
            pending = _latestFrame;
            receivedAt = _latestFrameAt;
         }

         // No frame since resume (which clears the old one), or no frame yet.
         // We do not invent a color or turn the lamp on just because PC owns it.
         if (pending is null)
         {
            return;
         }

         DateTimeOffset now = DateTimeOffset.UtcNow;
         // This tests the producer's health rather than the lamp's health.
         // If a once-active producer stops sending, its last color must not
         // grant perpetual PC ownership. It only runs when a frame exists.
         if (now - receivedAt > TimeSpan.FromMilliseconds(_options.FrameStaleAfterMs))
         {
            // An abandoned stream must not leave the Host claiming lamp control.
            await ReleaseUnsafeAsync("Frame stream expired", ct);
            return;
         }

         // The worker's timer is generic; the selected output policy lives
         // here. Static collapses the frame to one color, Razer preserves zones.
         switch (_settings.TransportMode)
         {
            case TransportMode.GoveeStatic:
               await ApplyStaticFrameUnsafeAsync(pending, now, ct);
               break;
            case TransportMode.GoveeRazer:
               await ApplyRealtimeFrameUnsafeAsync(pending, now, ct);
               break;
         }
      }
      finally
      {
         _lampGate.Release();
      }
   }

   /// <summary>
   /// Queries physical status even in Manual mode. When a real-time frame is
   /// armed, optional polling is disabled by default because some firmware does
   /// not answer devStatus in that mode.
   /// </summary>
   public async Task PollLampAsync(CancellationToken ct)
   {
      if (_lamp is null || !_options.EnableLampMonitoring)
      {
         return;
      }

      // Unlike the frame pump, polling is allowed to WAIT for the gate.
      // It asks the physical lamp for status, so it must not race an outgoing
      // command or a concurrent ownership transition.
      await _lampGate.WaitAsync(ct);
      try
      {
         // The official status query may time out while Razer streaming is
         // active. Skipping polls protects animation throughput, but means
         // we cannot reliably detect remote power-off during this interval.
         if (_realtimeArmed && !_options.PollWhileRealtimeArmed)
         {
            return;
         }

         GoveeDeviceStatus? status = await _lamp.GetStatusAsync(ct);
         await ObserveStatusUnsafeAsync(status, ct);
      }
      finally
      {
         _lampGate.Release();
      }
   }

   /// <summary>Processes one local management command while serializing ownership changes.</summary>
   public async Task<object> ExecuteCommandAsync(string command, CancellationToken ct)
   {
      // Only the command consumer calls this path. The same semaphore used
      // for physical sends makes resume/release atomic with respect to output:
      // a frame cannot be sent halfway through a change of owner.
      ArgumentException.ThrowIfNullOrWhiteSpace(command);
      await _lampGate.WaitAsync(ct);
      try
      {
         switch (command.Trim().ToLowerInvariant())
         {
            // Explicit handoff: SignalRGB sending frames is insufficient.
            // A second resume while PC already owns the lamp is idempotent.
            case BridgeIpcActions.Resume:
               if (_owner == ControlOwner.Manual)
               {
                  // Do not display the effect that happened to be in memory
                  // while the physical remote had control.
                  // Frames queued before explicit takeover are not replayed.
                  lock (_frameGate)
                  {
                     _latestFrame = null;
                     _latestFrameAt = DateTimeOffset.MinValue;
                  }
                  // Reset attempted-command history for this ownership session;
                  // otherwise deduplication might suppress our first frame.
                  _owner = ControlOwner.Pc;
                  _powerOnRequested = false;
                  _observedOnDuringOwnership = HasRecentStatusUnsafe() && _lastStatus?.IsOn == true;
                  _staticColorRequest = null;
                  _lastRealtimeColors = null;
                  _lastStaticAttemptAt = DateTimeOffset.MinValue;
                  _lastRealtimeFrameAt = DateTimeOffset.MinValue;
                  _lastPowerOnRequestedAt = DateTimeOffset.MinValue;
                  _mismatchPollCount = 0;
                  _mismatchSuspected = false;
                  _log.LogInformation("PC control granted by explicit resume command");
                  PublishUnsafe(BridgeEventKind.ControlOwnershipChanged, "PC");
               }
               return SnapshotUnsafe();

            // Releases permission to send; in real-time mode also attempts
            // to disarm the device. It NEVER sends a power-off or a saved color.
            case BridgeIpcActions.Release:
               await ReleaseUnsafeAsync("Explicit release", ct);
               return SnapshotUnsafe();

            // Local bookkeeping only: no physical network request.
            case BridgeIpcActions.GetHostStatus:
               return SnapshotUnsafe();

            // Fresh physical query, unlike "status". It can take up to the
            // configured lamp timeout, so it runs on the command-queue task,
            // not on the fast incoming-frame receive loop.
            case BridgeIpcActions.GetLampStatus:
               if (_lamp is null)
               {
                  return new { error = "Diagnostic mode has no physical lamp connection" };
               }
               if (_realtimeArmed && !_options.PollWhileRealtimeArmed)
               {
                  return new
                  {
                     error = "devStatus is intentionally suspended while Razer mode is armed",
                     status = SnapshotUnsafe()
                  };
               }
               GoveeDeviceStatus? status = await _lamp.GetStatusAsync(ct);
               await ObserveStatusUnsafeAsync(status, ct);
               return new { lamp = status, status = SnapshotUnsafe() };

            default:
               return new { error = $"Unknown command: {command}" };
         }
      }
      finally
      {
         _lampGate.Release();
      }
   }

   /// <summary>
   /// Attempts to disarm Razer during normal shutdown without turning the lamp off.
   /// Uses a bounded independent timeout because the Host's stop token is canceled.
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

   /// <summary>
   /// Applies the latest whole-lamp RGB target. Sends only on a new target or
   /// one scheduled verification retry; a successful UDP write is not proof
   /// that the lamp changed color.
   /// </summary>
   /// <remarks>
   /// Monitoring schedules verification by setting RetryPending. This method
   /// performs the actual physical write, keeping all lighting output on the
   /// pump path and respecting the static minimum-send interval.
   /// </remarks>
   private async Task ApplyStaticFrameUnsafeAsync(
      RgbFrame frame, DateTimeOffset now, CancellationToken ct)
   {
      RgbColor wanted = GoveeHelpers.AverageColor(frame);

      // A new requested color replaces the previous reconciliation attempt.
      // This matters when SignalRGB is animating: never retry an obsolete color.
      if (_staticColorRequest is null || _staticColorRequest.Color != wanted)
      {
         _staticColorRequest = new StaticColorRequest(wanted);
         _mismatchPollCount = 0;
         _mismatchSuspected = false;
      }

      StaticColorRequest request = _staticColorRequest;

      // A verified color needs no repeats. A suspended request has either
      // exhausted its retries or shown signs of an external change; writing
      // it repeatedly would risk fighting the remote or an app-selected scene.
      if (request.Verified || request.Suspended ||
         (request.Attempts > 0 && !request.RetryPending))
      {
         return;
      }

      // Respect the physical LAN rate limit for BOTH first attempts and retries.
      if (now - _lastStaticAttemptAt <
         TimeSpan.FromMilliseconds(_settings.MinimumSendIntervalMs))
      {
         return;
      }

      // If the lamp already reports the desired solid RGB mode, there is
      // nothing to transmit. A fresh observation is preferable to sending.
      if (request.Attempts == 0 && HasRecentStatusUnsafe() &&
         IsObservedStaticColor(_lastStatus, wanted))
      {
         request.Verified = true;
         _log.LogDebug("Static color {Color} already observed; no command sent", wanted);
         return;
      }

      // Do not send a color retry while the latest observation still says OFF.
      // Power-on recovery is a separate policy; retrying RGB will not repair it.
      if (request.Attempts > 0 && HasRecentStatusUnsafe() &&
         _lastStatus?.IsOn == false)
      {
         return;
      }

      await EnsurePoweredOnUnsafeAsync(ct);

      // Preserve the state that was actually OBSERVED before transmission.
      // Later, monitoring can distinguish "the lamp stayed where it was"
      // from "the lamp changed to another state", which may indicate a user.
      GoveeDeviceStatus? before = HasRecentStatusUnsafe() ? _lastStatus : null;
      await _lamp!.SetColorAsync(wanted, ct);

      request.ObservedBeforeAttempt = before;
      request.LastAttemptAt = DateTimeOffset.UtcNow;
      request.Attempts++;
      request.RetryPending = false;
      _lastStaticAttemptAt = request.LastAttemptAt;
      _framesSent++;

      _log.LogDebug("Requested static RGB {Color}; attempt {Attempt}",
         wanted, request.Attempts);
   }

   /// <summary>
   /// Compares a fresh status observation with the pending static RGB request.
   /// Schedules at most the configured number of retries for a stable mismatch.
   /// </summary>
   /// <remarks>
   /// This is deliberately NOT used for Razer: devStatus has no multizone
   /// frame or scene identifier. It also cannot detect an app scene whose
   /// displayed animation changes while the reported RGB remains fixed.
   /// </remarks>
   private async Task ReconcileStaticColorUnsafeAsync(
      GoveeDeviceStatus status, GoveeDeviceStatus? previous, CancellationToken ct)
   {
      StaticColorRequest? request = _staticColorRequest;
      if (request is null || request.Suspended)
      {
         return;
      }

      if (IsObservedStaticColor(status, request.Color))
      {
         // Confirmation is based on an observation, not a successful SendAsync.
         request.Verified = true;
         request.RetryPending = false;
         _mismatchPollCount = 0;
         _mismatchSuspected = false;
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
               _mismatchSuspected = true;
            }

            _mismatchPollCount = _mismatchSuspected ? _mismatchPollCount + 1 : 0;
            if (_mismatchPollCount >= _options.ColorMismatchRequiredPolls)
            {
               await ReleaseUnsafeAsync("Possible external static-color override", ct);
            }
         }
         else
         {
            request.Suspended = true;
            _log.LogWarning(
               "Observed {Color}/{Kelvin}K after verified RGB {Wanted}; " +
               "not resending this target (possible app/remote override)",
               status.Color, status.ColorTemperatureKelvin, request.Color);
         }
         return;
      }

      if (request.Attempts == 0 || _statusObservedAt is null ||
         _statusObservedAt <= request.LastAttemptAt ||
         _statusObservedAt.Value - request.LastAttemptAt <
            TimeSpan.FromMilliseconds(_options.StaticVerificationGraceMs))
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
         request.Suspended = true;
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
            request.RetryPending = true;
            _log.LogWarning(
               "Static RGB {Wanted} not observed after attempt {Attempt}; " +
               "scheduled bounded retry", request.Color, request.Attempts);
         }
      }
      else
      {
         // No confirmation after the retry. Stop; we cannot distinguish
         // ignored commands from modes that devStatus fails to describe.
         request.Suspended = true;
         _log.LogWarning(
            "Static RGB {Wanted} still not observed after {Attempts} attempts; " +
            "no more automatic writes", request.Color, request.Attempts);
      }
   }

   /// <summary>True when a device observation matches the desired solid RGB mode.</summary>
   private static bool IsObservedStaticColor(GoveeDeviceStatus? status, RgbColor wanted) =>
      status is { IsOn: true, ColorTemperatureKelvin: 0 } observed &&
      observed.Color == wanted;

   /// <summary>
   /// Compares only color/mode fields; brightness or power can change for
   /// unrelated reasons and should not masquerade as a new RGB selection.
   /// </summary>
   private static bool SameObservedColorMode(GoveeDeviceStatus left, GoveeDeviceStatus right) =>
      left.Color == right.Color &&
      left.ColorTemperatureKelvin == right.ColorTemperatureKelvin;

   /// <summary>
   /// Arms once, transmits changed multizone frames, and periodically resends
   /// the last frame as a firmware keepalive. The keepalive is an intentional
   /// exception to static-command deduplication.
   /// </summary>
   private async Task ApplyRealtimeFrameUnsafeAsync(
      RgbFrame frame, DateTimeOffset now, CancellationToken ct)
   {
      // The worker wakes more often than our maximum allowed physical
      // Razer rate. Skip ticks until the minimum spacing has elapsed.
      if (now - _lastRealtimeFrameAt <
         TimeSpan.FromMilliseconds(_options.RealtimeMinimumFrameIntervalMs))
      {
         return;
      }

      // Compare RGB CONTENTS, not RgbFrame references (record equality does
      // not deep-compare its IReadOnlyList). Identical frames are omitted
      // until the keepalive period expires, when we resend intentionally.
      if (_lastRealtimeColors is not null &&
         frame.Colors.SequenceEqual(_lastRealtimeColors) &&
         now - _lastRealtimeFrameAt <
            TimeSpan.FromMilliseconds(_options.RealtimeKeepaliveIntervalMs))
      {
         return;
      }

      // Power, arm, and send are distinct protocol operations. Owning the
      // lamp from the PC's viewpoint does not imply Razer is already armed.
      await EnsurePoweredOnUnsafeAsync(ct);
      if (!_realtimeArmed)
      {
         await _lamp!.SetRealtimeEnabledAsync(true, ct);
         // Best-effort transport assumption: we know we sent the ARM packet,
         // not whether the lamp entered real-time mode. This field is Host
         // bookkeeping, not a verified hardware bit.
         _realtimeArmed = true;
         _log.LogInformation("Razer mode armed");
         if (_options.RealtimeArmDelayMs > 0)
         {
            await Task.Delay(_options.RealtimeArmDelayMs, ct);
         }
      }

      // Each frame contains ALL logical RGB positions; it is not a delta.
      // The input frame is a private snapshot from ReceiveFrame, safe to read.
      await _lamp!.SendRealtimeFrameAsync(frame, ct);
      _lastRealtimeColors = frame.Colors.ToArray();
      _lastRealtimeFrameAt = DateTimeOffset.UtcNow;
      _framesSent++;
   }

   /// <summary>
   /// Turns the lamp on at most once per explicit PC-control session, and only
   /// when the last observed status does not already say it is on.
   /// </summary>
   private async Task EnsurePoweredOnUnsafeAsync(CancellationToken ct)
   {
      // If the lamp was recently observed on, avoid an unnecessary power
      // command. Otherwise request power on only once per PC ownership session.
      // Limitation: a lost power-on UDP packet is also suppressed thereafter.
      if ((HasRecentStatusUnsafe() && _lastStatus?.IsOn == true) ||
         _powerOnRequested)
      {
         return;
      }

      await _lamp!.SetPowerAsync(true, ct);
      _powerOnRequested = true;
      _lastPowerOnRequestedAt = DateTimeOffset.UtcNow;
      _powerCommandsSent++;
      await Task.Delay(BridgeHostDefaults.PowerCommandSettleDelayMs, ct);
   }

   /// <summary>
   /// Updates cached observed status, emits change events, and recognizes only
   /// evidence of remote takeover. Called under the physical lamp gate.
   /// </summary>
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
      GoveeDeviceStatus? previous = _lastStatus;
      _lastStatus = status;
      _statusObservedAt = DateTimeOffset.UtcNow;

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

      if (_owner != ControlOwner.Pc)
      {
         return;
      }

      // Only treat a later OFF as a possible external takeover after we've
      // observed this PC-owned session in an ON state. This avoids treating
      // an initial stale OFF reply as a remote power-off event.
      if (status.IsOn)
      {
         _observedOnDuringOwnership = true;
      }

      // Firmware reports state, not WHO caused it. An OFF after an observed
      // ON is our strongest available signal of manual intervention. The
      // grace interval helps reject stale replies after our power-on send.
      if (_settings.AutoReleaseWhenRemoteTurnsOff && !status.IsOn &&
         _observedOnDuringOwnership &&
         DateTimeOffset.UtcNow - _lastPowerOnRequestedAt >=
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

   /// <summary>
   /// Returns ownership to Manual; disarms real-time mode if it had been armed.
   /// Does not power off or restore a cached color, which would fight the remote.
   /// </summary>
   private async Task ReleaseUnsafeAsync(string reason, CancellationToken ct)
   {
      // Decide whether to report a transition and whether to send DISARM
      // BEFORE clearing our bookkeeping. We always stop PC output immediately,
      // even if the physical disarm request later fails.
      bool changed = _owner != ControlOwner.Manual || _realtimeArmed;
      bool disarm = _realtimeArmed;

      // Change policy first, before potentially failing network I/O.
      _owner = ControlOwner.Manual;
      // REVIEW: clearing this flag now loses our only indication that the
      // PHYSICAL lamp may still be armed if the disarm UDP send throws.
      // Shutdown/another release then has nothing left to retry. Track an
      // unresolved disarm independently of PC ownership (see review notes).
      _realtimeArmed = false;
      _powerOnRequested = false;
      _observedOnDuringOwnership = false;
      _staticColorRequest = null;
      _lastRealtimeColors = null;
      _mismatchSuspected = false;
      _mismatchPollCount = 0;

      // Clear any frame that might otherwise be replayed after manual release.
      // This is safe because ReceiveFrame's lock does not depend on _lampGate.
      lock (_frameGate)
      {
         _latestFrame = null;
         _latestFrameAt = DateTimeOffset.MinValue;
      }

      if (changed)
      {
         _log.LogInformation("Manual ownership: {Reason}", reason);
         PublishUnsafe(BridgeEventKind.ControlOwnershipChanged, reason);
      }

      // Disarming asks the lamp to stop interpreting real-time frames.
      // Unlike power-off or restoring a color, this is intended to return
      // control to its normal mode. UDP offers no success acknowledgment.
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
   /// Only trust a status snapshot for deduplication while it is fresh enough
   /// to describe approximately the current lamp, not an arbitrarily old scene.
   /// </summary>
   private bool HasRecentStatusUnsafe()
   {
      // Freshness is a heuristic window, not a proof that nobody used the
      // remote after the query. Stale status must not suppress new commands.
      return _lampOnline == true &&
         _statusObservedAt is DateTimeOffset observedAt &&
         DateTimeOffset.UtcNow - observedAt <= TimeSpan.FromMilliseconds(
            _settings.RemotePollIntervalMs + _settings.LampTimeoutMs);
   }

   private void UpdateConnectivityUnsafe(bool online)
   {
      if (_lampOnline == online)
      {
         return;
      }
      _lampOnline = online;
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

   private void PublishUnsafe(BridgeEventKind kind, string details) =>
      _events.Publish(new BridgeEvent(kind, DateTimeOffset.UtcNow, _lastStatus, details));

   // Produces the JSON-friendly status returned to local management clients.
   // The caller already holds _lampGate, except where not required for
   // diagnostic logging. Values include BOTH observed state and attempted I/O.
   private object SnapshotUnsafe() => new
   {
      mode = _settings.TransportMode.ToString(),
      ownership = _owner == ControlOwner.Pc ? "PC" : "Manual",
      razerArmed = _realtimeArmed,
      lampOnline = _lampOnline,
      lampStatusObservedAt = _statusObservedAt,
      lampStatusMayBeStale = _realtimeArmed && !_options.PollWhileRealtimeArmed,
      framesReceived = Interlocked.Read(ref _framesReceived),
      framesSent = _framesSent,
      powerCommandsSent = _powerCommandsSent,
      lastStaticColor = _staticColorRequest?.Color.ToString(),
      staticVerified = _staticColorRequest?.Verified,
      staticAttempts = _staticColorRequest?.Attempts,
      staticRetryPending = _staticColorRequest?.RetryPending,
      staticSuspended = _staticColorRequest?.Suspended,
      lamp = _lastStatus
   };

   public void Dispose()
   {
      _lamp?.Dispose();
      _lampGate.Dispose();
   }
   // STATE OWNERSHIP AND LOCK ORDER
   //   _frameGate: only latest frame/timestamp and diagnostic throttle.
   //   _lampGate: ownership, device status, last-sent attempts, Razer lifecycle.
   // If both are needed, acquire _lampGate first, then briefly _frameGate.
   // ReceiveFrame takes _frameGate alone, never _lampGate, preventing inversion.
   //
   // Runtime does not spawn a dedicated thread: BridgeWorker invokes its
   // methods from several asynchronous tasks.
   /// <summary>
   /// One desired solid RGB color and the evidence collected while applying it.
   /// Kept only under _lampGate; never exposed to the UDP receive loop.
   /// </summary>
   private sealed class StaticColorRequest(RgbColor color)
   {
      public RgbColor Color { get; } = color;
      public int Attempts { get; set; }
      public DateTimeOffset LastAttemptAt { get; set; } = DateTimeOffset.MinValue;
      public GoveeDeviceStatus? ObservedBeforeAttempt { get; set; }
      public bool Verified { get; set; }
      public bool RetryPending { get; set; }
      public bool Suspended { get; set; }
   }

   private enum ControlOwner { Manual, Pc }

   private readonly BridgeSettings _settings;
   private readonly BridgeHostOptions _options;
   private readonly ILogger<BridgeRuntime> _log;
   private readonly IBridgeEventPublisher _events;
   private readonly IGoveeRealtimeClient? _lamp;
   // This gate covers logical *policy* as well as physical device calls.
   // The client's own semaphore separately protects its UDP socket. Locking
   // at both levels is intentional: transport safety != ownership safety.
   private readonly SemaphoreSlim _lampGate = new(1, 1);
   private readonly object _frameGate = new();

   // Accessed only under _frameGate.
   private RgbFrame? _latestFrame;
   private DateTimeOffset _latestFrameAt = DateTimeOffset.MinValue;
   private DateTimeOffset _lastDiagnosticLog = DateTimeOffset.MinValue;

   // Accessed only under _lampGate.
   private ControlOwner _owner = ControlOwner.Manual;
   private bool _powerOnRequested;
   private bool _observedOnDuringOwnership;
   // Currently tracks a successful ARM send attempt, not verified hardware.
   private bool _realtimeArmed;
   private bool? _lampOnline;
   private GoveeDeviceStatus? _lastStatus;
   private DateTimeOffset? _statusObservedAt;
   // "last" here means attempted/submitted, not verified by device status.
   private StaticColorRequest? _staticColorRequest;
   private RgbColor[]? _lastRealtimeColors;
   private DateTimeOffset _lastStaticAttemptAt = DateTimeOffset.MinValue;
   private DateTimeOffset _lastRealtimeFrameAt = DateTimeOffset.MinValue;
   private DateTimeOffset _lastPowerOnRequestedAt = DateTimeOffset.MinValue;
   private int _mismatchPollCount;
   private bool _mismatchSuspected;
   private long _framesSent;
   private long _powerCommandsSent;

   // Atomic: written on the receive path, read on the status path.
   private long _framesReceived;
}
