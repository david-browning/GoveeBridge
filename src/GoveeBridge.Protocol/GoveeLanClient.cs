using System.Net;
using System.Net.Sockets;
using System.Text;
using GoveeBridge.Core;

namespace GoveeBridge.Protocol;

/// <summary>One owner for UDP 4002, which Govee uses as a fixed reply port.</summary>
public sealed class GoveeLanClient : IDisposable
{
    public GoveeLanClient(string ip, int timeoutMs)
    {
        if (!IPAddress.TryParse(ip, out IPAddress? address))
        {
            throw new ArgumentException("Invalid Govee lamp IP.", nameof(ip));
        }

        _lamp = new IPEndPoint(address, 4003);
        _timeoutMs = timeoutMs;
        
        // Fails deliberately if iCUE or the original Govee add-on still owns 4002.
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 4002));
    }

    public async Task SetPowerAsync(bool on, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await SendUnsafeAsync(GoveeLanPackets.Power(on), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetColorAsync(RgbColor color, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await SendUnsafeAsync(GoveeLanPackets.Color(color), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GoveeStatus?> GetStatusAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeoutMs);
            await SendUnsafeAsync(GoveeLanPackets.StatusRequest(), timeout.Token);

            while (true)
            {
                UdpReceiveResult response = await _udp.ReceiveAsync(timeout.Token);
                if (!response.RemoteEndPoint.Address.Equals(_lamp.Address))
                {
                    continue;
                }

                if (GoveeLanPackets.TryReadStatus(response.Buffer, out GoveeStatus? status))
                {
                    return status;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally 
        {
            _gate.Release(); 
        }
    }

    private async Task SendUnsafeAsync(string json, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _udp.SendAsync(bytes.AsMemory(), _lamp, ct);
    }

    public void Dispose()
    {
        _udp.Dispose();
        _gate.Dispose();
    }

    private readonly UdpClient _udp;
    private readonly IPEndPoint _lamp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _timeoutMs;
}
