using System.Threading;
using System.Threading.Tasks;

namespace XTransferTool.Discovery;

public interface IPeerResolver
{
    Task<ResolvedPeer?> TryResolveAsync(DiscoveredPeerEvent discovered, CancellationToken ct = default);
}

