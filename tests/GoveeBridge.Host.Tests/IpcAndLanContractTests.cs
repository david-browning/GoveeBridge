using System.Text;
using System.Text.Json;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using GoveeBridge.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

/// <summary>
/// The wire formats shared with GoveeHost.psm1 and the raw GoveeLan PowerShell
/// module. No sockets or lamp access: these tests assert stable public contracts.
/// </summary>
[TestClass]
public sealed class IpcAndLanContractTests
{
   private static JsonDocument IpcJson(object request) =>
      JsonDocument.Parse(JsonSerializer.Serialize(request, request.GetType()));

   private static JsonDocument LanJson(string json) => JsonDocument.Parse(json);

   [TestMethod]
   [DataRow("status")]
   [DataRow("lamp-status")]
   [DataRow("resume")]
   [DataRow("release")]
   [DataRow("arm")]
   [DataRow("disarm")]
   public void ManagementRequestFactoriesPreserveActionsAndTarget(string action)
   {
      object request = action switch
      {
         "status" => BridgeIpcRequests.Status("floor"),
         "lamp-status" => BridgeIpcRequests.LampStatus("floor"),
         "resume" => BridgeIpcRequests.Resume("floor"),
         "release" => BridgeIpcRequests.Release("floor"),
         "arm" => BridgeIpcRequests.Arm("floor"),
         "disarm" => BridgeIpcRequests.Disarm("floor"),
         _ => throw new InvalidOperationException()
      };
      using var json = IpcJson(request);
      var root = json.RootElement;
      Assert.AreEqual("command", root.GetProperty("type").GetString());
      Assert.AreEqual(action, root.GetProperty("action").GetString());
      Assert.AreEqual("floor", root.GetProperty("lampId").GetString());
      Assert.IsFalse(root.TryGetProperty("data", out _));
   }

   [TestMethod]
   public void PowerBrightnessRgbAndTemperatureRequestsUseNamedJsonFields()
   {
      object[] requests =
      [
         BridgeIpcRequests.SetPower(false, "floor"),
         BridgeIpcRequests.SetBrightness(42, "floor"),
         BridgeIpcRequests.SetColor(new RgbColor(0, 255, 255), "floor"),
         BridgeIpcRequests.SetColorTemperature(3500, "floor")
      ];
      string[] expectedActions = ["set-power", "set-brightness", "set-color", "set-color-temperature"];
      for (int index = 0; index < requests.Length; index++)
      {
         using var json = IpcJson(requests[index]);
         Assert.AreEqual("command", json.RootElement.GetProperty("type").GetString());
         Assert.AreEqual("floor", json.RootElement.GetProperty("lampId").GetString());
         Assert.AreEqual(expectedActions[index], json.RootElement.GetProperty("action").GetString());
      }
      using var power = IpcJson(requests[0]);
      Assert.IsFalse(power.RootElement.GetProperty("data").GetProperty("isOn").GetBoolean());
      using var brightness = IpcJson(requests[1]);
      Assert.AreEqual(42, brightness.RootElement.GetProperty("data").GetProperty("brightnessPercent").GetInt32());
      using var color = IpcJson(requests[2]);
      var rgb = color.RootElement.GetProperty("data").GetProperty("color");
      Assert.AreEqual(0, rgb.GetProperty("r").GetInt32());
      Assert.AreEqual(255, rgb.GetProperty("g").GetInt32());
      Assert.AreEqual(255, rgb.GetProperty("b").GetInt32());
      using var temperature = IpcJson(requests[3]);
      Assert.AreEqual(3500, temperature.RootElement.GetProperty("data").GetProperty("kelvin").GetInt32());
   }

   [TestMethod]
   public void FrameIsFlatRgbIntegersAndIncludesRequiredLampId()
   {
      var source = new List<RgbColor> { LampTestHarness.Red, LampTestHarness.Cyan };
      var request = BridgeIpcRequests.SendFrame(new RgbFrame(source), "floor");
      source.Clear(); // Outbound DTO must own a copy.
      using var json = IpcJson(request);
      var root = json.RootElement;
      Assert.AreEqual("rgb-frame", root.GetProperty("type").GetString());
      Assert.AreEqual("floor", root.GetProperty("lampId").GetString());
      Assert.AreEqual(2, root.GetProperty("count").GetInt32());
      CollectionAssert.AreEqual(new[] { 255, 0, 0, 0, 255, 255 },
         root.GetProperty("colors").EnumerateArray().Select(v => v.GetInt32()).ToArray());
   }

