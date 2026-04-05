using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XTransferTool.Discovery;

public sealed class UdpDiscoveryListener : IDiscoveryUdpListener
{
    private const int Port = 37020;
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event EventHandler<UdpAnnounceReceived>? AnnounceReceived;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_client is not null)
            return Task.CompletedTask;

        _client = new UdpClient(new IPEndPoint(IPAddress.Any, Port))
        {
            EnableBroadcast = true
        };

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = LoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* ignore */ }
        }

        _client?.Dispose();
        _client = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        if (_client is null)
            return;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await _client.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { continue; }

            try
            {
                var msg = JsonSerializer.Deserialize<UdpAnnounce>(res.Buffer);
                if (msg is null || msg.Type != "announce")
                    continue;

                // Basic replay window check ±30s
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (Math.Abs(now - msg.Ts) > 30)
                    continue;

                AnnounceReceived?.Invoke(this, new UdpAnnounceReceived(msg, res.RemoteEndPoint));
            }
            catch
            {
                // ignore bad packets
            }
        }
    }
}

