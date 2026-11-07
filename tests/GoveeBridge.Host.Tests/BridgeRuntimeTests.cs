using System.Text.Json;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

[TestClass]
public sealed class BridgeRuntimeTests
{
   private static BridgeRuntime CreateDiagnosticRuntime(IBridgeEventPublisher? events = null) =>
      new(new BridgeSettings { TransportMode = TransportMode.Diagnostic },
         new BridgeHostOptions(),
         events ?? new NullBridgeEventPublisher(),
         NullLogger<BridgeRuntime>.Instance);

   [TestMethod]
   public async Task Status_InitiallyReportsManualOwnership()
   {
      using BridgeRuntime runtime = CreateDiagnosticRuntime();
      BridgeIpcResponse reply = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.GetHostStatus, CancellationToken.None);

      Assert.IsInstanceOfType<BridgeHostStatusSnapshot>(reply);
      var status = (BridgeHostStatusSnapshot)reply;
      Assert.AreEqual("Manual", status.Ownership);
      Assert.AreEqual("Diagnostic", status.Mode);
      Assert.AreEqual(0L, status.FramesReceived);
      Assert.AreEqual(0L, status.FramesSent);
      Assert.IsNull(status.LampOnline);
   }

   [TestMethod]
   public async Task Resume_IsIdempotentAndReleaseReturnsManualOwnership()
   {
      var publisher = new EventCollector();
      using BridgeRuntime runtime = CreateDiagnosticRuntime(publisher);

      BridgeIpcResponse first = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.Resume, CancellationToken.None);
      BridgeIpcResponse second = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.Resume, CancellationToken.None);
      Assert.AreEqual("PC", ((BridgeHostStatusSnapshot)first).Ownership);
      Assert.AreEqual("PC", ((BridgeHostStatusSnapshot)second).Ownership);
      Assert.AreEqual(1, publisher.Events.Count);
      Assert.AreEqual(BridgeEventKind.ControlOwnershipChanged, publisher.Events[0].Kind);

      BridgeIpcResponse release = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.Release, CancellationToken.None);
      Assert.AreEqual("Manual", ((BridgeHostStatusSnapshot)release).Ownership);
      Assert.AreEqual(2, publisher.Events.Count);
   }

   [TestMethod]
   public async Task IncomingFrame_UpdatesReceiptCounterButNotPhysicalOutputInDiagnosticMode()
   {
      using BridgeRuntime runtime = CreateDiagnosticRuntime();
      runtime.ReceiveFrame(new RgbFrame([new RgbColor(255, 0, 0)]));
      runtime.ReceiveFrame(new RgbFrame([new RgbColor(0, 0, 255)]));
      await runtime.ExecuteCommandAsync(BridgeIpcActions.Resume, CancellationToken.None);
      await runtime.PumpFrameAsync(CancellationToken.None);

      var reply = (BridgeHostStatusSnapshot)await runtime.ExecuteCommandAsync(
         BridgeIpcActions.GetHostStatus, CancellationToken.None);
      Assert.AreEqual(2L, reply.FramesReceived);
      Assert.AreEqual(0L, reply.FramesSent);
      Assert.AreEqual(0L, reply.PowerCommandsSent);
   }

   [TestMethod]
   public async Task LampStatus_WithoutPhysicalClient_ReturnsTypedErrorWithOldWireShape()
   {
      using BridgeRuntime runtime = CreateDiagnosticRuntime();
      BridgeIpcResponse reply = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.GetLampStatus, CancellationToken.None);

      Assert.IsInstanceOfType<BridgeIpcErrorResponse>(reply);
      using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize((object)reply));
      Assert.AreEqual("Diagnostic mode has no physical lamp connection",
         json.RootElement.GetProperty("error").GetString());
      Assert.IsFalse(json.RootElement.TryGetProperty("status", out _));
   }

   [TestMethod]
   public async Task UnknownCommand_ReturnsErrorWithoutLosingOwnership()
   {
      using BridgeRuntime runtime = CreateDiagnosticRuntime();
      await runtime.ExecuteCommandAsync(BridgeIpcActions.Resume, CancellationToken.None);
      BridgeIpcResponse reply = await runtime.ExecuteCommandAsync(
         "does-not-exist", CancellationToken.None);

      Assert.IsInstanceOfType<BridgeIpcErrorResponse>(reply);
      Assert.AreEqual("Unknown command: does-not-exist", ((BridgeIpcErrorResponse)reply).Error);
      BridgeIpcResponse status = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.GetHostStatus, CancellationToken.None);
      Assert.AreEqual("PC", ((BridgeHostStatusSnapshot)status).Ownership);
   }

   [TestMethod]
   public async Task Status_UsesExistingCamelCaseWireNamesAndNullFields()
   {
      using BridgeRuntime runtime = CreateDiagnosticRuntime();
      object reply = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.GetHostStatus, CancellationToken.None);
      using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(reply));
      JsonElement root = json.RootElement;

      Assert.AreEqual("Manual", root.GetProperty("ownership").GetString());
      Assert.IsFalse(root.GetProperty("razerArmed").GetBoolean());
      Assert.AreEqual(0L, root.GetProperty("framesReceived").GetInt64());
      Assert.AreEqual(JsonValueKind.Null, root.GetProperty("staticVerified").ValueKind);
      Assert.AreEqual(JsonValueKind.Null, root.GetProperty("lastStaticColor").ValueKind);
      Assert.AreEqual(JsonValueKind.Null, root.GetProperty("lamp").ValueKind);
      Assert.IsFalse(root.TryGetProperty("Ownership", out _));
   }

   [TestMethod]
   public void LampStatusResult_SerializesBothPhysicalAndHostStatus()
   {
      var lamp = new GoveeDeviceStatus(true, 80, new RgbColor(30, 40, 50), 0);
      var host = new BridgeHostStatusSnapshot(
         "GoveeStatic", "PC", false, true, DateTimeOffset.UtcNow, false,
         10, 2, 1, "#1E2832", true, 1, false, false, lamp);
      object response = new BridgeIpcLampStatusResponse(lamp, host);
      using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(response));
      Assert.IsTrue(json.RootElement.TryGetProperty("lamp", out _));
      Assert.AreEqual("PC", json.RootElement.GetProperty("status").GetProperty("ownership").GetString());
   }

   private sealed class EventCollector : IBridgeEventPublisher
   {
      public List<BridgeEvent> Events { get; } = [];
      public void Publish(BridgeEvent bridgeEvent) => Events.Add(bridgeEvent);
   }
}
