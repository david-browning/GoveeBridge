using System.Net.Sockets;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Protocol;

namespace GoveeBridge.Host;

public sealed partial class BridgeRuntime
{
   /// <summary>
   /// Owns all mutable control-session policy. Only touched under _lampGate;
   /// this is a reference type to avoid accidental copies of mutable state.
   /// </summary>
   private sealed class ControlSessionState
   {
      public ControlOwner Owner { get; private set; } = ControlOwner.Manual;
      public bool IsPcOwner => Owner == ControlOwner.Pc;
      public bool ObservedOnDuringOwnership { get; set; }
      public bool RealtimeArmed { get; set; }
      public int MismatchPollCount { get; set; }
      public bool MismatchSuspected { get; set; }

      public void BeginPcOwnership(bool previouslyObservedOn)
      {
         Owner = ControlOwner.Pc;
         ObservedOnDuringOwnership = previouslyObservedOn;
         ResetColorMismatch();
      }

      public void Release()
      {
         Owner = ControlOwner.Manual;
         RealtimeArmed = false;
         ObservedOnDuringOwnership = false;
         ResetColorMismatch();
      }

      public void ResetColorMismatch()
      {
         MismatchPollCount = 0;
         MismatchSuspected = false;
      }
   }

   /// <summary>
   /// Owns all outgoing-attempt history and rate-limit/dedup checks. This state
   /// does not claim that a UDP command was physically applied by the lamp.
   /// </summary>
   private sealed class OutputAttemptState
   {
      public bool PowerOnRequested { get; private set; }
      public DateTimeOffset LastPowerOnRequestedAt { get; private set; } = DateTimeOffset.MinValue;
      public StaticColorRequest? StaticColorRequest { get; private set; }
      public DateTimeOffset LastStaticAttemptAt { get; private set; } = DateTimeOffset.MinValue;
      public RgbColor[]? LastRealtimeColors { get; private set; }
      public DateTimeOffset LastRealtimeFrameAt { get; private set; } = DateTimeOffset.MinValue;
      public long FramesSent { get; private set; }
      public long PowerCommandsSent { get; private set; }

      /// <summary>
      /// Replaces an old static target only when the requested color changes.
      /// A new target must not inherit the previous target's retry history.
      /// </summary>
      public StaticColorRequest GetStaticTarget(RgbColor color, out bool changed)
      {
         changed = StaticColorRequest is null || StaticColorRequest.Color != color;
         if (changed)
         {
            StaticColorRequest = new StaticColorRequest(color);
         }

         return StaticColorRequest!;
      }

      /// <summary>Enforces static physical send spacing for initial sends AND retries.</summary>
      public bool CanSendStatic(DateTimeOffset now, int minimumSendIntervalMs) =>
         now - LastStaticAttemptAt >= TimeSpan.FromMilliseconds(minimumSendIntervalMs);

      /// <summary>Records an attempted static send only AFTER the client send returns.</summary>
      public void RecordStaticSend(StaticColorRequest request, GoveeDeviceStatus? observedBefore)
      {
         request.RecordSend(observedBefore);
         LastStaticAttemptAt = request.LastAttemptAt;
         FramesSent++;
      }

      /// <summary>
      /// Different Razer frame contents are sent at the configured max rate;
      /// unchanged frames are periodically retransmitted as a keepalive.
      /// </summary>
      public bool ShouldSendRealtime(RgbFrame frame, DateTimeOffset now, BridgeHostOptions options)
      {
         if (now - LastRealtimeFrameAt <
            TimeSpan.FromMilliseconds(options.RealtimeMinimumFrameIntervalMs))
         {
            return false;
         }

         return LastRealtimeColors is null ||
            !frame.Colors.SequenceEqual(LastRealtimeColors) ||
            now - LastRealtimeFrameAt >=
               TimeSpan.FromMilliseconds(options.RealtimeKeepaliveIntervalMs);
      }

      /// <summary>Records an attempted Razer send after it completes locally.</summary>
      public void RecordRealtimeSend(RgbFrame frame)
      {
         LastRealtimeColors = frame.Colors.ToArray();
         LastRealtimeFrameAt = DateTimeOffset.UtcNow;
         FramesSent++;
      }

      /// <summary>Records an attempted power-on send once per PC session.</summary>
      public void RecordPowerOnSend()
      {
         PowerOnRequested = true;
         LastPowerOnRequestedAt = DateTimeOffset.UtcNow;
         PowerCommandsSent++;
      }

      public void ResetForNewSession()
      {
         PowerOnRequested = false;
         LastPowerOnRequestedAt = DateTimeOffset.MinValue;
         StaticColorRequest = null;
         LastStaticAttemptAt = DateTimeOffset.MinValue;
         LastRealtimeColors = null;
         LastRealtimeFrameAt = DateTimeOffset.MinValue;
         // Lifetime counters intentionally remain cumulative across sessions.
      }

