using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XTransferTool.Discovery;

public sealed class PeerDirectory : IPeerDirectory
{
    // mDNS/UDP discovery is best-effort; peers should remain visible long enough for UI users
    // to act, even if announcements are infrequent or packets are dropped.
    // With periodic mDNS queries, peers should stay Online; "stale" should be rare
    // (packet loss / sleep / interface changes). Keep the window generous to avoid
    // UI flicker and false "offline" impressions.
    private readonly TimeSpan _softStale = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _hardExpire = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, PeerRecord> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idByInstance = new(StringComparer.OrdinalIgnoreCase);
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(1));
    private readonly CancellationTokenSource _cts = new();

    public event EventHandler? Changed;

    public PeerDirectory()
    {
        _ = SweepLoopAsync(_cts.Token);
    }

    public IReadOnlyList<PeerRecord> Snapshot()
    {
        lock (_gate)
            return _byId.Values.OrderBy(p => p.Peer.Nickname, StringComparer.Ordinal).ToList();
    }

    public void Upsert(ResolvedPeer peer)
    {
        lock (_gate)
        {
            var presence = PeerPresenceState.Online;
            _byId[peer.Id] = new PeerRecord(peer with { LastSeenAt = DateTimeOffset.UtcNow }, presence);
            _idByInstance[peer.InstanceName] = peer.Id;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Touch(string peerId, DateTimeOffset seenAt)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(peerId, out var rec))
                _byId[peerId] = rec with { Peer = rec.Peer with { LastSeenAt = seenAt }, Presence = PeerPresenceState.Online };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkGoodbye(string instanceName)
    {
        lock (_gate)
        {
            if (_idByInstance.TryGetValue(instanceName, out var id))
            {
                _byId.Remove(id);
                _idByInstance.Remove(instanceName);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
        await Task.CompletedTask;
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(ct))
            {
                var now = DateTimeOffset.UtcNow;
                bool changed = false;

                lock (_gate)
                {
                    var ids = _byId.Keys.ToArray();
                    foreach (var id in ids)
                    {
                        var rec = _byId[id];
                        var age = now - rec.Peer.LastSeenAt;

                        if (age >= _hardExpire)
                        {
                            _byId.Remove(id);
                            changed = true;
                            continue;
                        }

                        var newPresence = age >= _softStale ? PeerPresenceState.Stale : PeerPresenceState.Online;
                        if (newPresence != rec.Presence)
                        {
                            _byId[id] = rec with { Presence = newPresence };
                            changed = true;
                        }
                    }
                }

                if (changed)
                    Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }
}

