using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using XTransferTool.Control.Proto;

namespace XTransferTool.Control;

public sealed class MessagingServiceImpl : MessagingService.MessagingServiceBase
{
    private readonly InMemoryEventBus _bus;
    private static readonly ConcurrentDictionary<string, string> Dedup = new(); // requestId -> serverMessageId

    public MessagingServiceImpl(InMemoryEventBus bus)
    {
        _bus = bus;
    }

    public override Task<SendMessageResponse> SendMessage(SendMessageRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "requestId required"));
        if (string.IsNullOrWhiteSpace(request.FromId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "fromId required"));
        if (request.ToCase == SendMessageRequest.ToOneofCase.None)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "to required"));
        if (request.Text is { Length: > 500 })
            throw new RpcException(new Status(StatusCode.InvalidArgument, "text too long"));

        var deduped = Dedup.TryGetValue(request.RequestId, out var existingId);
        var serverMessageId = existingId ?? Guid.NewGuid().ToString();
        Dedup.TryAdd(request.RequestId, serverMessageId);

        // V1 routing: only supports direct toPeerId by mapping to a sessionId of same value (demo).
        // Proper routing requires peer directory + active sessions (Phase 2->3 integration).
        var targetSessionId = request.ToCase switch
        {
            SendMessageRequest.ToOneofCase.ToPeerId => request.ToPeerId,
            SendMessageRequest.ToOneofCase.ToTagPath => "broadcast:" + string.Join("/", request.ToTagPath.Segments),
            _ => "unknown"
        };

        var inboxEvent = new MessageReceivedEvent
        {
            ServerMessageId = serverMessageId,
            FromId = request.FromId,
            FromDisplay = request.FromId,
            Text = request.Text ?? string.Empty,
            TransferId = request.TransferId ?? string.Empty,
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        inboxEvent.FileNames.AddRange(request.FileNames);

        _bus.Publish(targetSessionId, "inbox", inboxEvent.ToByteArray());

        return Task.FromResult(new SendMessageResponse
        {
            Accepted = true,
            ServerMessageId = serverMessageId,
            Deduped = deduped
        });
    }
}

