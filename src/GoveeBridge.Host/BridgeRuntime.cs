using GoveeBridge.Core;
using GoveeBridge.Protocol;

namespace GoveeBridge.Host;

/// <summary>
/// Sole owner of the physical lamp client and its control policy.
/// ReceiveFrame is intentionally non-blocking; physical UDP requests are serialized.
/// </summary>
public sealed class BridgeRuntime : IDisposable
{
   private readonly BridgeSettings _settings;
   private readonly BridgeHostOptions _options;
   private readonly ILogger<BridgeRuntime> _log;
   private readonly IBridgeEventPublisher _events;
   private readonly GoveeLanClient? _lamp;
   private readonly SemaphoreSlim _lampGate = new(1, 1);
   private readonly object _frameGate = new();

   private bool _pcControl;  // Always false at startup.
   private bool _poweredByBridge;
   private DateTimeOffset _lastPowerOn = DateTimeOffset.MinValue;
   private bool? _lampOnline;
   private int _mismatchPollCount;
   private RgbColor? _lastSent;
   private RgbColor? _latestFrameColor;
   private GoveeDeviceStatus? _lastStatus;
   private DateTimeOffset _latestFrameAt = DateTimeOffset.MinValue;
   private DateTimeOffset _lastSend = DateTimeOffset.MinValue;
   private DateTimeOffset _lastDiagnosticLog = DateTimeOffset.MinValue;
   private long _framesReceived;
   private long _colorsSent;

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

      // Diagnostic mode does not bind Govee's fixed reply port 4002.
      if (settings.TransportMode == TransportMode.GoveeStatic)
         _lamp = new GoveeLanClient(settings.LampIp!, settings.LampTimeoutMs);

