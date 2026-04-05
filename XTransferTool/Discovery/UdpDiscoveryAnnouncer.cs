using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XTransferTool.Discovery;

public sealed class UdpDiscoveryAnnouncer : IDiscoveryUdpAnnouncer
{
    private const int Port = 37020;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);

    private UdpClient? _client;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private string? _selfId;
    private UdpAnnounce? _payload;

    public Task StartAsync(DeviceIdentity identity, int controlPort, CancellationToken ct = default)
    {
        if (_client is not null)
            return Task.CompletedTask;

        _selfId = identity.Id;
        _payload = new UdpAnnounce
        {
            Id = identity.Id,
            Nickname = identity.Nickname,
            Tags = identity.Tags ?? [],
            Os = identity.Os,
            Ver = identity.Ver,
            ControlPort = controlPort,
            Cap = identity.Capabilities ?? [],
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        _client = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _timer = new PeriodicTimer(_interval);
        _ = LoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
        _client?.Dispose();
        _client = null;
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        if (_client is null || _payload is null)
            return;

        // slight jitter to avoid sync storm
        await Task.Delay(Random.Shared.Next(0, 300), ct);

        while (_timer is not null && await _timer.WaitForNextTickAsync(ct))
        {
            _payload.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_payload);

            if (bytes.Length > 1200)
            {
                // Conservative: drop extra tags to reduce size.
                _payload.Tags = _payload.Tags.Take(2).ToArray();
                bytes = JsonSerializer.SerializeToUtf8Bytes(_payload);
            }

            var endpoints = UdpNetUtil.GetBroadcastEndpoints(Port);
            foreach (var ep in endpoints)
            {
                try { await _client.SendAsync(bytes, ep, ct); }
                catch { /* ignore */ }
            }
        }
    }
}

