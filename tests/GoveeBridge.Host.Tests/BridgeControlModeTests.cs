using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

/// <summary>
/// Executable contract of the exported GoveeHost PowerShell management cmdlets:
/// Get-*Status, Resume, Release, Arm, Disarm, Set-*Power/Brightness/Color/Kelvin.
/// These are expectations, not assertions tailored to the implementation.
/// </summary>
[TestClass]
public sealed class BridgeControlModeTests
{
   [TestMethod]
   public async Task StartupIsManualAndStatusIsReadOnly()
   {
      using var rig = new LampTestHarness();
      var status = await rig.Snapshot();
      Assert.AreEqual("Manual", status.Mode);
      Assert.AreEqual("Manual", status.Ownership);
      Assert.IsFalse(status.RazerArmed);
      Assert.AreEqual("floor", status.LampId);
      Assert.IsNull(status.LampOnline);
      await rig.Snapshot();
      Assert.AreEqual(0, rig.FloorClient.Calls.Count);
   }

   [TestMethod]
   public async Task LampStatusQueriesFreshStateButDoesNotClaimOwnership()
   {
      using var rig = new LampTestHarness();
      var response = await rig.Command(BridgeIpcActions.GetLampStatus);
      Assert.IsInstanceOfType<BridgeIpcLampStatusResponse>(response);
      var status = (BridgeIpcLampStatusResponse)response;
      Assert.AreEqual(true, status.Lamp?.IsOn);
      Assert.AreEqual(true, status.Status.LampOnline);
      Assert.IsNotNull(status.Status.LampStatusObservedAt);
      Assert.AreEqual("Manual", status.Status.Mode);
      Assert.AreEqual(1, rig.FloorClient.Count("status"));
      Assert.AreEqual(0, rig.FloorClient.CountPrefix("power:"));
   }

   [TestMethod]
   public async Task TimeoutIsNotMisreportedAsObservedPowerOff()
   {
      using var rig = new LampTestHarness();
      rig.FloorClient.Status = null;
      var response = await rig.Command(BridgeIpcActions.GetLampStatus);
      Assert.IsInstanceOfType<BridgeIpcLampStatusResponse>(response);
      Assert.IsNull(((BridgeIpcLampStatusResponse)response).Lamp);
      var status = await rig.Snapshot();
      Assert.AreEqual(false, status.LampOnline);
      Assert.IsNull(status.LampStatusObservedAt);
      Assert.AreEqual("Manual", status.Mode);
   }

   [TestMethod]
   public async Task ResumeIsExplicitAndIdempotentWithoutUnnecessaryLampWrites()
   {
      using var rig = new LampTestHarness();
      Assert.AreEqual("Pc", (await rig.Ok(BridgeIpcActions.Resume)).Mode);
      Assert.AreEqual("Pc", (await rig.Ok(BridgeIpcActions.Resume)).Mode);
      Assert.AreEqual(0, rig.FloorClient.Calls.Count);
   }

