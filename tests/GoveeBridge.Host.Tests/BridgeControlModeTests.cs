using System.Text.Json;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

[TestClass]
public sealed class BridgeControlModeTests
{
   /// <summary>
   /// Creates two independently controllable sessions using fake clients;
   /// nothing binds Govee's fixed LAN response port during these tests.
   /// </summary>
   private static (BridgeRuntime Runtime, FakeLamp Floor, FakeLamp Desk) Create()
   {
      var floor = new FakeLamp();
      var desk = new FakeLamp();
      BridgeSettings settings = new()
      {
         Diagnostic = false,
         Lamps =
         [
            new BridgeLampSettings
            {
               Id = "floor", Ip = "192.168.1.50",
               Capabilities = new LampCapabilities
               {
                  SupportsRealtime = true, RealtimeZoneCount = 2
               }
            },
            new BridgeLampSettings
            {
               Id = "desk", Ip = "192.168.1.51",
               Capabilities = new LampCapabilities { SupportsRealtime = false }
            }
         ]
      };
      var runtime = new BridgeRuntime(settings,
         new BridgeHostOptions { RealtimeArmDelayMs = 0 },
         new NullBridgeEventPublisher(), NullLogger<BridgeRuntime>.Instance,
         entry => entry.Id == "floor" ? floor : desk);
      return (runtime, floor, desk);
   }

   /// <summary>
   /// Creates the two-component frame expected by the configured Razer lamp.
   /// </summary>
   private static RgbFrame TwoColors() => new(
      [new RgbColor(255, 0, 0), new RgbColor(0, 0, 255)]);

   /// <summary>
   /// Convenience overload for a parameterless command sent to one target.
   /// </summary>
   private static Task<BridgeIpcResponse> Command(
      BridgeRuntime runtime, string action, string lampId) =>
      runtime.ExecuteCommandAsync(action, null, lampId, CancellationToken.None);

   /// <summary>
   /// Verifies that commands are rejected or accepted according to mode,
   /// and that receiving a frame cannot implicitly ARM streaming.
   /// </summary>
   [TestMethod]
   public async Task PermissionTableAndExplicitArm()
   {
      var (runtime, floor, _) = Create();
      using (runtime)
      {
         using JsonDocument power = JsonDocument.Parse("""{"isOn":true}""");
         Assert.IsInstanceOfType<BridgeIpcErrorResponse>(await runtime.ExecuteCommandAsync(
            BridgeIpcActions.SetPower, power.RootElement, "floor", CancellationToken.None));
         await Command(runtime, BridgeIpcActions.Resume, "floor");
         Assert.IsTrue(runtime.ReceiveFrame(TwoColors(), "floor"));
         await runtime.PumpFrameAsync(CancellationToken.None);
         Assert.AreEqual(0, floor.Commands.Count(command => command == "frame"));
         await Command(runtime, BridgeIpcActions.Arm, "floor");
         using JsonDocument color = JsonDocument.Parse(
            """{"color":{"r":30,"g":40,"b":50}}""");
         Assert.IsInstanceOfType<BridgeIpcErrorResponse>(await runtime.ExecuteCommandAsync(
            BridgeIpcActions.SetSolidColor, color.RootElement, "floor", CancellationToken.None));
         Assert.IsInstanceOfType<BridgeIpcErrorResponse>(await Command(runtime, BridgeIpcActions.SetColorTemperature, "floor"));
         using JsonDocument brightness = JsonDocument.Parse("""{"brightnessPercent":60}""");
         Assert.IsInstanceOfType<BridgeHostStatusSnapshot>(await runtime.ExecuteCommandAsync(
            BridgeIpcActions.SetBrightness, brightness.RootElement, "floor", CancellationToken.None));
         Assert.IsTrue(floor.Commands.Contains("brightness:60"));
         await Command(runtime, BridgeIpcActions.Disarm, "floor");
         var status = (BridgeHostStatusSnapshot)await Command(runtime, BridgeIpcActions.GetHostStatus, "floor");
         Assert.AreEqual("Pc", status.Mode);
         Assert.IsFalse(status.RazerArmed);
      }
   }

