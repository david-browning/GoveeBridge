using System.Text.Json;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

/// <summary>
/// Checks Runtime dispatch independently of UDP and physical device I/O.
/// Packet-envelope validation belongs to BridgeWorker; these tests exercise
/// the action/payload contract and ownership transitions on a diagnostic Host.
/// </summary>
[TestClass]
public sealed class BridgeCommandBoundaryTests
{
   private static BridgeRuntime CreateRuntime() =>
      new(new BridgeSettings { TransportMode = TransportMode.Diagnostic },
         new BridgeHostOptions(),
         new NullBridgeEventPublisher(),
         NullLogger<BridgeRuntime>.Instance);

   [TestMethod]
   public async Task Status_RequiresNoData_ReturnsManualSnapshot()
   {
      using BridgeRuntime runtime = CreateRuntime();
      BridgeIpcResponse response = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.GetHostStatus, null, CancellationToken.None);

      Assert.IsInstanceOfType<BridgeHostStatusSnapshot>(response);
      Assert.AreEqual("Manual", ((BridgeHostStatusSnapshot)response).Ownership);
   }

   [TestMethod]
   public async Task ResumeAndRelease_PreserveOwnershipHandoff()
   {
      using BridgeRuntime runtime = CreateRuntime();
      var first = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.Resume, null, CancellationToken.None);
      var second = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.Release, null, CancellationToken.None);

      Assert.AreEqual("PC", ((BridgeHostStatusSnapshot)first).Ownership);
      Assert.AreEqual("Manual", ((BridgeHostStatusSnapshot)second).Ownership);
   }

   [TestMethod]
   public async Task SetPower_MissingData_ReturnsTypedError()
   {
      using BridgeRuntime runtime = CreateRuntime();
      BridgeIpcResponse response = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.SetPower, null, CancellationToken.None);

      Assert.IsInstanceOfType<BridgeIpcErrorResponse>(response);
      StringAssert.Contains(((BridgeIpcErrorResponse)response).Error, "Missing or invalid payload");
   }

   [TestMethod]
   public async Task SetPower_MissingRequiredProperty_ReturnsTypedError()
   {
      using BridgeRuntime runtime = CreateRuntime();
      using JsonDocument document = JsonDocument.Parse("""{}""");
      BridgeIpcResponse response = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.SetPower, document.RootElement, CancellationToken.None);

      Assert.IsInstanceOfType<BridgeIpcErrorResponse>(response);
   }

   [TestMethod]
   public async Task SetPower_ValidPayload_ReachesPhysicalClientGuard()
   {
      using BridgeRuntime runtime = CreateRuntime();
      using JsonDocument document = JsonDocument.Parse("""{"isOn":false}""");
      await runtime.ExecuteCommandAsync(BridgeIpcActions.Resume, null, CancellationToken.None);

      BridgeIpcResponse response = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.SetPower, document.RootElement, CancellationToken.None);
      Assert.IsInstanceOfType<BridgeIpcErrorResponse>(response);
      Assert.AreEqual("Diagnostic mode has no physical lamp connection",
         ((BridgeIpcErrorResponse)response).Error);
   }

   [TestMethod]
   public async Task UnknownCommand_DoesNotMutateOwnership()
   {
      using BridgeRuntime runtime = CreateRuntime();
      await runtime.ExecuteCommandAsync(BridgeIpcActions.Resume, null, CancellationToken.None);

      BridgeIpcResponse error = await runtime.ExecuteCommandAsync(
         "no-such-action", null, CancellationToken.None);
      BridgeIpcResponse status = await runtime.ExecuteCommandAsync(
         BridgeIpcActions.GetHostStatus, null, CancellationToken.None);

      Assert.IsInstanceOfType<BridgeIpcErrorResponse>(error);
      Assert.AreEqual("PC", ((BridgeHostStatusSnapshot)status).Ownership);
   }
}
