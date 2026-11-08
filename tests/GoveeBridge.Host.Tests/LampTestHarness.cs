using System.Text.Json;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

/// <summary>
/// A transport-free replacement for a physical Govee client. Records attempted
/// operations; it intentionally does NOT assume that a UDP write changes devStatus.
/// Runtime makes all calls for one lamp under its own gate.
/// </summary>
internal sealed class RecordingLamp : IGoveeRealtimeClient
{
   public List<string> Calls { get; } = [];
   public List<RgbColor> Colors { get; } = [];
   public List<RgbFrame> Frames { get; } = [];
   public Queue<GoveeDeviceStatus?> StatusReplies { get; } = new();
   public GoveeDeviceStatus? Status { get; set; } =
      new(true, 50, new RgbColor(0, 0, 0), 0);
   public bool FailArm { get; set; }
   public bool FailDisarm { get; set; }
   public bool FailColor { get; set; }
   public bool Disposed { get; private set; }

   public int Count(string exact) => Calls.Count(x => x == exact);
   public int CountPrefix(string prefix) => Calls.Count(x => x.StartsWith(prefix, StringComparison.Ordinal));

   public Task<GoveeDeviceStatus?> GetStatusAsync(CancellationToken ct)
   {
      ct.ThrowIfCancellationRequested();
      Calls.Add("status");
      return Task.FromResult(StatusReplies.Count > 0 ? StatusReplies.Dequeue() : Status);
   }

   public Task SetPowerAsync(bool on, CancellationToken ct)
   {
      ct.ThrowIfCancellationRequested();
      Calls.Add($"power:{on}");
      return Task.CompletedTask;
   }

   public Task SetColorAsync(RgbColor color, CancellationToken ct)
   {
      ct.ThrowIfCancellationRequested();
      if (FailColor) throw new IOException("Simulated static-color transport failure");
      Calls.Add($"color:{color}");
      Colors.Add(color);
      return Task.CompletedTask;
   }

   public Task SetRealtimeEnabledAsync(bool enabled, CancellationToken ct)
   {
      ct.ThrowIfCancellationRequested();
      if ((enabled && FailArm) || (!enabled && FailDisarm))
         throw new IOException("Simulated Razer ARM/DISARM transport failure");
      Calls.Add($"arm:{enabled}");
      return Task.CompletedTask;
   }

   public Task SetBrightnessAsync(int percentage, CancellationToken ct)
   {
      ct.ThrowIfCancellationRequested();

      // Validate using the production packet factory.
      _ = GoveeLanPackets.Brightness(percentage);

      Calls.Add($"brightness:{percentage}");
      return Task.CompletedTask;
   }

   public Task SetColorTemperatureAsync(int kelvin, CancellationToken ct)
   {
      ct.ThrowIfCancellationRequested();

      // Create a packet to validate the input.
      _ = GoveeLanPackets.ColorTemperature(kelvin);

      Calls.Add($"kelvin:{kelvin}");
      return Task.CompletedTask;
   }

   public Task SendRealtimeFrameAsync(RgbFrame frame, CancellationToken ct)
   {
      ct.ThrowIfCancellationRequested();

      // Create a packet to validate the input.
      _ = GoveeLanPackets.RazerFrame(frame, gradient: true);

      Calls.Add("frame");
      Frames.Add(new RgbFrame(frame.Colors.ToArray()));
      return Task.CompletedTask;
   }

   public void Dispose() => Disposed = true;
}

/// <summary>
/// Swap the backend without changing the command tests. Fake is used for
/// deterministic assertions. Diagnostic is genuinely socket-free and represents
/// an input-only Host, not a simulated device. Real hardware has a separate,
/// explicitly opted-in fixture in ManualHardwareTests.cs.
/// </summary>
internal sealed class LampTestHarness : IDisposable
{
   public const string Floor = "floor";
   public const string Desk = "desk";
   public readonly RecordingLamp FloorClient = new();
   public readonly RecordingLamp DeskClient = new();
   public BridgeSettings Settings { get; }
   public BridgeHostOptions Options { get; }
   public BridgeRuntime Runtime { get; }

   public LampTestHarness(
      bool diagnostic = false, bool twoLamps = false,
      int? zones = 8, bool supportsRealtime = true,
      Action<BridgeSettings>? configureSettings = null,
      Action<BridgeHostOptions>? configureOptions = null)
   {
      Settings = new BridgeSettings
      {
         Diagnostic = diagnostic,
         LogicalLedCount = 256,
         MinimumSendIntervalMs = 100,
         RemotePollIntervalMs = 500,
         LampTimeoutMs = 500,
         Lamps =
         [
            new BridgeLampSettings
            {
               Id = Floor, Ip = "192.0.2.10",
               Capabilities = new LampCapabilities
               {
                  SupportsRealtime = supportsRealtime, RealtimeZoneCount = zones
               }
            }
         ]
      };
      if (twoLamps)
      {
         Settings.Lamps.Add(new BridgeLampSettings
         {
            Id = Desk, Ip = "192.0.2.11",
            Capabilities = new LampCapabilities { SupportsRealtime = true, RealtimeZoneCount = zones }
         });
      }
      Options = new BridgeHostOptions
      {
         RealtimeArmDelayMs = 0,
         RealtimeMinimumFrameIntervalMs = 50,
         RealtimeKeepaliveIntervalMs = 250,
         StaticVerificationGraceMs = 0,
         FrameStaleAfterMs = 500
      };
      configureSettings?.Invoke(Settings);
      configureOptions?.Invoke(Options);
      Runtime = new BridgeRuntime(Settings, Options, new NullBridgeEventPublisher(),
         NullLogger<BridgeRuntime>.Instance,
         entry => entry.Id == Floor ? FloorClient : DeskClient);
   }

   public static RgbFrame Solid(RgbColor color, int zones = 8) =>
      new(Enumerable.Repeat(color, zones).ToArray());

   public static readonly RgbColor Red = new(255, 0, 0);
   public static readonly RgbColor Cyan = new(0, 255, 255);
   public static readonly RgbColor Blue = new(0, 0, 255);

   /// <summary>Calls the same action/payload dispatcher used by the PowerShell Host module.</summary>
   public async Task<BridgeIpcResponse> Command(
      string action, string? json = null, string lampId = Floor,
      CancellationToken ct = default)
   {
      using JsonDocument? document = json is null ? null : JsonDocument.Parse(json);
      return await Runtime.ExecuteCommandAsync(action, document?.RootElement, lampId, ct);
   }

   public async Task<BridgeHostStatusSnapshot> Ok(
      string action, string? json = null, string lampId = Floor)
   {
      BridgeIpcResponse response = await Command(action, json, lampId);
      Assert.IsInstanceOfType<BridgeHostStatusSnapshot>(response,
         $"Expected success for {action} but received {response}.");
      return (BridgeHostStatusSnapshot)response;
   }

   public async Task<BridgeIpcErrorResponse> Error(
      string action, string? json = null, string lampId = Floor)
   {
      BridgeIpcResponse response = await Command(action, json, lampId);
      Assert.IsInstanceOfType<BridgeIpcErrorResponse>(response,
         $"Expected an error for {action} but received {response}.");
      return (BridgeIpcErrorResponse)response;
   }

   public Task<BridgeHostStatusSnapshot> Snapshot(string lampId = Floor) =>
      Ok(BridgeIpcActions.GetHostStatus, lampId: lampId);

   public void Dispose() => Runtime.Dispose();
}
