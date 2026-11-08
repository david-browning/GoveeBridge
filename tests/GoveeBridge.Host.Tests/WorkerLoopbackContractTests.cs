using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GoveeBridge.Core;
using GoveeBridge.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

/// <summary>
/// Exercises the actual BridgeWorker UDP 127.0.0.1 IPC boundary with a fake
/// device. No Govee LAN port 4002/4003 is opened; each test chooses a temporary
/// loopback port instead of using a hard-coded production IPC port.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class WorkerLoopbackContractTests
{
   private static int ReserveCandidatePort()
   {
      using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
      return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
   }

   private static async Task<JsonDocument> Exchange(
      UdpClient sender, int port, string json)
   {
      byte[] bytes = Encoding.UTF8.GetBytes(json);
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
      await sender.SendAsync(bytes.AsMemory(), new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
      UdpReceiveResult response = await sender.ReceiveAsync(timeout.Token);
      Assert.AreEqual(port, response.RemoteEndPoint.Port,
         "A management reply should originate from the Host IPC endpoint.");
      return JsonDocument.Parse(response.Buffer);
   }

   private static string Json(object request) =>
      JsonSerializer.Serialize(request, request.GetType());

   [TestMethod]
   public async Task ValidCommandGetsReplyButMissingLampSelectorIsRejected()
   {
      int port = ReserveCandidatePort();
      using var rig = new LampTestHarness(configureSettings: s => s.FrameListenPort = port,
         configureOptions: o => o.EnableLampMonitoring = false);
      using var worker = new BridgeWorker(rig.Settings, rig.Options, rig.Runtime,
         NullLogger<BridgeWorker>.Instance);
      await worker.StartAsync(CancellationToken.None);
      try
      {
         using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
         using (var bad = await Exchange(udp, port,
            """{"type":"command","action":"status"}"""))
         {
            StringAssert.Contains(bad.RootElement.GetProperty("error").GetString()!, "lampId");
         }

         using (var valid = await Exchange(udp, port, Json(BridgeIpcRequests.Status("floor"))))
         {
            Assert.AreEqual("Manual", valid.RootElement.GetProperty("mode").GetString());
            Assert.AreEqual("floor", valid.RootElement.GetProperty("lampId").GetString());
         }
      }
      finally
      {
         await worker.StopAsync(CancellationToken.None);
      }
   }

   [TestMethod]
   public async Task WorkerSurvivesMalformedDatagramAndProcessesNextCommand()
   {
      int port = ReserveCandidatePort();
      using var rig = new LampTestHarness(configureSettings: s => s.FrameListenPort = port,
         configureOptions: o => o.EnableLampMonitoring = false);
      using var worker = new BridgeWorker(rig.Settings, rig.Options, rig.Runtime,
         NullLogger<BridgeWorker>.Instance);
      await worker.StartAsync(CancellationToken.None);
      try
      {
         using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
         using (var broken = await Exchange(udp, port, "{bad-json"))
         {
            Assert.IsTrue(broken.RootElement.TryGetProperty("error", out _));
         }
         using (var resume = await Exchange(udp, port, Json(BridgeIpcRequests.Resume("floor"))))
         {
            Assert.AreEqual("Pc", resume.RootElement.GetProperty("mode").GetString());
         }
      }
      finally
      {
         await worker.StopAsync(CancellationToken.None);
      }
   }

   [TestMethod]
   public async Task WorkerRoutesTypedSetPowerAndBrightnessByExplicitLampId()
   {
      int port = ReserveCandidatePort();
      using var rig = new LampTestHarness(twoLamps: true,
         configureSettings: s => s.FrameListenPort = port,
         configureOptions: o => o.EnableLampMonitoring = false);
      using var worker = new BridgeWorker(rig.Settings, rig.Options, rig.Runtime,
         NullLogger<BridgeWorker>.Instance);
      await worker.StartAsync(CancellationToken.None);
      try
      {
         using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
         using (var resume = await Exchange(udp, port, Json(BridgeIpcRequests.Resume("desk"))))
            Assert.AreEqual("Pc", resume.RootElement.GetProperty("mode").GetString());
         using (var power = await Exchange(udp, port, Json(BridgeIpcRequests.SetPower(true, "desk"))))
            Assert.AreEqual("desk", power.RootElement.GetProperty("lampId").GetString());
         using (var brightness = await Exchange(udp, port, Json(BridgeIpcRequests.SetBrightness(42, "desk"))))
            Assert.AreEqual("Pc", brightness.RootElement.GetProperty("mode").GetString());
         Assert.AreEqual(0, rig.FloorClient.Count("power:True"));
         Assert.AreEqual(1, rig.DeskClient.Count("power:True"));
         Assert.AreEqual(1, rig.DeskClient.Count("brightness:42"));
      }
      finally
      {
         await worker.StopAsync(CancellationToken.None);
      }
   }

   [TestMethod]
   public async Task FrameUdpIsUnacknowledgedAndMailboxIsPumpedAfterExplicitArm()
   {
      int port = ReserveCandidatePort();
      using var rig = new LampTestHarness(configureSettings: s => s.FrameListenPort = port,
         configureOptions: o => o.EnableLampMonitoring = false);
      using var worker = new BridgeWorker(rig.Settings, rig.Options, rig.Runtime,
         NullLogger<BridgeWorker>.Instance);
      await worker.StartAsync(CancellationToken.None);
      try
      {
         using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
         using (var resume = await Exchange(udp, port, Json(BridgeIpcRequests.Resume("floor"))))
            Assert.AreEqual("Pc", resume.RootElement.GetProperty("mode").GetString());
         using (var arm = await Exchange(udp, port, Json(BridgeIpcRequests.Arm("floor"))))
            Assert.AreEqual("PcStreaming", arm.RootElement.GetProperty("mode").GetString());

         byte[] frame = Encoding.UTF8.GetBytes(Json(BridgeIpcRequests.SendFrame(
            LampTestHarness.Solid(LampTestHarness.Cyan), "floor")));
         await udp.SendAsync(frame.AsMemory(), new IPEndPoint(IPAddress.Loopback, port));
         using (var noAck = new CancellationTokenSource(TimeSpan.FromMilliseconds(140)))
         {
            bool frameWasAcknowledged = false;
            try { await udp.ReceiveAsync(noAck.Token); frameWasAcknowledged = true; }
            catch (OperationCanceledException) { }
            Assert.IsFalse(frameWasAcknowledged, "A raw rgb-frame is fire-and-forget, not an IPC command.");
         }
         // The Worker pumps every 50 ms. Wait only until the fake actually sees
         // a frame; do not infer rendering from UDP sending.
         var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
         while ((await rig.Snapshot()).FramesSent == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
         Assert.IsTrue(rig.FloorClient.Frames.Count >= 1);
         Assert.AreEqual(LampTestHarness.Cyan, rig.FloorClient.Frames[0].Colors[0]);

         // Frames have no command response. An ordinary status command afterward
         // should still succeed on the same request socket.
         using var snapshot = await Exchange(udp, port, Json(BridgeIpcRequests.Status("floor")));
         Assert.IsTrue(snapshot.RootElement.GetProperty("framesReceived").GetInt64() >= 1);
         Assert.IsTrue(snapshot.RootElement.GetProperty("framesSent").GetInt64() >= 1);
      }
      finally
      {
         await worker.StopAsync(CancellationToken.None);
      }
   }
}
