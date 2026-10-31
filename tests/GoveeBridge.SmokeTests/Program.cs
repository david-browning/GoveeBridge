using System.Text;
using GoveeBridge.Core;
using GoveeBridge.Protocol;

namespace GoveeBridge.SmokeTests;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var frame = Encoding.UTF8.GetBytes("{\"type\":\"rgb-frame\",\"count\":2,\"colors\":[10,20,30,30,40,50]}");
            Require(BridgeWireProtocol.TryParseFrame(frame, 8, out RgbFrame? parsed) && parsed is not null, "Parse frame");
            Require(BridgeWireProtocol.AverageColor(parsed!) == new RgbColor(20, 30, 40), "Average frame");
            Require(!BridgeWireProtocol.TryParseFrame(Encoding.UTF8.GetBytes("{\"type\":\"rgb-frame\",\"count\":1,\"colors\":[999,0,0]}"), 8, out _), "Reject out-of-range color");
            Require(GoveeLanPackets.Power(false).Contains("\"value\":0"), "Power packet");
            Require(GoveeLanPackets.Color(new RgbColor(255, 128, 0)).Contains("\"colorTemInKelvin\":0"), "Color packet");
            const string sample = "{\"msg\":{\"cmd\":\"devStatus\",\"data\":{\"onOff\":1,\"brightness\":75,\"color\":{\"r\":1,\"g\":2,\"b\":3},\"colorTemInKelvin\":0}}}";
            Require(GoveeLanPackets.TryReadStatus(Encoding.UTF8.GetBytes(sample), out var status) && status!.IsOn && status.Color.B == 3, "Status packet");
            Console.WriteLine("All offline smoke checks passed.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