   /// <summary>
   /// Explicit OFF while streaming must not be reversed by later frames, and
   /// the next ON causes the first fresh frame to re-submit ARM before output.
   /// </summary>
   [TestMethod]
   public async Task PowerOffSuppressesStreamingUntilOnAndFreshFrame()
   {
      var (runtime, floor, _) = Create();
      using (runtime)
      {
         await Command(runtime, BridgeIpcActions.Resume, "floor");
         await Command(runtime, BridgeIpcActions.Arm, "floor");
         using JsonDocument off = JsonDocument.Parse("""{"isOn":false}""");
         await runtime.ExecuteCommandAsync(
            BridgeIpcActions.SetPower, off.RootElement, "floor", CancellationToken.None);
         Assert.IsTrue(runtime.ReceiveFrame(TwoColors(), "floor"));
         await runtime.PumpFrameAsync(CancellationToken.None);
         Assert.AreEqual(0, floor.Commands.Count(command => command == "frame"));
         var status = (BridgeHostStatusSnapshot)await Command(runtime, BridgeIpcActions.GetHostStatus, "floor");
         Assert.AreEqual("PcStreaming", status.Mode);
         Assert.AreEqual(false, status.RequestedPowerOn);

         using JsonDocument on = JsonDocument.Parse("""{"isOn":true}""");
         await runtime.ExecuteCommandAsync(
            BridgeIpcActions.SetPower, on.RootElement, "floor", CancellationToken.None);
         Assert.IsTrue(runtime.ReceiveFrame(TwoColors(), "floor"));
         await runtime.PumpFrameAsync(CancellationToken.None);
         Assert.AreEqual(1, floor.Commands.Count(command => command == "frame"));
         Assert.AreEqual(2, floor.Commands.Count(command => command == "arm:True"));
      }
   }

   /// <summary>
   /// Routing by stable lamp ID isolates commands, frames, and Host counters.
   /// </summary>
   [TestMethod]
   public async Task PerLampRoutingAndCapabilities()
   {
      var (runtime, floor, desk) = Create();
      using (runtime)
      {
         await Command(runtime, BridgeIpcActions.Resume, "floor");
         await Command(runtime, BridgeIpcActions.Resume, "desk");
         Assert.IsInstanceOfType<BridgeIpcErrorResponse>(
            await Command(runtime, BridgeIpcActions.Arm, "desk"));
         Assert.IsInstanceOfType<BridgeHostStatusSnapshot>(
            await Command(runtime, BridgeIpcActions.Arm, "floor"));
         Assert.IsTrue(runtime.ReceiveFrame(TwoColors(), "floor"));
         await runtime.PumpFrameAsync(CancellationToken.None);
         Assert.IsTrue(floor.Commands.Contains("frame"));
         Assert.IsFalse(desk.Commands.Contains("frame"));
         var first = (BridgeHostStatusSnapshot)await Command(runtime, BridgeIpcActions.GetHostStatus, "floor");
         var second = (BridgeHostStatusSnapshot)await Command(runtime, BridgeIpcActions.GetHostStatus, "desk");
         Assert.AreEqual("PcStreaming", first.Mode);
         Assert.AreEqual("Pc", second.Mode);
         Assert.AreEqual(1L, first.FramesReceived);
         Assert.AreEqual(0L, second.FramesReceived);
         Assert.IsFalse(runtime.ReceiveFrame(TwoColors(), "unknown"));
      }
   }

   /// <summary>
   /// Deliberate release clears Host ownership without powering the lamp off.
   /// </summary>
   [TestMethod]
   public async Task ReleaseDisarmsWithoutPoweringOff()
   {
      var (runtime, floor, _) = Create();
      using (runtime)
      {
         await Command(runtime, BridgeIpcActions.Resume, "floor");
         await Command(runtime, BridgeIpcActions.Arm, "floor");
         int powerOffBefore = floor.Commands.Count(command => command == "power:False");
         var status = (BridgeHostStatusSnapshot)await Command(runtime, BridgeIpcActions.Release, "floor");
         Assert.AreEqual("Manual", status.Mode);
         Assert.IsFalse(status.RazerArmed);
         Assert.IsTrue(floor.Commands.Contains("arm:False"));
         Assert.AreEqual(powerOffBefore, floor.Commands.Count(command => command == "power:False"));
      }
   }

