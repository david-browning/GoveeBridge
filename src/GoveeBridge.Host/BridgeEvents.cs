using GoveeBridge.Core;

namespace GoveeBridge.Host;

/// <summary>
/// Kinds of per-device observations and Host control-policy changes.
/// </summary>
public enum BridgeEventKind
{
   LampStatusChanged,
   LampOnline,
   LampOffline,
   ControlOwnershipChanged
}

/// <summary>
/// An event attached to a stable lampId. Legacy subscribers can continue to
/// inspect Kind, Timestamp, LampStatus and Details without knowing LampId.
/// </summary>
public sealed record BridgeEvent(
   BridgeEventKind Kind,
   DateTimeOffset Timestamp,
   GoveeDeviceStatus? LampStatus,
   string? Details,
   string LampId);

/// <summary>
/// Publishes events promptly without synchronous network I/O. A future pipe
/// broadcaster can route events by LampId to selected subscribers.
/// </summary>
public interface IBridgeEventPublisher
{
   /// <summary>
   /// Enqueues or delivers a per-device event without blocking lamp control.
   /// </summary>
   void Publish(BridgeEvent bridgeEvent);
}

/// <summary>
/// No-op publisher for deployments that have not enabled event subscribers.
/// </summary>
public sealed class NullBridgeEventPublisher : IBridgeEventPublisher
{
   /// <summary>
   /// Deliberately ignores published events; Host logs remain independent.
   /// </summary>
   public void Publish(BridgeEvent bridgeEvent) { }
}
