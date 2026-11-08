using System.Net;
using System.Net.Sockets;

namespace GoveeBridge.Core;

/// <summary>
/// One manually configured lamp. Id is the stable IPC selector; Ip is only its
/// current network address and may change after DHCP or future discovery.
/// </summary>
public sealed class BridgeLampSettings
{
   /// <summary>
   /// Stable, unique operator-assigned name used in IPC requests and logs.
   /// </summary>
   public string Id { get; set; } = "";

   /// <summary>
   /// Optional vendor device identifier for a future discovery registry.
   /// Do not assume a MAC address alone proves device capabilities.
   /// </summary>
   public string? DeviceId { get; set; }

   /// <summary>
   /// Current IPv4 address used for Govee LAN packets.
   /// </summary>
   public string Ip { get; set; } = "";

   /// <summary>
   /// Explicitly configured feature support for this device.
   /// </summary>
   public LampCapabilities Capabilities { get; set; } = new();

   /// <summary>
   /// Validates the stable selector, IP address, and capability profile.
   /// </summary>
   public void Validate(bool diagnostic)
   {
      if (string.IsNullOrWhiteSpace(Id) || Id != Id.Trim() || Id.Length > 64)
      {
         throw new ArgumentException("Lamp Id must be 1-64 non-whitespace characters without surrounding whitespace.", nameof(Id));
      }

      if (!diagnostic && (!IPAddress.TryParse(Ip, out IPAddress? address) ||
         address.AddressFamily != AddressFamily.InterNetwork))
      {
         throw new ArgumentException($"Lamp '{Id}' requires an IPv4 address.", nameof(Ip));
      }

      (Capabilities ?? throw new ArgumentException($"Lamp '{Id}' has no capability profile.")).Validate();
   }
}
