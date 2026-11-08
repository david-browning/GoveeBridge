using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Protocol;

namespace GoveeBridge.Host;

/// <summary>
/// Owns independently controlled lamp sessions. The Worker owns IPC and timer
/// cadence, while each session owns its state gate and physical-device policy.
/// </summary>
/// <remarks>
/// The Host has one shared UDP 4002 receiver and one control gate per lamp.
/// Methods suffixed Unsafe require the target lamp's Gate to be held; no method
/// acquires a second lamp's gate while holding the first.
/// </remarks>
public sealed partial class BridgeRuntime : IDisposable
{
   /// <summary>
   /// Creates configured lamp sessions with the shared physical LAN listener.
   /// </summary>
   public BridgeRuntime(
      BridgeSettings settings,
      BridgeHostOptions options,
      IBridgeEventPublisher events,
      ILogger<BridgeRuntime> log)
      : this(settings, options, events, log, null)
   {
   }

   /// <summary>
   /// Allows tests to supply per-lamp clients without binding the physical
   /// response port. Production uses the public four-argument constructor.
   /// </summary>
   internal BridgeRuntime(
      BridgeSettings settings,
      BridgeHostOptions options,
      IBridgeEventPublisher events,
      ILogger<BridgeRuntime> log,
      Func<BridgeLampSettings, IGoveeRealtimeClient>? clientFactory)
   {
      _settings = settings ?? throw new ArgumentNullException(nameof(settings));
      _options = options ?? throw new ArgumentNullException(nameof(options));
      _events = events ?? throw new ArgumentNullException(nameof(events));
      _log = log ?? throw new ArgumentNullException(nameof(log));
      settings.Validate();

      if (!settings.Diagnostic && clientFactory is null)
      {
         _network = new GoveeLanNetwork();
         clientFactory = lamp => new GoveeLanClient(_network, lamp.Ip, settings.LampTimeoutMs);
      }

      IReadOnlyList<BridgeLampSettings> configured = settings.Lamps.Count == 0 && settings.Diagnostic
         ? [new BridgeLampSettings { Id = "diagnostic" }]
         : settings.Lamps;

      _lamps = new Dictionary<string, LampSession>(StringComparer.OrdinalIgnoreCase);
      try
      {
         foreach (BridgeLampSettings entry in configured)
         {
            IGoveeRealtimeClient? client = settings.Diagnostic ? null : clientFactory!(entry);
            _lamps.Add(entry.Id, new LampSession(entry, client));
         }
      }
      catch
      {
         foreach (LampSession session in _lamps.Values)
         {
            session.Dispose();
         }
         _network?.Dispose();
         throw;
      }

      _log.LogInformation(
         "Bridge configured: Diagnostic={Diagnostic}, lamps={Count}",
         settings.Diagnostic, _lamps.Count);
   }

   /// <summary>
   /// Returns false for missing/unknown targets or mismatched configured zone
   /// counts. Valid frames are copied into this lamp's latest-frame mailbox.
   /// </summary>
   public bool ReceiveFrame(RgbFrame frame, string lampId)
   {
      ArgumentNullException.ThrowIfNull(frame);
      if (!TrySelectLamp(lampId, out LampSession? lamp))
      {
         return false;
      }

      int? zones = lamp!.Settings.Capabilities.RealtimeZoneCount;
      // The legacy IPC contract permits 256 entries; experimental Razer's
      // physical count field is only one byte, so no lamp can stream 256.
      if (frame.Colors.Count > byte.MaxValue ||
         (zones is not null && zones != frame.Colors.Count))
      {
         return false;
      }
      FrameReceipt receipt = lamp.Frames.Receive(frame, lamp.Client is null);
      if (receipt.LogDiagnostic)
      {
         _log.LogInformation("Diagnostic lamp {LampId}: frame #{Count}, {Zones} positions, average {Color}",
            lamp.Settings.Id, receipt.ReceivedCount, receipt.Frame.Colors.Count,
            GoveeHelpers.AverageColor(receipt.Frame));
      }
      return true;
   }

   /// <summary>
   /// Gives each lamp an independent output opportunity. No single global gate
   /// can stall the other lamps' control state while physical I/O is in flight.
   /// </summary>
   public Task PumpFrameAsync(CancellationToken ct) =>
      Task.WhenAll(_lamps.Values.Select(lamp => PumpLampAsync(lamp, ct)));

   /// <summary>
   /// Polls configured lamps independently, skipping physical queries when a
   /// Razer ARM attempt makes devStatus unreliable.
   /// </summary>
   public Task PollLampAsync(CancellationToken ct) =>
      _options.EnableLampMonitoring
         ? Task.WhenAll(_lamps.Values.Select(lamp => PollLampUnsafeAsync(lamp, ct)))
         : Task.CompletedTask;

