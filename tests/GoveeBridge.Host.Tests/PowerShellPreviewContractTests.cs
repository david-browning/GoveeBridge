using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using GoveeBridge.Core;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GoveeBridge.Host.Tests;

/// <summary>
/// OPTIONAL executable tests of the actual GoveeHost.psd1 adapter. These call
/// only -PreviewJson and -WhatIf: they never open UDP sockets or touch a lamp.
/// They require pwsh 7.6+ (Core targets .NET 10) and a built Core DLL.
/// </summary>
[TestClass]
public sealed class PowerShellPreviewContractTests
{
   private static string ModulePath()
   {
      DirectoryInfo? directory = new(AppContext.BaseDirectory);
      while (directory is not null)
      {
         string candidate = Path.Combine(directory.FullName,
            "src", "GoveeBridge.PowerShell.Host", "GoveeHost.psd1");
         if (File.Exists(candidate)) return candidate;
         directory = directory.Parent;
      }
      Assert.Inconclusive("Could not find GoveeHost.psd1. Run tests from a normal repository checkout.");
      throw new InvalidOperationException();
   }

   private static async Task<string> RunPowerShell(string body)
   {
      string coreDll = Path.Combine(AppContext.BaseDirectory, "GoveeBridge.Core.dll");
      if (!File.Exists(coreDll))
         Assert.Inconclusive("GoveeBridge.Core.dll must be built/copied to the test output directory.");
      string module = ModulePath().Replace("'", "''", StringComparison.Ordinal);
      string script = "$ErrorActionPreference='Stop'; " +
         "Import-Module -Name '" + module + "' -Force -ErrorAction Stop; " + body;
      string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
      using var process = new Process();
      process.StartInfo.FileName = "pwsh";
      process.StartInfo.ArgumentList.Add("-NoProfile");
      process.StartInfo.ArgumentList.Add("-NonInteractive");
      process.StartInfo.ArgumentList.Add("-EncodedCommand");
      process.StartInfo.ArgumentList.Add(encoded);
      process.StartInfo.RedirectStandardOutput = true;
      process.StartInfo.RedirectStandardError = true;
      process.StartInfo.UseShellExecute = false;
      process.StartInfo.CreateNoWindow = true;
      process.StartInfo.Environment["GOVEEBRIDGE_CORE_DLL"] = coreDll;
      try
      {
         process.Start();
      }
      catch (System.ComponentModel.Win32Exception)
      {
         Assert.Inconclusive("PowerShell preview tests require pwsh 7.6+ on PATH.");
         throw;
      }
      Task<string> stdout = process.StandardOutput.ReadToEndAsync();
      Task<string> stderr = process.StandardError.ReadToEndAsync();
      using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
      try
      {
         await process.WaitForExitAsync(deadline.Token);
      }
      catch (OperationCanceledException)
      {
         process.Kill(entireProcessTree: true);
         Assert.Fail("PowerShell preview subprocess exceeded 25 seconds.");
      }
      string output = await stdout;
      string errors = await stderr;
      Assert.AreEqual(0, process.ExitCode, $"PowerShell failed: {errors}\n{output}");
      // WhatIf can write informational lines to stdout. The script always
      // prints the actual result as its final JSON object.
      return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
         .Select(line => line.Trim())
         .LastOrDefault(line => line.StartsWith("{", StringComparison.Ordinal)) ?? output.Trim();
   }

   [TestMethod]
   [TestCategory("PowerShell")]
   public async Task AllPreviewableHostCmdletsUseCoreWireContracts()
   {
      const string body = """
         $requests = [ordered]@{
            status = (Get-GoveeHostStatus -LampId floor -PreviewJson | ConvertFrom-Json)
            lampStatus = (Get-GoveeHostLampStatus -LampId floor -PreviewJson | ConvertFrom-Json)
            resume = (Resume-GoveeHostControl -LampId floor -PreviewJson | ConvertFrom-Json)
            release = (Release-GoveeHostControl -LampId floor -PreviewJson | ConvertFrom-Json)
            arm = (Arm-GoveeHostStreaming -LampId floor -PreviewJson | ConvertFrom-Json)
            disarm = (Disarm-GoveeHostStreaming -LampId floor -PreviewJson | ConvertFrom-Json)
            power = (Set-GoveeHostPower -State Off -LampId floor -PreviewJson | ConvertFrom-Json)
            brightness = (Set-GoveeHostBrightness -BrightnessPercent 42 -LampId floor -PreviewJson | ConvertFrom-Json)
            color = (Set-GoveeHostColor -Color '#00FFFF' -LampId floor -PreviewJson | ConvertFrom-Json)
            kelvin = (Set-GoveeHostColorTemperature -Kelvin 3500 -LampId floor -PreviewJson | ConvertFrom-Json)
            frame = (Send-GoveeHostFrame -Colors @('#FF0000','#00FFFF') -LampId floor -PreviewJson | ConvertFrom-Json)
            solidFrame = (Send-GoveeHostSolidFrame -Color Red -LogicalLedCount 8 -LampId floor -PreviewJson | ConvertFrom-Json)
         }
         ConvertTo-Json -InputObject $requests -Depth 15 -Compress
         """;
      using var json = JsonDocument.Parse(await RunPowerShell(body));
      var root = json.RootElement;
      foreach (string key in new[] { "status", "lampStatus", "resume", "release", "arm", "disarm",
         "power", "brightness", "color", "kelvin", "frame", "solidFrame" })
      {
         Assert.AreEqual("floor", root.GetProperty(key).GetProperty("lampId").GetString(), key);
      }
      foreach (string key in new[] { "status", "lampStatus", "resume", "release", "arm", "disarm",
         "power", "brightness", "color", "kelvin" })
      {
         Assert.AreEqual("command", root.GetProperty(key).GetProperty("type").GetString(), key);
      }
      Assert.AreEqual("rgb-frame", root.GetProperty("frame").GetProperty("type").GetString());
      Assert.AreEqual(2, root.GetProperty("frame").GetProperty("count").GetInt32());
      Assert.AreEqual(8, root.GetProperty("solidFrame").GetProperty("count").GetInt32());
      Assert.AreEqual(255,
         root.GetProperty("color").GetProperty("data").GetProperty("color").GetProperty("g").GetInt32());
      Assert.IsFalse(root.GetProperty("power").GetProperty("data").GetProperty("isOn").GetBoolean());
   }