   [TestMethod]
   public void MissingLampIdIsRejectedByEveryOutboundFactory()
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
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.SetColor(LampTestHarness.Red, lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.SetColorTemperature(3500, lampId!));
         Assert.Throws<ArgumentException>(() => BridgeIpcRequests.SendFrame(
            LampTestHarness.Solid(LampTestHarness.Red), lampId!));
      }
   }

   [TestMethod]
   [DataRow("{\"type\":\"rgb-frame\",\"count\":1,\"colors\":[1,2,3]}")]
   [DataRow("{\"type\":\"rgb-frame\",\"lampId\":\"\",\"count\":1,\"colors\":[1,2,3]}")]
   [DataRow("{\"type\":\"rgb-frame\",\"lampId\":\"floor\",\"count\":2,\"colors\":[1,2,3]}")]
   [DataRow("{\"type\":\"rgb-frame\",\"lampId\":\"floor\",\"count\":1,\"colors\":[-1,2,3]}")]
   [DataRow("{\"type\":\"rgb-frame\",\"lampId\":\"floor\",\"count\":1,\"colors\":[256,2,3]}")]
   [DataRow("{\"type\":\"rgb-frame\",\"lampId\":\"floor\",\"count\":1,\"colors\":[1.5,2,3]}")]
   [DataRow("{\"type\":\"command\",\"lampId\":\"floor\",\"count\":1,\"colors\":[1,2,3]}")]
   public void MalformedFrameDatagramsCannotBecomeValidFrames(string payload)
   {
      Assert.IsFalse(GoveeHelpers.TryParseFrame(Encoding.UTF8.GetBytes(payload),
         8, out _, out _));
   }

   [TestMethod]
   public void InboundFrameParserPreservesLogicalColorOrder()
   {
      const string payload = """{"type":"rgb-frame","lampId":"floor","count":2,"colors":[255,0,0,0,255,255]}""";
      Assert.IsTrue(GoveeHelpers.TryParseFrame(Encoding.UTF8.GetBytes(payload),
         8, out RgbFrame? frame, out string? lampId));
      Assert.AreEqual("floor", lampId);
      Assert.AreEqual(LampTestHarness.Red, frame!.Colors[0]);
      Assert.AreEqual(LampTestHarness.Cyan, frame.Colors[1]);
   }

   [TestMethod]
   public void CoreFrameFactoryShouldNotAdvertiseMoreThanOneByteRazerCanSend()
   {
      var frame = LampTestHarness.Solid(LampTestHarness.Red, 256);
      Assert.Throws<ArgumentException>(() => BridgeIpcRequests.SendFrame(frame, "floor"));
   }

   [TestMethod]
   public void DirectLanPowerAndBrightnessPacketsUseExpectedCommands()
   {
      using var off = LanJson(GoveeLanPackets.Power(false));
      Assert.AreEqual("turn", off.RootElement.GetProperty("msg").GetProperty("cmd").GetString());
      Assert.AreEqual(0, off.RootElement.GetProperty("msg").GetProperty("data").GetProperty("value").GetInt32());
      using var on = LanJson(GoveeLanPackets.Power(true));
      Assert.AreEqual(1, on.RootElement.GetProperty("msg").GetProperty("data").GetProperty("value").GetInt32());
      using var brightness = LanJson(GoveeLanPackets.Brightness(73));
      Assert.AreEqual("brightness", brightness.RootElement.GetProperty("msg").GetProperty("cmd").GetString());
      Assert.AreEqual(73, brightness.RootElement.GetProperty("msg").GetProperty("data").GetProperty("value").GetInt32());
   }

   [TestMethod]
   public void DirectLanSolidRgbAndWhiteKelvinSelectDifferentPayloadModes()
   {
      using var rgb = LanJson(GoveeLanPackets.Color(LampTestHarness.Cyan));
      var rgbMsg = rgb.RootElement.GetProperty("msg");
      Assert.AreEqual("colorwc", rgbMsg.GetProperty("cmd").GetString());
      var rgbData = rgbMsg.GetProperty("data");
      Assert.AreEqual(0, rgbData.GetProperty("colorTemInKelvin").GetInt32());
      Assert.AreEqual(255, rgbData.GetProperty("color").GetProperty("g").GetInt32());
      using var kelvin = LanJson(GoveeLanPackets.ColorTemperature(6500));
      var kelvinData = kelvin.RootElement.GetProperty("msg").GetProperty("data");
      Assert.AreEqual(6500, kelvinData.GetProperty("colorTemInKelvin").GetInt32());
      Assert.AreEqual(0, kelvinData.GetProperty("color").GetProperty("g").GetInt32());
   }

   [TestMethod]
   public void RazerArmDisarmAndFrameHaveCorrectLengthOpcodeAndChecksum()
   {
      static byte[] Binary(string json)
      {
         using var document = JsonDocument.Parse(json);
         var msg = document.RootElement.GetProperty("msg");
         Assert.AreEqual("razer", msg.GetProperty("cmd").GetString());
         return Convert.FromBase64String(msg.GetProperty("data").GetProperty("pt").GetString()!);
      }
      static void Verify(byte[] packet, int opcode, int length)
      {
         Assert.AreEqual(0xBB, packet[0]);
         Assert.AreEqual(length, (packet[1] << 8) | packet[2]);
         Assert.AreEqual(opcode, packet[3]);
         int xor = 0;
         foreach (byte value in packet) xor ^= value;
         Assert.AreEqual(0, xor, "XOR checksum must make the complete packet checksum zero.");
      }
      byte[] arm = Binary(GoveeLanPackets.RazerEnabled(true));
      Verify(arm, 0xB1, 1);
      Assert.AreEqual(1, arm[4]);
      byte[] disarm = Binary(GoveeLanPackets.RazerEnabled(false));
      Verify(disarm, 0xB1, 1);
      Assert.AreEqual(0, disarm[4]);
      byte[] frame = Binary(GoveeLanPackets.RazerFrame(new RgbFrame(
         [LampTestHarness.Red, LampTestHarness.Cyan]), gradient: true));
      Verify(frame, 0xB0, 8);
      CollectionAssert.AreEqual(new byte[] { 1, 2, 255, 0, 0, 0, 255, 255 },
         frame.Skip(4).Take(8).ToArray());
   }

   [TestMethod]
   public void BrightnessPacketRejectsOutOfRangeValues()
   {
      foreach (int value in new[] { -1, 0, 101, int.MaxValue })
      {
         Assert.Throws<LampParameterException>(
             () => GoveeLanPackets.Brightness(value));
      }

      Assert.IsNotNull(GoveeLanPackets.Brightness(1));
      Assert.IsNotNull(GoveeLanPackets.Brightness(100));
   }

   [TestMethod]
   public void ValidDirectLanStatusReplyIsDecoded()
   {
      const string json = """{"msg":{"cmd":"devStatus","data":{"onOff":1,"brightness":67,"color":{"r":0,"g":255,"b":255},"colorTemInKelvin":0}}}""";
      Assert.IsTrue(GoveeLanPackets.TryReadStatus(Encoding.UTF8.GetBytes(json), out var status));
      Assert.IsNotNull(status);
      Assert.IsTrue(status.IsOn);
      Assert.AreEqual(67, status.Brightness);
      Assert.AreEqual(LampTestHarness.Cyan, status.Color);
   }

   [TestMethod]
   [DataRow(-1)]
   [DataRow(101)]
   [DataRow(255)]
   public void DirectLanStatusShouldRejectImpossibleBrightness(int brightness)
   {
      string json = $$"""
       {
           "msg": {
               "cmd": "devStatus",
               "data": {
                   "onOff": 1,
                   "brightness": {{brightness}},
                   "color": { "r": 255, "g": 0, "b": 0 },
                   "colorTemInKelvin": 0
               }
           }
       }
       """;

      bool accepted = GoveeLanPackets.TryReadStatus(
          Encoding.UTF8.GetBytes(json), out GoveeDeviceStatus? status);
      Assert.IsFalse(accepted);
      Assert.IsNull(status);
   }

   [TestMethod]
   public void LanStatusRejectsWrongCommandAndInvalidPower()
   {
      const string badPower = """{"msg":{"cmd":"devStatus","data":{"onOff":3,"brightness":50,"color":{"r":1,"g":2,"b":3}}}}""";
      const string wrongCommand = """{"msg":{"cmd":"turn","data":{"onOff":1,"brightness":50,"color":{"r":1,"g":2,"b":3}}}}""";
      Assert.IsFalse(GoveeLanPackets.TryReadStatus(Encoding.UTF8.GetBytes(badPower), out _));
      Assert.IsFalse(GoveeLanPackets.TryReadStatus(Encoding.UTF8.GetBytes(wrongCommand), out _));
   }
}