      _log.LogInformation("GoveeBridge mode={Mode}; ownership=Manual", settings.TransportMode);
   }

   /// <summary>Called on the IPC receive path. Never waits for lamp I/O.</summary>
   public void ReceiveFrame(RgbFrame frame)
   {
      RgbColor color = BridgeWireProtocol.AverageColor(frame);
      DateTimeOffset now = DateTimeOffset.UtcNow;
      lock (_frameGate)
      {
         _latestFrameColor = color;
         _latestFrameAt = now;
         _framesReceived++;

         if (_lamp is null && now - _lastDiagnosticLog >= TimeSpan.FromSeconds(1))
         {
            _lastDiagnosticLog = now;
            _log.LogInformation("Diagnostic frame #{Count}: {LedCount} LEDs, averaged {Color}",
                _framesReceived, frame.Colors.Count, color);
         }
      }
   }

   /// <summary>
   /// Sends at most one current frame per configured interval. Called by the Host's
   /// independent pump, not by the SignalRGB UDP receive loop.
   /// </summary>
   public async Task PumpFrameAsync(CancellationToken ct)
   {
      if (_lamp is null) return;

      RgbColor? pending;
      DateTimeOffset queuedAt;
      lock (_frameGate)
      {
         pending = _latestFrameColor;
         queuedAt = _latestFrameAt;
      }

      if (pending is null ||
          DateTimeOffset.UtcNow - queuedAt > TimeSpan.FromMilliseconds(_options.FrameStaleAfterMs))
         return;

      // A status poll might be in progress. Do not stall the frame pump.
      if (!await _lampGate.WaitAsync(0, ct)) return;
      try
      {
         if (!_pcControl) return;
         if (DateTimeOffset.UtcNow - _lastSend <
             TimeSpan.FromMilliseconds(_settings.MinimumSendIntervalMs)) return;
         if (_poweredByBridge && _lastSent == pending) return;

         if (!_poweredByBridge)
         {
            // Only after an explicit resume command. No startup auto-power-on.
            await _lamp.SetPowerAsync(true, ct);
            _poweredByBridge = true;
            _lastPowerOn = DateTimeOffset.UtcNow;
            // Govee may silently drop tightly spaced control datagrams.
            await Task.Delay(30, ct);
         }

         await _lamp.SetColorAsync(pending.Value, ct);
         _lastSent = pending;
         _lastSend = DateTimeOffset.UtcNow;
         _colorsSent++;
         _mismatchPollCount = 0;
      }
      finally { _lampGate.Release(); }
   }

   /// <summary>Polls on startup and on a timer, regardless of who owns the lamp.</summary>
   public async Task PollLampAsync(CancellationToken ct)
   {
      if (_lamp is null || !_options.EnableLampMonitoring) return;

      await _lampGate.WaitAsync(ct);
      try
      {
         GoveeDeviceStatus? status = await _lamp.GetStatusAsync(ct);
         ApplyObservedStatusUnsafe(status);
      }
      finally { _lampGate.Release(); }
   }

   private void ApplyObservedStatusUnsafe(GoveeDeviceStatus? status)
   {
      if (status is null)
      {
         UpdateConnectivityUnsafe(false);
         return;
      }

      UpdateConnectivityUnsafe(true);
      if (_lastStatus != status)
      {
         GoveeDeviceStatus? previous = _lastStatus;
         _lastStatus = status;
         if (_options.LogLampStatusChanges)
            _log.LogInformation(
                "Lamp state changed: On={On}, Brightness={Brightness}%, RGB={Color}, Kelvin={Kelvin} (previous={Previous})",
                status.IsOn, status.Brightness, status.Color, status.ColorTemperatureKelvin, previous);
         PublishUnsafe(BridgeEventKind.LampStatusChanged, status, "Observed via Govee status query");
      }

      // The one relatively unambiguous takeover signal we have: someone
      // turned the lamp off while we believed we controlled it.
      if (_pcControl && _poweredByBridge &&
          _settings.AutoReleaseWhenRemoteTurnsOff && !status.IsOn &&
          DateTimeOffset.UtcNow - _lastPowerOn >= TimeSpan.FromMilliseconds(1200))
      {
         ReleaseUnsafe("External power-off observed");
         return;
      }

      // Experimental heuristic for manual color changes, disabled by
      // default. Govee does not identify the actor in devStatus replies.
      if (_pcControl && _poweredByBridge && _options.AutoReleaseOnColorMismatch &&
          _lastSent.HasValue && status.IsOn && status.ColorTemperatureKelvin == 0 &&
          DateTimeOffset.UtcNow - _lastSend >=
              TimeSpan.FromMilliseconds(_options.ColorMismatchGraceMs))
      {
         _mismatchPollCount = status.Color != _lastSent.Value
             ? _mismatchPollCount + 1 : 0;
         if (_mismatchPollCount >= _options.ColorMismatchRequiredPolls)
            ReleaseUnsafe("Repeated unexpected lamp color (best-effort override)");
      }
      else
      {
         _mismatchPollCount = 0;
      }
   }

   public async Task<object> ExecuteCommandAsync(string command, CancellationToken ct)
   {
      await _lampGate.WaitAsync(ct);
      try
      {
         switch (command.ToLowerInvariant())
         {
            case "resume":
               _pcControl = true;
               _poweredByBridge = false;
               _lastSent = null;
               _mismatchPollCount = 0;
               _log.LogInformation("PC control enabled by explicit resume command");
               PublishUnsafe(BridgeEventKind.ControlOwnershipChanged, _lastStatus, "PC");
               return SnapshotUnsafe();
            case "release":
               ReleaseUnsafe("Explicit release");
               return SnapshotUnsafe();
            case "status":
               return SnapshotUnsafe();
            case "lamp-status":
               if (_lamp is null) return new { error = "Diagnostic mode has no physical lamp connection" };
               // An explicit query updates the cached state too; monitoring should
               // remain enabled in real mode for automatic change notifications.
               GoveeDeviceStatus? value = await _lamp.GetStatusAsync(ct);
               ApplyObservedStatusUnsafe(value);
               return new { lamp = value, status = SnapshotUnsafe() };
            default:
               return new { error = $"Unknown command: {command}" };
         }
      }
      finally { _lampGate.Release(); }
   }

   private void UpdateConnectivityUnsafe(bool online)
   {
      if (_lampOnline == online) return;
      _lampOnline = online;
      if (online) _log.LogInformation("Lamp online");
      else _log.LogWarning("Lamp unreachable or status timed out");
      PublishUnsafe(online ? BridgeEventKind.LampOnline : BridgeEventKind.LampOffline,
          _lastStatus, online ? "online" : "offline");
   }

   private void ReleaseUnsafe(string reason)
   {
      _pcControl = false;
      _poweredByBridge = false;
      _lastSent = null;
      _mismatchPollCount = 0;
      _log.LogInformation("Manual ownership: {Reason}; no further lighting commands", reason);
      PublishUnsafe(BridgeEventKind.ControlOwnershipChanged, _lastStatus, reason);
      // Do not turn the lamp off or automatically reclaim ownership.
   }

   private void PublishUnsafe(BridgeEventKind kind, GoveeDeviceStatus? status, string? details) =>
       _events.Publish(new BridgeEvent(kind, DateTimeOffset.UtcNow, status, details));

   private object SnapshotUnsafe() => new
   {
      mode = _settings.TransportMode.ToString(),
      ownership = _pcControl ? "PC" : "Manual",
      lampOnline = _lampOnline,
      framesReceived = Interlocked.Read(ref _framesReceived),
      colorsSent = _colorsSent,
      lastColor = _lastSent?.ToString(),
      lamp = _lastStatus
   };

   public void Dispose()
   {
      _lamp?.Dispose();
      _lampGate.Dispose();
   }
}
