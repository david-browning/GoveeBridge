using GoveeBridge.Core;

namespace GoveeBridge.Host;

public sealed partial class BridgeRuntime
{
   /// <summary>
   /// All mutable state and the asynchronous I/O gate for one configured lamp.
   /// A slow device must not monopolize other lamps' control sessions.
   /// </summary>
   private sealed class LampSession : IDisposable
   {
      /// <summary>
      /// Creates a session; the network client may be null in Diagnostic mode.
      /// </summary>
      public LampSession(BridgeLampSettings settings, IGoveeRealtimeClient? client)
      {
         Settings = settings;
         Client = client;
      }

      /// <summary>
      /// Stable identity and configured capabilities.
      /// </summary>
      public BridgeLampSettings Settings { get; }

      /// <summary>
      /// Physical API, or null when hardware operations are disabled.
      /// </summary>
      public IGoveeRealtimeClient? Client { get; }

      /// <summary>
      /// Serializes this lamp's mode, observations, commands and output.
      /// </summary>
      public SemaphoreSlim Gate { get; } = new(1, 1);

      /// <summary>
      /// Coalesces incoming frames for this lamp independently.
      /// </summary>
      public FrameMailbox Frames { get; } = new();

      /// <summary>
      /// Logical control and power intent.
      /// </summary>
      public ControlSessionState Session { get; } = new();

      /// <summary>
      /// Physical sends and retry bookkeeping.
      /// </summary>
      public OutputAttemptState Attempts { get; } = new();

      /// <summary>
      /// Last observed status, without treating it as acknowledgment.
      /// </summary>
      public LampObservationState Observed { get; } = new();

      /// <summary>
      /// Releases this lamp's client while the Host owns the shared LAN receiver.
      /// </summary>
      public void Dispose()
      {
         Client?.Dispose();
         Gate.Dispose();
      }
   }

   /// <summary>
   /// Host-requested control policy. The mode is not a measured hardware state.
   /// RealtimeArmed tracks a submitted ARM not yet followed by a successful
   /// DISARM submission, including cases where Mode is already Manual or Pc.
   /// </summary>
   private sealed class ControlSessionState
   {
      /// <summary>
      /// Determines which operations are permitted.
      /// </summary>
      public ControlMode Mode { get; private set; } = ControlMode.Manual;

      /// <summary>
      /// True whenever the Host holds either PC control mode.
      /// </summary>
      public bool IsPcOwner => Mode != ControlMode.Manual;

      /// <summary>
      /// True after ARM submission until successful DISARM submission.
      /// </summary>
      public bool RealtimeArmed { get; private set; }

      /// <summary>
      /// Explicit session power preference; null permits automatic startup power-on.
      /// </summary>
      public bool? RequestedPowerOn { get; private set; }

      /// <summary>
      /// Power cycling may have invalidated the hardware's Razer arm.
      /// </summary>
      public bool NeedsRealtimeRearm { get; private set; }

      /// <summary>
      /// Fallback activity timestamp while the streaming mailbox is empty.
      /// Prevents an armed stream with no frames from running indefinitely.
      /// </summary>
      public DateTimeOffset StreamingBeganAt { get; private set; } = DateTimeOffset.MinValue;

      /// <summary>
      /// Evidence used to detect unexpected remote power-off.
      /// </summary>
      public bool ObservedOnDuringOwnership { get; set; }

      /// <summary>
      /// Number of consecutive suspected external color changes.
      /// </summary>
      public int MismatchPollCount { get; set; }

      /// <summary>
      /// Whether a changed color/mode was observed during ownership.
      /// </summary>
      public bool MismatchSuspected { get; set; }

      /// <summary>
      /// Begins direct control. A pending physical DISARM must be cleared first.
      /// </summary>
      public void BeginPcOwnership(bool previouslyObservedOn)
      {
         Mode = ControlMode.Pc;
         RequestedPowerOn = null;
         NeedsRealtimeRearm = false;
         ObservedOnDuringOwnership = previouslyObservedOn;
         ResetColorMismatch();
      }

      /// <summary>
      /// Enters streaming only after a physical ARM command was submitted.
      /// </summary>
      public void BeginStreaming()
      {
         Mode = ControlMode.PcStreaming;
         RealtimeArmed = true;
         NeedsRealtimeRearm = false;
         StreamingBeganAt = DateTimeOffset.UtcNow;
      }