      public void ResetOnRelease()
      {
         PowerOnRequested = false;
         StaticColorRequest = null;
         LastRealtimeColors = null;
         // Keep timestamps: the next resume will reset them as before.
      }
   }

   /// <summary>Last devStatus observation and connectivity, not requested lamp output.</summary>
   private sealed class LampObservationState
   {
      public bool? Online { get; set; }
      public GoveeDeviceStatus? Status { get; private set; }
      public DateTimeOffset? ObservedAt { get; private set; }

      public GoveeDeviceStatus? Observe(GoveeDeviceStatus status)
      {
         GoveeDeviceStatus? previous = Status;
         Status = status;
         ObservedAt = DateTimeOffset.UtcNow;
         return previous;
      }

      public bool IsRecent(BridgeSettings settings) =>
         Online == true &&
         ObservedAt is DateTimeOffset at &&
         DateTimeOffset.UtcNow - at <= TimeSpan.FromMilliseconds(
            settings.RemotePollIntervalMs + settings.LampTimeoutMs);
   }

   /// <summary>Tracks one solid target through attempted sends and observations.</summary>
   private sealed class StaticColorRequest(RgbColor color)
   {
      public RgbColor Color { get; } = color;
      public int Attempts { get; private set; }
      public DateTimeOffset LastAttemptAt { get; private set; } = DateTimeOffset.MinValue;
      public GoveeDeviceStatus? ObservedBeforeAttempt { get; private set; }
      public bool Verified { get; private set; }
      public bool RetryPending { get; private set; }
      public bool Suspended { get; private set; }

      public bool NeedsSend => !Verified && !Suspended && (Attempts == 0 || RetryPending);

      /// <summary>An attempted command is not confirmation by devStatus.</summary>
      public void RecordSend(GoveeDeviceStatus? observedBefore)
      {
         ObservedBeforeAttempt = observedBefore;
         LastAttemptAt = DateTimeOffset.UtcNow;
         Attempts++;
         RetryPending = false;
      }

      public void ConfirmObserved()
      {
         Verified = true;
         RetryPending = false;
      }

      /// <summary>Never use a status received before the command as verification.</summary>
      public bool CanVerify(DateTimeOffset? observedAt, int graceMs) =>
         Attempts > 0 && observedAt is DateTimeOffset at &&
         at > LastAttemptAt &&
         at - LastAttemptAt >= TimeSpan.FromMilliseconds(graceMs);

      public void ScheduleRetry() => RetryPending = true;
      public void Suspend() => Suspended = true;
   }

   /// <summary>Immutable handoff of one latest-frame publication.</summary>
   private readonly record struct FrameReceipt(RgbFrame Frame, long ReceivedCount, bool LogDiagnostic);
   private readonly record struct FrameSnapshot(RgbFrame? Frame, DateTimeOffset ReceivedAt);

   /// <summary>
   /// Independent producer/consumer mailbox. The only lock here protects a frame
   /// snapshot and its receipt time; it never surrounds physical networking.
   /// </summary>
   private sealed class FrameMailbox
   {
      private readonly object _gate = new();
      private RgbFrame? _latest;
      private DateTimeOffset _receivedAt = DateTimeOffset.MinValue;
      private DateTimeOffset _lastDiagnosticLog = DateTimeOffset.MinValue;
      private long _receivedCount;

      public long ReceivedCount => Interlocked.Read(ref _receivedCount);

      public FrameReceipt Receive(RgbFrame frame, bool diagnostic)
      {
         // IReadOnlyList does not guarantee the caller's collection is immutable.
         RgbFrame copy = new(frame.Colors.ToArray());
         DateTimeOffset now = DateTimeOffset.UtcNow;
         long count = Interlocked.Increment(ref _receivedCount);
         bool log = false;
         lock (_gate)
         {
            _latest = copy;
            _receivedAt = now;
            if (diagnostic && now - _lastDiagnosticLog >=
               TimeSpan.FromMilliseconds(BridgeHostDefaults.DiagnosticLogIntervalMs))
            {
               _lastDiagnosticLog = now;
               log = true;
            }
         }
         return new FrameReceipt(copy, count, log);
      }

      public FrameSnapshot Snapshot()
      {
         lock (_gate)
         {
            return new FrameSnapshot(_latest, _receivedAt);
         }
      }

      public void Clear()
      {
         lock (_gate)
         {
            _latest = null;
            _receivedAt = DateTimeOffset.MinValue;
         }
      }
   }

   private enum ControlOwner { Manual, Pc }
}
