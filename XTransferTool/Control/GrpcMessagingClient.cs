using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using XTransferTool.Control.Proto;

namespace XTransferTool.Control;

public sealed class GrpcMessagingClient
{
    public async Task<SendMessageResponse> SendAsync(string address, int port, SendMessageRequest request, CancellationToken ct = default)
    {
        using var channel = GrpcChannel.ForAddress($"http://{address}:{port}");
        var client = new MessagingService.MessagingServiceClient(channel);
        return await client.SendMessageAsync(request, cancellationToken: ct);
    }
}