      /// <summary>
      /// Stops scheduling frames before a physical DISARM attempt completes.
      /// </summary>
      public void StopStreaming()
      {
         if (Mode == ControlMode.PcStreaming)
         {
            Mode = ControlMode.Pc;
         }
         NeedsRealtimeRearm = false;
         StreamingBeganAt = DateTimeOffset.MinValue;
      }

      /// <summary>
      /// Marks the physical DISARM command as successfully submitted.
      /// </summary>
      public void RecordDisarmSent()
      {
         RealtimeArmed = false;
         NeedsRealtimeRearm = false;
      }

      /// <summary>
      /// Marks requested power; explicit OFF suppresses all streaming output.
      /// </summary>
      public void SetPowerIntent(bool on)
      {
         RequestedPowerOn = on;
         if (!on && Mode == ControlMode.PcStreaming)
         {
            NeedsRealtimeRearm = true;
            StreamingBeganAt = DateTimeOffset.UtcNow;
         }
      }

      /// <summary>
      /// Marks a successful Razer re-arm submission after power cycling.
      /// </summary>
      public void RecordRearmSent()
      {
         RealtimeArmed = true;
         NeedsRealtimeRearm = false;
      }

      /// <summary>
      /// Returns Host ownership to Manual without falsely claiming DISARM succeeded.
      /// </summary>
      public void Release()
      {
         Mode = ControlMode.Manual;
         RequestedPowerOn = null;
         NeedsRealtimeRearm = false;
         StreamingBeganAt = DateTimeOffset.MinValue;
         ObservedOnDuringOwnership = false;
         ResetColorMismatch();
      }

      /// <summary>
      /// Clears evidence for a superseded or released color target.
      /// </summary>
      public void ResetColorMismatch()
      {
         MismatchPollCount = 0;
         MismatchSuspected = false;
      }
   }

   /// <summary>
   /// Bookkeeping for locally completed physical send attempts, bounded static
   /// RGB verification, and realtime deduplication. Not device acknowledgment.
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

      /// <summary>
      /// Enforces static physical send spacing for initial sends AND retries.
      /// </summary>
      public bool CanSendStatic(DateTimeOffset now, int minimumSendIntervalMs) =>
         now - LastStaticAttemptAt >= TimeSpan.FromMilliseconds(minimumSendIntervalMs);

      /// <summary>
      /// Records an attempted static send only AFTER the client send returns.
      /// </summary>
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

      /// <summary>
      /// Records an attempted Razer send after it completes locally.
      /// </summary>
      public void RecordRealtimeSend(RgbFrame frame)
      {
         LastRealtimeColors = frame.Colors.ToArray();
         LastRealtimeFrameAt = DateTimeOffset.UtcNow;
         FramesSent++;
      }

      /// <summary>
      /// Records an attempted power-on send once per PC session.
      /// </summary>
      public void RecordPowerOnSend()
      {
         PowerOnRequested = true;
         LastPowerOnRequestedAt = DateTimeOffset.UtcNow;
         PowerCommandsSent++;
      }

      /// <summary>
      /// Records a completed explicit power-on command without incrementing the
      /// automatic power-on counter or sending the same request on the next frame.
      /// </summary>
      public void RecordExplicitPowerOn()
      {
         PowerOnRequested = true;
         LastPowerOnRequestedAt = DateTimeOffset.UtcNow;
      }

      /// <summary>
      /// Permits a fresh power-on request after a deliberate power-off.
      /// </summary>
      public void ClearPowerOnRequest() => PowerOnRequested = false;

      /// <summary>
      /// Cancels obsolete static color verification when another mode or target wins.
      /// </summary>
      public void ClearStaticTarget() => StaticColorRequest = null;

      /// <summary>
      /// Requires the next eligible frame to be transmitted even if it has the
      /// same RGB content as a frame from before a power cycle or disarm.
      /// </summary>
      public void ClearRealtimeHistory()
      {
         LastRealtimeColors = null;
         LastRealtimeFrameAt = DateTimeOffset.MinValue;
      }

      /// <summary>
      /// Resets power, color, and frame bookkeeping for a newly claimed PC
      /// session while retaining lifetime send counters.
      /// </summary>
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

