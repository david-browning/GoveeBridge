using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

/// <summary>
/// Contract of Send-GoveeHostFrame, Send-GoveeHostSolidFrame and the animation
/// helper, including mailbox semantics, ownership and hardware-safety cases.
/// Drive the pump explicitly to avoid an unreliable background timer in unit tests.
/// </summary>
[TestClass]
public sealed class StreamingAndSafetyTests
{
   private static async Task StartStreaming(LampTestHarness rig)
   {
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.Arm);
   }

   [TestMethod]
   public async Task MailboxCoalescesOldFramesAndSendsLatestOnly()
   {
      using var rig = new LampTestHarness();
      await StartStreaming(rig);
      Assert.IsTrue(rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "floor"));
      Assert.IsTrue(rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Cyan), "floor"));
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(1, rig.FloorClient.Frames.Count);
      CollectionAssert.AreEqual(
         Enumerable.Repeat(LampTestHarness.Cyan, 8).ToArray(),
         rig.FloorClient.Frames[0].Colors.ToArray());
      var state = await rig.Snapshot();
      Assert.AreEqual(2L, state.FramesReceived);
      Assert.AreEqual(1L, state.FramesSent);
   }

   [TestMethod]
   public async Task MailboxCopiesInputBeforeCallerCanMutateIt()
   {
      using var rig = new LampTestHarness();
      await StartStreaming(rig);
      var colors = Enumerable.Repeat(LampTestHarness.Red, 8).ToList();
      Assert.IsTrue(rig.Runtime.ReceiveFrame(new RgbFrame(colors), "floor"));
      colors[0] = LampTestHarness.Cyan;
      colors.Clear();
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(8, rig.FloorClient.Frames[0].Colors.Count);
      Assert.AreEqual(LampTestHarness.Red, rig.FloorClient.Frames[0].Colors[0]);
   }

   [TestMethod]
   public async Task ColorChangesSurviveFrameDeduplication()
   {
      using var rig = new LampTestHarness();
      await StartStreaming(rig);
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Cyan), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      await Task.Delay(80);
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(2, rig.FloorClient.Frames.Count);
      Assert.AreEqual(LampTestHarness.Cyan, rig.FloorClient.Frames[0].Colors[0]);
      Assert.AreEqual(LampTestHarness.Red, rig.FloorClient.Frames[1].Colors[0]);
   }

   [TestMethod]
   public async Task UnchangedFrameIsDeduplicatedButEventuallyKeptAlive()
   {
      using var rig = new LampTestHarness();
      await StartStreaming(rig);
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      await Task.Delay(80);
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(1, rig.FloorClient.Frames.Count,
         "An identical frame should not be submitted on every pump tick.");
      await Task.Delay(220);
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(2, rig.FloorClient.Frames.Count,
         "Unchanged output still needs the configured real-time keepalive.");
   }

   [TestMethod]
   public async Task ReceivingFramesAloneNeverArmsTheRazerChannel()
   {
      using var rig = new LampTestHarness();
      for (int index = 0; index < 10; index++)
         rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(0, rig.FloorClient.Count("arm:True"));
      Assert.AreEqual(0, rig.FloorClient.Frames.Count);
      Assert.AreEqual("Manual", (await rig.Snapshot()).Mode);
   }

   [TestMethod]
   public async Task FramesReceivedBeforeArmAreNotReplayedAfterArm()
   {
      using var rig = new LampTestHarness();
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "floor");
      await StartStreaming(rig);
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(0, rig.FloorClient.Frames.Count);
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Cyan), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(LampTestHarness.Cyan, rig.FloorClient.Frames.Single().Colors[0]);
   }

   [TestMethod]
   public async Task StreamExpirationReleasesOwnershipAndDisarms()
   {
      using var rig = new LampTestHarness();
      await StartStreaming(rig);
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      await Task.Delay(570); // Minimum configured FrameStaleAfterMs is 500.
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual("Manual", (await rig.Snapshot()).Mode);
      Assert.AreEqual(1, rig.FloorClient.Count("arm:False"));
      Assert.AreEqual(0, rig.FloorClient.Count("power:False"));
   }

   [TestMethod]
   public async Task PowerOffPausesFramesAndPowerOnRearmsBeforeNextFreshFrame()
   {
      using var rig = new LampTestHarness();
      await StartStreaming(rig);
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(1, rig.FloorClient.Frames.Count);
      await rig.Ok(BridgeIpcActions.SetPower, """{"isOn":false}""");
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Cyan), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(1, rig.FloorClient.Frames.Count);
      await rig.Ok(BridgeIpcActions.SetPower, """{"isOn":true}""");
      await Task.Delay(80); // Respect the minimum physical frame interval.
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Cyan), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(2, rig.FloorClient.Count("arm:True"),
         "A deliberate power cycle requires rearming before the next frame.");
      Assert.AreEqual(LampTestHarness.Cyan, rig.FloorClient.Frames.Last().Colors[0]);
   }

   [TestMethod]
   public async Task StaleDevStatusMustNotSendOrdinaryPowerDuringArmedStreaming()
   {
      using var rig = new LampTestHarness();
      // Simulate a fresh observed ON result: arming will not send a redundant power
      // command, which exposes the regression when that observation later expires.
      var status = await rig.Command(BridgeIpcActions.GetLampStatus);
      Assert.IsInstanceOfType<BridgeIpcLampStatusResponse>(status);
      await StartStreaming(rig);
      Assert.AreEqual(0, rig.FloorClient.Count("power:True"));

      // 500ms poll period + 500ms status timeout = 1000ms freshness window.
      // This intentionally reproduces the bug fixed locally after main.
      await Task.Delay(1150);
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Cyan), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(1, rig.FloorClient.Frames.Count);
      Assert.AreEqual(0, rig.FloorClient.Count("power:True"),
         "Ordinary 'turn on' while Razer is armed may exit physical streaming mode.");
      Assert.AreEqual("PcStreaming", (await rig.Snapshot()).Mode);
   }

   [TestMethod]
   public async Task DifferentLampStreamsRemainIsolated()
   {
      using var rig = new LampTestHarness(twoLamps: true);
      await StartStreaming(rig);
      await rig.Ok(BridgeIpcActions.Resume, lampId: "desk");
      await rig.Ok(BridgeIpcActions.Arm, lampId: "desk");
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Cyan), "floor");
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "desk");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(LampTestHarness.Cyan, rig.FloorClient.Frames.Single().Colors[0]);
      Assert.AreEqual(LampTestHarness.Red, rig.DeskClient.Frames.Single().Colors[0]);
      await rig.Ok(BridgeIpcActions.Release, lampId: "floor");
      Assert.AreEqual("PcStreaming", (await rig.Snapshot("desk")).Mode);
   }

   [TestMethod]
   public async Task MismatchedZoneCountAndUnknownLampRejectFrames()
   {
      using var rig = new LampTestHarness(zones: 8);
      
      // Allow down-sampling
      Assert.IsTrue(rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red, 7), "floor"));
      
      // Non existent lamp does not send frames.
      Assert.IsFalse(rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "missing"));

      // Only the 1 frame for the floor lamp should have made it.
      Assert.AreEqual(1L, (await rig.Snapshot()).FramesReceived);
   }

   [TestMethod]
   public async Task EmptyFrameMustBeRejectedEvenWithoutConfiguredZoneCount()
   {
      using var rig = new LampTestHarness(zones: null);
      Assert.IsFalse(rig.Runtime.ReceiveFrame(new RgbFrame([]), "floor"),
         "An empty frame cannot be encoded by the physical client.");
      Assert.AreEqual(0L, (await rig.Snapshot()).FramesReceived);
   }

   [TestMethod]
   public async Task DiagnosticInputShouldAcceptConfigured256PositionFrame()
   {
      using var rig = new LampTestHarness(diagnostic: true, zones: null);
      Assert.IsTrue(rig.Runtime.ReceiveFrame(
         LampTestHarness.Solid(LampTestHarness.Red, 255), "floor"));
      Assert.AreEqual(1L, (await rig.Snapshot()).FramesReceived);
   }

   [TestMethod]
   public async Task OversizedFrameNeverReachesPhysicalClient()
   {
      using var rig = new LampTestHarness(zones: null);
      var huge = LampTestHarness.Solid(LampTestHarness.Red, 256);
      Assert.IsFalse(rig.Runtime.ReceiveFrame(huge, "floor"));
      await StartStreaming(rig);
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(0, rig.FloorClient.Frames.Count);
   }

   [TestMethod]
   public async Task ConcurrentFrameIngressRetainsAccurateReceiptCount()
   {
      using var rig = new LampTestHarness(zones: null);
      const int attempts = 400;
      await Task.WhenAll(Enumerable.Range(0, attempts)
         .Select(n => Task.Run(() => rig.Runtime.ReceiveFrame(
            LampTestHarness.Solid(n % 2 == 0 ? LampTestHarness.Red : LampTestHarness.Cyan), "floor"))));
      Assert.AreEqual((long)attempts, (await rig.Snapshot()).FramesReceived);
   }
}
