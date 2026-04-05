using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XTransferTool.Control.Proto;

namespace XTransferTool.Control;

public interface IControlChannel
{
    Task<HandshakeResponse> HandshakeAsync(string address, int port, HandshakeRequest request, CancellationToken ct = default);
    IAsyncEnumerable<EventEnvelope> SubscribeEventsAsync(string address, int port, SubscribeEventsRequest request, CancellationToken ct = default);
}