   /// <summary>
   /// Failed disarm preserves the recorded physical uncertainty and must not
   /// allow a subsequent direct color command until a successful retry.
   /// </summary>
   [TestMethod]
   public async Task FailedDisarmBlocksDirectColorUntilRetry()
   {
      var (runtime, floor, _) = Create();
      using (runtime)
      {
         await Command(runtime, BridgeIpcActions.Resume, "floor");
         await Command(runtime, BridgeIpcActions.Arm, "floor");
         floor.FailDisarm = true;
         Assert.IsInstanceOfType<BridgeIpcErrorResponse>(await Command(runtime, BridgeIpcActions.Disarm, "floor"));
         var state = (BridgeHostStatusSnapshot)await Command(runtime, BridgeIpcActions.GetHostStatus, "floor");
         Assert.AreEqual("Pc", state.Mode);
         Assert.IsTrue(state.RazerArmed);
         using JsonDocument color = JsonDocument.Parse("""{"color":{"r":255,"g":0,"b":0}}""");
         Assert.IsInstanceOfType<BridgeIpcErrorResponse>(await runtime.ExecuteCommandAsync(BridgeIpcActions.SetSolidColor, color.RootElement, "floor", CancellationToken.None));
         floor.FailDisarm = false;
         await Command(runtime, BridgeIpcActions.Disarm, "floor");
         Assert.IsFalse(((BridgeHostStatusSnapshot)await Command(runtime, BridgeIpcActions.GetHostStatus, "floor")).RazerArmed);
         Assert.IsInstanceOfType<BridgeHostStatusSnapshot>(await runtime.ExecuteCommandAsync(BridgeIpcActions.SetSolidColor, color.RootElement, "floor", CancellationToken.None));
      }
   }

   /// <summary>
   /// The fake implements the physical client contract without network I/O;
   /// command attempts are observable independently for each device.
   /// </summary>
   private sealed class FakeLamp : IGoveeRealtimeClient
   {
      /// <summary>
      /// Captured physical sends, including ARM/DISARM attempts.
      /// </summary>
      public List<string> Commands { get; } = [];

      /// <summary>
      /// Simulates a transport failure only for physical DISARM submissions.
      /// </summary>
      public bool FailDisarm { get; set; }

      /// <summary>
      /// Pretends that status is unavailable rather than inventing an ACK.
      /// </summary>
      public Task<GoveeDeviceStatus?> GetStatusAsync(CancellationToken ct) =>
         Task.FromResult<GoveeDeviceStatus?>(null);
      /// <summary>
      /// Records the power request.
      /// </summary>
      public Task SetPowerAsync(bool on, CancellationToken ct)
      {
         Commands.Add($"power:{on}");
         return Task.CompletedTask;
      }
      /// <summary>
      /// Records the brightness request.
      /// </summary>
      public Task SetBrightnessAsync(int percentage, CancellationToken ct)
      {
         Commands.Add($"brightness:{percentage}");
         return Task.CompletedTask;
      }
      /// <summary>
      /// Records a solid color request.
      /// </summary>
      public Task SetColorAsync(RgbColor color, CancellationToken ct)
      {
         Commands.Add("color");
         return Task.CompletedTask;
      }
      /// <summary>
      /// Records a white-temperature request.
      /// </summary>
      public Task SetColorTemperatureAsync(int kelvin, CancellationToken ct)
      {
         Commands.Add("kelvin");
         return Task.CompletedTask;
      }
      /// <summary>
      /// Records ARM/DISARM attempts, optionally failing physical DISARM.
      /// </summary>
      public Task SetRealtimeEnabledAsync(bool enabled, CancellationToken ct)
      {
         if (!enabled && FailDisarm)
         {
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkDown);
         }
         Commands.Add($"arm:{enabled}");
         return Task.CompletedTask;
      }
      /// <summary>
      /// Records one full RGB frame attempt.
      /// </summary>
      public Task SendRealtimeFrameAsync(RgbFrame frame, CancellationToken ct)
      {
         Commands.Add("frame");
         return Task.CompletedTask;
      }
      /// <summary>
      /// Has no sockets to release.
      /// </summary>
      public void Dispose() { }
   }
}
