using System.Net;
using System.Net.Sockets;
using GoveeBridge.Core;

namespace GoveeBridge.Protocol;

/// <summary>
/// A device-scoped Govee LAN client using the Host's shared UDP 4002 listener.
/// Each lamp has a separate I/O gate; the network's receive socket is shared.
/// </summary>
public sealed class GoveeLanClient : IGoveeRealtimeClient
{
   /// <summary>
   /// Creates one client using a shared physical LAN network and a current IPv4 address.
   /// </summary>
   public GoveeLanClient(GoveeLanNetwork network, string ip, int timeoutMs)
   {
      _network = network ?? throw new ArgumentNullException(nameof(network));
      if (!IPAddress.TryParse(ip, out IPAddress? address) ||
         address.AddressFamily != AddressFamily.InterNetwork)
      {
         throw new ArgumentException("A Govee lamp requires an IPv4 address.", nameof(ip));
      }
      _lamp = new IPEndPoint(address, GoveeLanPorts.Command);
      _timeoutMs = timeoutMs;
   }

   /// <summary>
   /// Submits an experimental real-time ARM or DISARM packet.
   /// </summary>
   public Task SetRealtimeEnabledAsync(bool enabled, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.RazerEnabled(enabled), ct);

   /// <summary>
   /// Submits a complete multizone Razer frame; no physical receipt is implied.
   /// </summary>
   public Task SendRealtimeFrameAsync(RgbFrame frame, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.RazerFrame(frame, _gradient), ct);

   /// <summary>
   /// Queries fresh status and accepts only a reply from this lamp's IPv4 address.
   /// </summary>
   public async Task<GoveeDeviceStatus?> GetStatusAsync(CancellationToken ct)
   {
      await _gate.WaitAsync(ct);
      try
      {
         return await _network.QueryStatusAsync(_lamp, _timeoutMs, ct);
      }
      finally
      {
         _gate.Release();
      }
   }

   /// <summary>
   /// Submits a power command.
   /// </summary>
   public Task SetPowerAsync(bool on, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.Power(on), ct);

   /// <summary>
   /// Submits a global brightness command with physical range validation.
   /// </summary>
   public Task SetBrightnessAsync(int percentage, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.Brightness(percentage), ct);

   /// <summary>
   /// Submits a solid RGB color command.
   /// </summary>
   public Task SetColorAsync(RgbColor color, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.Color(color), ct);

   /// <summary>
   /// Submits a white-temperature command using the device LAN codec.
   /// </summary>
   public Task SetColorTemperatureAsync(int kelvin, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.ColorTemperature(kelvin), ct);

   /// <summary>
   /// Serializes physical sends for this lamp, independently of other lamps.
   /// </summary>
   private async Task SendAsync(string json, CancellationToken ct)
   {
      await _gate.WaitAsync(ct);
      try
      {
         await _network.SendAsync(_lamp, json, ct);
      }
      finally
      {
         _gate.Release();
      }
   }

   /// <summary>
   /// Releases only this lamp's gate, not the Host-owned shared network.
   /// </summary>
   public void Dispose() => _gate.Dispose();

   /// <summary>
   /// Shared Host network and UDP listener.
   /// </summary>
   private readonly GoveeLanNetwork _network;

   /// <summary>
   /// Current physical destination endpoint.
   /// </summary>
   private readonly IPEndPoint _lamp;

   /// <summary>
   /// Device-scoped command serialization.
   /// </summary>
   private readonly SemaphoreSlim _gate = new(1, 1);

   /// <summary>
   /// Fresh physical status query timeout.
   /// </summary>
   private readonly int _timeoutMs;

   /// <summary>
   /// Legacy packet encoder gradient flag.
   /// </summary>
   private const bool _gradient = true;
}
