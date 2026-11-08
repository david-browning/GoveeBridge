using System.Text.Json.Serialization;

namespace GoveeBridge.Core.Ipc;

/// <summary>
/// Stable JSON property names used by Host-to-client IPC responses.
/// </summary>
/// <remarks>
/// These constants describe GoveeBridge's local IPC contract, not the physical
/// Govee LAN protocol. Changing a string value changes the wire format and can
/// break existing PowerShell or other clients; changing a C# identifier alone
/// does not. Keep the values consistent with the corresponding
/// <see cref="JsonPropertyNameAttribute"/> annotations below.
/// </remarks>
public static class BridgeIpcResponsePropertyNames
{
   /// <summary>
   /// Current Host control mode: Manual, Pc, or PcStreaming.
   /// </summary>
   public const string Mode = "mode";

   /// <summary>
   /// Stable configured lamp selector for this response.
   /// </summary>
   public const string LampId = "lampId";

   /// <summary>
   /// Explicit requested power state, or null if no direct power request exists.
   /// </summary>
   public const string RequestedPowerOn = "requestedPowerOn";

   /// <summary>
   /// Logical control owner: PC or Manual; not a physical-device acknowledgment.
   /// </summary>
   public const string Ownership = "ownership";

   /// <summary>
   /// Whether the Host believes it submitted a Razer arm request successfully.
   /// </summary>
   public const string RazerArmed = "razerArmed";

   /// <summary>
   /// Whether the latest status query succeeded, failed, or has not occurred.
   /// </summary>
   public const string LampOnline = "lampOnline";

   /// <summary>
   /// Timestamp of the latest successfully parsed physical status response.
   /// </summary>
   public const string LampStatusObservedAt = "lampStatusObservedAt";

   /// <summary>
   /// Whether status polling is intentionally suspended during Razer output.
   /// </summary>
   public const string LampStatusMayBeStale = "lampStatusMayBeStale";

   /// <summary>
   /// Number of valid RGB frames accepted by the Host input path.
   /// </summary>
   public const string FramesReceived = "framesReceived";

   /// <summary>
   /// Number of static RGB or Razer frame sends submitted by the Host.
   /// </summary>
   public const string FramesSent = "framesSent";

   /// <summary>
   /// Number of automatic power-on commands submitted by the Host.
   /// </summary>
   public const string PowerCommandsSent = "powerCommandsSent";

   /// <summary>
   /// Last requested static RGB color, not necessarily the observed color.
   /// </summary>
   public const string LastStaticColor = "lastStaticColor";

   /// <summary>
   /// Whether the active static target has been verified by observation.
   /// </summary>
   public const string StaticVerified = "staticVerified";

   /// <summary>
   /// Number of completed send attempts for the active static RGB target.
   /// </summary>
   public const string StaticAttempts = "staticAttempts";

   /// <summary>
   /// Whether monitoring has requested another bounded static-color attempt.
   /// </summary>
   public const string StaticRetryPending = "staticRetryPending";

   /// <summary>
   /// Whether automatic attempts for the active static target have stopped.
   /// </summary>
   public const string StaticSuspended = "staticSuspended";

   /// <summary>
   /// Nested physical device status observation, which may be absent or old.
   /// </summary>
   public const string Lamp = "lamp";

   /// <summary>
   /// Nested snapshot of the Host bookkeeping state.
   /// </summary>
   public const string Status = "status";

   /// <summary>
   /// Human-readable reason a management request could not be satisfied.
   /// </summary>
   public const string Error = "error";
}

/// <summary>
/// Common base type for mutually exclusive management-response DTOs.
/// </summary>
/// <remarks>
/// The derived types have different JSON shapes: a bare Host snapshot, a
/// physical-query result, or an error. Responses have no separate JSON type
/// discriminator. Serialize their concrete runtime types, as BridgeWorker does,
/// so their derived properties are not lost through base-type serialization.
/// </remarks>
public abstract record BridgeIpcResponse;