   [TestMethod]
   public async Task ManualAndDirectModesCannotStreamWithoutExplicitArm()
   {
      using var rig = new LampTestHarness();
      Assert.IsTrue(rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "floor"));
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(0, rig.FloorClient.Frames.Count);
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(0, rig.FloorClient.Frames.Count);
      Assert.AreEqual(1L, (await rig.Snapshot()).FramesReceived);
   }

   [TestMethod]
   public async Task ArmRequiresPcOwnershipAndRealtimeCapability()
   {
      using var rig = new LampTestHarness(supportsRealtime: false);
      StringAssert.Contains((await rig.Error(BridgeIpcActions.Arm)).Error, "not permitted");
      await rig.Ok(BridgeIpcActions.Resume);
      StringAssert.Contains((await rig.Error(BridgeIpcActions.Arm)).Error, "support");
      Assert.AreEqual(0, rig.FloorClient.Count("arm:True"));
   }

   [TestMethod]
   public async Task ArmThenDisarmThenReleasePreservesDevicePower()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      var streaming = await rig.Ok(BridgeIpcActions.Arm);
      Assert.AreEqual("PcStreaming", streaming.Mode);
      Assert.IsTrue(streaming.RazerArmed);
      Assert.AreEqual("Pc", (await rig.Ok(BridgeIpcActions.Disarm)).Mode);
      Assert.AreEqual("Manual", (await rig.Ok(BridgeIpcActions.Release)).Mode);
      Assert.AreEqual(1, rig.FloorClient.Count("arm:True"));
      Assert.AreEqual(1, rig.FloorClient.Count("arm:False"));
      Assert.AreEqual(0, rig.FloorClient.Count("power:False"));
   }

   [TestMethod]
   public async Task RepeatedArmDoesNotReissueRazerArm()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.Arm);
      await rig.Ok(BridgeIpcActions.Arm);
      Assert.AreEqual(1, rig.FloorClient.Count("arm:True"));
   }

   [TestMethod]
   public async Task ReleaseDuringStreamingDisarmsAndStopsOutput()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.Arm);
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(1, rig.FloorClient.Frames.Count);
      await rig.Ok(BridgeIpcActions.Release);
      rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Cyan), "floor");
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(1, rig.FloorClient.Frames.Count);
      Assert.AreEqual(1, rig.FloorClient.Count("arm:False"));
      Assert.AreEqual("Manual", (await rig.Snapshot()).Mode);
   }

   [TestMethod]
   public async Task FailedDisarmKeepsUncertaintyVisibleAndSupportsRetry()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.Arm);
      rig.FloorClient.FailDisarm = true;
      await rig.Error(BridgeIpcActions.Disarm);
      var uncertain = await rig.Snapshot();
      Assert.AreEqual("Pc", uncertain.Mode);
      Assert.IsTrue(uncertain.RazerArmed);
      await rig.Error(BridgeIpcActions.SetSolidColor, """{"color":{"r":1,"g":2,"b":3}}""");
      await rig.Error(BridgeIpcActions.Arm);
      rig.FloorClient.FailDisarm = false;
      var recovered = await rig.Ok(BridgeIpcActions.Disarm);
      Assert.IsFalse(recovered.RazerArmed);
      Assert.AreEqual("Pc", recovered.Mode);
   }

   [TestMethod]
   public async Task SetPowerRequiresOwnershipAndTracksExplicitIntent()
   {
      using var rig = new LampTestHarness();
      await rig.Error(BridgeIpcActions.SetPower, """{"isOn":true}""");
      await rig.Ok(BridgeIpcActions.Resume);
      Assert.AreEqual(true, (await rig.Ok(BridgeIpcActions.SetPower, """{"isOn":true}""")).RequestedPowerOn);
      Assert.AreEqual(false, (await rig.Ok(BridgeIpcActions.SetPower, """{"isOn":false}""")).RequestedPowerOn);
      Assert.AreEqual(1, rig.FloorClient.Count("power:True"));
      Assert.AreEqual(1, rig.FloorClient.Count("power:False"));
   }

   [TestMethod]
   public async Task BrightnessZeroMeansPowerOffAndNeverPhysicalBrightnessZero()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.SetBrightness, """{"brightnessPercent":1}""");
      await rig.Ok(BridgeIpcActions.SetBrightness, """{"brightnessPercent":100}""");
      var off = await rig.Ok(BridgeIpcActions.SetBrightness, """{"brightnessPercent":0}""");
      Assert.AreEqual(false, off.RequestedPowerOn);
      CollectionAssert.AreEqual(new[] { "brightness:1", "brightness:100" },
         rig.FloorClient.Calls.Where(c => c.StartsWith("brightness:", StringComparison.Ordinal)).ToArray());
      Assert.AreEqual(1, rig.FloorClient.Count("power:False"));
   }

   [TestMethod]
   [DataRow(-1)]
   [DataRow(101)]
   [DataRow(10000)]
   public async Task InvalidBrightnessIsReportedAsErrorWithoutPhysicalSend(int brightness)
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      var response = await rig.Error(BridgeIpcActions.SetBrightness,
         $"{{\"brightnessPercent\":{brightness}}}");
      StringAssert.Contains(response.Error, "Brightness");
      Assert.AreEqual(0, rig.FloorClient.CountPrefix("brightness:"));
   }

   [TestMethod]
   public async Task SolidColorIsPcDirectOnlyAndPreservesRgbComponents()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      var state = await rig.Ok(BridgeIpcActions.SetSolidColor,
         """{"color":{"r":0,"g":255,"b":255}}""");
      Assert.AreEqual("#00FFFF", state.LastStaticColor);
      CollectionAssert.AreEqual(new[] { LampTestHarness.Cyan }, rig.FloorClient.Colors);
      await rig.Ok(BridgeIpcActions.Arm);
      await rig.Error(BridgeIpcActions.SetSolidColor,
         """{"color":{"r":255,"g":0,"b":0}}""");
      Assert.AreEqual(1, rig.FloorClient.Colors.Count);
   }

   [TestMethod]
   public async Task ColorTemperatureChangesModeAndCancelsPendingStaticTarget()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Ok(BridgeIpcActions.SetSolidColor,
         """{"color":{"r":255,"g":0,"b":0}}""");
      Assert.IsNotNull((await rig.Snapshot()).LastStaticColor);
      await rig.Ok(BridgeIpcActions.SetColorTemperature, """{"kelvin":3500}""");
      Assert.IsNull((await rig.Snapshot()).LastStaticColor);
      Assert.AreEqual(1, rig.FloorClient.Count("kelvin:3500"));
   }

   [TestMethod]
   [DataRow(1999)]
   [DataRow(9001)]
   [DataRow(0)]
   public async Task InvalidKelvinIsReportedAsClientError(int kelvin)
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      var error = await rig.Error(BridgeIpcActions.SetColorTemperature, $"{{\"kelvin\":{kelvin}}}");
      StringAssert.Contains(error.Error, "temperature");
      Assert.AreEqual(0, rig.FloorClient.CountPrefix("kelvin:"));
   }

   [TestMethod]
   public async Task MissingPayloadAndUnknownCommandsReturnExplicitErrors()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      StringAssert.Contains((await rig.Error(BridgeIpcActions.SetPower)).Error, "payload");
      StringAssert.Contains((await rig.Error("turn-lamp-into-a-rocket")).Error, "Unknown");
      await rig.Error(BridgeIpcActions.SetBrightness, """{"brightnessPercent":"bright"}""");
      await rig.Error(BridgeIpcActions.SetSolidColor, """{"color":{"r":300,"g":0,"b":0}}""");
      Assert.AreEqual(0, rig.FloorClient.CountPrefix("brightness:"));
   }

   [TestMethod]
   public async Task NullRgbPayloadShouldBeExplainedAsInvalidInput()
   {
      using var rig = new LampTestHarness();
      await rig.Ok(BridgeIpcActions.Resume);
      var error = await rig.Error(BridgeIpcActions.SetSolidColor, """{"color":null}""");
      Assert.IsTrue(error.Error.Contains("color", StringComparison.OrdinalIgnoreCase) ||
         error.Error.Contains("payload", StringComparison.OrdinalIgnoreCase),
         "Malformed user input must be distinguished from a device/internal fault.");
   }

   [TestMethod]
   public async Task CapabilityFlagsBlockUnsupportedActionsWithoutSendingAnything()
   {
      using var rig = new LampTestHarness(configureSettings: settings =>
      {
         var cap = settings.Lamps[0].Capabilities;
         cap.SupportsBrightness = false;
         cap.SupportsPower = false;
         cap.SupportsRgb = false;
         cap.SupportsColorTemperature = false;
      });
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Error(BridgeIpcActions.SetPower, """{"isOn":true}""");
      await rig.Error(BridgeIpcActions.SetBrightness, """{"brightnessPercent":50}""");
      await rig.Error(BridgeIpcActions.SetSolidColor, """{"color":{"r":1,"g":2,"b":3}}""");
      await rig.Error(BridgeIpcActions.SetColorTemperature, """{"kelvin":3500}""");
      Assert.AreEqual(0, rig.FloorClient.Calls.Count);
   }

   [TestMethod]
   public async Task HardwareSubmissionFailureDoesNotPretendAColorWasApplied()
   {
      using var rig = new LampTestHarness();
      rig.FloorClient.FailColor = true;
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Error(BridgeIpcActions.SetSolidColor,
         """{"color":{"r":255,"g":0,"b":0}}""");
      Assert.AreEqual(0, rig.FloorClient.Colors.Count);
      Assert.AreEqual(0L, (await rig.Snapshot()).FramesSent);
   }

   [TestMethod]
   public async Task DifferentLampIdsHaveIndependentOwnershipAndCapabilities()
   {
      using var rig = new LampTestHarness(twoLamps: true);
      await rig.Ok(BridgeIpcActions.Resume, lampId: "floor");
      Assert.AreEqual("Manual", (await rig.Snapshot("desk")).Mode);
      await rig.Ok(BridgeIpcActions.Resume, lampId: "desk");
      await rig.Ok(BridgeIpcActions.SetPower, """{"isOn":false}""", "desk");
      Assert.AreEqual(0, rig.FloorClient.Count("power:False"));
      Assert.AreEqual(1, rig.DeskClient.Count("power:False"));
      await rig.Ok(BridgeIpcActions.Release, lampId: "floor");
      Assert.AreEqual("Pc", (await rig.Snapshot("desk")).Mode);
   }

   [TestMethod]
   public async Task UnknownOrAbsentLampIdNeverDefaultsToAnotherLamp()
   {
      using var rig = new LampTestHarness();
      await rig.Error(BridgeIpcActions.GetHostStatus, lampId: "missing");
      await rig.Error(BridgeIpcActions.GetHostStatus, lampId: "");
      await rig.Error(BridgeIpcActions.GetHostStatus, lampId: null!);
      Assert.AreEqual("Manual", (await rig.Snapshot()).Mode);
   }

   [TestMethod]
   public async Task DiagnosticModeAcceptsFramesButNeverSimulatesHardware()
   {
      using var rig = new LampTestHarness(diagnostic: true);
      Assert.IsTrue(rig.Runtime.ReceiveFrame(LampTestHarness.Solid(LampTestHarness.Red), "floor"));
      await rig.Runtime.PumpFrameAsync(CancellationToken.None);
      Assert.AreEqual(1L, (await rig.Snapshot()).FramesReceived);
      Assert.AreEqual(0L, (await rig.Snapshot()).FramesSent);
      await rig.Ok(BridgeIpcActions.Resume);
      await rig.Error(BridgeIpcActions.Arm);
      await rig.Error(BridgeIpcActions.GetLampStatus);
      Assert.AreEqual(0, rig.FloorClient.Calls.Count);
   }
}
