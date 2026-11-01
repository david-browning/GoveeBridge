using GoveeBridge.Core;

namespace GoveeBridge.Host;

/// <summary>Host event contract for a future per-client named-pipe broadcaster.</summary>
public enum BridgeEventKind
{
   LampStatusChanged,
   LampOnline,
   LampOffline,
   ControlOwnershipChanged
}

public sealed record BridgeEvent(
    BridgeEventKind Kind,
    DateTimeOffset Timestamp,
    GoveeDeviceStatus? LampStatus,
    string? Details);

/// <summary>
/// Publish must return promptly and never perform synchronous network I/O.
/// Future implementation can enqueue events into bounded per-subscriber channels.
/// </summary>
public interface IBridgeEventPublisher
{
   void Publish(BridgeEvent bridgeEvent);
}

/// <summary>No subscribers yet. Lamp changes are still written through ILogger.</summary>
public sealed class NullBridgeEventPublisher : IBridgeEventPublisher
{
   public void Publish(BridgeEvent bridgeEvent) { }
}
