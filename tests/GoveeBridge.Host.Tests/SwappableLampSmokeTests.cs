using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

/// <summary>
/// The same scenario can target a recording fake, a diagnostic input-only
/// Host, or a REAL physical Govee lamp. Real use requires three separate
/// environment variables so that ordinary dotnet test cannot touch hardware.
/// Do not run another Host concurrently: a real fixture owns UDP port 4002.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SwappableLampSmokeTests
{
   public TestContext TestContext { get; set; } = null!;

   [TestMethod]
   [TestCategory("Swappable")]
   public async Task CmdletWorkflowAgainstSelectedBackend()
   {
      string backend = Environment.GetEnvironmentVariable("GOVEE_TEST_BACKEND") ?? "Fake";
      bool real = backend.Equals("Real", StringComparison.OrdinalIgnoreCase);
      bool diagnostic = backend.Equals("Diagnostic", StringComparison.OrdinalIgnoreCase);
      if (!real && !diagnostic && !backend.Equals("Fake", StringComparison.OrdinalIgnoreCase))
      {
         Assert.Fail("GOVEE_TEST_BACKEND must be Fake, Diagnostic, or Real.");
      }

      string id = "floor";
      string ip = "192.0.2.10"; // TEST-NET-1, never a production destination.
      int zones = 8;
      if (real)
      {
         if (Environment.GetEnvironmentVariable("GOVEE_TEST_REAL") != "I_ACCEPT_LAMP_CHANGES")
         {
            Assert.Inconclusive("Real-lamp smoke tests require GOVEE_TEST_REAL=I_ACCEPT_LAMP_CHANGES.");
         }
         id = Environment.GetEnvironmentVariable("GOVEE_TEST_LAMP_ID") ?? "";
         ip = Environment.GetEnvironmentVariable("GOVEE_TEST_LAMP_IP") ?? "";
         if (!int.TryParse(Environment.GetEnvironmentVariable("GOVEE_TEST_ZONES"), out zones) ||
            zones is < 1 or > 255 || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ip))
         {
            Assert.Fail("Real mode requires GOVEE_TEST_LAMP_ID, GOVEE_TEST_LAMP_IP and GOVEE_TEST_ZONES (1..255).");
         }
      }

      var settings = new BridgeSettings
      {
         Diagnostic = diagnostic,
         LogicalLedCount = zones,
         Lamps =
         [
            new BridgeLampSettings
            {
               Id = id, Ip = ip,
               Capabilities = new LampCapabilities
               {
                  SupportsRealtime = true, RealtimeZoneCount = zones
               }
            }
         ]
      };
      var options = new BridgeHostOptions
      {
         RealtimeArmDelayMs = real ? 1200 : 0,
         RealtimeMinimumFrameIntervalMs = 100,
         RealtimeKeepaliveIntervalMs = 500,
         FrameStaleAfterMs = 5000
      };
      var fake = new RecordingLamp();
      using var runtime = real
         ? new BridgeRuntime(settings, options, new NullBridgeEventPublisher(),
            NullLogger<BridgeRuntime>.Instance)
         : new BridgeRuntime(settings, options, new NullBridgeEventPublisher(),
            NullLogger<BridgeRuntime>.Instance, _ => fake);

      async Task<BridgeIpcResponse> Cmd(string action, string? json = null)
      {
         using System.Text.Json.JsonDocument? doc = json is null ? null :
            System.Text.Json.JsonDocument.Parse(json);
         return await runtime.ExecuteCommandAsync(action, doc?.RootElement, id, CancellationToken.None);
      }
      async Task<BridgeHostStatusSnapshot> Expect(string action, string? json = null)
      {
         var reply = await Cmd(action, json);
         Assert.IsInstanceOfType<BridgeHostStatusSnapshot>(reply, $"{action}: {reply}");
         return (BridgeHostStatusSnapshot)reply;
      }

      TestContext.WriteLine($"Backend={backend}, LampId={id}, Zones={zones}");
      try
      {
         var initial = await Expect(BridgeIpcActions.GetHostStatus);
         Assert.AreEqual("Manual", initial.Mode);
         if (diagnostic)
         {
            Assert.IsTrue(runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red, zones), id));
            await runtime.PumpFrameAsync(CancellationToken.None);
            var snapshot = await Expect(BridgeIpcActions.GetHostStatus);
            Assert.AreEqual(1L, snapshot.FramesReceived);
            Assert.AreEqual(0L, snapshot.FramesSent);
            var error = await Cmd(BridgeIpcActions.GetLampStatus);
            Assert.IsInstanceOfType<BridgeIpcErrorResponse>(error);
            TestContext.WriteLine("Diagnostic mode accepts input but intentionally cannot drive or query a device.");
            return;
         }

         // These reproduce the Host PowerShell command flow. They do change a
         // physical lamp when the operator has explicitly selected Real.
         await Expect(BridgeIpcActions.Resume);
         await Expect(BridgeIpcActions.SetPower, """{"isOn":true}""");
         await Expect(BridgeIpcActions.SetBrightness, """{"brightnessPercent":50}""");
         TestContext.WriteLine("Expect 50% brightness and RED for two seconds.");
         await Expect(BridgeIpcActions.SetSolidColor,
            """{"color":{"r":255,"g":0,"b":0}}""");
         if (real) await Task.Delay(2000);
         TestContext.WriteLine("Expect 4000 K white for two seconds.");
         await Expect(BridgeIpcActions.SetColorTemperature, """{"kelvin":4000}""");
         if (real) await Task.Delay(2000);

         await Expect(BridgeIpcActions.Arm);
         foreach (RgbColor color in new[]
         {
            LampTestHarness.Cyan, LampTestHarness.Red, LampTestHarness.Cyan
         })
         {
            TestContext.WriteLine($"Expect real-time solid {color} for {(real ? 4 : 0.15)} seconds.");
            int iterations = real ? 16 : 1;
            for (int i = 0; i < iterations; i++)
            {
               Assert.IsTrue(runtime.ReceiveFrame(LampTestHarness.Solid(color, zones), id));
               await runtime.PumpFrameAsync(CancellationToken.None);
               await Task.Delay(real ? 250 : 150);
            }
         }
         Assert.AreEqual("PcStreaming", (await Expect(BridgeIpcActions.GetHostStatus)).Mode);
         await Expect(BridgeIpcActions.Disarm);
         var released = await Expect(BridgeIpcActions.Release);
         Assert.AreEqual("Manual", released.Mode);
         Assert.IsFalse(released.RazerArmed);
         TestContext.WriteLine("After DISARM, observe whether native light state returns. A UDP submission is not proof of hardware acceptance.");
         if (!real)
         {
            Assert.AreEqual(3, fake.Frames.Count);
            Assert.AreEqual(1, fake.Count("arm:False"));
         }
      }
      finally
      {
         if (!diagnostic)
         {
            try { await Cmd(BridgeIpcActions.Release); }
            catch (Exception ex) { TestContext.WriteLine($"Release cleanup failed: {ex.Message}"); }
         }
         await runtime.ShutdownAsync();
      }
   }
}
