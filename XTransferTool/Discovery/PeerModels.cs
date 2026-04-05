using System;

namespace XTransferTool.Discovery;

public sealed record ResolvedPeer(
    string Id,
    string Nickname,
    string[] Tags,
    string[] Addresses,
    int ControlPort,
    string[] Capabilities,
    string Os,
    string Ver,
    DateTimeOffset LastSeenAt,
    string InstanceName
);

public enum PeerPresenceState
{
    Online,
    Stale
}

public sealed record PeerRecord(
    ResolvedPeer Peer,
    PeerPresenceState Presence
);

