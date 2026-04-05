using System;
using System.Threading;
using System.Threading.Tasks;

namespace XTransferTool.Discovery;

public interface IDiscoveryAnnouncer : IAsyncDisposable
{
    Task StartAsync(DeviceIdentity identity, int controlPort, CancellationToken ct = default);
    Task UpdateIdentityAsync(DeviceIdentity identity, int controlPort, CancellationToken ct = default);
    Task StopAsync();
}

