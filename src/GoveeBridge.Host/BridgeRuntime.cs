using System.Net.Sockets;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Protocol;

namespace GoveeBridge.Host;

/// <summary>
/// Serializes lamp ownership and physical I/O while accepting RGB frames through a
/// separate latest-frame-wins mailbox. Manual ownership is the startup default.
/// </summary>
/// <remarks>
/// A successful UDP send is only an attempted command, never hardware confirmation.
/// Private methods suffixed Unsafe require _lampGate to be held by their caller.
/// </remarks>
public sealed partial class BridgeRuntime : IDisposable
{
   /// <summary>
   /// Creates the sole lamp client. Diagnostic mode does not bind the physical LAN port.
   /// </summary>
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
   /// Publishes a snapshot without awaiting the physical lamp; older frames are replaced.
   /// This fast path deliberately does not acquire the device/ownership semaphore.
   /// </summary>
   public void ReceiveFrame(RgbFrame frame)
   {
      ArgumentNullException.ThrowIfNull(frame);
      FrameReceipt receipt = _frames.Receive(frame, _lamp is null);
      if (receipt.LogDiagnostic)
      {
         _log.LogInformation("Diagnostic frame #{Count}: {LedCount} positions, average {Color}",
            receipt.ReceivedCount, receipt.Frame.Colors.Count, GoveeHelpers.AverageColor(receipt.Frame));
      }
   }

   /// <summary>
   /// Samples the newest frame rather than queuing old animation states behind lamp I/O.
   /// </summary>
   public async Task PumpFrameAsync(CancellationToken ct)
   {
      if (_lamp is null || !await _lampGate.WaitAsync(0, ct))
      {
         return;
      }

      try
      {
         if (!_session.IsPcOwner)
         {
            return;
         }

         FrameSnapshot pending = _frames.Snapshot();
         if (pending.Frame is null)
         {
            return;
         }

         DateTimeOffset now = DateTimeOffset.UtcNow;
         if (now - pending.ReceivedAt > TimeSpan.FromMilliseconds(_options.FrameStaleAfterMs))
         {
            await ReleaseUnsafeAsync("Frame stream expired", ct);
            return;
         }

         switch (_settings.TransportMode)
         {
            case TransportMode.GoveeStatic:
            {
               await ApplyStaticFrameUnsafeAsync(pending.Frame, now, ct);
               break;
            }
            case TransportMode.GoveeRazer:
            {
               await ApplyRealtimeFrameUnsafeAsync(pending.Frame, now, ct);
               break;
            }
         }
      }
      finally
      {
         _lampGate.Release();
      }
   }

   /// <summary>
   /// Reads status without competing with a command or output. Does not poll while
   /// streaming Razer by default because devStatus can be unavailable in that mode.
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
         if (_session.RealtimeArmed && !_options.PollWhileRealtimeArmed)
         {
            return;
         }

         await RefreshLampObservationUnsafeAsync(ct);
      }
      finally
      {
         _lampGate.Release();
      }
   }

   /// <summary>Disposes the network client and arbitration semaphore after Host shutdown.</summary>
   public void Dispose()
   {
      _lamp?.Dispose();
      _lampGate.Dispose();
   }

   private readonly BridgeSettings _settings;
   private readonly BridgeHostOptions _options;
   private readonly ILogger<BridgeRuntime> _log;
   private readonly IBridgeEventPublisher _events;
   private readonly IGoveeRealtimeClient? _lamp;

   // Lock order: _lampGate, then briefly the mailbox lock (never the reverse).
   // Everything in _session, _attempts and _observed is accessed under _lampGate.
   private readonly SemaphoreSlim _lampGate = new(1, 1);
   private readonly FrameMailbox _frames = new();
   private readonly ControlSessionState _session = new();
   private readonly OutputAttemptState _attempts = new();
   private readonly LampObservationState _observed = new();
}
