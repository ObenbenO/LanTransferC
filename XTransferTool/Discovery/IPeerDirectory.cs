using System;
using System.Collections.Generic;

namespace XTransferTool.Discovery;

public interface IPeerDirectory : IAsyncDisposable
{
    IReadOnlyList<PeerRecord> Snapshot();
    event EventHandler? Changed;

    void Upsert(ResolvedPeer peer);
    void MarkGoodbye(string instanceName);
    void Touch(string peerId, DateTimeOffset seenAt);
}