   /// <summary>
   /// Chooses a configured lamp from its stable ID. For a
   /// multi-lamp Host without a configured default, a selector is required.
   /// </summary>
   private bool TrySelectLamp(string lampId, out LampSession? lamp)
   {
      if (!_lamps.TryGetValue(lampId, out lamp))
      {
         return false;
      }

      return true;
   }

   /// <summary>
   /// Periodic device status request under this lamp's gate.
   /// </summary>
   private async Task PollLampUnsafeAsync(LampSession lamp, CancellationToken ct)
   {
      if (lamp.Client is null)
      {
         return;
      }
      await lamp.Gate.WaitAsync(ct);
      try
      {
         if (lamp.Session.RealtimeArmed && !_options.PollWhileRealtimeArmed)
         {
            return;
         }
         await RefreshLampObservationUnsafeAsync(lamp, ct);
      }
      finally
      {
         lamp.Gate.Release();
      }
   }

   /// <summary>
   /// Outputs one current frame or one pending static-color retry according to
   /// the selected control mode; never replays stale input from Manual mode.
   /// </summary>
   private async Task PumpLampAsync(LampSession lamp, CancellationToken ct)
   {
      if (lamp.Client is null || !await lamp.Gate.WaitAsync(0, ct))
      {
         return;
      }
      try
      {
         DateTimeOffset now = DateTimeOffset.UtcNow;
         if (lamp.Session.Mode == ControlMode.Pc)
         {
            StaticColorRequest? retry = lamp.Attempts.StaticColorRequest;
            if (retry is { NeedsSend: true } && lamp.Session.RequestedPowerOn != false)
            {
               await ApplyStaticColorUnsafeAsync(lamp, retry.Color, now, ct);
            }
            return;
         }
         if (lamp.Session.Mode != ControlMode.PcStreaming)
         {
            return;
         }

         FrameSnapshot pending = lamp.Frames.Snapshot();
         DateTimeOffset lastActivity = pending.Frame is null
            ? lamp.Session.StreamingBeganAt : pending.ReceivedAt;
         if (now - lastActivity > TimeSpan.FromMilliseconds(_options.FrameStaleAfterMs))
         {
            await ReleaseUnsafeAsync(lamp, "Frame stream expired", ct);
            return;
         }
         // A deliberate OFF pauses hardware output, not receipt of frames.
         // New frames still keep the logical streaming session alive.
         if (lamp.Session.RequestedPowerOn == false || pending.Frame is null)
         {
            return;
         }
         await ApplyRealtimeFrameUnsafeAsync(lamp, pending.Frame, now, ct);
      }
      finally
      {
         lamp.Gate.Release();
      }
   }

   /// <summary>
   /// Makes bounded best-effort disarm attempts without using the Worker's
   /// already-cancelled shutdown token.
   /// </summary>
   public Task ShutdownAsync() =>
      Task.WhenAll(_lamps.Values.Select(ShutdownLampAsync));

   /// <summary>
   /// Releases a single lamp independently of other lamps during shutdown.
   /// </summary>
   private async Task ShutdownLampAsync(LampSession lamp)
   {
      using CancellationTokenSource timeout = new(
         TimeSpan.FromMilliseconds(_settings.LampTimeoutMs));
      try
      {
         await lamp.Gate.WaitAsync(timeout.Token);
         try
         {
            await ReleaseUnsafeAsync(lamp, "Host stopping", timeout.Token);
         }
         finally
         {
            lamp.Gate.Release();
         }
      }
      catch (OperationCanceledException)
      {
         _log.LogWarning("Timed out releasing lamp {LampId} on shutdown", lamp.Settings.Id);
      }
   }

   /// <summary>
   /// Disposes device clients before the single shared UDP response listener.
   /// </summary>
   public void Dispose()
   {
      foreach (LampSession lamp in _lamps.Values)
      {
         lamp.Dispose();
      }
      _network?.Dispose();
   }

   /// <summary>
   /// Deployment settings shared across lamp sessions.
   /// </summary>
   private readonly BridgeSettings _settings;

   /// <summary>
   /// Scheduling and arbitration options.
   /// </summary>
   private readonly BridgeHostOptions _options;

   /// <summary>
   /// Events for downstream diagnostics.
   /// </summary>
   private readonly IBridgeEventPublisher _events;

   /// <summary>
   /// Host logger for control transitions and physical attempts.
   /// </summary>
   private readonly ILogger<BridgeRuntime> _log;

   /// <summary>
   /// Stable lampId to independent state/gate/client mapping.
   /// </summary>
   private readonly Dictionary<string, LampSession> _lamps;

   /// <summary>
   /// One physical LAN response socket shared by all lamp clients.
   /// </summary>
   private readonly GoveeLanNetwork? _network;
}
