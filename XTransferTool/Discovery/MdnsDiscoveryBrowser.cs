using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Makaretu.Dns;

namespace XTransferTool.Discovery;

public sealed class MdnsDiscoveryBrowser : IDiscoveryBrowser
{
    private readonly string _serviceType;
    private ServiceDiscovery? _sd;
    private Channel<DiscoveredPeerEvent>? _events;
    private readonly HashSet<string> _seenInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _periodicQuery = TimeSpan.FromSeconds(5);
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MdnsDiscoveryBrowser(string? serviceType = null)
    {
        _serviceType = serviceType ?? DiscoveryConstants.ServiceType;
    }

    public IAsyncEnumerable<DiscoveredPeerEvent> BrowseAsync(CancellationToken ct = default)
    {
        if (_sd is not null)
            throw new InvalidOperationException("Browser already started.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _events = Channel.CreateUnbounded<DiscoveredPeerEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var ms = new MulticastService(DiscoveryDiagnostics.FilterMdnsInterfaces);
        _sd = new ServiceDiscovery(ms);
        _sd.ServiceInstanceDiscovered += OnDiscovered;
        _sd.ServiceInstanceShutdown += OnShutdown;
        _sd.Mdns.AnswerReceived += OnAnswerReceived;

        _sd.QueryServiceInstances(_serviceType);
        _timer = new PeriodicTimer(_periodicQuery);
        _loop = PeriodicQueryLoopAsync(_cts.Token);

        return ReadAllAsync(_events.Reader, ct);
    }

    public void Refresh()
    {
        // Best-effort: trigger a new query to speed up discovery.
        _sd?.QueryServiceInstances(_serviceType);
    }

    public ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _timer?.Dispose();
        _timer = null;

        if (_sd is not null)
        {
            _sd.ServiceInstanceDiscovered -= OnDiscovered;
            _sd.ServiceInstanceShutdown -= OnShutdown;
            _sd.Mdns.AnswerReceived -= OnAnswerReceived;
            _sd.Dispose();
            _sd = null;
        }

        _cts?.Dispose();
        _cts = null;
        _events?.Writer.TryComplete();
        _events = null;
        return ValueTask.CompletedTask;
    }

    private void OnDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        var instance = e.ServiceInstanceName.ToString().TrimEnd('.');
        if (_seenInstances.Add(instance))
        {
            // Actively resolve SRV/TXT for the instance. Some responders send only the PTR
            // in the first answer; SRV/TXT may require an explicit query.
            try
            {
                _sd?.Mdns.SendQuery(e.ServiceInstanceName, type: DnsType.ANY);
            }
            catch
            {
                // ignore
            }
        }

        _events?.Writer.TryWrite(new DiscoveredPeerEvent(
            DiscoveredPeerEventType.PeerDiscovered,
            instance,
            DateTimeOffset.UtcNow,
            e.Message
        ));
    }

    private void OnShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        _events?.Writer.TryWrite(new DiscoveredPeerEvent(
            DiscoveredPeerEventType.PeerRemoved,
            e.ServiceInstanceName.ToString().TrimEnd('.'),
            DateTimeOffset.UtcNow,
            e.Message
        ));
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        // If we see SRV/TXT records for an instance, surface them as a PeerDiscovered event
        // so the resolver can extract port/TXT/address details.
        var msg = e.Message;
        if (msg is null)
            return;

        var records = msg.Answers.Concat(msg.AdditionalRecords);
        var hasSrv = records.OfType<SRVRecord>().Any();
        var hasTxt = records.OfType<TXTRecord>().Any();
        if (!hasSrv && !hasTxt)
            return;

        var instance = records
            .OfType<SRVRecord>()
            .Select(r => r.Name?.ToString())
            .Concat(records.OfType<TXTRecord>().Select(r => r.Name?.ToString()))
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

        if (string.IsNullOrWhiteSpace(instance))
            return;

        instance = instance.TrimEnd('.');
        _events?.Writer.TryWrite(new DiscoveredPeerEvent(
            DiscoveredPeerEventType.PeerDiscovered,
            instance,
            DateTimeOffset.UtcNow,
            msg
        ));
    }

    private async Task PeriodicQueryLoopAsync(CancellationToken ct)
    {
        // Keep presence fresh. Without periodic queries, mDNS may be silent for long periods,
        // leading to "stale/offline" UI even though peers are fine.
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    _sd?.QueryServiceInstances(_serviceType);
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private static async IAsyncEnumerable<DiscoveredPeerEvent> ReadAllAsync(
        ChannelReader<DiscoveredPeerEvent> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var item))
                yield return item;
        }
    }
}

