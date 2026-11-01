using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GoveeBridge.Core;

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

   protected override async Task ExecuteAsync(CancellationToken stoppingToken)
   {
      using UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, _settings.FrameListenPort));
      using CancellationTokenSource workerLifetime =
         CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
      var ct = workerLifetime.Token;

      var commands = Channel.CreateBounded<UdpReceiveResult>(
         new BoundedChannelOptions(BridgeHostDefaults.CommandQueueCapacity)
         {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
         });

      _log.LogInformation(
         "Loopback UDP IPC listening at 127.0.0.1:{Port}; local clients are not authenticated",
         _settings.FrameListenPort);

      Task monitor = MonitorLampAsync(ct);
      Task pump = PumpFramesAsync(ct);
      Task commandProcessor = ProcessCommandsAsync(udp, commands.Reader, ct);

      try
      {
         while (!ct.IsCancellationRequested)
         {
            var packet = await udp.ReceiveAsync(ct);
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

            // Physical lamp I/O must never block the frame receive path.
            if (!commands.Writer.TryWrite(packet))
            {
               await SendReplyAsync(udp, packet.RemoteEndPoint,
                  new { error = "Command queue full" }, ct);
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
            // Expected when stopping the worker.
         }
         finally
         {
            // Attempt disarm even when a background task faults unexpectedly.
            await _runtime.ShutdownAsync();
         }
      }
   }

   /// <summary>
   /// Parses small command datagrams and returns a JSON status or error response.
   /// The only supported management actions are resume, release, status,
   /// and lamp-status; frames are consumed on the receive loop instead.
   /// </summary>
   private async Task ProcessCommandsAsync(
      UdpClient udp,
      ChannelReader<UdpReceiveResult> reader,
      CancellationToken ct)
   {
      await foreach (UdpReceiveResult packet in reader.ReadAllAsync(ct))
      {
         try
         {
            using JsonDocument document = JsonDocument.Parse(packet.Buffer);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("type", out JsonElement type) ||
               type.ValueKind != JsonValueKind.String ||
               type.GetString() != "command" ||
               !root.TryGetProperty("action", out JsonElement action) ||
               action.ValueKind != JsonValueKind.String ||
               string.IsNullOrWhiteSpace(action.GetString()))
            {
               await SendReplyAsync(udp, packet.RemoteEndPoint,
                  new { error = "Invalid command packet" }, ct);
               continue;
            }

            object reply = await _runtime.ExecuteCommandAsync(action.GetString()!, ct);
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
               new { error = "Malformed command" }, ct);
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
      byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
      await udp.SendAsync(bytes.AsMemory(), endpoint, ct);
   }

   /// <summary>Monitor immediately, then poll at the configured frequency.</summary>
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
   /// Drives physical output independently from the client-facing UDP receive loop.
   /// Frames are coalesced in BridgeRuntime and are not queued individually.
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

   private readonly BridgeSettings _settings;
   private readonly BridgeHostOptions _options;
   private readonly BridgeRuntime _runtime;
   private readonly ILogger<BridgeWorker> _log;
}
