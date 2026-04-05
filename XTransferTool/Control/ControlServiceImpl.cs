using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Grpc.Core;
using XTransferTool.Control.Proto;

namespace XTransferTool.Control;

public sealed class ControlServiceImpl : ControlService.ControlServiceBase
{
    private readonly InMemoryEventBus _bus;

    public ControlServiceImpl(InMemoryEventBus bus)
    {
        _bus = bus;
    }

    public override Task<HandshakeResponse> Handshake(HandshakeRequest request, ServerCallContext context)
    {
        // V1: Accept all compatible clients (version gating will be added per step-doc).
        var sessionId = Guid.NewGuid().ToString();
        _bus.EnsureSession(sessionId);

        var response = new HandshakeResponse
        {
            ServerId = LocalIds.DeviceId,
            Nickname = LocalIds.Nickname,
            Os = LocalIds.Os,
            AppVersion = LocalIds.AppVersion,
            Accepted = true,
            Reason = "",
            SessionId = sessionId,
            ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        response.Tags.Add(LocalIds.Tags);
        response.Capabilities.Add(LocalIds.Capabilities);

        return Task.FromResult(response);
    }

    public override async Task SubscribeEvents(SubscribeEventsRequest request, IServerStreamWriter<EventEnvelope> responseStream, ServerCallContext context)
    {
        // Replay buffer (best-effort) per step-doc 06.
        var replay = _bus.SnapshotSince(request.SessionId, request.SinceSeq);
        foreach (var ev in replay)
            await responseStream.WriteAsync(ev);

        var reader = _bus.SubscribeLive(request.SessionId);
        await foreach (var ev in reader.ReadAllAsync(context.CancellationToken))
            await responseStream.WriteAsync(ev);
    }
}

internal static class LocalIds
{
    public static string DeviceId { get; } = Guid.NewGuid().ToString();
    public static string Nickname { get; } = "X传输工具";
    public static string[] Tags { get; } = ["会场1", "A片区"];
    public static string Os { get; } =
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "windows"
            : "macos";
    public static string AppVersion { get; } = "0.1.0";
    public static string[] Capabilities { get; } = ["file", "remote"];
}

