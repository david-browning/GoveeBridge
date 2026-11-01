using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GoveeBridge.Core;

namespace GoveeBridge.Host;

/// <summary>
/// Loopback-only IPC receiver. Commands, monitoring, and device output run on
/// independent loops; the receive loop never waits for physical lamp I/O.
/// </summary>
public sealed class BridgeWorker : BackgroundService
{
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

   protected override async Task ExecuteAsync(CancellationToken stoppingToken)
   {
      using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, _settings.FrameListenPort));
      _log.LogInformation("Bridge IPC listening on 127.0.0.1:{Port}", _settings.FrameListenPort);

      var commands = Channel.CreateBounded<UdpReceiveResult>(new BoundedChannelOptions(64)
      {
         SingleReader = true,
         SingleWriter = true,
         FullMode = BoundedChannelFullMode.Wait
      });

      Task monitor = MonitorLampAsync(stoppingToken);
      Task pump = PumpFramesAsync(stoppingToken);
      Task commandProcessor = ProcessCommandsAsync(udp, commands.Reader, stoppingToken);
      try
      {
         while (!stoppingToken.IsCancellationRequested)
         {
            UdpReceiveResult packet = await udp.ReceiveAsync(stoppingToken);
            if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address)) continue;

            if (BridgeWireProtocol.TryParseFrame(packet.Buffer, _settings.LogicalLedCount, out RgbFrame? frame))
            {
               _runtime.ReceiveFrame(frame!);
               continue;
            }

            // Do not wait for lamp commands or status queries on the frame path.
            // A saturated bounded queue rejects new commands, not RGB frames.
            if (!commands.Writer.TryWrite(packet))
            {
               byte[] busy = Encoding.UTF8.GetBytes("{\"error\":\"Command queue full\"}");
               await udp.SendAsync(busy.AsMemory(), packet.RemoteEndPoint, stoppingToken);
            }
         }
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
      finally
      {
         commands.Writer.TryComplete();
         try { await Task.WhenAll(monitor, pump, commandProcessor); }
         catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
      }
   }

   private async Task ProcessCommandsAsync(
       UdpClient udp,
       ChannelReader<UdpReceiveResult> reader,
       CancellationToken ct)
   {
      await foreach (UdpReceiveResult packet in reader.ReadAllAsync(ct))
      {
         try
         {
            using JsonDocument doc = JsonDocument.Parse(packet.Buffer);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out JsonElement type) || type.GetString() != "command") continue;
            if (!root.TryGetProperty("action", out JsonElement action) || action.ValueKind != JsonValueKind.String) continue;

            object result = await _runtime.ExecuteCommandAsync(action.GetString()!, ct);
            byte[] reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result));
            await udp.SendAsync(reply.AsMemory(), packet.RemoteEndPoint, ct);
         }
         catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
         {
            _log.LogWarning(ex, "Rejected malformed command datagram");
         }
         catch (Exception ex) when (ex is SocketException or IOException)
         {
            _log.LogWarning(ex, "Command processing failed; accepting subsequent commands");
         }
      }
   }

   private async Task MonitorLampAsync(CancellationToken ct)
   {
      // First query immediately instead of waiting a full polling interval.
      await TryPollAsync(ct);
      using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settings.RemotePollIntervalMs));
      while (await timer.WaitForNextTickAsync(ct))
         await TryPollAsync(ct);
   }

   private async Task TryPollAsync(CancellationToken ct)
   {
      try { await _runtime.PollLampAsync(ct); }
      catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
      catch (Exception ex) when (ex is SocketException or IOException)
      {
         _log.LogWarning(ex, "Lamp status poll failed; retrying");
      }
   }

   private async Task PumpFramesAsync(CancellationToken ct)
   {
      using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.FramePumpIntervalMs));
      while (await timer.WaitForNextTickAsync(ct))
      {
         try { await _runtime.PumpFrameAsync(ct); }
         catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
         catch (Exception ex) when (ex is SocketException or IOException)
         {
            _log.LogWarning(ex, "Lamp frame send failed; retrying");
         }
      }
   }
}
