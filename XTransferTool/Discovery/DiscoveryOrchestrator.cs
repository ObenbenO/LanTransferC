using System;
using System.Threading;
using System.Threading.Tasks;

namespace XTransferTool.Discovery;

public sealed class DiscoveryOrchestrator : IAsyncDisposable
{
    private readonly IDiscoveryAnnouncer _announcer;
    private readonly IDiscoveryBrowser _browser;
    private readonly IPeerResolver _resolver;
    private readonly IPeerDirectory _directory;

    private CancellationTokenSource? _cts;
    private Task? _browseTask;

    public DiscoveryOrchestrator(
        IDiscoveryAnnouncer announcer,
        IDiscoveryBrowser browser,
        IPeerResolver resolver,
        IPeerDirectory directory)
    {
        _announcer = announcer;
        _browser = browser;
        _resolver = resolver;
        _directory = directory;
    }

    public IPeerDirectory Directory => _directory;

    public void Refresh()
    {
        _browser.Refresh();
    }

    public async Task StartAsync(DeviceIdentity identity, int controlPort, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await _announcer.StartAsync(identity, controlPort, _cts.Token);
        _browseTask = BrowseLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        _cts.Cancel();
        try { if (_browseTask is not null) await _browseTask; } catch { /* ignore */ }
        await _announcer.StopAsync();
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _browser.DisposeAsync();
        await _directory.DisposeAsync();
        await _announcer.DisposeAsync();
    }

    private async Task BrowseLoopAsync(CancellationToken ct)
    {
        await foreach (var ev in _browser.BrowseAsync(ct))
        {
            if (ev.Type == DiscoveredPeerEventType.PeerRemoved)
            {
                _directory.MarkGoodbye(ev.InstanceName);
                continue;
            }

            var resolved = await _resolver.TryResolveAsync(ev, ct);
            if (resolved is not null)
                _directory.Upsert(resolved);
        }
    }
}

