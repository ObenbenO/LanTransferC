using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using XTransferTool.Control.Proto;

namespace XTransferTool.Control;

public sealed class InMemoryEventBus
{
    private const int MaxEventsPerSession = 5000;

    private long _seq;
    private readonly ConcurrentDictionary<string, SessionStore> _sessions = new(StringComparer.Ordinal);

    public void EnsureSession(string sessionId) => _sessions.GetOrAdd(sessionId, _ => new SessionStore());

    public void Publish(string sessionId, string type, byte[] payload)
    {
        var store = _sessions.GetOrAdd(sessionId, _ => new SessionStore());

        var env = new EventEnvelope
        {
            Seq = Interlocked.Increment(ref _seq),
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = type,
            Payload = Google.Protobuf.ByteString.CopyFrom(payload)
        };

        store.Add(env, MaxEventsPerSession);
    }

    public IReadOnlyList<EventEnvelope> SnapshotSince(string sessionId, long sinceSeq)
    {
        if (!_sessions.TryGetValue(sessionId, out var store))
            return [];
        return store.SnapshotSince(sinceSeq);
    }

    public ChannelReader<EventEnvelope> SubscribeLive(string sessionId)
    {
        var store = _sessions.GetOrAdd(sessionId, _ => new SessionStore());
        return store.Subscribe();
    }

    private sealed class SessionStore
    {
        private readonly object _gate = new();
        private readonly LinkedList<EventEnvelope> _buffer = new();
        private readonly List<Channel<EventEnvelope>> _subs = [];

        public void Add(EventEnvelope env, int max)
        {
            Channel<EventEnvelope>[] subs;

            lock (_gate)
            {
                _buffer.AddLast(env);
                while (_buffer.Count > max)
                    _buffer.RemoveFirst();

                subs = _subs.ToArray();
            }

            foreach (var s in subs)
                s.Writer.TryWrite(env);
        }

        public IReadOnlyList<EventEnvelope> SnapshotSince(long sinceSeq)
        {
            lock (_gate)
            {
                return _buffer.Where(e => e.Seq > sinceSeq).ToList();
            }
        }

        public ChannelReader<EventEnvelope> Subscribe()
        {
            var ch = Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            lock (_gate)
                _subs.Add(ch);

            return ch.Reader;
        }
    }
}

