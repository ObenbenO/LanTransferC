using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using XTransferTool.Control.Proto;

namespace XTransferTool.Remote;

public sealed class RemoteControlServiceImpl : RemoteControlService.RemoteControlServiceBase
{
    private readonly RemoteSessionStore _sessions;
    private readonly ConcurrentDictionary<string, BlockingCollection<IceCandidate>> _iceQueues = new(StringComparer.Ordinal);

    public RemoteControlServiceImpl(RemoteSessionStore sessions)
    {
        _sessions = sessions;
    }

    public override Task<CreateRemoteSessionResponse> CreateSession(CreateRemoteSessionRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.FromId) ||
            string.IsNullOrWhiteSpace(request.ToPeerId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "requestId/fromId/toPeerId required"));

        var rec = _sessions.GetOrCreate(request.RequestId, () => new RemoteSessionRecord(
            SessionId: Guid.NewGuid().ToString(),
            FromId: request.FromId,
            ToPeerId: request.ToPeerId,
            Mode: request.Mode,
            CreatedAt: DateTimeOffset.UtcNow,
            SdpOffer: "",
            SdpAnswer: ""
        ));

        _iceQueues.TryAdd(rec.SessionId, new BlockingCollection<IceCandidate>(boundedCapacity: 2048));

        return Task.FromResult(new CreateRemoteSessionResponse
        {
            Accepted = true,
            SessionId = rec.SessionId,
            Reason = ""
        });
    }

    public override Task<RemoteOfferResponse> Offer(RemoteOfferRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.FromId) || string.IsNullOrWhiteSpace(request.SdpOffer))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "sessionId/fromId/sdpOffer required"));

        if (!_sessions.TryGet(request.SessionId, out _))
            return Task.FromResult(new RemoteOfferResponse { Accepted = false, Reason = "unknown sessionId" });

        // V1 stub answer (real implementation plugs WebRTC).
        var answer = "v=0\r\n" +
                     "o=xtransfer 0 0 IN IP4 127.0.0.1\r\n" +
                     "s=xtransfer-remote\r\n" +
                     "t=0 0\r\n";

        _sessions.UpdateOfferAnswer(request.SessionId, request.SdpOffer, answer);

        return Task.FromResult(new RemoteOfferResponse
        {
            Accepted = true,
            SdpAnswer = answer,
            Reason = ""
        });
    }

    public override async Task TrickleIce(IAsyncStreamReader<IceCandidate> requestStream, IServerStreamWriter<IceCandidate> responseStream, ServerCallContext context)
    {
        // Simple relay: any received candidate is echoed back to other side via per-session queue.
        // V1: since we don't track peers, we just echo back to the same stream.
        await foreach (var cand in requestStream.ReadAllAsync(context.CancellationToken))
        {
            await responseStream.WriteAsync(cand);
        }
    }

    public override async Task InputStream(IAsyncStreamReader<RemoteInputEvent> requestStream, IServerStreamWriter<RemoteInputAck> responseStream, ServerCallContext context)
    {
        await foreach (var ev in requestStream.ReadAllAsync(context.CancellationToken))
        {
            // V1: accept all; future: authorize by session/mode and throttle.
            await responseStream.WriteAsync(new RemoteInputAck
            {
                Seq = ev.Seq,
                Accepted = true,
                Reason = ""
            });
        }
    }

    public override async Task SubscribeStats(SubscribeRemoteStatsRequest request, IServerStreamWriter<RemoteStatsEnvelope> responseStream, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "sessionId required"));

        var interval = request.IntervalMs <= 0 ? 500 : request.IntervalMs;
        interval = Math.Clamp(interval, 100, 2000); // clamp per doc: min 100ms, default 500ms

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
        while (await timer.WaitForNextTickAsync(context.CancellationToken))
        {
            var env = new RemoteStatsEnvelope
            {
                TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RttMs = 9,
                Fps = 60,
                BitrateMbps = 18,
                PacketLoss = 0.0f,
                Resolution = "1920x1080"
            };
            await responseStream.WriteAsync(env);
        }
    }
}

