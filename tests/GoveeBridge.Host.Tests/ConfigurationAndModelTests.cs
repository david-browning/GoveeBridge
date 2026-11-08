using GoveeBridge.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

/// <summary>
/// Configuration safety and device-independent color models. These mirror
/// settings relied on by both the Host and the PowerShell-facing contract.
/// </summary>
[TestClass]
public sealed class ConfigurationAndModelTests
{
   private static BridgeSettings Settings(bool diagnostic = false) => new()
   {
      Diagnostic = diagnostic,
      LogicalLedCount = 8,
      Lamps =
      [
         new BridgeLampSettings
         {
            Id = "floor", Ip = "192.0.2.10",
            Capabilities = new LampCapabilities
            {
               SupportsRealtime = true, RealtimeZoneCount = 8
            }
         }
      ]
   };

   [TestMethod]
   public void ConfigurationMustExplicitlyNameAtLeastOneLamp()
   {
      Assert.Throws<ArgumentException>(() => new BridgeSettings { Diagnostic = true }.Validate());
      Assert.Throws<ArgumentException>(() => new BridgeSettings { Diagnostic = false }.Validate());
   }

   [TestMethod]
   public void DiagnosticModeDoesNotRequirePhysicalIpButStillRequiresId()
   {
      var settings = Settings(diagnostic: true);
      settings.Lamps[0].Ip = "";
      settings.Validate();
      settings.Lamps[0].Id = "";
      Assert.Throws<ArgumentException>(() => settings.Validate());
   }

   [TestMethod]
   [DataRow("")]
   [DataRow(" ")]
   [DataRow(" floor")]
   [DataRow("floor ")]
   public void InvalidLampSelectorsAreRejected(string lampId)
   {
      var settings = Settings();
      settings.Lamps[0].Id = lampId;
      Assert.Throws<ArgumentException>(() => settings.Validate());
   }

   [TestMethod]
   public void DuplicateLampIdsAreCaseInsensitive()
   {
      var settings = Settings();
      settings.Lamps.Add(new BridgeLampSettings { Id = "FLOOR", Ip = "192.0.2.11" });
      Assert.Throws<ArgumentException>(() => settings.Validate());
   }

   [TestMethod]
   public void TwoPhysicalLampsCannotShareAnIp()
   {
      var settings = Settings();
      settings.Lamps.Add(new BridgeLampSettings { Id = "desk", Ip = "192.0.2.10" });
      Assert.Throws<ArgumentException>(() => settings.Validate());
   }

   [TestMethod]
   public void PhysicalLampMustHaveAValidIpv4Address()
   {
      var settings = Settings();
      foreach (string ip in new[] { "", "example.invalid", "::1" })
      {
         settings.Lamps[0].Ip = ip;
         Assert.Throws<ArgumentException>(() => settings.Validate());
      }
      settings.Lamps[0].Ip = "192.0.2.10";
      settings.Validate();
   }

   [TestMethod]
   public void ConfiguredRealtimeZonesMustFitBothProtocolAndInputLimit()
   {
      var settings = Settings();
      settings.Lamps[0].Capabilities.RealtimeZoneCount = 256;
      Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
      settings.Lamps[0].Capabilities.RealtimeZoneCount = 9;
      Assert.Throws<ArgumentException>(() => settings.Validate());
      settings.Lamps[0].Capabilities.RealtimeZoneCount = 8;
      settings.Validate();
   }

   [TestMethod]
   public void RuntimeLimitsMustRejectImpossibleSchedulingOptions()
   {
      var options = new BridgeHostOptions
      {
         RealtimeMinimumFrameIntervalMs = 500,
         RealtimeKeepaliveIntervalMs = 250
      };
      Assert.Throws<ArgumentException>(() => options.Validate());
      options.RealtimeKeepaliveIntervalMs = 2000;
      options.Validate();
      options.FrameStaleAfterMs = 100;
      Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
   }

   [TestMethod]
   public void ColorFactoriesPreserveMutuallyExclusiveAppearanceTypes()
   {
      var solid = LightColorState.Solid(LampTestHarness.Cyan);
      Assert.AreEqual(LampTestHarness.Cyan, solid.Color);
      var frame = LampTestHarness.Solid(LampTestHarness.Red);
      var multizone = LightColorState.Zones(frame);
      Assert.AreSame(frame, multizone.Frame);
      Assert.AreEqual(3500, LightColorState.White(3500).Kelvin);
      Assert.Throws<ArgumentOutOfRangeException>(() => LightColorState.White(0));
      Assert.Throws<ArgumentException>(() => LightColorState.Zones(new RgbFrame([])));
   }

   [TestMethod]
   public void AverageColorIsExplicitUtilityNotImplicitOutputMode()
   {
      var frame = new RgbFrame([LampTestHarness.Red, LampTestHarness.Blue]);
      Assert.AreEqual(new RgbColor(128, 0, 128), GoveeHelpers.AverageColor(frame));
      Assert.AreEqual(new RgbColor(0, 0, 0), GoveeHelpers.AverageColor(new RgbFrame([])));
   }
}
