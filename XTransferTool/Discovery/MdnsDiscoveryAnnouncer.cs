using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;
using Serilog;

namespace XTransferTool.Discovery;

public sealed class MdnsDiscoveryAnnouncer : IDiscoveryAnnouncer
{
    private readonly TimeSpan _refresh = TimeSpan.FromSeconds(60);

    private ServiceDiscovery? _sd;
    private ServiceProfile? _profile;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    public Task StartAsync(DeviceIdentity identity, int controlPort, CancellationToken ct = default)
    {
        if (_sd is not null)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ms = new MulticastService(DiscoveryDiagnostics.FilterMdnsInterfaces);
        ms.Start();
        _sd = new ServiceDiscovery(ms);

        _profile = BuildProfile(identity, controlPort);
        _sd.Advertise(_profile);
        _sd.Announce(_profile);
        LogAnnounce(identity, controlPort);

        _timer = new PeriodicTimer(_refresh);
        _ = RefreshLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public Task UpdateIdentityAsync(DeviceIdentity identity, int controlPort, CancellationToken ct = default)
    {
        if (_sd is null)
            return StartAsync(identity, controlPort, ct);

        // Re-advertise on changes.
        _profile = BuildProfile(identity, controlPort);
        _sd.Advertise(_profile);
        _sd.Announce(_profile);
        LogAnnounce(identity, controlPort);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;

        if (_sd is not null && _profile is not null)
        {
            try
            {
                // Best effort goodbye.
                _sd.Unadvertise(_profile);
            }
            catch
            {
                // ignore
            }
        }

        _profile = null;
        _sd?.Dispose();
        _sd = null;
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private static void LogAnnounce(DeviceIdentity identity, int controlPort)
    {
        var host = Dns.GetHostName();
        var ipv4 = DiscoveryDiagnostics.FormatLocalUnicastIPv4ForLog();
        var mdnsIf = DiscoveryDiagnostics.FormatMdnsBindInterfacesForLog();
        Log.Information(
            "[discovery] announce mDNS instance=\"{Instance}\" id={Id} controlPort={ControlPort} dnsHostname={Host} mdnsIfacesIpv4=[{MdnsIf}] localUnicastIPv4=[{Ipv4}] (peers map SRV target hostname -> A/AAAA; compare with what the receiver logs as [discovery] resolved)",
            identity.InstanceName(),
            identity.Id,
            controlPort,
            host,
            mdnsIf,
            ipv4);
    }

    private ServiceProfile BuildProfile(DeviceIdentity identity, int controlPort)
    {
        // InstanceName collision avoidance: include short device id.
        var profile = new ServiceProfile(identity.InstanceName(), DiscoveryConstants.ServiceType, (ushort)controlPort);

        var txt = MdnsTxtBuilder.Build(identity, controlPort);
        foreach (KeyValuePair<string, string> kv in txt)
            profile.AddProperty(kv.Key, kv.Value);

        return profile;
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        if (_sd is null)
            return;

        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(ct))
            {
                if (_profile is null)
                    continue;

                // Makaretu.Dns will respond to queries; re-advertise is a simple refresh signal.
                _sd.Advertise(_profile);
                _sd.Announce(_profile);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }
}

