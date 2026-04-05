using System;
using System.Collections.Generic;
using System.Threading;

namespace XTransferTool.Discovery;

public interface IDiscoveryBrowser : IAsyncDisposable
{
    IAsyncEnumerable<DiscoveredPeerEvent> BrowseAsync(CancellationToken ct = default);
    void Refresh();
}

public enum DiscoveredPeerEventType
{
    PeerDiscovered,
    PeerRemoved
}

public sealed record DiscoveredPeerEvent(
    DiscoveredPeerEventType Type,
    string InstanceName,
    DateTimeOffset SeenAt,
    object? Raw = null
);