      /// <summary>
      /// Discards pending output intent when returning to Manual control.
      /// </summary>
      public void ResetOnRelease()
      {
         PowerOnRequested = false;
         StaticColorRequest = null;
         LastRealtimeColors = null;
         // Keep timestamps: the next resume will reset them as before.
      }
   }

   /// <summary>
   /// Last devStatus observation and connectivity, not requested lamp output.
   /// </summary>
   private sealed class LampObservationState
   {
      public bool? Online { get; set; }
      public GoveeDeviceStatus? Status { get; private set; }
      public DateTimeOffset? ObservedAt { get; private set; }

      /// <summary>
      /// Stores the latest physical observation and returns the prior snapshot
      /// for change detection, updating the timestamp even for equal values.
      /// </summary>
      public GoveeDeviceStatus? Observe(GoveeDeviceStatus status)
      {
         GoveeDeviceStatus? previous = Status;
         Status = status;
         ObservedAt = DateTimeOffset.UtcNow;
         return previous;
      }

      /// <summary>
      /// Reports whether the cached observation falls within the best-effort
      /// freshness window; not proof that external control did not change it.
      /// </summary>
      public bool IsRecent(BridgeSettings settings) =>
         Online == true &&
         ObservedAt is DateTimeOffset at &&
         DateTimeOffset.UtcNow - at <= TimeSpan.FromMilliseconds(
            settings.RemotePollIntervalMs + settings.LampTimeoutMs);
   }

   /// <summary>
   /// Tracks one solid target through attempted sends and observations.
   /// </summary>
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

      /// <summary>
      /// An attempted command is not confirmation by devStatus.
      /// </summary>
      public void RecordSend(GoveeDeviceStatus? observedBefore)
      {
         ObservedBeforeAttempt = observedBefore;
         LastAttemptAt = DateTimeOffset.UtcNow;
         Attempts++;
         RetryPending = false;
      }

      /// <summary>
      /// Stops retrying only after an independently observed matching color.
      /// </summary>
      public void ConfirmObserved()
      {
         Verified = true;
         RetryPending = false;
      }

      /// <summary>
      /// Never use a status received before the command as verification.
      /// </summary>
      public bool CanVerify(DateTimeOffset? observedAt, int graceMs) =>
         Attempts > 0 && observedAt is DateTimeOffset at &&
         at > LastAttemptAt &&
         at - LastAttemptAt >= TimeSpan.FromMilliseconds(graceMs);

      /// <summary>
      /// Requests another bounded physical send without performing it here.
      /// </summary>
      public void ScheduleRetry() => RetryPending = true;
      /// <summary>
      /// Permanently stops automatic sends for this target after suspected
      /// external intervention or retry exhaustion.
      /// </summary>
      public void Suspend() => Suspended = true;
   }

   /// <summary>
   /// Immutable handoff of one latest-frame publication.
   /// </summary>
   private readonly record struct FrameReceipt(RgbFrame Frame, long ReceivedCount, bool LogDiagnostic);
   /// <summary>
   /// One atomic read of the latest frame and its receipt time.
   /// </summary>
   private readonly record struct FrameSnapshot(RgbFrame? Frame, DateTimeOffset ReceivedAt);

   /// <summary>
   /// Independent producer/consumer mailbox. The only lock here protects a frame
   /// snapshot and its receipt time; it never surrounds physical networking.
   /// </summary>
   private sealed class FrameMailbox
   {
      /// <summary>
      /// Protects mailbox contents without holding a physical I/O lock.
      /// </summary>
      private readonly object _gate = new();
      /// <summary>
      /// Immutable private copy of the latest received frame.
      /// </summary>
      private RgbFrame? _latest;
      /// <summary>
      /// Timestamp of the frame currently in the mailbox.
      /// </summary>
      private DateTimeOffset _receivedAt = DateTimeOffset.MinValue;
      /// <summary>
      /// Limits diagnostic frame logging without throttling input.
      /// </summary>
      private DateTimeOffset _lastDiagnosticLog = DateTimeOffset.MinValue;
      /// <summary>
      /// Cumulative valid input receipt count, including superseded frames.
      /// </summary>
      private long _receivedCount;

      public long ReceivedCount => Interlocked.Read(ref _receivedCount);

      /// <summary>
      /// Publishes a private copy of the newest frame while updating the
      /// receipt count and rate-limited diagnostic logging state.
      /// </summary>
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

      /// <summary>
      /// Atomically reads the latest frame and its receipt timestamp.
      /// </summary>
      public FrameSnapshot Snapshot()
      {
         lock (_gate)
         {
            return new FrameSnapshot(_latest, _receivedAt);
         }
      }

      /// <summary>
      /// Forgets frames from earlier ownership or power sessions so they are
      /// never replayed after an explicit control transition.
      /// </summary>
      public void Clear()
      {
         lock (_gate)
         {
            _latest = null;
            _receivedAt = DateTimeOffset.MinValue;
         }
      }
   }

}
