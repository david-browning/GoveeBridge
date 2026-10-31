using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GoveeBridge.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("Usage: GoveeBridge.Cli <status|lamp-status|release|resume> [--port 41005]");
            Console.WriteLine("All commands use loopback UDP; service owns the physical lamp.");
            return 0;
        }

        string action = args[0].ToLowerInvariant();
        if (action is not ("status" or "lamp-status" or "release" or "resume"))
        {
            Console.Error.WriteLine("Unknown command: " + action);
            return 2;
        }

        int port = args.Length == 3 && args[1] == "--port" && int.TryParse(args[2], out int p) ? p : 41005;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var bridge = new IPEndPoint(IPAddress.Loopback, port);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "command", action }));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            await udp.SendAsync(payload.AsMemory(), bridge, cts.Token);
            UdpReceiveResult result = await udp.ReceiveAsync(cts.Token);
            if (!IPAddress.IsLoopback(result.RemoteEndPoint.Address) || result.RemoteEndPoint.Port != port)
            {
                return 2;
            }

            string json = Encoding.UTF8.GetString(result.Buffer);
            using JsonDocument doc = JsonDocument.Parse(json);
            Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("GoveeBridge did not reply. Is the Host running?");
            return 1;
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
