using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using XTransferTool.Control.Proto;

namespace XTransferTool.Control;

public sealed class GrpcControlChannel : IControlChannel
{
    public async Task<HandshakeResponse> HandshakeAsync(string address, int port, HandshakeRequest request, CancellationToken ct = default)
    {
        using var channel = GrpcChannel.ForAddress($"http://{address}:{port}");
        var client = new ControlService.ControlServiceClient(channel);
        return await client.HandshakeAsync(request, cancellationToken: ct);
    }

    public async IAsyncEnumerable<EventEnvelope> SubscribeEventsAsync(
        string address,
        int port,
        SubscribeEventsRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var channel = GrpcChannel.ForAddress($"http://{address}:{port}");
        var client = new ControlService.ControlServiceClient(channel);
        using var call = client.SubscribeEvents(request, cancellationToken: ct);

        while (await call.ResponseStream.MoveNext(ct))
            yield return call.ResponseStream.Current;
    }
}

