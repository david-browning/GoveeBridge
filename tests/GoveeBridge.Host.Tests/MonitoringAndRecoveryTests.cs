using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

/// <summary>
/// Contracts of status polling, static-color reconciliation, retry caps and
/// remote priority. Physical Govee replies are observations, not acknowledgments.
/// </summary>
[TestClass]
public sealed class MonitoringAndRecoveryTests
{
   private static GoveeDeviceStatus On(RgbColor color) => new(true, 50, color, 0);
   private static GoveeDeviceStatus Off(RgbColor color) => new(false, 50, color, 0);

   [TestMethod]
   public async Task PollingCanObserveWithoutTakingOwnership()
   {
      using var rig = new LampTestHarness();
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      var state = await rig.Snapshot();
      Assert.AreEqual("Manual", state.Mode);
      Assert.AreEqual(true, state.LampOnline);
      Assert.AreEqual(1, rig.FloorClient.Count("status"));
      Assert.AreEqual(0, rig.FloorClient.CountPrefix("power:"));
   }

   [TestMethod]
   public async Task MissingDeviceReplyDoesNotReleasePcControl()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      rig.FloorClient.Status = null;
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      Assert.AreEqual("Pc", (await rig.Snapshot()).Mode);
      Assert.AreEqual(false, (await rig.Snapshot()).LampOnline);
   }

   [TestMethod]
   public async Task ObservedExternalOffYieldsOwnershipWithoutPoweringOnAgain()
   {
      using var rig = new LampTestHarness();
      rig.FloorClient.Status = On(LampTestHarness.Red);
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      await rig.Ok(BridgeIpcActions.Resume);
      rig.FloorClient.Status = Off(LampTestHarness.Red);
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      Assert.AreEqual("Manual", (await rig.Snapshot()).Mode);
      Assert.AreEqual(0, rig.FloorClient.Count("power:True"));
   }

   [TestMethod]
   public async Task ExternalOffDoesNotForceReleaseWhenUserDisablesThatPolicy()
   {
      using var rig = new LampTestHarness(configureSettings: s =>
         s.AutoReleaseWhenRemoteTurnsOff = false);
      rig.FloorClient.Status = On(LampTestHarness.Red);
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      await rig.Ok(BridgeIpcActions.Resume);
      rig.FloorClient.Status = Off(LampTestHarness.Red);
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      Assert.AreEqual("Pc", (await rig.Snapshot()).Mode);
   }

   [TestMethod]
   public async Task PollingIsSuspendedDuringArmedStreamingByDefault()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.Arm);
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      Assert.AreEqual(0, rig.FloorClient.Count("status"));
      Assert.IsTrue((await rig.Snapshot()).LampStatusMayBeStale);
      var error = await rig.Error(BridgeIpcActions.GetLampStatus);
      StringAssert.Contains(error.Error, "suspended");
   }

   [TestMethod]
   public async Task PollingMayBeExplicitlyEnabledDuringStreaming()
   {
      using var rig = new LampTestHarness(configureOptions: o =>
         o.PollWhileRealtimeArmed = true);
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.Arm);
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      Assert.AreEqual(1, rig.FloorClient.Count("status"));
      Assert.IsFalse((await rig.Snapshot()).LampStatusMayBeStale);
   }

   [TestMethod]
   public async Task AlreadyObservedMatchingStaticColorNeedsNoWrite()
   {
      using var rig = new LampTestHarness();
      rig.FloorClient.Status = On(LampTestHarness.Red);
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      await rig.Ok(BridgeIpcActions.Resume);
      var result = await rig.Ok(BridgeIpcActions.SetSolidColor,
         """{"color":{"r":255,"g":0,"b":0}}""");
      Assert.AreEqual(true, result.StaticVerified);
      Assert.AreEqual(0, rig.FloorClient.Colors.Count);
   }

   [TestMethod]
   public async Task UnverifiedStaticColorHasBoundedRetriesNotAnInfiniteLoop()
   {
      using var rig = new LampTestHarness(configureOptions: o =>
      {
         o.StaticVerificationGraceMs = 0;
         o.StaticColorRetryLimit = 1;
      });
      rig.FloorClient.Status = On(new RgbColor(0, 0, 0));
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.SetSolidColor,
         """{"color":{"r":255,"g":0,"b":0}}""");
      Assert.AreEqual(1, rig.FloorClient.Colors.Count);
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      Assert.AreEqual(true, (await rig.Snapshot()).StaticRetryPending);
      await Task.Delay(135); // MinimumSendIntervalMs=100
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(2, rig.FloorClient.Colors.Count);
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      Assert.AreEqual(true, (await rig.Snapshot()).StaticSuspended);
      await Task.Delay(135);
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(2, rig.FloorClient.Colors.Count);
   }

   [TestMethod]
   public async Task UnexpectedThirdColorSuspendsRetryRatherThanFightingRemote()
   {
      using var rig = new LampTestHarness();
      rig.FloorClient.Status = On(new RgbColor(0, 0, 0));
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.SetSolidColor,
         """{"color":{"r":255,"g":0,"b":0}}""");
      rig.FloorClient.Status = On(LampTestHarness.Blue);
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      Assert.AreEqual(true, (await rig.Snapshot()).StaticSuspended);
      await Task.Delay(140);
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(1, rig.FloorClient.Colors.Count);
   }

   [TestMethod]
   public async Task StaticRetryIsCanceledWhenUserChangesToWhiteTemperature()
   {
      using var rig = new LampTestHarness();
      rig.FloorClient.Status = On(new RgbColor(0, 0, 0));
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.SetSolidColor,
         """{"color":{"r":255,"g":0,"b":0}}""");
      await rig.Runtime.PollLampAsync(CancellationToken.None);
      Assert.AreEqual(true, (await rig.Snapshot()).StaticRetryPending);
      await rig.Ok(BridgeIpcActions.SetColorTemperature, """{"kelvin":4000}""");
      await Task.Delay(140);
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(1, rig.FloorClient.Colors.Count);
      Assert.IsNull((await rig.Snapshot()).LastStaticColor);
   }
}
