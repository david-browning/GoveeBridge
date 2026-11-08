using System.Text;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoveeBridge.Host.Tests;

[TestClass]
public sealed class LampIdRequiredTests
{
   private static readonly RgbFrame _onePixel = new([new RgbColor(1, 2, 3)]);

   /// <summary>
   /// All IPC requests require a lamp Id.
   /// </summary>
   [TestMethod]
   public void OutboundRequestsRequireARealLampId()
   {
      foreach (string? lampId in new string?[] { null, "", "   " })
      {
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.Status(lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.LampStatus(lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.Resume(lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.Release(lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.Arm(lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.Disarm(lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.SetPower(true, lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.SetBrightness(50, lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.SetColor(new RgbColor(1, 2, 3), lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.SetColorTemperature(3500, lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.SendFrame(_onePixel, lampId!));
      }

      var request = BridgeIpcRequests.Status("floor");
      Assert.AreEqual("floor", request.LampId);
      Assert.AreEqual("floor", BridgeIpcRequests.SendFrame(_onePixel, "floor").LampId);
   }

   [TestMethod]
   public void IncomingFramesRequireLampId()
   {
      foreach (string packet in new[]
      {
         """{"type":"rgb-frame","count":1,"colors":[1,2,3]}""",
         """{"type":"rgb-frame","lampId":null,"count":1,"colors":[1,2,3]}""",
         """{"type":"rgb-frame","lampId":"","count":1,"colors":[1,2,3]}""",
         """{"type":"rgb-frame","lampId":"  ","count":1,"colors":[1,2,3]}""",
         """{"type":"rgb-frame","lampId":12,"count":1,"colors":[1,2,3]}"""
      })

      {
         Assert.IsFalse(GoveeHelpers.TryParseFrame(Encoding.UTF8.GetBytes(packet),
            8, out _, out _));
      }

      byte[] valid = Encoding.UTF8.GetBytes(
         """{"type":"rgb-frame","lampId":"desk","count":1,"colors":[1,2,3]}""");
      Assert.IsTrue(GoveeHelpers.TryParseFrame(valid, 8, out RgbFrame? frame,
         out string? selectedLampId));
      Assert.AreEqual("desk", selectedLampId);
      Assert.AreEqual(1, frame!.Colors.Count);
   }

   [TestMethod]
   public async Task RuntimeNeverInfersALamp()
   {
      Assert.Throws<ArgumentException>(() => new BridgeSettings { Diagnostic = true }.Validate());
      var settings = new BridgeSettings
      {
         Diagnostic = true,
         Lamps = [new BridgeLampSettings { Id = "floor" }]
      };

      using var runtime = new BridgeRuntime(settings, new BridgeHostOptions(),
         new NullBridgeEventPublisher(), NullLogger<BridgeRuntime>.Instance);

      Assert.IsInstanceOfType<BridgeIpcErrorResponse>(
         await runtime.ExecuteCommandAsync(BridgeIpcActions.GetHostStatus,
            null, null!, CancellationToken.None));
      Assert.IsInstanceOfType<BridgeIpcErrorResponse>(
         await runtime.ExecuteCommandAsync(BridgeIpcActions.Resume,
            null, "", CancellationToken.None));
      Assert.IsFalse(runtime.ReceiveFrame(_onePixel, null!));
      Assert.IsFalse(runtime.ReceiveFrame(_onePixel, ""));
      Assert.IsInstanceOfType<BridgeHostStatusSnapshot>(
         await runtime.ExecuteCommandAsync(BridgeIpcActions.GetHostStatus,
            null, "floor", CancellationToken.None));
   }
}
