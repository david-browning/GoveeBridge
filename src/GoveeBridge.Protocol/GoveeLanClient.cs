using System.Net;
using System.Net.Sockets;
using System.Text;
using GoveeBridge.Core;

namespace GoveeBridge.Protocol;

/// <summary>
/// Owns the fixed Govee LAN response socket for one lamp. The Host is responsible
/// for arbitrating access and for deciding whether a command may be issued.
/// </summary>
/// <remarks>
/// Sends are UDP best effort: completing a send does not prove the lamp applied it.
/// This client cannot distinguish physical-remote actions from other LAN clients.
/// </remarks>
public sealed class GoveeLanClient : IGoveeRealtimeClient
{
   public GoveeLanClient(string ip, int timeoutMs)
   {
      if (!IPAddress.TryParse(ip, out IPAddress? address) ||
         address.AddressFamily != AddressFamily.InterNetwork)
      {
         throw new ArgumentException("A Govee lamp requires an IPv4 address.", nameof(ip));
      }

      _lamp = new IPEndPoint(address, GoveeLanPorts.Command);
      _timeoutMs = timeoutMs;

      // This must be the only owner of the local fixed reply port.
      _udp = new UdpClient(new IPEndPoint(IPAddress.Any, GoveeLanPorts.Response));
   }

   public Task SetRealtimeEnabledAsync(bool enabled, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.RazerEnabled(enabled), ct);

   public Task SendRealtimeFrameAsync(RgbFrame frame, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.RazerFrame(frame, _gradient), ct);

   public async Task<GoveeDeviceStatus?> GetStatusAsync(CancellationToken ct)
   {
      await _gate.WaitAsync(ct);
      try
      {
         using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
         timeout.CancelAfter(_timeoutMs);
         await SendUnsafeAsync(GoveeLanPackets.StatusRequest(), timeout.Token);

         while (true)
         {
            UdpReceiveResult response = await _udp.ReceiveAsync(timeout.Token);
            if (!response.RemoteEndPoint.Address.Equals(_lamp.Address))
            {
               // Ignore packets that did not come from the lamp that this class
               // instance is monitoring.
               continue;
            }

            if (GoveeLanPackets.TryReadStatus(
               response.Buffer, out GoveeDeviceStatus? status))
            {
               return status;
            }
         }
      }
      catch (OperationCanceledException) when (!ct.IsCancellationRequested)
      {
         return null;
      }
      finally
      {
         _gate.Release();
      }

   }

   public Task SetPowerAsync(bool on, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.Power(on), ct);

   public Task SetBrightnessAsync(int percentage, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.Brightness(percentage), ct);

   public Task SetColorAsync(RgbColor color, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.Color(color), ct);

   public Task SetColorTemperatureAsync(int kelvin, CancellationToken ct) =>
      SendAsync(GoveeLanPackets.ColorTemperature(kelvin), ct);

   private async Task SendAsync(string json, CancellationToken ct)
   {
      await _gate.WaitAsync(ct);
      try
      {
         await SendUnsafeAsync(json, ct);
      }
      finally
      {
         _gate.Release();
      }
   }

   private async Task SendUnsafeAsync(string json, CancellationToken ct)
   {
      byte[] bytes = Encoding.UTF8.GetBytes(json);
      await _udp.SendAsync(bytes.AsMemory(), _lamp, ct);
   }

   public void Dispose()
   {
      _udp.Dispose();
      _gate.Dispose();
   }

   private const bool _gradient = true;

   private readonly UdpClient _udp;
   private readonly IPEndPoint _lamp;
   private readonly SemaphoreSlim _gate = new(1, 1);
   private readonly int _timeoutMs;
}