   [TestMethod]
   [TestCategory("PowerShell")]
   public async Task HostPreviewParsesNamedColorsAndRejectsInvalidComponents()
   {
      const string body = """
         $valid = (Set-GoveeHostColor -Color Amber -LampId floor -PreviewJson | ConvertFrom-Json)
         $invalid = $false
         try {
            Set-GoveeHostColor -Color @{R=300;G=0;B=0} -LampId floor -PreviewJson -ErrorAction Stop | Out-Null
         }
         catch { $invalid = $true }
         [pscustomobject]@{ amber=$valid.data.color; invalidRejected=$invalid } | ConvertTo-Json -Compress -Depth 8
         """;
      using var json = JsonDocument.Parse(await RunPowerShell(body));
      Assert.IsTrue(json.RootElement.GetProperty("invalidRejected").GetBoolean());
      Assert.AreEqual(255, json.RootElement.GetProperty("amber").GetProperty("r").GetInt32());
      Assert.AreEqual(191, json.RootElement.GetProperty("amber").GetProperty("g").GetInt32());
   }
   [TestMethod]
   [TestCategory("PowerShell")]
   public async Task DirectLanPatternValidatesZoneCountWithoutOpeningSockets()
   {
      string lanModule = Path.GetFullPath(Path.Combine(
         Path.GetDirectoryName(ModulePath())!, "..", "GoveeBridge.PowerShell", "GoveeLan.psd1"));
      if (!File.Exists(lanModule))
         Assert.Inconclusive("Could not find GoveeLan.psd1 in the repository checkout.");
      string quoted = lanModule.Replace("'", "''", StringComparison.Ordinal);
      string body = "Import-Module -Name '" + quoted + "' -Force -ErrorAction Stop; " + """
         $badCount = $false
         try {
            Set-GoveePattern -LampIp 192.0.2.10 -Colors @('#FF0000','#00FFFF') -LEDCount 3 -Seconds 1 -WhatIf -ErrorAction Stop
         } catch { $badCount = $true }
         # WhatIf is evaluated after validation/encoding, but never sends UDP.
         Set-GoveePattern -LampIp 192.0.2.10 -Colors @('#FF0000','#00FFFF') -LEDCount 2 -Seconds 1 -ArmDelayMs 0 -WhatIf | Out-Null
         [pscustomobject]@{ mismatchRejected=$badCount } | ConvertTo-Json -Compress
         """;
      using var json = JsonDocument.Parse(await RunPowerShell(body));
      Assert.IsTrue(json.RootElement.GetProperty("mismatchRejected").GetBoolean());
   }

   [TestMethod]
   [TestCategory("PowerShell")]
   public async Task ActualPowerShellAnimationAlternatesFrameColorsOnFakeUdpReceiver()
   {
      // No Govee LAN or BridgeRuntime here. Capture the real PowerShell
      // helper's outbound IPC packets on a temporary loopback port.
      using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
      int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
      var captured = new ConcurrentQueue<RgbColor>();
      var invalid = new ConcurrentQueue<string>();
      using var stop = new CancellationTokenSource();
      Task consume = Task.Run(async () =>
      {
         while (!stop.IsCancellationRequested)
         {
            UdpReceiveResult packet;
            try { packet = await receiver.ReceiveAsync(stop.Token); }
            catch (OperationCanceledException) { break; }
            if (!GoveeHelpers.TryParseFrame(packet.Buffer, 8,
               out RgbFrame? frame, out string? lampId) || lampId != "floor" || frame!.Colors.Count != 8)
            {
               invalid.Enqueue("Malformed frame or missing/wrong lampId");
               continue;
            }
            if (!frame.Colors.All(color => color == frame.Colors[0]))
            {
               invalid.Enqueue("Animation should send uniform solid-color frames");
               continue;
            }
            captured.Enqueue(frame.Colors[0]);
         }
      });
      try
      {
         string script = "Start-GoveeHostAnimation -FirstColor '#00FFFF' " +
            "-SecondColor '#FF0000' -IntervalSeconds 1 -DurationSeconds 3 " +
            "-FrameRefreshMs 100 -LogicalLedCount 8 -LampId floor -Port " + port + "; 'done'";
         await RunPowerShell(script);
      }
      finally
      {
         stop.Cancel();
         await consume;
      }
      Assert.AreEqual(0, invalid.Count, string.Join("; ", invalid));
      var changes = new List<RgbColor>();
      foreach (RgbColor color in captured)
      {
         if (changes.Count == 0 || changes[^1] != color) changes.Add(color);
      }
      CollectionAssert.AreEqual(
         new[] { new RgbColor(0, 255, 255), new RgbColor(255, 0, 0), new RgbColor(0, 255, 255) },
         changes.ToArray(),
         "PowerShell's animation helper should alternate at each requested interval.");
   }

}
