using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;

namespace GoveeBridge.Host;

/// <summary>
/// Hosts loopback-only UDP IPC, a bounded management-command queue, independent
/// status polling, and a latest-frame-wins output pump. Does not own lamp policy.
/// </summary>
/// <remarks>
/// Loopback restricts network exposure but DOES NOT authenticate local processes.
/// Do not grant this endpoint privileged controls without future IPC hardening.
/// </remarks>
public sealed class BridgeWorker : BackgroundService
{
   // DI supplies one Runtime shared by these concurrent loops. The Worker
   // owns IPC/timing; the Runtime owns mutable device-control state.
   private readonly BridgeSettings _settings;
   private readonly BridgeHostOptions _options;
   private readonly BridgeRuntime _runtime;
   private readonly ILogger<BridgeWorker> _log;

   public BridgeWorker(
      BridgeSettings settings,
      BridgeHostOptions options,
      BridgeRuntime runtime,
      ILogger<BridgeWorker> log)
   {
      _settings = settings;
      _options = options;
      _runtime = runtime;
      _log = log;
   }

   // As a BackgroundService, ExecuteAsync is started by .NET's generic Host.
   // It owns the loopback socket and the lifetimes of its helper tasks.
   protected override async Task ExecuteAsync(CancellationToken stoppingToken)
   {
      // This is local IPC, not the physical Govee LAN socket or its reply port.
      using UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, _settings.FrameListenPort));

      // On Windows, ICMP Port Unreachable in response to a late UDP reply can
      // surface as SocketError.ConnectionReset on the next ReceiveAsync.
      // A client that times out must not kill this long-lived IPC listener.
      if (OperatingSystem.IsWindows())
      {
         // Windows SDK MSWSock.h: SIO_UDP_CONNRESET = _WSAIOW(IOC_VENDOR, 12).
         // https://learn.microsoft.com/en-us/windows/win32/winsock/winsock-ioctls
         const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
         udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[sizeof(int)], null);
      }

      // If ExecuteAsync exits unexpectedly, stop all sibling tasks as well.
      using var workerLifetime =
         CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
      var ct = workerLifetime.Token;

      // The channel contains management datagrams only. RGB frames go directly
      // to Runtime's latest-frame-wins mailbox and never back up this queue.
      var commands = Channel.CreateBounded<UdpReceiveResult>(
         new BoundedChannelOptions(BridgeHostDefaults.CommandQueueCapacity)
         {
            SingleReader = true,
            SingleWriter = true,
            // TryWrite rejects when full; it does not wait for capacity.
            FullMode = BoundedChannelFullMode.Wait
         });

      _log.LogInformation("Loopback UDP IPC listening at 127.0.0.1:{Port}; local clients are not authenticated",
         _settings.FrameListenPort);

      Task monitor = MonitorLampAsync(ct);
      Task pump = PumpFramesAsync(ct);
      Task commandProcessor = ProcessCommandsAsync(udp, commands.Reader, ct);

      try
      {
         // Keep this path short. It never waits for the physical lamp.
         while (!ct.IsCancellationRequested)
         {
            UdpReceiveResult packet = await udp.ReceiveAsync(ct);
            if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address))
            {
               continue;
            }

            if (packet.Buffer.Length > BridgeHostDefaults.MaximumIpcDatagramBytes)
            {
               _log.LogWarning("Rejected oversized local IPC datagram ({Size} bytes)",
                  packet.Buffer.Length);
               continue;
            }

            if (GoveeHelpers.TryParseFrame(
               packet.Buffer, _settings.LogicalLedCount, out RgbFrame? frame))
            {
               _runtime.ReceiveFrame(frame!);
               continue;
            }

            // Every non-frame packet goes to the management consumer, including
            // malformed input, which receives an error rather than being ignored.
            if (!commands.Writer.TryWrite(packet))
            {
               await SendReplyAsync(udp, packet.RemoteEndPoint,
                  new BridgeIpcErrorResponse("Command queue full"), ct);
            }
         }
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
         // Normal Host shutdown.
      }
      finally
      {
         workerLifetime.Cancel();
         commands.Writer.TryComplete();
         try
         {
            await Task.WhenAll(monitor, pump, commandProcessor);
         }
         catch (OperationCanceledException) when (ct.IsCancellationRequested)
         {
            // Normal Host shutdown.
         }
         finally
         {
            // Stop frame output and perform a bounded best-effort disarm.
            await _runtime.ShutdownAsync();
         }
      }
   }

   /// <summary>
   /// Validates the incoming JSON command envelope, then passes its action and
   /// optional data to Runtime. The Runtime does not need the wire envelope.
   /// </summary>
   private async Task ProcessCommandsAsync(
      UdpClient udp,
      ChannelReader<UdpReceiveResult> reader,
      CancellationToken ct)
   {
      // One consumer preserves command-queue order. _lampGate separately
      // serializes lamp I/O with polling and the frame pump.
      await foreach (UdpReceiveResult packet in reader.ReadAllAsync(ct))
      {
         try
         {
            using JsonDocument document = JsonDocument.Parse(packet.Buffer);
            JsonElement root = document.RootElement;

            // The Worker owns envelope validation. A command's 'data' property
            // remains optional: resume, release, and status omit it entirely.
            if (root.ValueKind != JsonValueKind.Object ||
               !root.TryGetProperty(BridgeIpcPropertyNames.Type, out JsonElement type) ||
               type.ValueKind != JsonValueKind.String ||
               type.GetString() != BridgeIpcMessageTypes.Command ||
               !root.TryGetProperty(BridgeIpcPropertyNames.Action, out JsonElement action) ||
               action.ValueKind != JsonValueKind.String ||
               string.IsNullOrWhiteSpace(action.GetString()))
            {
               await SendReplyAsync(udp, packet.RemoteEndPoint,
                  new BridgeIpcErrorResponse("Invalid command packet"), ct);
               continue;
            }

            JsonElement? data = root.TryGetProperty(
               BridgeIpcPropertyNames.Data, out JsonElement value)
               ? value : null;

            // JsonDocument stays alive across this await, so the optional
            // JsonElement is valid until Runtime finishes consuming it.
            BridgeIpcResponse reply = await _runtime.ExecuteCommandAsync(
               action.GetString()!, data, ct);
            await SendReplyAsync(udp, packet.RemoteEndPoint, reply, ct);
         }
         catch (OperationCanceledException) when (ct.IsCancellationRequested)
         {
            break;
         }
         catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
         {
            _log.LogWarning(ex, "Rejected malformed local command");
            await SendReplyAsync(udp, packet.RemoteEndPoint,
               new BridgeIpcErrorResponse("Malformed command"), ct);
         }
         catch (Exception ex) when (ex is SocketException or IOException)
         {
            _log.LogWarning(ex, "Failed to execute local command");
         }
      }
   }

   private static async Task SendReplyAsync(
      UdpClient udp, IPEndPoint endpoint, object payload, CancellationToken ct)
   {
      // The client may close its ephemeral port before this response arrives.
      // UDP send success does not establish that the client received it.
      byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
      await udp.SendAsync(bytes.AsMemory(), endpoint, ct);
   }

   /// <summary>Poll immediately, then at the configured cadence.</summary>
   private async Task MonitorLampAsync(CancellationToken ct)
   {
      await TryPollAsync(ct);
      using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_settings.RemotePollIntervalMs));
      while (await timer.WaitForNextTickAsync(ct))
      {
         await TryPollAsync(ct);
      }
   }

   private async Task TryPollAsync(CancellationToken ct)
   {
      try
      {
         await _runtime.PollLampAsync(ct);
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
      catch (Exception ex) when (ex is SocketException or IOException)
      {
         _log.LogWarning(ex, "Lamp status poll failed; will retry");
      }
   }

   /// <summary>
   /// Samples the latest frame on a fixed cadence without queuing obsolete
   /// animation frames. Output mode, ownership, and deduplication remain Runtime policy.
   /// </summary>
   private async Task PumpFramesAsync(CancellationToken ct)
   {
      using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_options.FramePumpIntervalMs));
      while (await timer.WaitForNextTickAsync(ct))
      {
         try
         {
            await _runtime.PumpFrameAsync(ct);
         }
         catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
         catch (Exception ex) when (ex is SocketException or IOException)
         {
            _log.LogWarning(ex, "Lamp output failed; next frame will be retried");
         }
      }
   }
}
