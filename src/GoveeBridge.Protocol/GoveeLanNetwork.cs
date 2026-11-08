using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GoveeBridge.Core;

namespace GoveeBridge.Protocol;

/// <summary>
/// Owns the one fixed Govee LAN response socket (UDP 4002) for an entire Host.
/// Incoming status datagrams are correlated by source IPv4 address; discovery
/// and device identity reconciliation can be added here later.
/// </summary>
/// <remarks>
/// Govee status replies do not contain reliable request IDs. Consequently,
/// one query per lamp IP is allowed at a time and late responses from an old
/// query can still be mistaken for a newer one. An IP must identify at most
/// one configured lamp. A UDP send is not a device acknowledgment.
/// </remarks>
public sealed class GoveeLanNetwork : IDisposable
{
   /// <summary>
   /// Opens the response port once and starts the shared receiver.
   /// </summary>
   public GoveeLanNetwork()
   {
      _udp = new UdpClient(new IPEndPoint(IPAddress.Any, GoveeLanPorts.Response));
      if (OperatingSystem.IsWindows())
      {
         // Windows SDK mswsock.h: SIO_UDP_CONNRESET. A dead destination must
         // not terminate the only receiver shared by all lamps.
         const int SioUdpConnReset = unchecked((int)0x9800000C);
         _udp.Client.IOControl(SioUdpConnReset, new byte[sizeof(int)], null);
      }
      _receiveTask = ReceiveLoopAsync(_stop.Token);
   }

   /// <summary>
   /// Sends one physical Govee JSON datagram to an identified lamp.
   /// </summary>
   public async Task SendAsync(IPEndPoint endpoint, string json, CancellationToken ct)
   {
      byte[] bytes = Encoding.UTF8.GetBytes(json);
      await _udp.SendAsync(bytes.AsMemory(), endpoint, ct);
   }

   /// <summary>
   /// Sends a status request and waits for a status packet from precisely this
   /// address. Queries to other lamps may proceed concurrently.
   /// </summary>
   public async Task<GoveeDeviceStatus?> QueryStatusAsync(
      IPEndPoint endpoint, int timeoutMs, CancellationToken ct)
   {
      var pending = new TaskCompletionSource<GoveeDeviceStatus>(
         TaskCreationOptions.RunContinuationsAsynchronously);
      if (!_pending.TryAdd(endpoint.Address, pending))
      {
         throw new InvalidOperationException(
            $"A status request for {endpoint.Address} is already pending.");
      }

      try
      {
         using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
         timeout.CancelAfter(timeoutMs);
         try
         {
            await SendAsync(endpoint, GoveeLanPackets.StatusRequest(), timeout.Token);
            return await pending.Task.WaitAsync(timeout.Token);
         }
         catch (OperationCanceledException) when (!ct.IsCancellationRequested &&
            !_stop.IsCancellationRequested)
         {
            return null;
         }
      }
      finally
      {
         _pending.TryRemove(endpoint.Address, out _);
      }
   }

   /// <summary>
   /// Dispatches status replies by source IP; a packet from one lamp must not
   /// satisfy another lamp's pending query.
   /// </summary>
   private async Task ReceiveLoopAsync(CancellationToken ct)
   {
      try
      {
         while (!ct.IsCancellationRequested)
         {
            UdpReceiveResult result = await _udp.ReceiveAsync(ct);
            if (GoveeLanPackets.TryReadStatus(result.Buffer, out GoveeDeviceStatus? status) &&
               status is not null &&
               _pending.TryGetValue(result.RemoteEndPoint.Address, out var pending))
            {
               pending.TrySetResult(status);
            }
            else
            {
               // TODO: Rate-limited log of rejected packets.
            }
         }
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
      catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
      catch (SocketException) when (ct.IsCancellationRequested) { }
      // Unexpected receiver failures must fail outstanding queries rather than
      // leaving clients waiting until the next operation discovers the problem.
      catch (Exception ex)
      {
         foreach (var query in _pending.Values)
         {
            query.TrySetException(ex);
         }
         throw;
      }
   }

   /// <summary>
   /// Stops the shared listener after its individual lamp clients have stopped.
   /// </summary>
   public void Dispose()
   {
      _stop.Cancel();
      _udp.Dispose();
      try
      {
         _receiveTask.GetAwaiter().GetResult();
      }
      catch (OperationCanceledException) { }
      _stop.Dispose();
   }

   /// <summary>
   /// One response port shared by every physical lamp.
   /// </summary>
   private readonly UdpClient _udp;

   /// <summary>
   /// Pending status queries keyed by IPv4 address.
   /// </summary>
   private readonly ConcurrentDictionary<IPAddress, TaskCompletionSource<GoveeDeviceStatus>>
      _pending = new();

   /// <summary>
   /// Stops the continuous receive task at Host shutdown.
   /// </summary>
   private readonly CancellationTokenSource _stop = new();

   /// <summary>
   /// Single task that demultiplexes responses from all lamps.
   /// </summary>
   private readonly Task _receiveTask;
}