/// <summary>
/// Immutable snapshot of the Host's logical ownership, delivery attempts, and
/// last known physical status. Returned directly by status, resume, and release.
/// </summary>
/// <remarks>
/// This is a diagnostic snapshot of Host bookkeeping, not proof of the lamp's
/// physical state. All send counters count submissions, not device acknowledgments.
/// Fields about the static color refer to the current verification request, and
/// are nullable when there is no active static request. The JSON names deliberately
/// match the original anonymous-object response for client compatibility.
/// </remarks>
/// <param name="Mode">
/// Host-selected <see cref="ControlMode"/> as its enum name: Manual, Pc, or PcStreaming.
/// This is a control policy and not a hardware-observed mode.
/// </param>
/// <param name="Ownership">
/// Current logical owner, "PC" or "Manual". Manual is the startup default;
/// receiving frames does not automatically grant PC ownership. This does not
/// indicate which app or remote last changed the actual lamp.
/// </param>
/// <param name="RazerArmed">
/// True after the Host has submitted a Razer-arm request and has not yet cleared
/// its arm bookkeeping. Failed DISARM submissions retain this flag so the Host
/// can retry; false still does not prove the lamp applied the DISARM packet.
/// </param>
/// <param name="LampOnline">
/// True if the latest attempted devStatus poll returned a valid response; false
/// if it failed or timed out; null if no result has established connectivity.
/// This is reachability as last observed, not a continuous heartbeat.
/// </param>
/// <param name="LampStatusObservedAt">
/// Timestamp of the most recent valid devStatus response, or null if none has
/// been received. It updates even when the returned status values did not change.
/// Compare with the current time to determine freshness.
/// </param>
/// <param name="LampStatusMayBeStale">
/// True specifically when Razer mode is armed and the Host is configured not to
/// poll devStatus while streaming. False does NOT guarantee that the cached
/// observation is recent; check <paramref name="LampStatusObservedAt"/>.
/// </param>
/// <param name="FramesReceived">
/// Total valid RGB frame messages accepted by the Host since startup, including
/// frames received while Manual owns the lamp or frames later superseded by a
/// newer snapshot. This is not the number of frames displayed.
/// </param>
/// <param name="FramesSent">
/// Total static-color and real-time frame output sends completed by the Host
/// since startup. Deduplicated, skipped, or throttled frames do not increment
/// it. A completed UDP send does not confirm the lamp displayed that frame.
/// </param>
/// <param name="PowerCommandsSent">
/// Number of automatic power-on requests completed by the Host since startup.
/// This counts automatic power-on attempts in ARM/frame output, not explicit
/// set-power management commands or verified physical state changes.
/// </param>
/// <param name="LastStaticColor">
/// Color string (for example, "#FF0000") requested by the current static-color
/// reconciliation attempt. Null if no static target is active. This is a
/// desired color, not necessarily the lamp's currently observed color.
/// </param>
/// <param name="StaticVerified">
/// True when the current static target matches a suitable devStatus observation;
/// false while an active request is unverified; null when there is no active
/// static request. Verification is only as reliable as devStatus, which cannot
/// describe all dynamic Govee scenes.
/// </param>
/// <param name="StaticAttempts">
/// Number of completed SetColorAsync attempts for the current direct RGB target.
/// Zero means no color send was needed or attempted; null means no active target.
/// Each completed send is a transport attempt, not physical acknowledgment.
/// </param>
/// <param name="StaticRetryPending">
/// True if monitoring scheduled a retry of the current static RGB target and
/// the output pump has not yet performed it. False if no retry is queued; null
/// if there is no static target. Scheduling respects rate limits and the retry cap.
/// </param>
/// <param name="StaticSuspended">
/// True when automatic sends for the current static target have been stopped,
/// for example after retry exhaustion or an unexpected external color/mode
/// change. False while that target remains eligible; null if no target exists.
/// Suspension alone does not necessarily mean ownership was released.
/// </param>
/// <param name="Lamp">
/// Last successfully observed <see cref="GoveeDeviceStatus"/>, or null if none
/// is cached. The observation can be stale and does not expose the current
/// animation frame or identify who last changed the lamp.
/// </param>
/// <param name="LampId">
/// Stable configured lamp target associated with this per-device snapshot.
/// </param>
/// <param name="RequestedPowerOn">
/// Explicit power request during the current session: true, false, or null
/// when the Host has no explicit power intent. Not a hardware acknowledgment.
/// </param>
public sealed record BridgeHostStatusSnapshot(
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.Mode)] string Mode,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.Ownership)] string Ownership,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.RazerArmed)] bool RazerArmed,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.LampOnline)] bool? LampOnline,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.LampStatusObservedAt)] DateTimeOffset? LampStatusObservedAt,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.LampStatusMayBeStale)] bool LampStatusMayBeStale,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.FramesReceived)] long FramesReceived,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.FramesSent)] long FramesSent,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.PowerCommandsSent)] long PowerCommandsSent,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.LastStaticColor)] string? LastStaticColor,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.StaticVerified)] bool? StaticVerified,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.StaticAttempts)] int? StaticAttempts,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.StaticRetryPending)] bool? StaticRetryPending,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.StaticSuspended)] bool? StaticSuspended,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.Lamp)] GoveeDeviceStatus? Lamp,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.LampId)] string LampId,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.RequestedPowerOn)] bool? RequestedPowerOn = null
) : BridgeIpcResponse;

/// <summary>
/// Reply to an explicit lamp-status request made through the Host.
/// </summary>
/// <remarks>
/// The Host queries the physical device and updates its observation cache before
/// capturing <paramref name="Status"/>. A timeout may produce a null
/// <paramref name="Lamp"/> while retaining an older cached lamp observation
/// inside <paramref name="Status"/>; these two values therefore have different
/// freshness guarantees. If physical polling is disallowed during Razer mode,
/// the Host returns a <see cref="BridgeIpcErrorResponse"/> instead.
/// </remarks>
/// <param name="Lamp">
/// Physical <see cref="GoveeDeviceStatus"/> returned by this particular query,
/// or null if no usable status was returned. It is not the potentially older
/// cached status from the Host snapshot.
/// </param>
/// <param name="Status">
/// Host snapshot taken after the query result was processed. Includes the
/// updated connectivity bookkeeping, cached lamp status, and frame counters.
/// </param>
public sealed record BridgeIpcLampStatusResponse(
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.Lamp)] GoveeDeviceStatus? Lamp,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.Status)] BridgeHostStatusSnapshot Status
) : BridgeIpcResponse;

/// <summary>
/// Reply for an unsupported, invalid, or temporarily unavailable Host command.
/// </summary>
/// <remarks>
/// A response represents command-processing failure, not proof of a physical
/// device failure. The optional status snapshot is omitted from JSON when null
/// to preserve the existing error response shape.
/// </remarks>
/// <param name="Error">
/// Human-readable description of the problem, such as an unknown command or
/// unavailable physical status polling. Clients should not treat this free-form
/// message as a stable machine-readable error code.
/// </param>
/// <param name="Status">
/// Optional cached Host snapshot with additional diagnostic context, provided
/// for example when devStatus polling is suspended during Razer streaming.
/// Null means no snapshot was attached, and the JSON field is omitted.
/// </param>
public sealed record BridgeIpcErrorResponse(
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.Error)] string Error,
   [property: JsonPropertyName(BridgeIpcResponsePropertyNames.Status)]
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   BridgeHostStatusSnapshot? Status = null
) : BridgeIpcResponse;
