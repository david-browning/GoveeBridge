using System.Net.Sockets;
using GoveeBridge.Core;
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
      RgbFrame snapshot = new(frame.Colors.ToArray());
      DateTimeOffset now = DateTimeOffset.UtcNow;
      long count = Interlocked.Increment(ref _framesReceived);
      bool logDiagnostic = false;

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
      if (_lamp is null || !await _lampGate.WaitAsync(0, ct))
      {
         return;
      }

      try
      {
         if (_owner != ControlOwner.Pc)
         {
            return;
         }

         RgbFrame? pending;
         DateTimeOffset receivedAt;
         lock (_frameGate)
         {
            pending = _latestFrame;
            receivedAt = _latestFrameAt;
         }

         if (pending is null)
         {
            return;
         }

         DateTimeOffset now = DateTimeOffset.UtcNow;
         if (now - receivedAt > TimeSpan.FromMilliseconds(_options.FrameStaleAfterMs))
         {
            // An abandoned stream must not leave the Host claiming lamp control.
            await ReleaseUnsafeAsync("Frame stream expired", ct);
            return;
         }

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

      await _lampGate.WaitAsync(ct);
      try
      {
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
      ArgumentException.ThrowIfNullOrWhiteSpace(command);
      await _lampGate.WaitAsync(ct);
      try
      {
         switch (command.Trim().ToLowerInvariant())
         {
            case "resume":
               if (_owner == ControlOwner.Manual)
               {
                  // Frames queued before explicit takeover are not replayed.
                  lock (_frameGate)
                  {
                     _latestFrame = null;
                     _latestFrameAt = DateTimeOffset.MinValue;
                  }
                  _owner = ControlOwner.Pc;
                  _powerOnRequested = false;
                  _observedOnDuringOwnership = HasRecentStatusUnsafe() && _lastStatus?.IsOn == true;
                  _lastStaticColor = null;
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

            case "release":
               await ReleaseUnsafeAsync("Explicit release", ct);
               return SnapshotUnsafe();

            case "status":
               return SnapshotUnsafe();

            case "lamp-status":
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

   /// <summary>Sets the whole-lamp RGB color only if a new request is warranted.</summary>
   private async Task ApplyStaticFrameUnsafeAsync(
      RgbFrame frame, DateTimeOffset now, CancellationToken ct)
   {
      if (now - _lastStaticAttemptAt <
         TimeSpan.FromMilliseconds(_settings.MinimumSendIntervalMs))
      {
         return;
      }

      RgbColor wanted = GoveeHelpers.AverageColor(frame);
      if (_lastStaticColor == wanted)
      {
         return;
      }

      await EnsurePoweredOnUnsafeAsync(ct);

      // Only infer a match from a sufficiently recent status observation.
      // Status reports are not acknowledgments of earlier UDP sends.
      if (HasRecentStatusUnsafe() &&
         _lastStatus is { IsOn: true, ColorTemperatureKelvin: 0 } observed &&
         observed.Color == wanted)
      {
         _lastStaticColor = wanted;
         _lastStaticAttemptAt = DateTimeOffset.UtcNow;
         return;
      }

      await _lamp!.SetColorAsync(wanted, ct);
      _lastStaticColor = wanted;
      _lastStaticAttemptAt = DateTimeOffset.UtcNow;
      _framesSent++;
      _mismatchPollCount = 0;
      _mismatchSuspected = false;
   }

   /// <summary>
   /// Arms once, transmits changed multizone frames, and periodically resends
   /// the last frame as a firmware keepalive. The keepalive is an intentional
   /// exception to static-command deduplication.
   /// </summary>
   private async Task ApplyRealtimeFrameUnsafeAsync(
      RgbFrame frame, DateTimeOffset now, CancellationToken ct)
   {
      if (now - _lastRealtimeFrameAt <
         TimeSpan.FromMilliseconds(_options.RealtimeMinimumFrameIntervalMs))
      {
         return;
      }

      if (_lastRealtimeColors is not null &&
         frame.Colors.SequenceEqual(_lastRealtimeColors) &&
         now - _lastRealtimeFrameAt <
            TimeSpan.FromMilliseconds(_options.RealtimeKeepaliveIntervalMs))
      {
         return;
      }

      await EnsurePoweredOnUnsafeAsync(ct);
      if (!_realtimeArmed)
      {
         await _lamp!.SetRealtimeEnabledAsync(true, ct);
         _realtimeArmed = true;
         _log.LogInformation("Razer mode armed");
         if (_options.RealtimeArmDelayMs > 0)
         {
            await Task.Delay(_options.RealtimeArmDelayMs, ct);
         }
      }

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
      if (status is null)
      {
         UpdateConnectivityUnsafe(false);
         return;
      }

      UpdateConnectivityUnsafe(true);
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

      if (status.IsOn)
      {
         _observedOnDuringOwnership = true;
      }

      if (_settings.AutoReleaseWhenRemoteTurnsOff && !status.IsOn &&
         _observedOnDuringOwnership &&
         DateTimeOffset.UtcNow - _lastPowerOnRequestedAt >=
            TimeSpan.FromMilliseconds(_options.RemotePowerOffGraceMs))
      {
         await ReleaseUnsafeAsync("Observed external power-off", ct);
         return;
      }

      // Optional heuristic applies only to static RGB, and only after a
      // changed observation provides evidence of an unexpected manual edit.
      if (_settings.TransportMode != TransportMode.GoveeStatic ||
         !_options.AutoReleaseOnColorMismatch || _lastStaticColor is null ||
         !status.IsOn || status.ColorTemperatureKelvin != 0 ||
         DateTimeOffset.UtcNow - _lastStaticAttemptAt <
            TimeSpan.FromMilliseconds(_options.ColorMismatchGraceMs))
      {
         _mismatchPollCount = 0;
         _mismatchSuspected = false;
         return;
      }

      if (status.Color == _lastStaticColor.Value)
      {
         _mismatchPollCount = 0;
         _mismatchSuspected = false;
      }
      else
      {
         if (previous is not null && previous.Color != status.Color)
         {
            _mismatchSuspected = true;
         }
         _mismatchPollCount = _mismatchSuspected ? _mismatchPollCount + 1 : 0;
         if (_mismatchPollCount >= _options.ColorMismatchRequiredPolls)
         {
            await ReleaseUnsafeAsync("Possible external color override", ct);
         }
      }
   }

   /// <summary>
   /// Returns ownership to Manual; disarms real-time mode if it had been armed.
   /// Does not power off or restore a cached color, which would fight the remote.
   /// </summary>
   private async Task ReleaseUnsafeAsync(string reason, CancellationToken ct)
   {
      bool changed = _owner != ControlOwner.Manual || _realtimeArmed;
      bool disarm = _realtimeArmed;

      // Change policy first, before potentially failing network I/O.
      _owner = ControlOwner.Manual;
      _realtimeArmed = false;
      _powerOnRequested = false;
      _observedOnDuringOwnership = false;
      _lastStaticColor = null;
      _lastRealtimeColors = null;
      _mismatchSuspected = false;
      _mismatchPollCount = 0;

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
      lastStaticColor = _lastStaticColor?.ToString(),
      lamp = _lastStatus
   };

   public void Dispose()
   {
      _lamp?.Dispose();
      _lampGate.Dispose();
   }

   private enum ControlOwner { Manual, Pc }

   private readonly BridgeSettings _settings;
   private readonly BridgeHostOptions _options;
   private readonly ILogger<BridgeRuntime> _log;
   private readonly IBridgeEventPublisher _events;
   private readonly IGoveeRealtimeClient? _lamp;
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
   private bool _realtimeArmed;
   private bool? _lampOnline;
   private GoveeDeviceStatus? _lastStatus;
   private DateTimeOffset? _statusObservedAt;
   private RgbColor? _lastStaticColor;
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
