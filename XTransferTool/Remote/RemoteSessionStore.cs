using System;
using System.Collections.Concurrent;

namespace XTransferTool.Remote;

public sealed class RemoteSessionStore
{
    private readonly ConcurrentDictionary<string, string> _sessionIdByRequestId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RemoteSessionRecord> _bySessionId = new(StringComparer.Ordinal);

    public RemoteSessionRecord GetOrCreate(string requestId, Func<RemoteSessionRecord> factory)
    {
        var sessionId = _sessionIdByRequestId.GetOrAdd(requestId, _ =>
        {
            var rec = factory();
            _bySessionId[rec.SessionId] = rec;
            return rec.SessionId;
        });

        return _bySessionId[sessionId];
    }

    public bool TryGet(string sessionId, out RemoteSessionRecord record)
        => _bySessionId.TryGetValue(sessionId, out record!);

    public void UpdateOfferAnswer(string sessionId, string offer, string answer)
    {
        if (_bySessionId.TryGetValue(sessionId, out var rec))
            _bySessionId[sessionId] = rec with { SdpOffer = offer, SdpAnswer = answer };
    }
}

public sealed record RemoteSessionRecord(
    string SessionId,
    string FromId,
    string ToPeerId,
    string Mode,
    DateTimeOffset CreatedAt,
    string SdpOffer,
    string SdpAnswer
);

