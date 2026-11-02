using System.Text.Json;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.IpcContractTests;

/// <summary>
/// Verifies the application-facing IPC wire contract. These tests are offline:
/// no Host process, UDP socket, or physical lamp is required.
///
/// Compare parsed JSON rather than strings so insignificant whitespace or
/// property ordering does not cause a false failure.
/// </summary>
[TestClass]
[TestCategory("IPC Contract")]
public sealed class BridgeIpcRequestTests
{
   [TestMethod]
   public void Status_UsesExistingAction()
   {
      AssertJson(BridgeIpcRequests.Status(),
         """{"type":"command","action":"status"}""");
   }

   [TestMethod]
   public void LampStatus_UsesExistingAction()
   {
      AssertJson(BridgeIpcRequests.LampStatus(),
         """{"type":"command","action":"lamp-status"}""");
   }

   [TestMethod]
   public void Resume_UsesExistingAction()
   {
      AssertJson(BridgeIpcRequests.Resume(),
         """{"type":"command","action":"resume"}""");
   }

   [TestMethod]
   public void Release_UsesExistingAction()
   {
      AssertJson(BridgeIpcRequests.Release(),
         """{"type":"command","action":"release"}""");
   }

   [TestMethod]
   [DataRow(true)]
   [DataRow(false)]
   public void SetPower_SerializesBooleanPayload(bool isOn)
   {
      string expected = isOn
         ? """{"type":"command","action":"set-power","data":{"isOn":true}}"""
         : """{"type":"command","action":"set-power","data":{"isOn":false}}""";

      AssertJson(BridgeIpcRequests.SetPower(isOn), expected);
   }

   [TestMethod]
   [DataRow(0, """{"type":"command","action":"set-brightness","data":{"brightnessPercent":0}}""")]
   [DataRow(60, """{"type":"command","action":"set-brightness","data":{"brightnessPercent":60}}""")]
   [DataRow(100, """{"type":"command","action":"set-brightness","data":{"brightnessPercent":100}}""")]
   public void SetBrightness_SerializesSupportedPercentages(int percentage, string expectedJson)
   {
      AssertJson(BridgeIpcRequests.SetBrightness(percentage), expectedJson);
   }

   [TestMethod]
   [DataRow(-1)]
   [DataRow(101)]
   public void SetBrightness_RejectsOutOfRangeValues(int percentage)
   {
      Assert.ThrowsExactly<ArgumentOutOfRangeException>(
         () => BridgeIpcRequests.SetBrightness(percentage));
   }

   [TestMethod]
   public void SetColor_SerializesSolidRgbComponents()
   {
      AssertJson(BridgeIpcRequests.SetColor(new RgbColor(255, 128, 0)),
         """{"type":"command","action":"set-color","data":{"color":{"r":255,"g":128,"b":0}}}""");
   }

   [TestMethod]
   public void SetColorTemperature_SerializesKelvin()
   {
      AssertJson(BridgeIpcRequests.SetColorTemperature(3500),
         """{"type":"command","action":"set-color-temperature","data":{"kelvin":3500}}""");
   }

   [TestMethod]
   [DataRow(0)]
   [DataRow(-100)]
   public void SetColorTemperature_RejectsNonPositiveKelvin(int kelvin)
   {
      Assert.ThrowsExactly<ArgumentOutOfRangeException>(
         () => BridgeIpcRequests.SetColorTemperature(kelvin));
   }

   [TestMethod]
   public void SendFrame_PreservesLegacyFlatRgbFormat()
   {
      var frame = new RgbFrame([
         new RgbColor(255, 0, 0),
         new RgbColor(0, 0, 255)
      ]);

      AssertJson(BridgeIpcRequests.SendFrame(frame),
         """{"type":"rgb-frame","count":2,"colors":[255,0,0,0,0,255]}""");
   }

   [TestMethod]
   public void SendFrame_SerializesColorsAsJsonNumbers_NotBase64()
   {
      string json = JsonSerializer.Serialize(BridgeIpcRequests.SendFrame(
         new RgbFrame([new RgbColor(1, 2, 3)])));
      using JsonDocument document = JsonDocument.Parse(json);

      JsonElement colors = document.RootElement.GetProperty(BridgeIpcPropertyNames.Colors);
      Assert.AreEqual(JsonValueKind.Array, colors.ValueKind);
      Assert.AreEqual(3, colors.GetArrayLength());
      Assert.AreEqual(1, colors[0].GetInt32());
      Assert.AreEqual(2, colors[1].GetInt32());
      Assert.AreEqual(3, colors[2].GetInt32());
   }

   [TestMethod]
   public void SendFrame_CreatesSnapshotOfCallerColors()
   {
      // A caller may reuse a mutable buffer for the next animation frame.
      // The already-created IPC request must not change underneath it.
      var colors = new List<RgbColor> { new(255, 0, 0) };
      BridgeIpcFrameRequest request = BridgeIpcRequests.SendFrame(new RgbFrame(colors));

      colors[0] = new RgbColor(0, 255, 0);
      AssertJson(request,
         """{"type":"rgb-frame","count":1,"colors":[255,0,0]}""");
   }

   [TestMethod]
   public void SendFrame_AcceptsMaximumCoreFrameCount()
   {
      var colors = Enumerable.Repeat(new RgbColor(1, 2, 3), 256).ToArray();
      BridgeIpcFrameRequest request = BridgeIpcRequests.SendFrame(new RgbFrame(colors));

      Assert.AreEqual(256, request.Count);
      Assert.AreEqual(256 * 3, request.Colors.Count);
   }

   [TestMethod]
   public void SendFrame_RejectsNullFrame()
   {
      Assert.ThrowsExactly<ArgumentNullException>(
         () => BridgeIpcRequests.SendFrame(null!));
   }

   [TestMethod]
   public void SendFrame_RejectsEmptyFrame()
   {
      Assert.ThrowsExactly<ArgumentException>(
         () => BridgeIpcRequests.SendFrame(new RgbFrame([])));
   }

   [TestMethod]
   public void SendFrame_RejectsMoreThan256Colors()
   {
      var colors = Enumerable.Repeat(new RgbColor(0, 0, 0), 257).ToArray();

      Assert.ThrowsExactly<ArgumentException>(
         () => BridgeIpcRequests.SendFrame(new RgbFrame(colors)));
   }

   /// <summary>
   /// Compares the observable JSON contract, not the internal DTO layout.
   /// A failed assertion includes both payloads for easier Test Explorer diagnosis.
   /// </summary>
   private static void AssertJson(object request, string expectedJson)
   {
      string actualJson = JsonSerializer.Serialize(request);
      using JsonDocument actual = JsonDocument.Parse(actualJson);
      using JsonDocument expected = JsonDocument.Parse(expectedJson);

      Assert.IsTrue(JsonElement.DeepEquals(expected.RootElement, actual.RootElement),
         $"Serialized request differs from the wire contract.\nExpected: {expectedJson}\nActual:   {actualJson}");
   }
}
