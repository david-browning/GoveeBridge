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
   // It is the owner of the loopback socket and the lifetimes of its helper
   // tasks; Runtime does not create its own worker thread.
   protected override async Task ExecuteAsync(CancellationToken stoppingToken)
   {
      // Binding the LOCAL endpoint to IPAddress.Loopback makes this a local
      // application IPC port, not a Govee LAN socket. It is distinct from
      // Govee's fixed physical reply port 4002, owned by GoveeLanClient.
      using UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, _settings.FrameListenPort));
      // A linked token lets us stop *all* helper loops whenever either the
      // Host requests shutdown or ExecuteAsync exits unexpectedly.
      using var workerLifetime =
         CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
      var ct = workerLifetime.Token;

      // The command channel is a bounded FIFO of *management datagrams*.
      // RGB frames are NEVER queued here; they are coalesced by Runtime.
      // SingleReader/SingleWriter match our one receive loop + one consumer.
      var commands = Channel.CreateBounded<UdpReceiveResult>(
         new BoundedChannelOptions(BridgeHostDefaults.CommandQueueCapacity)
         {
            SingleReader = true,
            SingleWriter = true,
            // We actually call TryWrite, not WriteAsync: on saturation the
            // packet is rejected immediately rather than waiting for room.
            // This enum does not cause a wait when using TryWrite.
            FullMode = BoundedChannelFullMode.Wait
         });

      _log.LogInformation("Loopback UDP IPC listening at 127.0.0.1:{Port}; local clients are not authenticated",
         _settings.FrameListenPort);

      // Starting an async method produces a Task. Awaiting timers/UDP does
      // not permanently occupy a separate OS thread for each operation.
      // These run concurrently: observation, output, and management commands.
      Task monitor = MonitorLampAsync(ct);
      Task pump = PumpFramesAsync(ct);
      Task commandProcessor = ProcessCommandsAsync(udp, commands.Reader, ct);

      try
      {
         // Fast path: receive one loopback UDP datagram, classify it, and
         // immediately return to listening. Never await physical lamp I/O here.
         while (!ct.IsCancellationRequested)
         {
            UdpReceiveResult packet = await udp.ReceiveAsync(ct);
            if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address))
            {
               continue;
            }

            // Limit parsing/allocation work from local processes. Loopback
            // prevents LAN access but does NOT authenticate the local caller.
            if (packet.Buffer.Length > BridgeHostDefaults.MaximumIpcDatagramBytes)
            {
               _log.LogWarning("Rejected oversized local IPC datagram ({Size} bytes)",
                  packet.Buffer.Length);
               continue;
            }

            // RGB frames are the high-volume traffic. Parsing validates shape
            // and LED count; ReceiveFrame just publishes the newest snapshot.
            if (GoveeHelpers.TryParseFrame(
               packet.Buffer, _settings.LogicalLedCount, out RgbFrame? frame))
            {
               _runtime.ReceiveFrame(frame!);
               continue;
            }

            // Physical lamp I/O must never block the frame receive path.
            // Every non-frame packet is *offered* to the command parser,
            // including malformed datagrams. The consumer returns an error.
            // Never let a slow lamp-status query stall rendering inputs.
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
      // On service shutdown OR unexpected exit, cancel the sibling loops,
      // complete the command queue, wait for cleanup, then ask Runtime to
      // release device ownership and make a bounded disarm attempt.
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
            // Use Runtime's independent shutdown token: ct is now canceled.
            // This intentionally leaves the lamp powered as it was.
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
      // Exactly one consumer means management actions are *dequeued* in order.
      // Device work itself is further serialized by Runtime's _lampGate.
      // Note that arrival-order fairness between pump/poll/commands is NOT
      // guaranteed by a semaphore.
      await foreach (UdpReceiveResult packet in reader.ReadAllAsync(ct))
      {
         try
         {
            // Commands currently use {"type":"command","action":"resume"}
            // (or release/status/lamp-status). This IPC schema is distinct
            // from the JSON packets sent over LAN to the physical device.
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

            // Potentially slow: release may disarm Razer, lamp-status may poll
            // the device. Run here, not in the fast UDP receive loop.
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
      // Send the response to the UDP source endpoint, not to the lamp.
      // UDP replies are also best-effort: an IPC caller needs its own timeout.
      byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
      await udp.SendAsync(bytes.AsMemory(), endpoint, ct);
   }

   /// <summary>Monitor immediately, then poll at the configured frequency.</summary>
   private async Task MonitorLampAsync(CancellationToken ct)
   {
      // Query once at startup so Runtime has a baseline observation before
      // the first PC takeover, then use a timer for subsequent observations.
      await TryPollAsync(ct);
      // PeriodicTimer does not build a growing backlog of missed intervals.
      // Each query completes (or times out) before the next loop iteration.
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
         // Runtime decides whether monitoring is enabled and whether Razer
         // mode makes polling unreliable. Worker merely supplies the cadence.
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
      // Pump frequency is only a chance to output. Runtime applies separate
      // static and Razer minimum send intervals and deduplicates frames.
      using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_options.FramePumpIntervalMs));
      while (await timer.WaitForNextTickAsync(ct))
      {
         try
         {
            // No per-frame queue: this reads whatever latest frame Runtime
            // currently holds, so the physical lamp never plays stale backlog.
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
