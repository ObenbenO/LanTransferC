using System;
using System.Threading;
using System.Threading.Tasks;

namespace XTransferTool.Discovery;

public interface IDiscoveryUdpListener : IAsyncDisposable
{
    event EventHandler<UdpAnnounceReceived>? AnnounceReceived;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}

